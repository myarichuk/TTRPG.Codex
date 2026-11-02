using System;
using DefaultEcs;
using DefaultEcs.System;
using System.Collections.Generic;
using Codex.Core.Components;

namespace Codex.Core;

/// <summary>
/// One ECS world. Before 3.1 this was registered as a single DI singleton shared by every
/// campaign, every circuit, and every user on the server (B8) - not thread-safe, and one bad
/// script or a stray <c>/demo</c> click advanced every duration for everyone. <c>CampaignRuntime</c>
/// now owns exactly one <see cref="CodexWorld"/> per live campaign; nothing outside this class and
/// its single-writer command loop should ever touch <see cref="InnerWorld"/> directly.
/// </summary>
public sealed class CodexWorld : IDisposable
{
    private readonly World _world;
    private ISystem<float>? _systems;
    private readonly List<ISystem<float>> _registeredSystems = new();

    public World InnerWorld => _world;

    public CodexWorld()
    {
        _world = new World();
    }

    public void AddSystem(ISystem<float> system)
    {
        _registeredSystems.Add(system);
        _systems = new SequentialSystem<float>(_registeredSystems.ToArray());
    }

    public Entity CreateEntity()
    {
        return _world.CreateEntity();
    }

    /// <summary>Runs every registered <see cref="ISystem{T}"/> once. Plugin-registered systems
    /// (e.g. SWFFG's WoundSystem) that react to an event component still drain via this; it no
    /// longer drives status-effect durations (see <see cref="AdvanceTurn"/>).</summary>
    public void Tick(float deltaTime)
    {
        _systems?.Update(deltaTime);
    }

    /// <summary>
    /// Adds a status effect to <paramref name="entity"/> without disturbing any effect already
    /// there (B6 fix - the old API replaced whatever single status was already set). Rounds tick
    /// down on <see cref="AdvanceTurn"/> at the <paramref name="expiry"/> boundary of the turn of
    /// the actor named by <paramref name="anchorId"/>; a round count below one means the effect
    /// lasts until it is removed.
    /// </summary>
    public void AddStatus(Entity entity, string effectId, string sourceId, int roundsRemaining, EffectExpiry expiry, string anchorId, string? heldBy = null)
    {
        if (!entity.Has<ActiveEffectsComponent>())
        {
            entity.Set(new ActiveEffectsComponent());
        }

        var rounds = roundsRemaining < 1 ? ActiveEffect.Indefinite : roundsRemaining;
        entity.Get<ActiveEffectsComponent>().Effects.Add(new ActiveEffect(effectId, sourceId, rounds, expiry, anchorId, string.IsNullOrEmpty(heldBy) ? null : heldBy));
    }

    /// <summary>Scripting convenience (kept for <see cref="Codex.Core.Scripting.ScriptApi"/>): a script
    /// knows entities, not actor ids, so the effect is anchored to nobody's turn and lasts until the
    /// table removes it.</summary>
    public void AddStatus(Entity entity, string effectId, string sourceId, double roundsRemaining) =>
        AddStatus(entity, effectId, sourceId, (int)Math.Ceiling(roundsRemaining), EffectExpiry.EndOfTurn, string.Empty);

    /// <summary>
    /// Advances one turn boundary for the actor <paramref name="anchorId"/>: every timed
    /// <see cref="ActiveEffect"/> anchored to it, on any entity, whose expiry matches
    /// <paramref name="boundary"/> ticks its <see cref="ActiveEffect.RoundsRemaining"/> down by one
    /// round and is removed once it reaches zero. Durations are whole rounds tied to a specific
    /// creature's turn, not wall-clock <see cref="Tick"/> (B6/B8) - calling this is how a DM
    /// combat console advances the encounter, not a timer. Returns the effects that expired, as
    /// (entity, effect) pairs, so the caller can log them.
    /// </summary>
    public List<(Entity Entity, ActiveEffect Effect)> AdvanceTurn(string anchorId, EffectExpiry boundary = EffectExpiry.EndOfTurn)
    {
        var expired = new List<(Entity, ActiveEffect)>();
        if (string.IsNullOrEmpty(anchorId))
        {
            return expired;
        }

        using var set = _world.GetEntities().With<ActiveEffectsComponent>().AsSet();
        foreach (ref readonly var entity in set.GetEntities())
        {
            var effects = entity.Get<ActiveEffectsComponent>().Effects;
            for (var i = effects.Count - 1; i >= 0; i--)
            {
                var effect = effects[i];
                if (effect.IsIndefinite || effect.Expiry != boundary || !string.Equals(effect.AnchorId, anchorId, StringComparison.Ordinal))
                {
                    continue;
                }

                var remaining = effect.RoundsRemaining - 1;
                if (remaining <= 0)
                {
                    effects.RemoveAt(i);
                    expired.Add((entity, effect));
                }
                else
                {
                    effects[i] = effect with { RoundsRemaining = remaining };
                }
            }
        }

        return expired;
    }

    /// <summary>Removes every instance of <paramref name="effectId"/> from <paramref name="entity"/>; returns how many went.</summary>
    public int RemoveStatus(Entity entity, string effectId)
    {
        if (!entity.Has<ActiveEffectsComponent>())
        {
            return 0;
        }

        return entity.Get<ActiveEffectsComponent>().Effects.RemoveAll(e => string.Equals(e.EffectId, effectId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ends, on every entity, the timed effects whose clock is <paramref name="anchorId"/>'s turn: that creature
    /// died or left the fight, so its turn will not come round to tick them (R4). Effects that last until removed stay.</summary>
    public List<(Entity Entity, ActiveEffect Effect)> ExpireAnchoredTo(string anchorId) =>
        RemoveEverywhere(effect => !effect.IsIndefinite && string.Equals(effect.AnchorId, anchorId, StringComparison.Ordinal));

    /// <summary>Ends, on every entity, the effects <paramref name="actorId"/>'s concentration holds up (R4).</summary>
    public List<(Entity Entity, ActiveEffect Effect)> EndHeldBy(string actorId) =>
        RemoveEverywhere(effect => string.Equals(effect.HeldBy, actorId, StringComparison.Ordinal));

    private List<(Entity Entity, ActiveEffect Effect)> RemoveEverywhere(Predicate<ActiveEffect> match)
    {
        var removed = new List<(Entity, ActiveEffect)>();
        using var set = _world.GetEntities().With<ActiveEffectsComponent>().AsSet();
        foreach (ref readonly var entity in set.GetEntities())
        {
            var effects = entity.Get<ActiveEffectsComponent>().Effects;
            for (var i = effects.Count - 1; i >= 0; i--)
            {
                if (match(effects[i]))
                {
                    removed.Add((entity, effects[i]));
                    effects.RemoveAt(i);
                }
            }
        }

        return removed;
    }

    /// <summary>True while <paramref name="entity"/> is under <paramref name="effectId"/>.</summary>
    public static bool HasStatus(Entity entity, string effectId) =>
        entity.Has<ActiveEffectsComponent>() &&
        entity.Get<ActiveEffectsComponent>().Effects.Exists(e => string.Equals(e.EffectId, effectId, StringComparison.OrdinalIgnoreCase));

    // The pool that holds a pool's maximum. The world does not know the system, so the table passes the
    // system's rule (a resource's maxKey, R9); "{name}_Max" is that rule's own fallback.
    private static string MaxKey(Func<string, string>? maxKeyOf, string poolName) => maxKeyOf?.Invoke(poolName) ?? poolName + "_Max";

    /// <summary>Applies healing immediately, clamped to the pool's maximum when present and never below zero.</summary>
    public void ApplyHealing(Entity entity, string poolName, int amount, Func<string, string>? maxKeyOf = null)
    {
        if (amount <= 0 || !entity.Has<ResourcePoolComponent>())
        {
            return;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var next = pool.Get(poolName) + amount;
        if (pool.Pools.TryGetValue(MaxKey(maxKeyOf, poolName), out var max))
        {
            next = Math.Min(next, max);
        }

        pool.Set(poolName, Math.Max(next, 0));
    }

    /// <summary>Spends (negative) or regains (positive) points of an expendable pool such as a spell
    /// slot level, clamped to 0 and the pool's maximum. A pool the actor doesn't have is ignored.</summary>
    public void AdjustResource(Entity entity, string poolName, int delta, Func<string, string>? maxKeyOf = null)
    {
        if (delta == 0 || !entity.Has<ResourcePoolComponent>())
        {
            return;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        if (!pool.Pools.TryGetValue(poolName, out var current))
        {
            return;
        }

        var max = pool.Pools.TryGetValue(MaxKey(maxKeyOf, poolName), out var m) ? m : int.MaxValue;
        pool.Set(poolName, Math.Clamp(current + delta, 0, max));
    }

    /// <summary>Refills each named pool to its maximum (a rest).</summary>
    public void RestoreResources(Entity entity, IEnumerable<string> poolNames, Func<string, string>? maxKeyOf = null)
    {
        if (!entity.Has<ResourcePoolComponent>())
        {
            return;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        foreach (var name in poolNames)
        {
            if (pool.Pools.TryGetValue(MaxKey(maxKeyOf, name), out var max))
            {
                pool.Set(name, max);
            }
        }
    }

    public void Dispose()
    {
        _systems?.Dispose();
        _world.Dispose();
    }
}
