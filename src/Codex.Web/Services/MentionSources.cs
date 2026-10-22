using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>
/// Gathers what a note can mention beyond the cast: the campaign's places and its items (the system's inventory
/// types from the active packs, plus homebrew). The repositories have already decided what the viewer may see.
/// </summary>
public sealed class MentionSources(IRegionRepository regions, CampaignLibrary library, ISessionRepository sessionRepo,
    INoteRepository noteRepo, IJournalRepository journalRepo, IWikiRepository wikiRepo)
{
    public async Task<(IReadOnlyList<LocationDocument> Places, IReadOnlyList<MentionItem> Items)> LoadAsync(CampaignDocument campaign, CampaignAccess access)
    {
        var places = (await regions.GetVisibleForCampaignAsync(access)).SelectMany(r => r.Locations).ToList();

        var items = new List<MentionItem>();
        foreach (var type in library.Definition(campaign)?.SheetRules?.Inventory?.Types ?? Enumerable.Empty<string>())
        {
            if (!library.CanRead(campaign, access, type)) continue;
            items.AddRange((await library.OptionsAsync(campaign, access, type))
                .Select(i => new MentionItem(ItemKey(i.Entry.Type, i.Entry.Id), i.Entry.Name, PlayerVisible: i.Homebrew?.IsPublished ?? true)));
        }

        return (places, items);
    }

    public async Task<MentionDirectory> DirectoryAsync(CampaignDocument campaign, CampaignAccess access, IEnumerable<ActorDocument> actors)
    {
        var (places, items) = await LoadAsync(campaign, access);
        return new MentionDirectory(actors, places, items, access.IsDm);
    }

    /// <summary>What a page needs to list the writing that mentions something: the campaign's sessions (for their table
    /// notes), the rest of what this viewer may read (recaps, comments, their journal, biographies and DM notes), and a
    /// directory to name the other mentions in all of it for them.</summary>
    /// <param name="actors">The characters this viewer may see, already redacted for them.</param>
    /// <param name="forWriting">The page has a box that takes mentions, so it needs the directory even when nothing mentions anything yet.</param>
    public async Task<(IReadOnlyList<SessionDocument> Sessions, MentionWritings Writings, MentionDirectory Mentions)> NotesAsync(CampaignDocument campaign, CampaignAccess access, IEnumerable<ActorDocument> actors, bool forWriting = false)
    {
        var cast = actors.ToList();
        var sessions = (await sessionRepo.GetAllForCampaignAsync(campaign.Id)).ToList();
        var writings = new MentionWritings(sessions,
            await noteRepo.GetVisibleForCampaignAsync(access),
            await journalRepo.GetMineAsync(access),
            cast,
            await wikiRepo.GetVisibleForCampaignAsync(access),
            access.IsDm, access.UserId);
        var any = forWriting || writings.HasMentions || sessions.Any(s => s.Notes.Any(n => SessionMentions.HasMentions(n.Text)));
        return (sessions, writings, any ? await DirectoryAsync(campaign, access, cast) : MentionDirectory.Empty);
    }

    public static string ItemKey(string type, string id) => $"{type}/{id}";
}
