using System.Collections.Generic;

namespace Codex.Core.Components;

public struct StatusEffectComponent
{
    public string EffectId { get; init; }
    public string PackId { get; init; }

    public StatusEffectComponent(string effectId, string packId)
    {
        EffectId = effectId;
        PackId = packId;
    }
}

public struct DurationComponent
{
    public float RoundsRemaining { get; set; }

    public DurationComponent(float roundsRemaining)
    {
        RoundsRemaining = roundsRemaining;
    }

    public DurationComponent(double roundsRemaining)
    {
        RoundsRemaining = (float)roundsRemaining;
    }

    public DurationComponent(int roundsRemaining)
    {
        RoundsRemaining = (float)roundsRemaining;
    }
}

public struct ResourcePoolComponent
{
    public Dictionary<string, int> Pools { get; init; }

    public ResourcePoolComponent()
    {
        Pools = new Dictionary<string, int>();
    }

    public void Set(string name, int value) => Pools[name] = value;
    public int Get(string name) => Pools.TryGetValue(name, out var val) ? val : 0;

    public void Modify(string name, int delta)
    {
        Pools[name] = Get(name) + delta;
    }
}

public record Modifier(string Stat, int Value, string Source, string? Description = null);

public struct StatModifierComponent
{
    public List<Modifier> Modifiers { get; init; }

    public StatModifierComponent()
    {
        Modifiers = new List<Modifier>();
    }
}