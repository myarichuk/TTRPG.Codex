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

    /// <summary>
    /// B9 remediation. A child list (Triggers/Requires/Costs/Effects) REPLACES the base's list
    /// wholesale as soon as the child specifies anything in it - it never appends to the base.
    /// The previous behavior appended unconditionally, so overriding Fireball's damage from 8d6
    /// to 10d6 resolved to "10d6 + 8d6" instead of "10d6". Only a list the child left null/empty
    /// falls back to (a deep clone of) the base's. Opt-in additive merging (e.g. a future
    /// `merge: append` flag per field) is left for later - this is plain, predictable override
    /// semantics, which is what every other list here already assumed it had.
    /// </summary>
    public void MergeFrom(IAbilityDefinition baseAbility)
    {
        if (string.IsNullOrEmpty(Name)) Name = baseAbility.Name;
        Description ??= baseAbility.Description;
        IconPath ??= baseAbility.IconPath;

        Triggers = ReplaceOrCloneBase(Triggers, baseAbility.Triggers);
        Requires = ReplaceOrCloneBase(Requires, baseAbility.Requires);
        Costs = ReplaceOrCloneBase(Costs, baseAbility.Costs);
        Effects = ReplaceOrCloneBase(Effects, baseAbility.Effects);

        if (baseAbility.Metadata != null)
        {
            Metadata ??= new Dictionary<string, object>();
            foreach (var kvp in baseAbility.Metadata)
            {
                if (!Metadata.ContainsKey(kvp.Key)) Metadata[kvp.Key] = kvp.Value;
            }
        }
    }

    private static List<TypedComponent>? ReplaceOrCloneBase(List<TypedComponent>? child, List<TypedComponent>? baseList)
    {
        if (child is { Count: > 0 }) return child;
        return baseList?.Select(c => c.Clone()).ToList();
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

public class RulesEntryDefinition : IRulesEntryDefinition
{
    public string Id { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string PackId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Inherits { get; set; }
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> Properties { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();

    public void MergeFrom(IRulesEntryDefinition baseEntry)
    {
        if (string.IsNullOrEmpty(Name)) Name = baseEntry.Name;
        Description ??= baseEntry.Description;
        if (string.IsNullOrEmpty(Kind)) Kind = baseEntry.Kind;

        foreach (var tag in baseEntry.Tags) if (!Tags.Contains(tag)) Tags.Add(tag);
        foreach (var kvp in baseEntry.Properties) if (!Properties.ContainsKey(kvp.Key)) Properties[kvp.Key] = kvp.Value;
        foreach (var kvp in baseEntry.Metadata) if (!Metadata.ContainsKey(kvp.Key)) Metadata[kvp.Key] = kvp.Value;
    }
}