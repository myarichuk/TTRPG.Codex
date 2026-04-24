using System;
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

public enum CommentVisibility
{
    Private, // Only visible to the author and DM
    Public   // Visible to everyone in the campaign
}

public record KnowerEntry(string EntityId, KnowledgeLevel Level, string? Source = null, string? Notes = null);

public class FactDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }
    public List<KnowerEntry> KnownBy { get; set; } = new();
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
    public string KnowerId { get; set; } = string.Empty;
    public List<string> FactIds { get; set; } = new();
}
