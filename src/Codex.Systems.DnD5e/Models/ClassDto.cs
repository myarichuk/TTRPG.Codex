using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Codex.Systems.DnD5e.Models;

public class ClassDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hit_die")]
    public int HitDie { get; set; }

    [JsonPropertyName("primary_ability")]
    public string PrimaryAbility { get; set; } = string.Empty;

    [JsonPropertyName("saves")]
    public List<string> Saves { get; set; } = new List<string>();
}
