using System.Collections;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// The multiple attack penalty (<c>sheetRules.multipleAttack</c>): what the Nth attack of a turn adds to the attack bonus. One function for
/// the Actions panel and for a roll definition's <c>attackPenalty(attackNumber)</c>, so the two cannot disagree.
/// </summary>
public static class MultipleAttack
{
    /// <summary>The number the <paramref name="attackNumber"/>th attack (1 is the first) adds. <paramref name="weaponProperty"/> reads a
    /// property of the weapon (null for no weapon), to find a trait that lowers it. Zero when the system has no such rule.</summary>
    public static int Penalty(MultipleAttackRules? rules, int attackNumber, Func<string, object?>? weaponProperty = null)
    {
        if (rules == null) return 0;
        var steps = rules.Penalties;
        if (weaponProperty != null)
        {
            foreach (var variant in rules.Variants.Where(v => v.Penalties.Count > 0))
            {
                if (Words(weaponProperty(variant.Property)).Contains(variant.Has, StringComparer.OrdinalIgnoreCase))
                {
                    steps = variant.Penalties;
                    break;
                }
            }
        }

        if (steps.Count == 0) return 0;
        return steps[Math.Clamp(attackNumber, 1, steps.Count) - 1];
    }

    /// <summary>The traits a property holds: a list's members, or one text split at commas.</summary>
    private static IEnumerable<string> Words(object? value) => value switch
    {
        null => Array.Empty<string>(),
        string text => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        IDictionary => Array.Empty<string>(),
        IEnumerable items => items.Cast<object?>().Select(i => i?.ToString()?.Trim() ?? string.Empty).Where(w => w.Length > 0),
        _ => new[] { value.ToString() ?? string.Empty }
    };

    /// <summary>What is wrong with the rule as written, for the loader.</summary>
    public static IEnumerable<string> Problems(MultipleAttackRules rules)
    {
        if (rules.Penalties.Count == 0) yield return "sheetRules.multipleAttack needs penalties (one number per attack, the last for every later one).";
        if (rules.Penalties.Any(p => p > 0)) yield return "sheetRules.multipleAttack penalties are numbers to add, zero or less.";
        foreach (var variant in rules.Variants)
        {
            if (variant.Property.Trim().Length == 0 || variant.Has.Trim().Length == 0) yield return "A sheetRules.multipleAttack variant needs a property and a trait (has).";
            if (variant.Penalties.Count == 0) yield return $"The multipleAttack variant for \"{variant.Has}\" needs penalties.";
        }
    }
}
