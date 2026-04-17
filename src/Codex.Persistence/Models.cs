using System.Text.Json.Nodes;

namespace Codex.Persistence;

public class CampaignDocument
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public JsonObject WorldState { get; set; } = new();
}

public class CharacterDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public JsonObject State { get; set; } = new();
}

public class UserDocument
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
}

public class SessionDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string Recap { get; set; } = string.Empty;
    public List<SessionNote> Notes { get; set; } = new();
    public List<SessionEvent> Events { get; set; } = new();
}

public class SessionNote
{
    public string AuthorId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
}

public class SessionEvent
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
