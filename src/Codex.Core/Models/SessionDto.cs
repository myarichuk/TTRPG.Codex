namespace Codex.Core.Models;

public class SessionDto
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string Recap { get; set; } = string.Empty;
    public List<SessionEventDto> Events { get; set; } = new();
    public List<SessionNoteDto> Notes { get; set; } = new();
    public List<string> Gallery { get; set; } = new();
}

public class SessionEventDto
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class SessionNoteDto
{
    public string AuthorId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
}
