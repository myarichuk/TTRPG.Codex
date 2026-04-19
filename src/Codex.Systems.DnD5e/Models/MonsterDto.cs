using System.Text.Json.Serialization;

namespace Codex.Systems.DnD5e.Models;

public class MonsterDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("challenge_rating")]
    public double ChallengeRating { get; set; }

    [JsonPropertyName("hit_points")]
    public int HitPoints { get; set; }
}
