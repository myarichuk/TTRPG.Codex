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
/// One status effect an entity is currently under. Durations are whole rounds that tick on
/// <see cref="Codex.Core.CodexWorld.AdvanceTurn"/> - never on wall-clock time - and expire
/// relative to a specific creature's turn (<see cref="AnchorId"/>, the actor whose turn boundary
/// counts), matching how conditions actually work at a table. The anchor is an id, not a live
/// entity, so the effect can be stored with the actor and survive the table being ended.
/// A <see cref="RoundsRemaining"/> below zero means "until removed".
/// </summary>
public record ActiveEffect(string EffectId, string SourceId, int RoundsRemaining, EffectExpiry Expiry, string AnchorId)
{
    public const int Indefinite = -1;

    public bool IsIndefinite => RoundsRemaining < 0;
}

/// <summary>An entity can have any number of concurrent effects, each expiring independently.</summary>
public struct ActiveEffectsComponent
{
    public List<ActiveEffect> Effects { get; init; }

    public ActiveEffectsComponent()
    {
        Effects = new List<ActiveEffect>();
    }
}

/// <summary>Depletable pools: "HP", "HP_Max", "TempHP", "Mana", ... keyed by the system's resource keys.</summary>
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

/// <summary>
/// The system-defined attributes (ability scores, characteristics) of an actor, keyed by the
/// <c>AttributeDefinition.Key</c> of its system. Generic on purpose: a homebrew system with
/// "Grit" and "Guile" needs no new component type, just a different <c>system.yaml</c>.
/// </summary>
public struct AttributesComponent
{
    public Dictionary<string, int> Values { get; init; }

    public AttributesComponent()
    {
        Values = new Dictionary<string, int>();
    }

    public int Get(string key) => Values.TryGetValue(key, out var val) ? val : 0;
    public void Set(string key, int value) => Values[key] = value;
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
