using System.Text;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// Which conditions hold on a sheet (docs/SHEET_CONDITIONS_DESIGN.md): the ones the sheet lists under
/// <see cref="SheetModel.ConditionsKey"/>, and every valued condition whose value on the sheet is above 0. The one reader both engines
/// share, so a condition holds the same way for a sheet kind and for sheet rules.
/// </summary>
public static class SheetConditions
{
    /// <summary>The key a valued condition's value is kept under when <c>value.key</c> is not written: <c>condition_frightened</c>.</summary>
    public static string DefaultKey(string condition) => "condition_" + Slug(condition);

    /// <summary>The name in lower case with each run of anything but letters and digits made one <c>_</c> (<c>"Half Cover"</c> is <c>half_cover</c>).</summary>
    public static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) slug.Append(char.ToLowerInvariant(c));
            else if (slug.Length > 0 && slug[^1] != '_') slug.Append('_');
        }

        return slug.ToString().TrimEnd('_');
    }

    /// <summary>The key a valued condition keeps its value under: <c>value.key</c>, else <see cref="DefaultKey"/>.</summary>
    public static string KeyOf(string condition, ConditionValueDefinition value) =>
        string.IsNullOrWhiteSpace(value.Key) ? DefaultKey(condition) : value.Key;

    /// <summary>Every valued condition of the system (a <c>conditionEffects</c> entry with a <c>value</c>), with the key its value lives under.</summary>
    public static IEnumerable<(string Condition, string Key, ConditionValueDefinition Value)> Valued(SystemDefinition system)
    {
        foreach (var (name, effect) in system.ConditionEffects)
        {
            if (effect.Value is { } value) yield return (name, KeyOf(name, value), value);
        }
    }

    /// <summary>The <c>conditionEffects</c> entry of <paramref name="condition"/> (names compare without regard to case), with its name as the system writes it.</summary>
    public static (string Name, ConditionEffectDefinition Effect)? EffectOf(SystemDefinition system, string condition)
    {
        foreach (var (name, effect) in system.ConditionEffects)
        {
            if (string.Equals(name, condition, StringComparison.OrdinalIgnoreCase)) return (name, effect);
        }

        return null;
    }

    /// <summary>What <c>value</c> is in the effects of a condition that holds: the number under its key on the sheet, or 1 for a condition with no
    /// <c>value</c>. A valued condition holds only while its number is above 0 (<see cref="Of"/>), so for one that holds it is that number.</summary>
    public static double ValueOf(string condition, ConditionEffectDefinition effect, IReadOnlyDictionary<string, object> sheet) =>
        effect.Value is { } value && YamlValues.ToNumber(sheet.GetValueOrDefault(KeyOf(condition, value))) is { } number && number > 0 ? number : 1;

    /// <summary>A copy of <paramref name="sheet"/> with <c>value</c> added: what the formulas of the condition's effects are worked out on.</summary>
    public static IReadOnlyDictionary<string, object> WithValue(string condition, ConditionEffectDefinition effect, IReadOnlyDictionary<string, object> sheet)
    {
        var copy = new Dictionary<string, object>(sheet.Count + 1, StringComparer.Ordinal);
        foreach (var (key, value) in sheet) copy[key] = value;
        copy[ValueName] = ValueOf(condition, effect, sheet);
        return copy;
    }

    /// <summary>The name a condition's formulas read its value by.</summary>
    public const string ValueName = "value";

    /// <summary>The targets <see cref="SheetModel"/> reads on a system with sheet kinds, so the ones a condition's modifier would reach through it: a
    /// kind's value, track or keyed entry (<c>skills.balance</c>, its <c>@group</c>s), and each name a named roll's <c>modifiers('...')</c> reads.
    /// The loader sets them against what <c>sheetRules</c> claims (<see cref="SheetRuleEngine"/>): a target in both would count a condition twice.</summary>
    public static IReadOnlySet<string> KindTargets(SystemDefinition system)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in system.SheetKinds)
        {
            foreach (var value in kind.Values) targets.Add(value.Key);
            foreach (var track in kind.Tracks) targets.Add(track.Key);
            foreach (var list in kind.KeyedLists)
            {
                foreach (var key in list.Keys) targets.UnionWith(SheetModel.Targets(list, key, key.Key));
            }
        }

        foreach (var roll in system.Rolls) targets.UnionWith(RollTargets.Of(system, roll));
        return targets;
    }

    /// <summary>The conditions that hold on <paramref name="sheet"/>: the ones it lists, and each valued condition whose key on the sheet is above 0.
    /// A valued condition holds only then, listed or not: one the sheet lists with its value at 0 or unset is left out.</summary>
    public static List<string> Of(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var conditions = SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey);
        foreach (var (name, key, _) in Valued(system))
        {
            var holds = YamlValues.ToNumber(sheet.GetValueOrDefault(key)) is > 0;
            var listed = conditions.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            if (!holds && listed >= 0) conditions.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            else if (holds && listed < 0) conditions.Add(name);
        }

        return conditions;
    }
}
