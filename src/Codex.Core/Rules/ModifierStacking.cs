using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// Which typed modifiers on one target count, by their types' stacking rules (overhaul section 3, "Modifiers"). One routine for
/// a sheet kind's values (<see cref="SheetModel"/>) and for 5e's armor class (<see cref="SheetRuleEngine"/>), so the two cannot
/// disagree about what "highest" means.
/// <list type="bullet">
/// <item><c>sum</c> (the default): every one counts.</item>
/// <item><c>highest</c> / <c>lowest</c>: of the bonuses of a type, the largest (smallest) counts; penalties of the type are a group
/// of their own, ruled by <see cref="ModifierTypesDefinition.Penalties"/> (3.5: dodge stacks, armor does not).</item>
/// <item><c>best</c>: exactly one of the type counts, the greatest value, penalties included. A base chosen among candidates:
/// 5e's armor class is the best of worn armor, Unarmored Defense and 10 + Dexterity.</item>
/// </list>
/// A tie goes to the one listed first.
/// </summary>
public static class ModifierStacking
{
    public const string Best = "best";

    /// <summary>The rule for a type: the system's, else <paramref name="fallback"/>'s for that type, else the system's default.</summary>
    public static string RuleOf(ModifierTypesDefinition types, string type, IReadOnlyDictionary<string, string>? fallback = null) =>
        types.Types.GetValueOrDefault(type) ?? fallback?.GetValueOrDefault(type) ?? types.Default;

    /// <summary>Each modifier with whether it counts, grouped by type (and, except for <c>best</c>, bonus or penalty) in the order
    /// the groups first appear, each group in its own order.</summary>
    public static IReadOnlyList<(T Item, string Type, double Amount, bool Counted)> Stack<T>(IEnumerable<(string Type, double Amount, T Item)> reached,
        ModifierTypesDefinition types, IReadOnlyDictionary<string, string>? fallback = null)
    {
        var result = new List<(T, string, double, bool)>();
        var listed = reached.Select((r, index) => (r.Type, r.Amount, r.Item, Index: index)).ToList();
        foreach (var group in listed.GroupBy(r => (r.Type, Penalty: !IsBest(types, r.Type, fallback) && r.Amount < 0)))
        {
            var members = group.ToList();
            var rule = IsBest(types, group.Key.Type, fallback) ? Best
                : group.Key.Penalty ? types.Penalties
                : RuleOf(types, group.Key.Type, fallback);
            // By place, not by value: two equal bonuses of a "highest" type are still one that counts and one that does not.
            var counted = rule.ToLowerInvariant() switch
            {
                Best => new[] { members.MaxBy(r => r.Amount).Index },
                "highest" => new[] { members.MaxBy(r => Math.Abs(r.Amount)).Index },
                "lowest" => new[] { members.MinBy(r => Math.Abs(r.Amount)).Index },
                _ => members.Select(r => r.Index).ToArray()
            };
            foreach (var member in members) result.Add((member.Item, member.Type, member.Amount, counted.Contains(member.Index)));
        }

        return result;
    }

    private static bool IsBest(ModifierTypesDefinition types, string type, IReadOnlyDictionary<string, string>? fallback) =>
        string.Equals(RuleOf(types, type, fallback), Best, StringComparison.OrdinalIgnoreCase);
}
