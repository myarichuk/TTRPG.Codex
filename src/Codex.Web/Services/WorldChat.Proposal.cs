using System.Text;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Microsoft.Extensions.AI;

namespace Codex.Web.Services;

// What the DM does with the chat's proposal: apply what they kept, or discard it.
public sealed partial class WorldChat
{
    /// <summary>Saves the proposal's ticked entries and tells the model what they became, so the conversation can build on them.</summary>
    public async Task<WorldBuildResult> ApplyAsync(CancellationToken ct = default)
    {
        if (IsBusy) throw new InvalidOperationException("A turn or an apply is under way.");
        claimed = true;
        try
        {
            return await ApplyNowAsync(ct);
        }
        finally
        {
            claimed = false;
        }
    }

    private async Task<WorldBuildResult> ApplyNowAsync(CancellationToken ct)
    {
        var proposal = Proposal ?? throw new LocalizedException("WorldChat.Error.NoProposal");
        await StillDmAsync();
        if (Document.ApplyingSince is { } since && DateTime.UtcNow - since < ApplyClaim)
        {
            throw new LocalizedException("WorldChat.Error.ApplyingElsewhere");
        }

        // Stored first: if another tab changed this chat (or applied this very proposal) since it was loaded, the save is refused
        // here, before anything is made, rather than making it all twice. It says an apply is under way, so a tab that opens the
        // chat after it, before the first entry is stored, does not start another (D10).
        Document.ApplyingSince = DateTime.UtcNow;
        try
        {
            await SaveAsync();
        }
        catch
        {
            Document.ApplyingSince = null;
            throw;
        }

        WorldBuildResult result;
        try
        {
            // Each entry is kept in the chat the moment it is stored, so an apply cut short by anything (a failure, a closed tab,
            // a restart) leaves a chat that knows what it made: Apply again only adds the rest.
            result = await builder.SaveAsync(campaign, access, proposal, ct, SaveAsync);
        }
        catch (Exception ex) when (!IsStale(ex))
        {
            Document.ApplyingSince = null;
            await SaveAsync();
            throw;
        }

        Document.ApplyingSince = null;
        Proposal = null;
        Held = null;
        Document.ProposalWarnings = new List<string>();
        Document.Decided = new List<string>();

        // Everything new starts hidden; a change to something players can already see is not hidden, so the model is told which.
        var note = new StringBuilder("(A note from the app, not from the game master: the game master applied your proposal.");
        var made = new List<string>();
        made.AddRange(result.Pages.Select(page => $"page existing:{page.Slug} \"{page.Title}\""));
        made.AddRange(result.Places.Select(place => $"place existing:{WorldBundle.PlacePrefix}{place.Id} \"{place.Name}\""));
        made.AddRange(result.Actors.Select(actor => $"cast member \"{actor.Name}\""));
        made.AddRange(result.Entries.Select(entry => $"homebrew existing:{WorldBundle.HomebrewPrefix}{entry.Type}/{entry.EntryId} \"{entry.Name}\""));
        if (result.Facts.Count > 0) made.Add($"{result.Facts.Count} secret(s) in Lore");
        if (made.Count > 0) note.Append(" New, and hidden from the players until the game master reveals them: " + string.Join("; ", made) + ".");
        foreach (var change in result.Changes)
        {
            var what = change.Target switch
            {
                EditTarget.Place => "description of the place", EditTarget.Actor => "history of the cast member", EditTarget.Homebrew => "homebrew entry",
                EditTarget.Region => "region", EditTarget.Fact => "secret in Lore", _ => "text of the page"
            };
            if (change.Removed)
            {
                var gone = change.Target switch
                {
                    EditTarget.Place => "place \"{0}\" is gone; the places inside it moved up to its parent.",
                    EditTarget.Region => "region \"{0}\" is gone, with every place in it.",
                    EditTarget.Actor => "cast member \"{0}\" was retired: out of play, but their page and mentions remain.",
                    EditTarget.Homebrew => "homebrew entry \"{0}\" is gone.",
                    EditTarget.Fact => "secret \"{0}\" is gone from Lore.",
                    _ => "page \"{0}\" is gone."
                };
                note.Append(" Deleted: the " + string.Format(System.Globalization.CultureInfo.InvariantCulture, gone, change.Name));
                continue;
            }

            note.Append($" Changed: the {what} \"{change.Name}\" " + (change.Text ? "now reads as you wrote it" : "was renamed, moved or retyped as you asked") + "; " +
                        (change.Shared ? "the players can see it, so they read the change, all but its hidden blocks." : "it is hidden from the players."));
        }

        if (result.Shortlisted.Count > 0) note.Append(" On the Bestiary shortlist (the game master's own): " + string.Join("; ", result.Shortlisted.Select(c => $"\"{c.Name}\"")) + ".");
        if (result.Retagged > 0) note.Append($" Tags changed on {result.Retagged} page(s) or entries.");
        history.Add(new ChatMessage(ChatRole.User, note.Append(')').ToString()));
        Document.Lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, Localizer.For(Lang)["WorldChat.Applied", result.Count], icon: "circle-check") { Links = Links(result) });
        await SaveAsync();
        return result;
    }

    /// <summary>A link as the model reads it: what it is, and the key to use for it.</summary>
    private static string Described(WorldChatLink link) => link.Kind switch
    {
        "page" => $"page existing:{link.Id} \"{link.Name}\"",
        "place" => $"place existing:{WorldBundle.PlacePrefix}{link.Id} \"{link.Name}\"",
        "actor" => $"cast member \"{link.Name}\"",
        "homebrew" => $"homebrew existing:{WorldBundle.HomebrewPrefix}{link.Id} \"{link.Name}\"",
        "bestiary" => $"\"{link.Name}\" on the Bestiary",
        "lore" => $"{link.Name} secret(s) in Lore",
        "tags" => $"tags changed on {link.Name} page(s) or entries",
        _ => $"the change to \"{link.Name}\""
    };

    /// <summary>What an applied proposal made or changed, as links the conversation keeps.</summary>
    private static List<WorldChatLink> Links(WorldBuildResult result)
    {
        var links = new List<WorldChatLink>();
        links.AddRange(result.Pages.Select(p => new WorldChatLink { Kind = "page", Id = p.Slug, Name = p.Title }));
        links.AddRange(result.Actors.Select(a => new WorldChatLink { Kind = "actor", Id = a.Id, Name = a.Name }));
        // A new place's page is linked already, and the page leads to the place.
        links.AddRange(result.Places.Where(p => result.Pages.All(page => page.Id != p.WikiPageId)).Select(p => new WorldChatLink { Kind = "place", Id = p.Id, Name = p.Name }));
        links.AddRange(result.Entries.Select(e => new WorldChatLink { Kind = "homebrew", Id = $"{e.Type}/{e.EntryId}", Name = e.Name }));
        links.AddRange(result.Changes.Where(c => !c.Removed).Select(c => new WorldChatLink { Kind = "change-" + c.Target.ToString().ToLowerInvariant(), Id = c.Id, Name = c.Name }));
        links.AddRange(result.Shortlisted.Select(b => new WorldChatLink { Kind = "bestiary", Id = $"{b.EntryType}/{b.EntryId}", Name = b.Name }));
        if (result.Retagged > 0) links.Add(new WorldChatLink { Kind = "tags", Name = result.Retagged.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (result.Facts.Count > 0) links.Add(new WorldChatLink { Kind = "lore", Name = result.Facts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return links;
    }

    /// <summary>Drops the proposal without saving it, and tells the model so.</summary>
    public async Task DiscardAsync()
    {
        if (Proposal is not { } proposal) return;
        if (IsBusy) throw new InvalidOperationException("A turn or an apply is under way.");
        // An apply that stopped part way already made some of it; that stays, and both the DM and the model are told what it is.
        var kept = await builder.SavedLinksAsync(proposal, access);
        Proposal = null;
        Held = null;
        Document.ProposalWarnings = new List<string>();
        Document.Decided = new List<string>();
        var l = Localizer.For(Lang);
        if (kept.Count == 0)
        {
            history.Add(new ChatMessage(ChatRole.User, "(A note from the app: the game master discarded your last proposal; nothing from it was saved.)"));
            Document.Lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, l["WorldChat.Discarded"], icon: "x"));
        }
        else
        {
            history.Add(new ChatMessage(ChatRole.User, "(A note from the app: the game master discarded the rest of your last proposal. An earlier apply that stopped " +
                $"part way had already saved some of it, which still exists, hidden from the players: {string.Join("; ", kept.Select(Described))}.)"));
            Document.Lines.Add(new WorldChatLine(WorldChatSpeaker.Tool, l["WorldChat.DiscardedRest"], icon: "x") { Links = kept });
        }
        await SaveAsync();
    }
}
