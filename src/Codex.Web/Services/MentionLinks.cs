using System.Text.Json;
using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>Where a mention leads, and the suggestion list a note box offers, shared by every box that takes mentions.</summary>
public static class MentionLinks
{
    /// <summary>A character's sheet, the place on the Places page, or the library entry.</summary>
    public static string Href(string campaignId, MentionKind kind, string id) => kind switch
    {
        MentionKind.Actor => $"/campaigns/{campaignId}/actors/{Uri.EscapeDataString(id)}",
        MentionKind.Place => $"/campaigns/{campaignId}/world#place-{Uri.EscapeDataString(id)}",
        // Item ids are "type/id"; the library route takes them as two segments.
        _ => $"/campaigns/{campaignId}/library/{string.Join('/', id.Split('/', 2).Select(Uri.EscapeDataString))}"
    };

    /// <summary>The page that holds a piece of writing.</summary>
    public static string Href(string campaignId, WrittenMention mention) => mention.Home switch
    {
        MentionHome.Session => $"/campaigns/{campaignId}/sessions/{Uri.EscapeDataString(mention.HomeId)}",
        MentionHome.Character => $"/campaigns/{campaignId}/actors/{Uri.EscapeDataString(mention.HomeId)}",
        MentionHome.WikiPage => $"/campaigns/{campaignId}/wiki/{Uri.EscapeDataString(mention.HomeId)}",
        _ => $"/campaigns/{campaignId}/journal#journal-{Uri.EscapeDataString(mention.HomeId)}"
    };

    /// <summary>Formatted text with its mentions named and linked for the directory's viewer, as markup.</summary>
    public static string ToHtml(string? text, MentionDirectory mentions, string campaignId) =>
        mentions.ToHtml(text, (kind, id) => Href(campaignId, kind, id));

    /// <summary>
    /// The suggestions app-startup.js offers after @, # or $, as JSON for a box's data-mentions attribute: characters
    /// first (those in <paramref name="first"/>, then the party, then everyone else), then places, then items.
    /// </summary>
    public static string Json(MentionDirectory mentions, IEnumerable<ActorDocument> actors, IEnumerable<string>? first = null)
    {
        var kinds = actors.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Kind);
        var front = first?.ToHashSet() ?? new HashSet<string>();
        var ordered = mentions.Candidates
            .OrderBy(c => c.Kind)
            .ThenBy(c => c.Kind != MentionKind.Actor ? 0 : front.Contains(c.Id) ? 0 : kinds.TryGetValue(c.Id, out var kind) && kind == ActorKind.PlayerCharacter ? 1 : 2)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(ordered.Select(c => new { t = c.Trigger.ToString(), n = c.Name, k = Label(c, kinds) }));
    }

    private static string Label(MentionCandidate candidate, IReadOnlyDictionary<string, ActorKind> kinds) => candidate.Kind switch
    {
        MentionKind.Actor => kinds.GetValueOrDefault(candidate.Id, ActorKind.NonPlayerCharacter) switch
        {
            ActorKind.PlayerCharacter => "PC",
            ActorKind.Monster => "Monster",
            _ => "NPC"
        },
        MentionKind.Place => "Place",
        _ => "Item"
    };
}

/// <summary>The wiki pages a text box offers after "[[" (app-startup.js), as JSON for its data-pages attribute: the most
/// recently updated first, so the page being worked on is at hand.</summary>
public static class PageSuggestions
{
    public static string Json(IEnumerable<WikiPageDocument> pages, Func<WikiPageKind, string> kindLabel) =>
        JsonSerializer.Serialize(pages
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new { n = p.Title, k = kindLabel(p.Kind) }));
}
