using System.Collections.Generic;
using DefaultEcs;

namespace Codex.Core.Components;

/// <summary>Which turn boundary an <see cref="ActiveEffect"/>'s round count ticks down on.</summary>
public enum EffectExpiry
{
    StartOfTurn,
    EndOfTurn
}

/// <summary>
/// One status effect an entity is currently under (3.4/B6). Durations are whole rounds that tick
/// on <see cref="Codex.Core.CodexWorld.AdvanceTurn"/> - never on wall-clock time - and expire
/// relative to a specific creature's turn (<see cref="AnchorEntity"/>), matching how conditions
/// actually work at a table.
/// </summary>
public record ActiveEffect(string EffectId, string SourceId, int RoundsRemaining, EffectExpiry Expiry, Entity AnchorEntity);

/// <summary>
/// Replaces the old single-slot <c>StatusEffectComponent</c>/<c>DurationComponent</c> pair, which
/// could hold at most one status at a time - adding Stunned while Poisoned was active silently
/// overwrote Poisoned (B6, confirmed: Poisoned then Stunned left only Stunned, and Stunned's
/// expiry then also removed Poisoned's tracking). An entity can have any number of concurrent
/// effects here, each expiring independently.
/// </summary>
public struct ActiveEffectsComponent
{
    public List<ActiveEffect> Effects { get; init; }

    public ActiveEffectsComponent()
    {
        Effects = new List<ActiveEffect>();
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