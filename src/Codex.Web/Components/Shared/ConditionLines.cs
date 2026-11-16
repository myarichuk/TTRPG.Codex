using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>A valued condition as a stepper draws it: the name the rules know it by, the pool and sheet key its value lives under, and its highest value.</summary>
public sealed record ValuedCondition(string Name, string Key, int? Max);

/// <summary>A condition or spell effect that holds, for a list: the rules' name, its value when it has one (null for an unvalued one), the conditions that bring
/// it (<see cref="From"/>: empty for one that only the sheet or the table holds), and whether it is held on its own (<see cref="Listed"/>): one that is not
/// has no remove button, since it ends when its source does.</summary>
public sealed record ConditionLine(string Name, int? Value, IReadOnlyList<string>? From = null, bool Listed = true);

/// <summary>One entry of the condition picker: what is typed (the name in the reader's language), what the list says beside it, and whether it is a spell effect.</summary>
public sealed record PickOption(string Name, string Shown, string Value, bool SpellEffect);

/// <summary>
/// What the table and the sheet page show about conditions (docs/SHEET_CONDITIONS_DESIGN.md, "Sheet and table"): the valued conditions of a system
/// for the stepper, the conditions that hold for a read-only list, and the picker's entries. Plain data in and out, so each is tested without a page.
/// </summary>
public static class ConditionLines
{
    /// <summary>Every valued condition of the system, with its highest value worked out on <paramref name="sheet"/> (the live view, so a formula reads what
    /// the table holds now). Exhaustion declared only in <c>vitals.exhaustion</c>, with no <c>conditionEffects</c> entry, is still a stepper.</summary>
    public static IReadOnlyList<ValuedCondition> Valued(SystemDefinition? system, IReadOnlyDictionary<string, object>? sheet)
    {
        var found = new List<ValuedCondition>();
        if (system == null) return found;
        var engine = new SheetRuleEngine(system, NullEntryResolver.Instance);
        var live = sheet ?? new Dictionary<string, object>();
        foreach (var (name, key, _) in SheetConditions.Valued(system)) found.Add(new ValuedCondition(name, key, engine.ConditionMax(name, live)));
        if (system.Vitals?.Exhaustion is { } exhaustion && !found.Any(v => v.Key == exhaustion.Key))
        {
            found.Insert(0, new ValuedCondition(exhaustion.Condition ?? "Exhaustion", exhaustion.Key, exhaustion.MaxLevel));
        }

        return found;
    }

    /// <summary>The conditions and effects that hold on <paramref name="sheet"/> (<see cref="SheetConditions.Of"/>), each with its value when it has one.
    /// A valued one that is above 0 is listed once, under the name the system writes it by.</summary>
    public static IReadOnlyList<ConditionLine> Holding(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var valued = Valued(system, null).ToDictionary(v => v.Name, v => v.Key, StringComparer.OrdinalIgnoreCase);
        var lines = new List<ConditionLine>();
        foreach (var held in SheetConditions.Holding(system, sheet))
        {
            int? value = valued.ContainsKey(held.Name) ? (int)held.Value : null;
            lines.Add(new ConditionLine(held.Name, value, held.From, held.Listed));
        }

        // Exhaustion held only by vitals.exhaustion (no conditionEffects entry) is not in Of; its level in the sheet is its record.
        foreach (var v in Valued(system, null).Where(v => !lines.Any(l => string.Equals(l.Name, v.Name, StringComparison.OrdinalIgnoreCase))))
        {
            if (YamlValues.ToNumber(sheet.GetValueOrDefault(v.Key)) is { } level && level > 0) lines.Add(new ConditionLine(v.Name, (int)level));
        }

        return lines;
    }

    /// <summary>The picker's entries: the system's conditions (and its concentration condition), then its spell effects as their own group. A datalist has
    /// no groups, so an effect says so in its <c>label</c>; a valued condition that holds says its value there too. <paramref name="values"/> is the creature's
    /// value of each valued condition by key (0 when absent). <paramref name="group"/> is the group's name in the reader's language.</summary>
    public static IReadOnlyList<PickOption> Options(SystemDefinition? system, IReadOnlyDictionary<string, int>? values, string group, Func<string, int, string> withValue)
    {
        var options = new List<PickOption>();
        if (system == null) return options;
        var valued = Valued(system, null).ToDictionary(v => v.Name, v => v.Key, StringComparer.OrdinalIgnoreCase);
        var conditions = system.Conditions
            .Concat(system.Vitals?.Concentration is { } concentration ? new[] { concentration.Condition } : Array.Empty<string>())
            .Concat(valued.Keys.Where(k => !system.Effects.Contains(k, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => !system.Effects.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        void Add(string name, bool effect)
        {
            var shown = system.DisplayCondition(name);
            var level = valued.TryGetValue(name, out var key) ? values?.GetValueOrDefault(key) ?? 0 : 0;
            var text = level > 0 ? withValue(shown, level) : shown;
            options.Add(new PickOption(name, shown, effect ? $"{text} ({group})" : text, effect));
        }

        foreach (var name in conditions) Add(name, false);
        foreach (var name in system.Effects.Distinct(StringComparer.OrdinalIgnoreCase)) Add(name, true);
        return options;
    }

    /// <summary>The rules' name for what was typed: a name as the system writes it, or one as the reader's language writes it (a name from the picker),
    /// matched without regard to case. Anything else is returned as typed, trimmed.</summary>
    public static string Canonical(SystemDefinition? system, string typed)
    {
        var text = typed.Trim();
        if (system == null || text.Length == 0) return text;
        var known = system.Conditions.Concat(system.Effects).Concat(system.ConditionEffects.Keys)
            .Concat(system.Vitals?.Concentration is { } concentration ? new[] { concentration.Condition } : Array.Empty<string>())
            .Concat(system.Vitals?.Exhaustion?.Condition is { } exhaustion ? new[] { exhaustion } : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (known.FirstOrDefault(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase)) is { } direct) return direct;
        return known.FirstOrDefault(k => string.Equals(system.DisplayCondition(k), text, StringComparison.CurrentCultureIgnoreCase)) ?? text;
    }

    /// <summary>The valued condition of this name (without regard to case), or null when it has no value.</summary>
    public static ValuedCondition? ValuedNamed(SystemDefinition? system, IReadOnlyDictionary<string, object>? sheet, string name) =>
        Valued(system, sheet).FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
}
