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

public struct TagsComponent
{
    public List<string> Tags { get; init; }

    public TagsComponent()
    {
        Tags = new List<string>();
    }
}

// For modeling "Who knows whom" or "Where is what" in ECS
public struct ActorReferenceComponent
{
    public string TargetId { get; init; }
    public string RelationType { get; init; }
    public Dictionary<string, object> Metadata { get; init; }

    public ActorReferenceComponent()
    {
        TargetId = string.Empty;
        RelationType = "Unknown";
        Metadata = new Dictionary<string, object>();
    }
}