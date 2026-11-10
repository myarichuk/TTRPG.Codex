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

    /// <summary>Required, unique within its list: the key the value is stored under on the sheet or entry.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The field's name as the form shows it.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>How the value is entered and kept. Default <c>Text</c>; an <c>EntityReference</c> needs
    /// <c>targetEntityType</c>.</summary>
    public FieldType Type { get; set; } = FieldType.Text;
    /// <summary>A line of help the form shows under the field.</summary>
    public string? Description { get; set; }
    /// <summary>The choices of a <c>Dropdown</c>.</summary>
    public List<string>? Options { get; set; }
    /// <summary>The value the form shows until one is set; for an actor field, also the value a new character starts with.</summary>
    public object? DefaultValue { get; set; }
    /// <summary>Marks the field as required in the form. Only a mark: nothing refuses an empty value.</summary>
    public bool IsRequired { get; set; }

    /// <summary>For <see cref="FieldType.EntityReference"/>: the content type the field points at.</summary>
    public string? TargetEntityType { get; set; }

    /// <summary>Grouping heading in the rendered form.</summary>
    public string? Category { get; set; } = "General";

    /// <summary>Lower bound for <see cref="FieldType.Number"/>.</summary>
    public double? Min { get; set; }

    /// <summary>Upper bound for <see cref="FieldType.Number"/>.</summary>
    public double? Max { get; set; }

    /// <summary>A short name where space is tight (the sheet's plate): "AC" for Armor Class. The label when unset.</summary>
    public string? Abbreviation { get; set; }

    /// <summary>The unit a number is measured in ("ft"), written after it where it stands alone.</summary>
    public string? Unit { get; set; }

    /// <summary>Set on a localized copy of a system: dropdown/tag value -> text to show. Values themselves never change,
    /// because the rules compare them; use <see cref="Display"/> wherever one is shown.</summary>
    [NotInYaml]
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

        // A price such as "25 gp": the amount stays, the coin is shown by the translation of "gp".
        var price = Price.Match(value);
        if (price.Success && OptionLabels.TryGetValue(price.Groups[2].Value, out var coin)) return $"{price.Groups[1].Value} {coin}";

        return value;
    }

    private static readonly System.Text.RegularExpressions.Regex Price =
        new(@"^([\d,]+) (cp|sp|ep|gp|pp)$", System.Text.RegularExpressions.RegexOptions.Compiled);
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
