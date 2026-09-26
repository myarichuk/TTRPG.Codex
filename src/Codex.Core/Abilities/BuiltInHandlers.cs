using Codex.Core.Components;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Core.Abilities;

/// <summary>Built-in TRCE handlers covering the table loop: pools, damage, healing, statuses.</summary>
public static class BuiltInHandlers
{
    public static IEnumerable<IRequirementHandler> Requirements(IDiceRoller? diceRoller = null)
    {
        yield return new ResourceRequirementHandler();
        yield return new AttackRequirementHandler(diceRoller);
    }

    public static IEnumerable<ICostHandler> Costs()
    {
        yield return new ResourceCostHandler();
    }

    public static IEnumerable<IEffectHandler> Effects(IDiceRoller? diceRoller = null)
    {
        yield return new DamageEffectHandler(diceRoller);
        yield return new HealEffectHandler(diceRoller);
        yield return new AddStatusEffectHandler(diceRoller);
    }

    // Pack YAML uses lowercase keys (pool, amount, dice); in-code definitions may use
    // capitalized ones. Lookups are ordinal-case-insensitive so both spellings work.
    public static bool TryGetParam(IReadOnlyDictionary<string, object>? p, string key, out object? value)
        => TryParam(p, key, out value);

    private static bool TryParam(IReadOnlyDictionary<string, object>? p, string key, out object? value)
    {
        value = null;
        if (p == null)
        {
            return false;
        }

        if (p.TryGetValue(key, out value) && value != null)
        {
            return true;
        }

        foreach (var kvp in p)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase) && kvp.Value != null)
            {
                value = kvp.Value;
                return true;
            }
        }

        return false;
    }

    public static string GetString(IReadOnlyDictionary<string, object>? p, string key, string fallback = "")
        => TryParam(p, key, out var v) ? v!.ToString() ?? fallback : fallback;

    public static int GetInt(IReadOnlyDictionary<string, object>? p, string key, int fallback = 0)
    {
        if (!TryParam(p, key, out var v))
        {
            return fallback;
        }

        return v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var n) => n,
            _ => fallback
        };
    }

    public static int ResolveAmount(IReadOnlyDictionary<string, object>? p, IDiceRoller? dice)
    {
        if (TryParam(p, "Dice", out var diceObj) && !string.IsNullOrWhiteSpace(diceObj?.ToString()))
        {
            return (dice ?? StandardDiceRoller.Instance).Roll(diceObj.ToString()!).Total;
        }

        return GetInt(p, "Amount");
    }

    public static int RollD20(IDiceRoller? dice)
        => (dice ?? StandardDiceRoller.Instance).Roll("1d20").Total;

    /// <summary>Reads a named modifier ("save:dex", "attack", "ac") off an entity's
    /// <see cref="StatModifierComponent"/>, or null when the entity carries none.</summary>
    public static int? GetStatModifier(DefaultEcs.Entity entity, string stat)
    {
        if (!entity.Has<StatModifierComponent>())
        {
            return null;
        }

        foreach (var modifier in entity.Get<StatModifierComponent>().Modifiers)
        {
            if (string.Equals(modifier.Stat, stat, StringComparison.OrdinalIgnoreCase))
            {
                return modifier.Value;
            }
        }

        return null;
    }

    private static int? GetOptionalInt(IReadOnlyDictionary<string, object>? p, string key)
    {
        if (!TryParam(p, key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var n) => n,
            _ => null,
        };
    }

    private sealed class ResourceRequirementHandler : IRequirementHandler
    {
        public string Type => "ResourceRequirement";

        public bool CanSatisfy(TypedComponent requirement, AbilityExecutionContext context, out string? reason)
        {
            var pool = GetString(requirement.Params, "Pool", "HP");
            var min = GetInt(requirement.Params, "MinAmount");
            var has = context.Caster.Has<ResourcePoolComponent>()
                ? context.Caster.Get<ResourcePoolComponent>().Get(pool)
                : 0;
            if (has < min)
            {
                reason = $"Requires {min} {pool} (has {has}).";
                return false;
            }

            reason = null;
            return true;
        }
    }

    /// <summary>Attack roll gate (6.3): d20 + bonus vs AC. A miss fails the ability -
    /// costs are already paid by then, matching 5e (a missed spell still spends the
    /// slot). Bonus resolves params → context → caster "attack" modifier → +0; AC
    /// resolves params → context → target "ac" modifier, and fails closed when unknown.
    /// Natural 20 hits, natural 1 misses; no critical damage (a later slice).</summary>
    private sealed class AttackRequirementHandler(IDiceRoller? diceRoller) : IRequirementHandler
    {
        public string Type => "Attack";

        public bool CanSatisfy(TypedComponent requirement, AbilityExecutionContext context, out string? reason)
        {
            var targets = context.AllTargets();
            if (targets.Count == 0)
            {
                reason = "Attack needs a target.";
                return false;
            }

            var bonus = GetOptionalInt(requirement.Params, "AttackBonus")
                ?? GetOptionalInt(context.Params, "AttackBonus")
                ?? GetStatModifier(context.Caster, "attack")
                ?? 0;

            var target = targets[0];
            var armorClass = GetOptionalInt(requirement.Params, "TargetAc")
                ?? GetOptionalInt(context.Params, "TargetAc")
                ?? GetStatModifier(target, "ac");
            if (armorClass == null)
            {
                reason = "Attack needs a target AC (params.targetAc, context targetAc, or target 'ac' modifier).";
                return false;
            }

            var roll = RollD20(diceRoller);
            if (roll >= 20 || (roll > 1 && roll + bonus >= armorClass))
            {
                reason = null;
                return true;
            }

            reason = $"Attack missed (d20 {roll} + {bonus} vs AC {armorClass}).";
            return false;
        }
    }

    private sealed class ResourceCostHandler : ICostHandler
    {
        public string Type => "ResourceCost";

        public bool CanPay(TypedComponent cost, AbilityExecutionContext context, out string? reason)
        {
            var pool = GetString(cost.Params, "Pool", "HP");
            var amount = GetInt(cost.Params, "Amount");
            var has = context.Caster.Has<ResourcePoolComponent>()
                ? context.Caster.Get<ResourcePoolComponent>().Get(pool)
                : 0;
            if (has < amount)
            {
                reason = $"Cannot pay {amount} {pool} (has {has}).";
                return false;
            }

            reason = null;
            return true;
        }

        public void Pay(TypedComponent cost, AbilityExecutionContext context)
        {
            var pool = GetString(cost.Params, "Pool", "HP");
            var amount = GetInt(cost.Params, "Amount");
            var pools = context.Caster.Get<ResourcePoolComponent>();
            pools.Modify(pool, -amount);
        }
    }

    /// <summary>Resolves an optional saving throw (6.3). Returns false when the effect
    /// carries no save; otherwise rolls d20 + the target's "save:{ability}" modifier
    /// (+0 when unknown) against the DC and reports whether the target saved. The DC
    /// resolves params → context and fails closed when unknown - DCs are caster-derived
    /// (8 + proficiency + modifier) and never live in the imported data.</summary>
    private static bool TryResolveSave(
        TypedComponent effect, AbilityExecutionContext context, DefaultEcs.Entity target,
        IDiceRoller? diceRoller, out string successMode)
    {
        successMode = "negates";
        var ability = GetString(effect.Params, "SaveAbility");
        if (string.IsNullOrWhiteSpace(ability))
        {
            return false;
        }

        var dc = GetOptionalInt(effect.Params, "SaveDc") ?? GetOptionalInt(context.Params, "SaveDc");
        if (dc == null)
        {
            throw new InvalidOperationException(
                "Saving throw needs a DC (params.saveDc or context saveDc).");
        }

        successMode = GetString(effect.Params, "SaveSuccess", "negates");
        var modifier = GetOptionalInt(context.Params, "SaveModifier")
            ?? GetStatModifier(target, $"save:{ability}")
            ?? 0;
        return RollD20(diceRoller) + modifier >= dc;
    }

    private sealed class DamageEffectHandler(IDiceRoller? diceRoller) : IEffectHandler
    {
        public string Type => "Damage";

        public void Apply(TypedComponent effect, AbilityExecutionContext context, string sourceId)
        {
            var targets = context.AllTargets();
            if (targets.Count == 0)
            {
                throw new InvalidOperationException("Damage effect needs a target.");
            }

            var pool = GetString(effect.Params, "Pool", "HP");
            // One damage roll per effect, individual saves per target (6.3) - a fireball
            // rolls 8d6 once; each creature in the burst saves (or not) against it.
            var amount = ResolveAmount(effect.Params, diceRoller);
            foreach (var target in targets)
            {
                var saved = TryResolveSave(effect, context, target, diceRoller, out var successMode);
                if (saved && !string.Equals(successMode, "half", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                context.World.ApplyDamage(target, pool, saved ? amount / 2 : amount);
            }
        }
    }

    private sealed class HealEffectHandler(IDiceRoller? diceRoller) : IEffectHandler
    {
        public string Type => "Heal";

        public void Apply(TypedComponent effect, AbilityExecutionContext context, string sourceId)
        {
            var targets = context.AllTargets();
            if (targets.Count == 0)
            {
                targets = new[] { context.Caster };
            }

            var pool = GetString(effect.Params, "Pool", "HP");
            var amount = ResolveAmount(effect.Params, diceRoller);
            foreach (var target in targets)
            {
                context.World.ApplyHealing(target, pool, amount);
            }
        }
    }

    private sealed class AddStatusEffectHandler(IDiceRoller? diceRoller) : IEffectHandler
    {
        public string Type => "AddStatus";

        public void Apply(TypedComponent effect, AbilityExecutionContext context, string sourceId)
        {
            var targets = context.AllTargets();
            if (targets.Count == 0)
            {
                throw new InvalidOperationException("AddStatus effect needs a target.");
            }

            var effectId = GetString(effect.Params, "EffectId");
            if (string.IsNullOrWhiteSpace(effectId))
            {
                throw new InvalidOperationException("AddStatus effect needs EffectId.");
            }

            var rounds = Math.Max(1, GetInt(effect.Params, "Rounds", 1));
            var expiry = GetString(effect.Params, "Expiry", "EndOfTurn") == "StartOfTurn"
                ? EffectExpiry.StartOfTurn
                : EffectExpiry.EndOfTurn;
            var source = GetString(effect.Params, "SourceId", sourceId);
            foreach (var target in targets)
            {
                // A save negates the status outright; "half" is meaningless for a condition.
                if (TryResolveSave(effect, context, target, diceRoller, out _))
                {
                    continue;
                }

                context.World.AddStatus(target, effectId, source, rounds, expiry, target);
            }
        }
    }
}
