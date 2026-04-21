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