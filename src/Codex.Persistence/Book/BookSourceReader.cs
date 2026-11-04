using Codex.Core;

namespace Codex.Persistence;

/// <summary>
/// Reads a campaign for a book. Redaction lives in the repositories (<c>RavenActorRepository.Redact</c>, the session, region, fact,
/// note and wiki repositories, <c>SessionEventRedactor</c>), and a second filter written here would drift from them the first time
/// a field is added. So the party edition is not a filtered DM bundle: every collection is read through the same access-scoped
/// call a player's page makes, as a player who owns no character. Its user id is made up for each read and equals no stored owner
/// (not even an empty one, which <see cref="CampaignAccess.PartyPreview"/>'s empty id would), so no "my own comes whole"
/// carve-out in a repository can apply to it.
/// <para>The only unscoped reads are the DM edition's encounters, and the session ids, which are enumerated here and then each
/// session is read through <see cref="ISessionRepository.GetVisibleAsync"/>; the list itself never leaves
/// <see cref="SessionIdsAsync"/>. Journals and world chats are private and never read.</para>
/// <para>The repositories hand some fields to players untouched (a fact's open-ended <c>Metadata</c>, the account ids that author
/// pages, notes and homebrew and own characters), and a book has no use for them, so the party edition clears them: it holds no
/// account id at all.</para>
/// <para>On top of the repositories, the party edition takes secret-block markers out of every text (a revealed block keeps its
/// <c>:::party</c> marker for the app to show as new) and then refuses to continue if a marker line is still there.</para>
/// </summary>
public sealed class BookSourceReader(
    ICampaignRepository campaigns,
    IActorRepository actors,
    ISessionRepository sessions,
    IEncounterRepository encounters,
    IFactRepository facts,
    INoteRepository notes,
    IRegionRepository regions,
    IHomebrewRepository homebrew,
    IWikiRepository wiki)
{
    /// <summary>The campaign's book source for <paramref name="audience"/>, or null when the caller is not the campaign's DM
    /// (a player has nothing to print that they cannot already open) or the campaign is gone.</summary>
    public async Task<BookSource?> ReadAsync(CampaignAccess dm, BookAudience audience)
    {
        if (!dm.IsDm)
        {
            return null;
        }

        var reader = audience == BookAudience.Dm ? dm : Stranger(dm.CampaignId);
        if (await campaigns.GetAsync(dm.CampaignId) is not { } campaign)
        {
            return null;
        }

        var party = audience == BookAudience.Party;
        var clean = party ? (Func<string?, string?>)Clean : text => text;

        var cast = (await actors.GetVisibleForCampaignAsync(reader)).Where(a => !a.Retired).ToList();
        foreach (var actor in cast)
        {
            actor.Biography = clean(actor.Biography);
            actor.PublicDescription = clean(actor.PublicDescription);
            if (party)
            {
                actor.OwnerUserId = null;
            }
        }

        var readSessions = new List<SessionDocument>();
        foreach (var id in await SessionIdsAsync(dm.CampaignId))
        {
            if (await sessions.GetVisibleAsync(id, reader) is not { } session || (party && session.Status != SessionStatus.Completed))
            {
                continue;
            }

            session.Recap = clean(session.Recap) ?? string.Empty;
            foreach (var note in session.Notes)
            {
                note.Text = clean(note.Text) ?? string.Empty;
                if (party)
                {
                    note.AuthorId = string.Empty;
                }
            }

            if (party)
            {
                session.EncounterIds = new List<string>();
                session.RollLog.ForEach(roll => roll.RollerUserId = string.Empty);
            }

            readSessions.Add(session);
        }

        var lore = (await facts.GetVisibleForCampaignAsync(reader, new HashSet<string>())).ToList();
        foreach (var fact in lore)
        {
            fact.Details = clean(fact.Details);
            fact.Summary = clean(fact.Summary) ?? string.Empty;
            if (party)
            {
                // Open-ended, and the fact repository hands it to players as it is: nothing says what is in it.
                fact.Metadata = new Dictionary<string, object>();
            }
        }

        var map = (await regions.GetVisibleForCampaignAsync(reader)).ToList();
        foreach (var location in map.SelectMany(r => r.Locations))
        {
            location.Description = clean(location.Description);
            if (party)
            {
                // The repository documents Feature as DM only; this keeps the book from depending on that being true forever.
                location.Feature = null;
                location.Metadata = new Dictionary<string, object>();
            }
        }

        var pages = (await wiki.GetVisibleForCampaignAsync(reader)).ToList();
        foreach (var page in pages)
        {
            page.Body = clean(page.Body) ?? string.Empty;
            if (party)
            {
                page.AuthorUserId = string.Empty;
            }
        }

        var tableNotes = (await notes.GetVisibleForCampaignAsync(reader)).Where(n => n.Visibility == CommentVisibility.Public).ToList();
        foreach (var note in tableNotes)
        {
            note.Content = clean(note.Content) ?? string.Empty;
            if (party)
            {
                note.AuthorId = string.Empty;
            }
        }

        var entries = (await homebrew.GetVisibleForCampaignAsync(reader)).ToList();
        foreach (var entry in entries)
        {
            entry.Description = clean(entry.Description);
            if (party)
            {
                entry.AuthorUserId = string.Empty;
            }
        }

        var fights = audience == BookAudience.Dm
            ? (await encounters.GetAllForCampaignAsync(dm.CampaignId)).ToList()
            : new List<EncounterDocument>();

        return new BookSource(
            audience,
            new BookCampaign(campaign.Id, campaign.Name, campaign.SystemId, clean(campaign.Tagline) ?? string.Empty,
                clean(campaign.Description) ?? string.Empty, campaign.ActivePacks.ToList()),
            cast,
            readSessions.OrderBy(s => s.Date).ToList(),
            lore,
            map,
            pages,
            tableNotes,
            entries,
            fights);
    }

    /// <summary>A player of the campaign who is nobody: no account has this id and no character names it as owner.</summary>
    internal static CampaignAccess Stranger(string campaignId) =>
        new(campaignId, "book-reader:" + Guid.NewGuid().ToString("N"), CampaignRole.Player);

    // The one unscoped read: ids and nothing else. Each session is then read through GetVisibleAsync.
    private async Task<IReadOnlyList<string>> SessionIdsAsync(string campaignId) =>
        (await sessions.GetAllForCampaignAsync(campaignId)).Select(s => s.Id).ToList();

    /// <summary>What a player who owns no character reads of <paramref name="text"/>, with no marker lines. Throws when one is
    /// left, which would mean the secret-block grammar and this check disagree: a book is not made from that text.</summary>
    internal static string? Clean(string? text)
    {
        if (text == null)
        {
            return null;
        }

        var result = SecretText.ForPlayers(text);
        if (result.Split('\n').Any(line => SecretText.IsMarkerLine(line.TrimEnd('\r'))))
        {
            throw new InvalidOperationException("A secret-block marker survived reading a text for the party edition; the book is not built.");
        }

        return result;
    }
}
