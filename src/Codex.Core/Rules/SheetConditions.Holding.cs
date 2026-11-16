using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One condition that holds: its worked-out value (the stored number, or what a holder brings if that is higher; 1 for an unvalued one),
/// whether the sheet holds it on its own, and the conditions that bring it.</summary>
public sealed record HeldCondition(string Name, double Value, bool Listed, IReadOnlyList<string> From);

/// <summary>A valued condition lowered at a moment: its key, and the number it had and now has.</summary>
public sealed record ConditionDrop(string Condition, string Key, int From, int To);

public static partial class SheetConditions
{
    /// <summary>The conditions that hold on <paramref name="sheet"/>: <see cref="Listed"/>, then what they imply (<see cref="Holding"/>).</summary>
    public static List<string> Of(SystemDefinition system, IReadOnlyDictionary<string, object> sheet) =>
        Holding(system, sheet).Select(h => h.Name).ToList();

    /// <summary>
    /// <see cref="Listed"/> with <c>implies</c> expanded to a fixpoint (docs/CONDITION_IMPLIES_DESIGN.md), never stored. Listed ones come first, in order, then the
    /// implied ones in the order they are found. A condition's value is the highest of its own and what each holder brings, clamped to its maximum: the rules
    /// of the game count the highest and add nothing up. Names compare without regard to case.
    /// </summary>
    public static IReadOnlyList<HeldCondition> Holding(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var listed = Listed(system, sheet);
        var held = new List<Held>();
        int Find(string name) => held.FindIndex(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (var name in listed)
        {
            if (Find(name) >= 0) continue;
            var effect = EffectOf(system, name);
            held.Add(new Held(effect?.Name ?? name, effect is { } found ? ValueOf(found.Name, found.Effect, sheet) : 1, true, new List<string>()));
        }

        // A formula on an implied value is worked out by a SheetModel, whose own constructor reads what holds: that inner read has no use for implied
        // conditions (it only works the formula out), so it stops here rather than go round.
        if (!_workingFormula && system.ConditionEffects.Values.Any(e => e.Implies.Count > 0))
        {
            SheetRuleEngine? engine = null;
            // Values only go up and the graph has no cycles (the loader refuses one), so the passes stop; the cap is for a graph that was never loaded.
            for (var pass = 0; pass <= system.ConditionEffects.Count + 1; pass++)
            {
                var changed = false;
                foreach (var (holder, holderValue) in held.Select(h => (h.Name, h.Value)).ToList())
                {
                    if (EffectOf(system, holder) is not { } source) continue;
                    foreach (var implied in source.Effect.Implies)
                    {
                        var target = EffectOf(system, implied.Condition);
                        var name = target?.Name ?? implied.Condition;
                        double value = 1;
                        if (target?.Effect.Value != null)
                        {
                            engine ??= new SheetRuleEngine(system, NullEntryResolver.Instance);
                            value = ImpliedValue(system, engine, implied, name, holderValue, sheet);
                        }

                        if (value <= 0) continue;
                        var at = Find(name);
                        if (at < 0)
                        {
                            held.Add(new Held(name, value, false, new List<string> { holder }));
                            changed = true;
                            continue;
                        }

                        var now = held[at];
                        if (!now.From.Contains(holder, StringComparer.OrdinalIgnoreCase) && !string.Equals(now.Name, holder, StringComparison.OrdinalIgnoreCase))
                        {
                            now.From.Add(holder);
                            changed = true;
                        }

                        if (value > now.Value)
                        {
                            held[at] = now with { Value = value };
                            changed = true;
                        }
                    }
                }

                if (!changed) break;
            }
        }

        return held.Select(h => new HeldCondition(h.Name, h.Value, h.Listed, h.From)).ToList();
    }

    [ThreadStatic]
    private static bool _workingFormula;

    private sealed record Held(string Name, double Value, bool Listed, List<string> From);

    /// <summary>The value an implied valued condition is brought at: a number, or a formula in which <c>value</c> is the holder's, rounded down and clamped
    /// to the condition's maximum. 1 when none is written; 0 (it does not hold) for a formula that cannot be worked out.</summary>
    private static double ImpliedValue(SystemDefinition system, SheetRuleEngine engine, ConditionImplication implied, string name, double holderValue, IReadOnlyDictionary<string, object> sheet)
    {
        // Both the formula and the maximum are worked out by a SheetModel, whose constructor reads what holds: the guard stays up for all of it.
        var outer = _workingFormula;
        _workingFormula = true;
        try
        {
            double value = 1;
            if (!string.IsNullOrWhiteSpace(implied.Value))
            {
                if (double.TryParse(implied.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) value = number;
                else
                {
                    var view = WithValue(holderValue, sheet);
                    value = SheetFormulas.Number(new SheetModel(system, NullEntryResolver.Instance, view).Formula(implied.Value, new Dictionary<string, double> { [ValueName] = holderValue }));
                }
            }

            value = double.IsFinite(value) ? Math.Floor(value) : 0;
            return engine.ConditionMax(name, sheet) is { } max ? Math.Min(value, max) : value;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            _workingFormula = outer;
        }
    }

    /// <summary>Each valued condition whose <c>decrease.on</c> is <paramref name="moment"/> (without regard to case) and whose stored number is above 0,
    /// lowered by <c>by</c>, never below 0. <paramref name="stored"/> reads a condition's number by its key. Pure: the caller writes it.</summary>
    public static IReadOnlyList<ConditionDrop> Drops(SystemDefinition system, Func<string, int?> stored, string moment)
    {
        var drops = new List<ConditionDrop>();
        foreach (var (name, key, value) in Valued(system))
        {
            if (value.Decrease is not { } decrease || decrease.By <= 0 || !string.Equals(decrease.On, moment, StringComparison.OrdinalIgnoreCase)) continue;
            if (stored(key) is not { } now || now <= 0) continue;
            drops.Add(new ConditionDrop(name, key, now, Math.Max(0, now - decrease.By)));
        }

        return drops;
    }

    /// <summary><see cref="Drops"/> over a sheet, written back as numbers (0 stays written as 0). Returns what changed.</summary>
    public static IReadOnlyList<ConditionDrop> Drop(SystemDefinition system, Dictionary<string, object> sheet, string moment)
    {
        var drops = Drops(system, key => YamlValues.ToNumber(sheet.GetValueOrDefault(key)) is { } n ? (int)n : null, moment);
        foreach (var drop in drops) sheet[drop.Key] = (long)drop.To;
        return drops;
    }

    /// <summary>Whether <paramref name="moment"/> is one a condition's value can drop at on this system: the turn's ends, a rest or a declared moment.</summary>
    public static bool IsDropMoment(SystemDefinition system, string moment) =>
        moment.Equals("turnEnd", StringComparison.OrdinalIgnoreCase) || moment.Equals("turnStart", StringComparison.OrdinalIgnoreCase) || SystemMoments.IsMoment(system, moment);

    /// <summary>The conditions that stay as conditions of their own when <paramref name="removed"/> ends (an implied item with <c>stays: true</c>: Prone after
    /// Unconscious), as the system writes them. The caller adds each unless it is already there.</summary>
    public static IReadOnlyList<string> Stays(SystemDefinition system, string removed)
    {
        if (EffectOf(system, removed) is not { } source) return Array.Empty<string>();
        return source.Effect.Implies.Where(i => i.Stays).Select(i => EffectOf(system, i.Condition)?.Name ?? i.Condition).ToList();
    }
}
