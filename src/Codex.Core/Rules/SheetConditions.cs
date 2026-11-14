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

    /// <summary>The conditions that hold on <paramref name="sheet"/>: its own, then each valued condition whose key on the sheet is above 0
    /// (unless the sheet lists it too).</summary>
    public static List<string> Of(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var conditions = SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey);
        foreach (var (name, key, _) in Valued(system))
        {
            if (YamlValues.ToNumber(sheet.GetValueOrDefault(key)) is > 0
                && !conditions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                conditions.Add(name);
            }
        }

        return conditions;
    }
}
