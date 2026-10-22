using Codex.Core;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;
using DefaultEcs;

namespace Codex.Persistence.Runtime;

/// <summary>
/// The table's vitals rules, as <c>system.yaml</c>'s <c>vitals:</c> section describes them: damage
/// types against a creature's resistances, what reaching zero does, death saves, temporary hit
/// points, exhaustion levels, hit dice and rests. Everything here runs on the runtime's command
/// loop and mutates the entity it is handed; the commands call in and describe what came back.
/// A system without <see cref="VitalsRules"/> gets the plain behaviour: damage stops at zero.
/// </summary>
internal static class TableRules
{
    public static string PrimaryKey(CampaignRuntime runtime) => runtime.System?.PrimaryResource?.Key ?? "HP";
    public static string PrimaryMaxKey(CampaignRuntime runtime) => runtime.System?.PrimaryResource?.MaxKey ?? "HP_Max";
    public static string TemporaryKey(CampaignRuntime runtime) => runtime.System?.Vitals?.TemporaryResource ?? "TempHP";

    private static string MaxKeyFor(CampaignRuntime runtime, string key) =>
        runtime.System?.Resources.FirstOrDefault(r => r.Key == key)?.MaxKey ?? key + "_Max";

    // ---- Damage ----

    public sealed record DamageOutcome(int Amount, int Dealt, int Absorbed, string? Modifier, bool WentDown, bool Died, int FailuresAdded)
    {
        public static readonly DamageOutcome Nothing = new(0, 0, 0, null, false, false, 0);
    }

    /// <summary>
    /// Applies damage: immunity cancels it, resistance halves it (rounded down), vulnerability doubles
    /// it; the temporary pool absorbs first; the primary pool stops at zero. Reaching zero follows
    /// <see cref="DownRules"/>: an overflow of the maximum or a non-player creature dies, a player
    /// character falls unconscious and starts death saves, and damage while already at zero is a
    /// failed save (two for a critical hit).
    /// </summary>
    public static DamageOutcome ApplyDamage(CampaignRuntime runtime, string actorId, Entity entity, string poolName, int amount, string? damageType, bool isCritical)
    {
        if (amount <= 0 || !entity.Has<ResourcePoolComponent>())
        {
            return DamageOutcome.Nothing;
        }

        var doc = runtime.GetActorDocument(actorId);
        var (dealt, modifier) = Modify(runtime, doc, amount, damageType);
        if (dealt <= 0)
        {
            return new DamageOutcome(amount, 0, 0, modifier, false, false, 0);
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var remaining = dealt;
        var absorbed = 0;
        var isPrimary = poolName == PrimaryKey(runtime);
        if (isPrimary)
        {
            var temporary = pool.Get(TemporaryKey(runtime));
            if (temporary > 0)
            {
                absorbed = Math.Min(temporary, remaining);
                pool.Set(TemporaryKey(runtime), temporary - absorbed);
                remaining -= absorbed;
            }
        }

        var current = pool.Get(poolName);
        var next = current - remaining;
        var max = pool.Pools.TryGetValue(MaxKeyFor(runtime, poolName), out var m) ? m : (int?)null;
        if (max.HasValue)
        {
            next = Math.Min(next, max.Value);
        }

        pool.Set(poolName, Math.Max(next, 0));

        if (!isPrimary || next > 0 || runtime.System?.Vitals?.Down is not { } down)
        {
            return new DamageOutcome(amount, dealt, absorbed, modifier, false, false, 0);
        }

        if (IsDead(runtime, entity))
        {
            return new DamageOutcome(amount, dealt, absorbed, modifier, false, false, 0);
        }

        var overflow = -next;
        var isPlayer = doc?.Kind == ActorKind.PlayerCharacter;
        var saves = runtime.System?.Vitals?.DeathSaves;

        if ((down.InstantDeathAtMax && max is > 0 && overflow >= max.Value) || (!isPlayer && down.NonPlayersDieAtZero))
        {
            Die(runtime, entity, actorId);
            return new DamageOutcome(amount, dealt, absorbed, modifier, true, true, 0);
        }

        if (current <= 0)
        {
            // Already down: a hit is a failed death save, two when it was a critical.
            if (saves == null)
            {
                return new DamageOutcome(amount, dealt, absorbed, modifier, false, false, 0);
            }

            runtime.World.RemoveStatus(entity, down.StableCondition);
            if (!CodexWorld.HasStatus(entity, down.Condition))
            {
                runtime.World.AddStatus(entity, down.Condition, "zero hit points", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actorId);
            }

            var failures = isCritical ? 2 : 1;
            var outcome = RecordDeathSave(runtime, actorId, entity, success: false, failures);
            return new DamageOutcome(amount, dealt, absorbed, modifier, false, outcome.Died, failures);
        }

        runtime.World.RemoveStatus(entity, down.StableCondition);
        runtime.World.AddStatus(entity, down.Condition, "zero hit points", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actorId);
        ResetDeathSaves(runtime, entity);
        return new DamageOutcome(amount, dealt, absorbed, modifier, true, false, 0);
    }

    /// <summary>The damage after the creature's immunities, resistances and vulnerabilities to <paramref name="damageType"/>.</summary>
    private static (int Dealt, string? Modifier) Modify(CampaignRuntime runtime, ActorDocument? doc, int amount, string? damageType)
    {
        if (string.IsNullOrWhiteSpace(damageType) || doc == null || runtime.System?.Vitals?.Damage is not { } rules)
        {
            return (amount, null);
        }

        if (Lists(doc.Sheet, rules.ImmunitiesField, damageType))
        {
            return (0, "immune");
        }

        var resisted = Lists(doc.Sheet, rules.ResistancesField, damageType);
        var vulnerable = Lists(doc.Sheet, rules.VulnerabilitiesField, damageType);
        var dealt = amount;
        if (resisted) dealt /= 2;
        if (vulnerable) dealt *= 2;
        var modifier = resisted && vulnerable ? "resisted and vulnerable" : resisted ? "resisted" : vulnerable ? "vulnerable" : null;
        return (dealt, modifier);
    }

    /// <summary>True when the sheet's tag list names the damage type outright ("fire", "cold, lightning"). Whole words only,
    /// so the "Poisoned" condition among immunities is not poison damage, and conditional entries
    /// ("Bludgeoning, Piercing, and Slashing from nonmagical attacks") never apply on their own.</summary>
    private static bool Lists(IReadOnlyDictionary<string, object> sheet, string field, string damageType)
    {
        if (!sheet.TryGetValue(field, out var raw) || raw == null)
        {
            return false;
        }

        IEnumerable<string> entries = raw switch
        {
            string s => s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            System.Collections.IEnumerable list => list.Cast<object?>().Select(o => o?.ToString() ?? string.Empty),
            _ => Array.Empty<string>()
        };

        return entries
            .Where(entry => !IsConditional(entry))
            .SelectMany(entry => entry.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .SelectMany(part => part.Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(part => part.StartsWith("and ", StringComparison.OrdinalIgnoreCase) ? part[4..].Trim() : part)
            .Any(part => string.Equals(part, damageType.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An entry with a condition attached ("... from nonmagical attacks", "... that aren't silvered"):
    /// the table can't tell whether this hit meets it, so the DM applies it by hand rather than the table
    /// applying it to every hit of that type.</summary>
    private static bool IsConditional(string entry) =>
        ConditionalMarkers.Any(marker => entry.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ConditionalMarkers = { " from ", "nonmagical", "that aren't", "that are not", "except", " while ", " when " };

    // ---- Healing and temporary hit points ----

    public sealed record HealOutcome(int Healed, bool WasDead, bool Revived);

    /// <summary>Heals, clamped to the maximum. A dead creature is not healed - the DM removes the dead
    /// condition first (a revival spell) - and a creature brought above zero is back on its feet:
    /// the down and stable conditions go and the death-save tally resets.</summary>
    public static HealOutcome ApplyHealing(CampaignRuntime runtime, string actorId, Entity entity, string poolName, int amount)
    {
        if (amount <= 0 || !entity.Has<ResourcePoolComponent>())
        {
            return new HealOutcome(0, false, false);
        }

        if (IsDead(runtime, entity))
        {
            return new HealOutcome(0, true, false);
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var before = pool.Get(poolName);
        runtime.World.ApplyHealing(entity, poolName, amount);
        var after = pool.Get(poolName);

        var revived = poolName == PrimaryKey(runtime) && before <= 0 && after > 0 && GetUp(runtime, entity);
        return new HealOutcome(after - before, false, revived);
    }

    /// <summary>Temporary hit points don't stack: the creature keeps the higher of what it has and what it is offered.</summary>
    public static bool GrantTemporary(CampaignRuntime runtime, Entity entity, int amount)
    {
        if (amount <= 0 || !entity.Has<ResourcePoolComponent>())
        {
            return false;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var key = TemporaryKey(runtime);
        if (pool.Get(key) >= amount)
        {
            return false;
        }

        pool.Set(key, amount);
        return true;
    }

    // ---- Death saves ----

    public sealed record DeathSaveOutcome(int? Roll, bool? Success, int Successes, int Failures, bool Stabilized, bool Died, bool Revived, bool NotApplicable)
    {
        public static readonly DeathSaveOutcome None = new(null, null, 0, 0, false, false, false, true);
    }

    /// <summary>Rolls a death save with the table's dice and records it: a natural critical success
    /// brings the creature back with one hit point, a critical failure counts twice.</summary>
    public static DeathSaveOutcome RollDeathSave(CampaignRuntime runtime, string actorId, Entity entity)
    {
        if (runtime.System?.Vitals?.DeathSaves is not { } rules || !IsDown(runtime, entity) || IsDead(runtime, entity))
        {
            return DeathSaveOutcome.None;
        }

        var roll = runtime.DiceRoller.Roll(rules.Die).Total;
        if (rules.CriticalSuccess is { } crit && roll >= crit)
        {
            ResetDeathSaves(runtime, entity);
            var pool = entity.Get<ResourcePoolComponent>();
            pool.Set(PrimaryKey(runtime), Math.Max(1, pool.Get(PrimaryKey(runtime))));
            GetUp(runtime, entity);
            return new DeathSaveOutcome(roll, true, 0, 0, false, false, true, false);
        }

        if (rules.CriticalFailure is { } fumble && roll <= fumble)
        {
            return RecordDeathSave(runtime, actorId, entity, success: false, count: 2) with { Roll = roll };
        }

        return RecordDeathSave(runtime, actorId, entity, roll >= rules.SuccessOn, 1) with { Roll = roll };
    }

    /// <summary>Records a death save the player rolled themselves (or a hit while down).</summary>
    public static DeathSaveOutcome RecordDeathSave(CampaignRuntime runtime, string actorId, Entity entity, bool success, int count)
    {
        if (runtime.System?.Vitals is not { DeathSaves: { } rules, Down: { } down } || !entity.Has<ResourcePoolComponent>() || IsDead(runtime, entity))
        {
            return DeathSaveOutcome.None;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var successes = pool.Get(rules.SuccessesKey);
        var failures = pool.Get(rules.FailuresKey);
        if (success)
        {
            successes += Math.Max(1, count);
        }
        else
        {
            failures += Math.Max(1, count);
        }

        if (failures >= rules.FailuresToDie)
        {
            Die(runtime, entity, actorId);
            return new DeathSaveOutcome(null, success, successes, failures, false, true, false, false);
        }

        if (successes >= rules.SuccessesToStabilize)
        {
            Stabilize(runtime, actorId, entity);
            return new DeathSaveOutcome(null, success, successes, failures, true, false, false, false);
        }

        pool.Set(rules.SuccessesKey, successes);
        pool.Set(rules.FailuresKey, failures);
        return new DeathSaveOutcome(null, success, successes, failures, false, false, false, false);
    }

    /// <summary>A creature at zero that is no longer dying: still unconscious, no more saves.</summary>
    public static bool Stabilize(CampaignRuntime runtime, string actorId, Entity entity)
    {
        if (runtime.System?.Vitals?.Down is not { } down || IsDead(runtime, entity) || !IsDown(runtime, entity))
        {
            return false;
        }

        ResetDeathSaves(runtime, entity);
        if (!CodexWorld.HasStatus(entity, down.StableCondition))
        {
            runtime.World.AddStatus(entity, down.StableCondition, "stabilized", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actorId);
        }

        return true;
    }

    // ---- Exhaustion ----

    /// <summary>Moves the exhaustion level by <paramref name="delta"/> within 0 and the maximum; reaching the maximum kills.</summary>
    public static (int Level, bool Died)? AdjustExhaustion(CampaignRuntime runtime, string actorId, Entity entity, int delta)
    {
        if (runtime.System?.Vitals?.Exhaustion is not { } rules || !entity.Has<ResourcePoolComponent>())
        {
            return null;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var level = Math.Clamp(pool.Get(rules.Key) + delta, 0, rules.MaxLevel);
        pool.Set(rules.Key, level);
        pool.Set(rules.Key + "_Max", rules.MaxLevel);
        if (level >= rules.MaxLevel && runtime.System?.Vitals?.Down != null && !IsDead(runtime, entity))
        {
            Die(runtime, entity, actorId);
            return (level, true);
        }

        return (level, false);
    }

    // ---- Hit dice ----

    public sealed record HitDiceOutcome(int Spent, int Healed, IReadOnlyList<int> Rolls, int Modifier);

    /// <summary>Spends up to <paramref name="count"/> hit dice of <paramref name="dieSides"/> sides: each die plus the
    /// attribute modifier (never below zero per die) heals the primary resource.</summary>
    public static HitDiceOutcome? SpendHitDice(CampaignRuntime runtime, string actorId, Entity entity, int count, int dieSides)
    {
        if (runtime.System?.Vitals?.HitDice is not { } rules || count <= 0 || dieSides < 2 || !entity.Has<ResourcePoolComponent>())
        {
            return null;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var available = pool.Get(rules.Key);
        var spend = Math.Min(count, available);
        if (spend <= 0)
        {
            return new HitDiceOutcome(0, 0, Array.Empty<int>(), 0);
        }

        var modifier = AttributeModifier(runtime, actorId, entity, rules.Attribute);
        var rolls = new List<int>();
        var healed = 0;
        for (var i = 0; i < spend; i++)
        {
            var roll = runtime.DiceRoller.Roll($"1d{dieSides}").Total;
            rolls.Add(roll);
            healed += Math.Max(0, roll + modifier);
        }

        pool.Set(rules.Key, available - spend);
        var outcome = ApplyHealing(runtime, actorId, entity, PrimaryKey(runtime), healed);
        return new HitDiceOutcome(spend, outcome.Healed, rolls, modifier);
    }

    // ---- Rests ----

    /// <summary>Applies a named rest: the slot pools the caller passes, then whatever the rest rule says
    /// (resources refilled or cleared, hit dice regained, exhaustion eased, conditions lifted). Returns
    /// what happened, in words, for the log.</summary>
    public static List<string> Rest(CampaignRuntime runtime, string actorId, Entity entity, string restName, IEnumerable<string> slotPools)
    {
        var happened = new List<string>();
        runtime.World.RestoreResources(entity, slotPools);
        if (slotPools.Any())
        {
            happened.Add("slots refilled");
        }

        if (runtime.System?.Vitals?.FindRest(restName) is not { } rule || !entity.Has<ResourcePoolComponent>())
        {
            return happened;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        foreach (var key in rule.Restores)
        {
            if (pool.Pools.TryGetValue(MaxKeyFor(runtime, key), out var max))
            {
                var before = pool.Get(key);
                pool.Set(key, max);
                if (key == PrimaryKey(runtime) && before <= 0 && max > 0)
                {
                    GetUp(runtime, entity);
                }

                happened.Add($"{Label(runtime, key)} restored");
            }
        }

        foreach (var key in rule.Clears)
        {
            if (pool.Get(key) != 0)
            {
                pool.Set(key, 0);
                happened.Add($"{Label(runtime, key)} cleared");
            }
        }

        if (runtime.System?.Vitals?.HitDice is { } hitDice && pool.Pools.TryGetValue(hitDice.MaxKey, out var diceMax) && diceMax > 0)
        {
            var current = pool.Get(hitDice.Key);
            var regained = rule.HitDiceRegain.ToLowerInvariant() switch
            {
                "all" => diceMax - current,
                "half" => Math.Max(1, diceMax / 2),
                _ => 0
            };
            if (regained > 0 && current < diceMax)
            {
                pool.Set(hitDice.Key, Math.Min(diceMax, current + regained));
                happened.Add($"{Math.Min(regained, diceMax - current)} hit dice regained");
            }
        }

        if (rule.ExhaustionDelta != 0 && runtime.System?.Vitals?.Exhaustion is { } exhaustion && pool.Get(exhaustion.Key) > 0)
        {
            var adjusted = AdjustExhaustion(runtime, actorId, entity, rule.ExhaustionDelta);
            if (adjusted is { } a)
            {
                happened.Add(a.Level == 0 ? "exhaustion gone" : $"exhaustion now {a.Level}");
            }
        }

        foreach (var condition in rule.ClearsConditions)
        {
            if (runtime.World.RemoveStatus(entity, condition) > 0)
            {
                happened.Add($"{condition} lifted");
            }
        }

        return happened;
    }

    // ---- State queries ----

    public static bool IsDead(CampaignRuntime runtime, Entity entity) =>
        runtime.System?.Vitals?.Down is { } down && CodexWorld.HasStatus(entity, down.DeadCondition);

    /// <summary>At zero on the primary resource and not dead.</summary>
    public static bool IsDown(CampaignRuntime runtime, Entity entity) =>
        entity.Has<ResourcePoolComponent>() && entity.Get<ResourcePoolComponent>().Get(PrimaryKey(runtime)) <= 0 && !IsDead(runtime, entity);

    /// <summary>The attribute's modifier for a creature: from its live attributes, else its sheet, else the default.</summary>
    public static int AttributeModifier(CampaignRuntime runtime, string actorId, Entity entity, string attributeKey)
    {
        var definition = runtime.System?.Attributes.FirstOrDefault(a => a.Key == attributeKey);
        if (definition == null || definition.Modifier != "half-minus-five")
        {
            return 0;
        }

        int? score = entity.Has<AttributesComponent>() && entity.Get<AttributesComponent>().Values.TryGetValue(attributeKey, out var live) ? live : null;
        score ??= runtime.GetActorDocument(actorId) is { } doc ? YamlValues.GetInt(doc.Sheet, attributeKey) : null;
        return (int)Math.Floor(((score ?? definition.Default) - 10) / 2.0);
    }

    private static void Die(CampaignRuntime runtime, Entity entity, string actorId)
    {
        if (runtime.System?.Vitals?.Down is not { } down)
        {
            return;
        }

        runtime.World.RemoveStatus(entity, down.Condition);
        runtime.World.RemoveStatus(entity, down.StableCondition);
        ResetDeathSaves(runtime, entity);
        if (!CodexWorld.HasStatus(entity, down.DeadCondition))
        {
            runtime.World.AddStatus(entity, down.DeadCondition, "died", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actorId);
        }
    }

    /// <summary>Back above zero: no longer down or stable, death saves reset. Returns whether anything changed.</summary>
    private static bool GetUp(CampaignRuntime runtime, Entity entity)
    {
        if (runtime.System?.Vitals?.Down is not { } down)
        {
            return false;
        }

        var removed = runtime.World.RemoveStatus(entity, down.Condition) + runtime.World.RemoveStatus(entity, down.StableCondition);
        ResetDeathSaves(runtime, entity);
        return removed > 0;
    }

    private static void ResetDeathSaves(CampaignRuntime runtime, Entity entity)
    {
        if (runtime.System?.Vitals?.DeathSaves is not { } rules || !entity.Has<ResourcePoolComponent>())
        {
            return;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        pool.Pools.Remove(rules.SuccessesKey);
        pool.Pools.Remove(rules.FailuresKey);
    }

    private static string Label(CampaignRuntime runtime, string key) =>
        runtime.System?.Resources.FirstOrDefault(r => r.Key == key)?.Label ?? key;
}
