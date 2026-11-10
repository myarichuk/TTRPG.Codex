using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A creature as the vitals handlers read it: its pools (current values, maxima, temporary hit points, the death-save
/// tallies, exhaustion, hit dice), the conditions it carries, whether it is a player character, and its sheet (whose lists name
/// its immunities, resistances and vulnerabilities).</summary>
/// <param name="HasPools">False for a creature with no pools at all, which only <see cref="VitalsHandlers.Revive"/> reads: it
/// then changes conditions and leaves the pools alone.</param>
public sealed record VitalsView(IReadOnlyDictionary<string, int> Pools, IReadOnlyCollection<string> Conditions, bool IsPlayer,
    IReadOnlyDictionary<string, object>? Sheet, bool HasPools = true);

/// <summary>What a vitals handler returns: the intents for the table to apply, in order, and what they came to, for the log.</summary>
public sealed record VitalsResult<T>(IReadOnlyList<SheetIntent> Intents, T Report);

/// <param name="ConcentrationDc">The save a concentrating creature now owes to keep concentrating; null when it was not.</param>
/// <param name="ConcentrationLost">It was concentrating and went down or died, which ends it.</param>
public sealed record DamageReport(int Amount, int Dealt, int Absorbed, string? Modifier, bool WentDown, bool Died, int FailuresAdded,
    int? ConcentrationDc = null, bool ConcentrationLost = false)
{
    public static readonly DamageReport Nothing = new(0, 0, 0, null, false, false, 0);
}

public sealed record DeathSaveReport(int? Roll, bool? Success, int Successes, int Failures, bool Stabilized, bool Died, bool Revived, bool NotApplicable)
{
    public static readonly DeathSaveReport None = new(null, null, 0, 0, false, false, false, true);
}

/// <param name="WasDead">The creature is dead, so nothing was healed: the DM revives it first.</param>
/// <param name="Revived">It was down and is back on its feet.</param>
public sealed record HealReport(int Healed, bool WasDead, bool Revived)
{
    public static readonly HealReport Nothing = new(0, false, false);
}

/// <param name="Rolls">Each hit die rolled, in order; the modifier is added to each, and a die never heals below zero.</param>
public sealed record HitDiceReport(int Spent, int Healed, IReadOnlyList<int> Rolls, int Modifier);

/// <summary>One thing a rest did, in order: <see cref="Kind"/> is <c>slots</c>, <c>restored</c> or <c>cleared</c> (with
/// <see cref="Key"/>, a pool), <c>hitDice</c> (with <see cref="Amount"/> regained), <c>exhaustionGone</c>, <c>exhaustionNow</c>
/// (with the level) or <c>lifted</c> (with <see cref="Key"/>, a condition).</summary>
public sealed record RestStep(string Kind, string? Key = null, int Amount = 0);

/// <summary>
/// The built-in vitals handlers (overhaul step 8): damage, healing, temporary hit points, death saves, stabilizing, reviving,
/// concentration, exhaustion, hit dice and rests, as
/// <c>system.yaml</c>'s <c>vitals:</c> section describes them (srd5e's). Each reads a <see cref="VitalsView"/> and returns
/// intents; nothing here changes a creature. The table applies the intents to the creature it read
/// (Codex.Persistence.Runtime.TableRules), in order.
/// <para>The intents are the ones a sheet knows (<c>set { track, to }</c> on a pool, <c>condition { add, source } | { remove }</c>,
/// <c>roll</c> left for the table) and three the table does: <c>counter { key, to } | { key, clear: true }</c> for a tally the
/// table keeps beside the sheet (the death saves, both ways), <c>kill</c> (the dead condition; down, stable and the tallies
/// gone; and what the creature held and the effects counted on its turns end with it) and <c>endConcentration</c> (the
/// condition goes, and every effect it held up, on whichever creature).</para>
/// <para>Each handler works on a scratch copy of the view, so a later step reads what an earlier one did, exactly as the rule
/// reads: a hit while down is a failed save, three failures kill, a kill ends concentration.</para>
/// </summary>
public static class VitalsHandlers
{
    public const string CounterKind = "counter";
    public const string KillKind = "kill";
    public const string EndConcentrationKind = "endConcentration";

    /// <summary>The roll a concentrating creature owes when it is hurt, left for the table: <c>roll { definition, dc }</c>.</summary>
    public const string ConcentrationSave = "concentration";

    // ---- Damage ----

    /// <summary>
    /// Damage to <paramref name="poolName"/>: immunity cancels it, resistance halves it (rounded down), vulnerability doubles it;
    /// the temporary pool absorbs first; the primary pool stops at zero. Reaching zero follows <see cref="DownRules"/>: an
    /// overflow of the maximum or a non-player creature dies, a player character falls unconscious and starts death saves, and
    /// damage while already at zero is a failed save (two for a critical hit). A concentrating creature that is hurt owes a save
    /// (half the damage, within the rule's bounds), unless it went down or died, which ends its concentration.
    /// </summary>
    public static VitalsResult<DamageReport> Damage(SystemDefinition? system, VitalsView view, string poolName, int amount, string? damageType, bool isCritical)
    {
        var s = new Scratch(system, view);
        var concentrating = s.IsConcentrating;
        var report = DamageCore(s, poolName, amount, damageType, isCritical);
        if (!concentrating || report.Dealt <= 0 || poolName != s.Primary) return s.Done(report);

        if (report.Died || report.WentDown || s.IsDown)
        {
            s.EndConcentration();
            return s.Done(report with { ConcentrationLost = true });
        }

        var dc = s.Vitals!.Concentration!.DcFor(report.Dealt);
        s.Leave(new SheetIntent("roll", null, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["definition"] = ConcentrationSave, ["dc"] = (double)dc, ["attribute"] = s.Vitals.Concentration.SaveAttribute
        }));
        return s.Done(report with { ConcentrationDc = dc });
    }

    private static DamageReport DamageCore(Scratch s, string poolName, int amount, string? damageType, bool isCritical)
    {
        if (amount <= 0) return DamageReport.Nothing;

        var (dealt, modifier) = Modify(s, amount, damageType);
        if (dealt <= 0) return new DamageReport(amount, 0, 0, modifier, false, false, 0);

        var remaining = dealt;
        var absorbed = 0;
        var isPrimary = poolName == s.Primary;
        if (isPrimary)
        {
            var temporary = s.Get(s.Temporary);
            if (temporary > 0)
            {
                absorbed = Math.Min(temporary, remaining);
                s.Set(s.Temporary, temporary - absorbed);
                remaining -= absorbed;
            }
        }

        var current = s.Get(poolName);
        var next = current - remaining;
        int? max = s.Pools.TryGetValue(s.MaxKeyOf(poolName), out var m) ? m : null;
        if (max.HasValue) next = Math.Min(next, max.Value);
        s.Set(poolName, Math.Max(next, 0));

        if (!isPrimary || next > 0 || s.Vitals?.Down is not { } down) return new DamageReport(amount, dealt, absorbed, modifier, false, false, 0);
        if (s.IsDead) return new DamageReport(amount, dealt, absorbed, modifier, false, false, 0);

        var overflow = -next;
        if ((down.InstantDeathAtMax && max is > 0 && overflow >= max.Value) || (!s.IsPlayer && down.NonPlayersDieAtZero))
        {
            s.Kill();
            return new DamageReport(amount, dealt, absorbed, modifier, true, true, 0);
        }

        if (current <= 0)
        {
            // Already down: a hit is a failed death save, two when it was a critical.
            if (s.Vitals.DeathSaves == null) return new DamageReport(amount, dealt, absorbed, modifier, false, false, 0);
            s.RemoveCondition(down.StableCondition);
            if (!s.HasCondition(down.Condition)) s.AddCondition(down.Condition, "zero hit points");
            var failures = isCritical ? 2 : 1;
            var saved = RecordCore(s, success: false, failures);
            return new DamageReport(amount, dealt, absorbed, modifier, false, saved.Died, failures);
        }

        s.RemoveCondition(down.StableCondition);
        s.AddCondition(down.Condition, "zero hit points");
        s.ResetDeathSaves();
        return new DamageReport(amount, dealt, absorbed, modifier, true, false, 0);
    }

    /// <summary>The damage after the creature's immunities, resistances and vulnerabilities to <paramref name="damageType"/>.</summary>
    private static (int Dealt, string? Modifier) Modify(Scratch s, int amount, string? damageType)
    {
        if (string.IsNullOrWhiteSpace(damageType) || s.Sheet == null || s.Vitals?.Damage is not { } rules) return (amount, null);
        if (Lists(s.Sheet, rules.ImmunitiesField, damageType)) return (0, "immune");

        var resisted = Lists(s.Sheet, rules.ResistancesField, damageType);
        var vulnerable = Lists(s.Sheet, rules.VulnerabilitiesField, damageType);
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
        if (!sheet.TryGetValue(field, out var raw) || raw == null) return false;

        IEnumerable<string> entries = raw switch
        {
            string text => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
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

    /// <summary>An entry with a condition attached ("... from nonmagical attacks", "... that aren't silvered"): the table can't
    /// tell whether this hit meets it, so the GM applies it by hand rather than the table applying it to every hit of that type.</summary>
    private static bool IsConditional(string entry) => ConditionalMarkers.Any(marker => entry.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ConditionalMarkers = { " from ", "nonmagical", "that aren't", "that are not", "except", " while ", " when " };

    // ---- Death saves ----

    /// <summary>A death save rolled by the table: <paramref name="roll"/> rolls the rule's die (the host's dice). A natural
    /// critical success brings the creature back with one hit point, a critical failure counts twice. Nothing when the creature
    /// is not down or is dead.</summary>
    public static VitalsResult<DeathSaveReport> RollDeathSave(SystemDefinition? system, VitalsView view, Func<string, int> roll)
    {
        var s = new Scratch(system, view);
        if (s.Vitals?.DeathSaves is not { } rules || !s.IsDown || s.IsDead) return s.Done(DeathSaveReport.None);

        var rolled = roll(rules.Die);
        if (rules.CriticalSuccess is { } crit && rolled >= crit)
        {
            s.ResetDeathSaves();
            s.Set(s.Primary, Math.Max(1, s.Get(s.Primary)));
            s.GetUp();
            return s.Done(new DeathSaveReport(rolled, true, 0, 0, false, false, true, false));
        }

        if (rules.CriticalFailure is { } fumble && rolled <= fumble) return s.Done(RecordCore(s, success: false, count: 2) with { Roll = rolled });
        return s.Done(RecordCore(s, rolled >= rules.SuccessOn, 1) with { Roll = rolled });
    }

    /// <summary>A death save the player rolled themselves (or a hit while down): <paramref name="count"/> successes or failures.</summary>
    public static VitalsResult<DeathSaveReport> RecordDeathSave(SystemDefinition? system, VitalsView view, bool success, int count)
    {
        var s = new Scratch(system, view);
        return s.Done(RecordCore(s, success, count));
    }

    private static DeathSaveReport RecordCore(Scratch s, bool success, int count)
    {
        if (s.Vitals is not { DeathSaves: { } rules, Down: not null } || s.IsDead) return DeathSaveReport.None;

        var successes = s.Get(rules.SuccessesKey);
        var failures = s.Get(rules.FailuresKey);
        if (success) successes += Math.Max(1, count);
        else failures += Math.Max(1, count);

        if (failures >= rules.FailuresToDie)
        {
            s.Kill();
            return new DeathSaveReport(null, success, successes, failures, false, true, false, false);
        }

        if (successes >= rules.SuccessesToStabilize)
        {
            StabilizeCore(s);
            return new DeathSaveReport(null, success, successes, failures, true, false, false, false);
        }

        s.Counter(rules.SuccessesKey, successes);
        s.Counter(rules.FailuresKey, failures);
        return new DeathSaveReport(null, success, successes, failures, false, false, false, false);
    }

    /// <summary>A creature at zero that is no longer dying: still unconscious, no more saves. False when it is not down, or dead.</summary>
    public static VitalsResult<bool> Stabilize(SystemDefinition? system, VitalsView view)
    {
        var s = new Scratch(system, view);
        return s.Done(StabilizeCore(s));
    }

    private static bool StabilizeCore(Scratch s)
    {
        if (s.Vitals?.Down is not { } down || s.IsDead || !s.IsDown) return false;
        s.ResetDeathSaves();
        if (!s.HasCondition(down.StableCondition)) s.AddCondition(down.StableCondition, "stabilized");
        return true;
    }

    // ---- Rests ----

    /// <summary>A named rest: the slot pools refilled, then what the rest rule says (pools refilled or cleared, hit dice regained,
    /// exhaustion eased, conditions lifted). A creature brought up from zero gets up.</summary>
    public static VitalsResult<IReadOnlyList<RestStep>> Rest(SystemDefinition? system, VitalsView view, string restName, IReadOnlyCollection<string> slotPools)
    {
        var s = new Scratch(system, view);
        var steps = new List<RestStep>();
        foreach (var name in slotPools)
        {
            if (s.Pools.TryGetValue(s.MaxKeyOf(name), out var slotMax)) s.Set(name, slotMax);
        }

        if (slotPools.Count > 0) steps.Add(new RestStep("slots"));
        if (s.Vitals?.FindRest(restName) is not { } rule) return s.Done<IReadOnlyList<RestStep>>(steps);

        foreach (var key in rule.Restores)
        {
            if (!s.Pools.TryGetValue(s.MaxKeyOf(key), out var max)) continue;
            var before = s.Get(key);
            s.Set(key, max);
            if (key == s.Primary && before <= 0 && max > 0) s.GetUp();
            steps.Add(new RestStep("restored", key));
        }

        foreach (var key in rule.Clears)
        {
            if (s.Get(key) == 0) continue;
            s.Set(key, 0);
            steps.Add(new RestStep("cleared", key));
        }

        if (s.Vitals.HitDice is { } hitDice && s.Pools.TryGetValue(hitDice.MaxKey, out var diceMax) && diceMax > 0)
        {
            var current = s.Get(hitDice.Key);
            var regained = rule.HitDiceRegain.ToLowerInvariant() switch
            {
                "all" => diceMax - current,
                "half" => Math.Max(1, diceMax / 2),
                _ => 0
            };
            if (regained > 0 && current < diceMax)
            {
                s.Set(hitDice.Key, Math.Min(diceMax, current + regained));
                steps.Add(new RestStep("hitDice", Amount: Math.Min(regained, diceMax - current)));
            }
        }

        if (rule.ExhaustionDelta != 0 && s.Vitals.Exhaustion is { } exhaustion && s.Get(exhaustion.Key) > 0)
        {
            var level = AdjustExhaustionCore(s, exhaustion, rule.ExhaustionDelta);
            steps.Add(level == 0 ? new RestStep("exhaustionGone") : new RestStep("exhaustionNow", Amount: level));
        }

        foreach (var condition in rule.ClearsConditions)
        {
            if (s.RemoveCondition(condition)) steps.Add(new RestStep("lifted", condition));
        }

        return s.Done<IReadOnlyList<RestStep>>(steps);
    }

    /// <summary>The exhaustion level moved by <paramref name="delta"/> within 0 and the maximum; reaching the maximum kills.
    /// Null when the system has no exhaustion.</summary>
    public static VitalsResult<(int Level, bool Died)?> AdjustExhaustion(SystemDefinition? system, VitalsView view, int delta)
    {
        var s = new Scratch(system, view);
        if (s.Vitals?.Exhaustion is not { } rules) return s.Done<(int, bool)?>(null);
        var wasDead = s.IsDead;
        var level = AdjustExhaustionCore(s, rules, delta);
        return s.Done<(int, bool)?>((level, !wasDead && s.IsDead));
    }

    /// <summary>The exhaustion level moved within 0 and the maximum; reaching the maximum kills.</summary>
    private static int AdjustExhaustionCore(Scratch s, ExhaustionRules rules, int delta)
    {
        var level = Math.Clamp(s.Get(rules.Key) + delta, 0, rules.MaxLevel);
        s.Set(rules.Key, level);
        s.Set(s.MaxKeyOf(rules.Key), rules.MaxLevel);
        if (level >= rules.MaxLevel && s.Vitals?.Down != null && !s.IsDead) s.Kill();
        return level;
    }

    // ---- Healing, temporary hit points, hit dice, reviving ----

    /// <summary>
    /// Heals <paramref name="poolName"/> by <paramref name="amount"/>, up to its maximum when it has one and never below zero. A
    /// dead creature is not healed: the DM revives it first. A creature brought above zero on the primary pool is back on its
    /// feet: no longer down or stable, its death saves reset.
    /// </summary>
    public static VitalsResult<HealReport> Heal(SystemDefinition? system, VitalsView view, string poolName, int amount)
    {
        var s = new Scratch(system, view);
        return s.Done(HealCore(s, poolName, amount));
    }

    private static HealReport HealCore(Scratch s, string poolName, int amount)
    {
        if (amount <= 0) return HealReport.Nothing;
        if (s.IsDead) return new HealReport(0, true, false);

        var before = s.Get(poolName);
        var next = before + amount;
        if (s.Pools.TryGetValue(s.MaxKeyOf(poolName), out var max)) next = Math.Min(next, max);
        next = Math.Max(next, 0);
        s.Set(poolName, next);

        var revived = poolName == s.Primary && before <= 0 && next > 0 && s.GetUp();
        return new HealReport(next - before, false, revived);
    }

    /// <summary>Temporary hit points do not stack: the creature keeps the higher of what it has and
    /// <paramref name="amount"/>. Whether it took the new amount.</summary>
    public static VitalsResult<bool> GrantTemporary(SystemDefinition? system, VitalsView view, int amount)
    {
        var s = new Scratch(system, view);
        if (amount <= 0 || s.Get(s.Temporary) >= amount) return s.Done(false);
        s.Set(s.Temporary, amount);
        return s.Done(true);
    }

    /// <summary>
    /// Spends up to <paramref name="count"/> hit dice of <paramref name="dieSides"/> sides, as many as the creature has: each
    /// die (<paramref name="roll"/>, given the die, <c>1d8</c>) plus <paramref name="modifier"/>, never below zero a die, heals
    /// the primary pool as <see cref="Heal"/> does. Null when the system has no hit dice or the request is not one.
    /// </summary>
    public static VitalsResult<HitDiceReport?> SpendHitDice(SystemDefinition? system, VitalsView view, int count, int dieSides, int modifier, Func<string, int> roll)
    {
        var s = new Scratch(system, view);
        if (s.Vitals?.HitDice is not { } rules || count <= 0 || dieSides < 2) return s.Done<HitDiceReport?>(null);

        var available = s.Get(rules.Key);
        var spend = Math.Min(count, available);
        if (spend <= 0) return s.Done<HitDiceReport?>(new HitDiceReport(0, 0, Array.Empty<int>(), 0));

        var rolls = new List<int>();
        var healed = 0;
        for (var i = 0; i < spend; i++)
        {
            var rolled = roll($"1d{dieSides}");
            rolls.Add(rolled);
            healed += Math.Max(0, rolled + modifier);
        }

        s.Set(rules.Key, available - spend);
        var outcome = HealCore(s, s.Primary, healed);
        return s.Done<HitDiceReport?>(new HitDiceReport(spend, outcome.Healed, rolls, modifier));
    }

    /// <summary>Back from the dead (a revival spell, or a death marked by mistake): no longer dead, down or stable, the death
    /// saves reset, and on at least one hit point, so the next hit does not finish a creature still carrying two failed saves.
    /// False when it was not dead, or the system has no death.</summary>
    public static VitalsResult<bool> Revive(SystemDefinition? system, VitalsView view)
    {
        var s = new Scratch(system, view);
        if (s.Vitals?.Down is not { } down || !s.IsDead) return s.Done(false);

        s.RemoveCondition(down.DeadCondition);
        s.GetUp();
        if (view.HasPools) s.Set(s.Primary, Math.Max(1, s.Get(s.Primary)));
        return s.Done(true);
    }

    // ---- The scratch creature ----

    /// <summary>The view, copied, changed as the handler goes, recording each change as an intent.</summary>
    private sealed class Scratch
    {
        private readonly List<SheetIntent> _intents = new();
        private readonly SystemDefinition? _system;
        private readonly HashSet<string> _conditions;

        public Scratch(SystemDefinition? system, VitalsView view)
        {
            _system = system;
            Pools = new Dictionary<string, int>(view.Pools, StringComparer.Ordinal);
            _conditions = new HashSet<string>(view.Conditions, StringComparer.OrdinalIgnoreCase);
            IsPlayer = view.IsPlayer;
            Sheet = view.Sheet;
            Primary = SystemDefinition.PrimaryKeyOf(system);
            Temporary = SystemDefinition.TemporaryKeyOf(system);
        }

        public Dictionary<string, int> Pools { get; }
        public bool IsPlayer { get; }
        public IReadOnlyDictionary<string, object>? Sheet { get; }
        public string Primary { get; }
        public string Temporary { get; }
        public VitalsRules? Vitals => _system?.Vitals;

        public string MaxKeyOf(string key) => SystemDefinition.PoolMaxKey(_system, key);

        public VitalsResult<T> Done<T>(T report) => new(_intents, report);

        public int Get(string key) => Pools.TryGetValue(key, out var value) ? value : 0;

        public void Set(string key, int value)
        {
            Pools[key] = value;
            _intents.Add(Intent("set", ("track", key), ("to", (double)value)));
        }

        public void Counter(string key, int value)
        {
            Pools[key] = value;
            _intents.Add(Intent(CounterKind, ("key", key), ("to", (double)value)));
        }

        public void Leave(SheetIntent intent) => _intents.Add(intent);

        public bool HasCondition(string condition) => _conditions.Contains(condition);

        public void AddCondition(string condition, string source)
        {
            _conditions.Add(condition);
            _intents.Add(Intent("condition", ("add", condition), ("source", source)));
        }

        public bool RemoveCondition(string condition)
        {
            if (!_conditions.Remove(condition)) return false;
            _intents.Add(Intent("condition", ("remove", condition)));
            return true;
        }

        public bool IsDead => Vitals?.Down is { } down && HasCondition(down.DeadCondition);

        /// <summary>At zero on the primary pool and not dead.</summary>
        public bool IsDown => Get(Primary) <= 0 && !IsDead;

        public bool IsConcentrating => Vitals?.Concentration is { } rules && HasCondition(rules.Condition);

        public void ResetDeathSaves()
        {
            if (Vitals?.DeathSaves is not { } rules) return;
            foreach (var key in new[] { rules.SuccessesKey, rules.FailuresKey })
            {
                if (Pools.Remove(key)) _intents.Add(Intent(CounterKind, ("key", key), ("clear", true)));
            }
        }

        /// <summary>The table's kill, as one intent; here, what it leaves the creature as.</summary>
        public void Kill()
        {
            if (Vitals?.Down is not { } down || IsDead) return;
            _conditions.Remove(down.Condition);
            _conditions.Remove(down.StableCondition);
            if (Vitals.DeathSaves is { } saves)
            {
                Pools.Remove(saves.SuccessesKey);
                Pools.Remove(saves.FailuresKey);
            }

            if (Vitals.Concentration is { } concentration) _conditions.Remove(concentration.Condition);
            _conditions.Add(down.DeadCondition);
            _intents.Add(Intent(KillKind));
        }

        public void EndConcentration()
        {
            if (Vitals?.Concentration is { } rules) _conditions.Remove(rules.Condition);
            _intents.Add(Intent(EndConcentrationKind));
        }

        /// <summary>Back above zero: no longer down or stable, death saves reset. Whether anything changed.</summary>
        public bool GetUp()
        {
            if (Vitals?.Down is not { } down) return false;
            var removed = RemoveCondition(down.Condition) | RemoveCondition(down.StableCondition);
            ResetDeathSaves();
            return removed;
        }

        private static SheetIntent Intent(string kind, params (string Key, object? Value)[] fields) =>
            new(kind, null, fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));
    }
}
