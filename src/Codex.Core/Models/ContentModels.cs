using System.Collections.Generic;
using System.Linq;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Models;

public class AbilityDefinition : IAbilityDefinition
{
    public string Id { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string PackId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? IconPath { get; set; }
    public string? Inherits { get; set; }

    public List<TypedComponent>? Triggers { get; set; }
    public List<TypedComponent>? Requires { get; set; }
    public List<TypedComponent>? Costs { get; set; }
    public List<TypedComponent>? Effects { get; set; }

    public Dictionary<string, object>? Metadata { get; set; }

    public void MergeFrom(IAbilityDefinition baseAbility)
    {
        if (string.IsNullOrEmpty(Name)) Name = baseAbility.Name;
        Description ??= baseAbility.Description;
        IconPath ??= baseAbility.IconPath;

        if (baseAbility.Triggers != null)
        {
            Triggers ??= new List<TypedComponent>();
            Triggers.AddRange(baseAbility.Triggers);
        }

        if (baseAbility.Requires != null)
        {
            Requires ??= new List<TypedComponent>();
            Requires.AddRange(baseAbility.Requires);
        }

        if (baseAbility.Costs != null)
        {
            Costs ??= new List<TypedComponent>();
            Costs.AddRange(baseAbility.Costs);
        }

        if (baseAbility.Effects != null)
        {
            Effects ??= new List<TypedComponent>();
            Effects.AddRange(baseAbility.Effects);
        }

        if (baseAbility.Metadata != null)
        {
            Metadata ??= new Dictionary<string, object>();
            foreach (var kvp in baseAbility.Metadata)
            {
                if (!Metadata.ContainsKey(kvp.Key)) Metadata[kvp.Key] = kvp.Value;
            }
        }
    }
}

// AbilityEffect class is no longer needed as we use TypedComponent
// But I'll check if other models depend on it first.

public class ActorDefinition : IActorDefinition
{
    public string Id { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string PackId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Inherits { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> ImagePaths { get; set; } = new();
    public Dictionary<string, int> Resources { get; set; } = new();
    public List<string> Abilities { get; set; } = new();
    public Dictionary<string, object> Properties { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();

    public void MergeFrom(IActorDefinition baseActor)
    {
        if (string.IsNullOrEmpty(Name)) Name = baseActor.Name;
        Description ??= baseActor.Description;

        foreach (var tag in baseActor.Tags) if (!Tags.Contains(tag)) Tags.Add(tag);
        foreach (var img in baseActor.ImagePaths) if (!ImagePaths.Contains(img)) ImagePaths.Add(img);
        foreach (var ability in baseActor.Abilities) if (!Abilities.Contains(ability)) Abilities.Add(ability);

        foreach (var kvp in baseActor.Resources) if (!Resources.ContainsKey(kvp.Key)) Resources[kvp.Key] = kvp.Value;
        foreach (var kvp in baseActor.Properties) if (!Properties.ContainsKey(kvp.Key)) Properties[kvp.Key] = kvp.Value;
        foreach (var kvp in baseActor.Metadata) if (!Metadata.ContainsKey(kvp.Key)) Metadata[kvp.Key] = kvp.Value;
    }
}

public class LocationDefinition : ILocationDefinition
{
    public string Id { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string PackId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ParentId { get; set; }
    public List<string> ImagePaths { get; set; } = new();
    public List<LocationEdge> Connections { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}