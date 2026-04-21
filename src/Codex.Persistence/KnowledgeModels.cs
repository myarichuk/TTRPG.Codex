using System.Collections.Generic;

namespace Codex.Persistence;

public enum KnowledgeLevel
{
    None,
    Rumor,
    Heard,
    Witnessed,
    Full
}

public record KnowerEntry(string EntityId, KnowledgeLevel Level, string? Source = null, string? Notes = null);

public class FactDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }

    // Who knows this fact
    public List<KnowerEntry> KnownBy { get; set; } = new();

    // Related entities (e.g., this fact is ABOUT these NPCs or Locations)
    public List<string> RelatedEntityIds { get; set; } = new();

    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class KnowledgeEntry
{
    public string KnowerId { get; set; } = string.Empty;
    public List<string> FactIds { get; set; } = new();
}