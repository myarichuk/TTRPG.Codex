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
