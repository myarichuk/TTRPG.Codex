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
    public TriggerType Trigger { get; set; }
    public string? Inherits { get; set; }
    public Dictionary<string, int>? Costs { get; set; }
    public List<AbilityEffect>? Effects { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }

    List<IAbilityEffect>? IAbilityDefinition.Effects => Effects?.Cast<IAbilityEffect>().ToList();

    public void MergeFrom(IAbilityDefinition baseAbility)
    {
        if (string.IsNullOrEmpty(Name)) Name = baseAbility.Name;
        Description ??= baseAbility.Description;
        IconPath ??= baseAbility.IconPath;
        if (Trigger == TriggerType.Passive && baseAbility.Trigger != TriggerType.Passive) Trigger = baseAbility.Trigger;

        if (baseAbility.Costs != null)
        {
            Costs ??= new Dictionary<string, int>();
            foreach (var kvp in baseAbility.Costs)
            {
                if (!Costs.ContainsKey(kvp.Key)) Costs[kvp.Key] = kvp.Value;
            }
        }

        if (baseAbility.Effects != null)
        {
            Effects ??= new List<AbilityEffect>();
            foreach (var effect in baseAbility.Effects)
            {
                Effects.Add(new AbilityEffect
                {
                    Type = effect.Type,
                    Script = effect.Script,
                    Params = effect.Params != null ? new Dictionary<string, object>(effect.Params) : null
                });
            }
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

public class AbilityEffect : IAbilityEffect
{
    public string Type { get; set; } = string.Empty;
    public string? Script { get; set; }
    public Dictionary<string, object>? Params { get; set; }
}

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