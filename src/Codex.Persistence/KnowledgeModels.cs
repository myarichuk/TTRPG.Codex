using System;
using System.Collections.Generic;
using System.Linq;

namespace Codex.Persistence;

public enum KnowledgeLevel
{
    None,
    Rumor,
    Heard,
    Witnessed,
    Full
}

public enum CommentVisibility
{
    Private, // Only visible to the author and DM
    Public   // Visible to everyone in the campaign
}

public record KnowerEntry(string EntityId, KnowledgeLevel Level, string? Source = null, string? Notes = null);

public enum FactVisibility
{
    /// <summary>Only the DM can see this fact exists, regardless of <see cref="FactDocument.KnownBy"/>.</summary>
    DmOnly,

    /// <summary>Visible to any player whose actor is in <see cref="FactDocument.KnownBy"/>.</summary>
    KnowersOnly,

    /// <summary>Visible to every campaign member.</summary>
    Public
}

public class FactDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }
    public FactVisibility Visibility { get; set; } = FactVisibility.DmOnly;
    public List<KnowerEntry> KnownBy { get; set; } = new();
    /// <summary>What the fact is about, as <see cref="FactLinks"/> keys (<c>actor:id</c>, <c>place:id</c>, <c>item:type/id</c>,
    /// <c>wiki:slug</c>): rebuilt from its text on every save, so a page or a sheet can list the lore about it.</summary>
    public List<string> RelatedEntityIds { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class NoteDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty; // The ID of the NPC, Location, or Ability
    public string AuthorId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public CommentVisibility Visibility { get; set; } = CommentVisibility.Private;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class KnowledgeEntry
{
    public string CampaignId { get; set; } = string.Empty;
    public string KnowerId { get; set; } = string.Empty;
    public List<string> FactIds { get; set; } = new();
}

/// <summary>What a fact is about, read off its text: the characters, places and items it mentions (<c>[[actor:id]]</c>,
/// stored the way every editor stores a mention) and the wiki pages it links to (<c>[[Title]]</c>).</summary>
public static class FactLinks
{
    public static string Actor(string id) => $"actor:{id}";
    public static string Place(string id) => $"place:{id}";
    public static string Page(string slug) => $"wiki:{slug}";

    public static IEnumerable<string> Of(params string?[] texts)
    {
        var found = new List<string>();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            found.AddRange(SessionMentions.TokenPattern().Matches(text).Select(m => $"{m.Groups[1].Value}:{m.Groups[2].Value}"));
            found.AddRange(Codex.Core.WikiText.ExtractLinks(text).Select(title => Page(Codex.Core.WikiText.Slugify(title))));
        }

        return found.Distinct(StringComparer.Ordinal);
    }

    /// <summary>The facts about any of <paramref name="keys"/>.</summary>
    public static IEnumerable<FactDocument> About(IEnumerable<FactDocument> facts, params string?[] keys)
    {
        var wanted = keys.Where(k => !string.IsNullOrEmpty(k) && !k.EndsWith(':')).ToHashSet(StringComparer.Ordinal);
        return facts.Where(f => f.RelatedEntityIds.Any(wanted.Contains));
    }
}
