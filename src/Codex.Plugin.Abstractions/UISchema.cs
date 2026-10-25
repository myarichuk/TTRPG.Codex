using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public enum FieldType
{
    Text,
    LongText,
    Number,
    Boolean,
    Dropdown,
    TagList,
    ImagePicker,
    EntityReference, // Reference another entity (e.g., a character's class, an NPC's parent organization)
    Collection // A list of rows, each a small map (name / description / level ...)
}

public enum PreferredEditor
{
    Form,
    Graph,
    Hybrid,
    Timeline // For story/event authoring
}

/// <summary>
/// One field of a schema-driven form. A plain class (not a positional record) so it can be read
/// straight out of <c>system.yaml</c>; the positional constructor is kept for code that builds
/// schemas by hand.
/// </summary>
public class FieldDefinition
{
    public FieldDefinition()
    {
    }

    public FieldDefinition(
        string key,
        string label,
        FieldType type,
        string? description = null,
        List<string>? options = null,
        object? defaultValue = null,
        bool isRequired = false,
        string? targetEntityType = null,
        string? category = "General")
    {
        Key = key;
        Label = label;
        Type = type;
        Description = description;
        Options = options;
        DefaultValue = defaultValue;
        IsRequired = isRequired;
        TargetEntityType = targetEntityType;
        Category = category;
    }

    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public FieldType Type { get; set; } = FieldType.Text;
    public string? Description { get; set; }
    public List<string>? Options { get; set; }
    public object? DefaultValue { get; set; }
    public bool IsRequired { get; set; }

    /// <summary>For <see cref="FieldType.EntityReference"/>: the content type the field points at.</summary>
    public string? TargetEntityType { get; set; }

    /// <summary>Grouping heading in the rendered form.</summary>
    public string? Category { get; set; } = "General";

    /// <summary>Lower bound for <see cref="FieldType.Number"/>.</summary>
    public double? Min { get; set; }

    /// <summary>Upper bound for <see cref="FieldType.Number"/>.</summary>
    public double? Max { get; set; }

    /// <summary>Set on a localized copy of a system: dropdown/tag value -> text to show. Values themselves never change,
    /// because the rules compare them; use <see cref="Display"/> wherever one is shown.</summary>
    public Dictionary<string, string>? OptionLabels { get; set; }

    /// <summary>The text to show for a stored dropdown/tag value; the value itself when it has no translation.</summary>
    public string Display(string value)
    {
        if (OptionLabels == null) return value;
        if (OptionLabels.TryGetValue(value, out var shown)) return shown;
        // Pack data and the system's own values differ in case ("lawful evil" vs "Lawful Evil").
        foreach (var (key, label) in OptionLabels)
        {
            if (string.Equals(key, value, StringComparison.OrdinalIgnoreCase)) return label;
        }

        return value;
    }
}

public record GraphEdgeSchema(
    string EdgeType,
    string Label,
    string TargetNodeType,
    bool IsBidirectional = false,
    string? Color = null
);

public record GraphNodeMetadata(
    string NodeType,
    string Icon,
    string Color,
    List<GraphEdgeSchema> AllowedEdges
);

public record UISchema(
    string EntityType,
    PreferredEditor PreferredEditor,
    List<FieldDefinition> Fields,
    List<GraphNodeMetadata>? GraphMetadata = null,
    string? Intent = "General" // e.g., "WorldBuilding", "Combat", "Knowledge"
);
