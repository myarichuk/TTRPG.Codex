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
    EntityReference, // Reference another entity (e.g., NPC's parent organization)
    Collection // A list of sub-objects or IDs
}

public enum PreferredEditor
{
    Form,
    Graph,
    Hybrid,
    Timeline // For story/event authoring
}

public record FieldDefinition(
    string Key,
    string Label,
    FieldType Type,
    string? Description = null,
    List<string>? Options = null,
    object? DefaultValue = null,
    bool IsRequired = false,
    string? TargetEntityType = null, // Used if Type == EntityReference
    string? Category = "General" // For grouping fields in the UI
);

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