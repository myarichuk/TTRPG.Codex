using System.Text.Json.Serialization;

namespace Codex.Systems.DnD5e.Models;

public class ComponentsDto
{
    [JsonPropertyName("material")]
    public bool Material { get; set; }

    [JsonPropertyName("raw")]
    public string Raw { get; set; } = string.Empty;

    [JsonPropertyName("somatic")]
    public bool Somatic { get; set; }

    [JsonPropertyName("verbal")]
    public bool Verbal { get; set; }

    [JsonPropertyName("materials_needed")]
    public List<string>? MaterialsNeeded { get; set; }
}

public class SpellDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    [JsonPropertyName("school")]
    public string School { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("casting_time")]
    public string CastingTime { get; set; } = string.Empty;

    [JsonPropertyName("classes")]
    public List<string> Classes { get; set; } = new();

    [JsonPropertyName("components")]
    public ComponentsDto? Components { get; set; }

    [JsonPropertyName("duration")]
    public string Duration { get; set; } = string.Empty;

    [JsonPropertyName("range")]
    public string Range { get; set; } = string.Empty;

    [JsonPropertyName("ritual")]
    public bool Ritual { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("higher_levels")]
    public string? HigherLevels { get; set; }
}
