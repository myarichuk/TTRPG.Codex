using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// Which typed modifiers on one target count, by their types' stacking rules (overhaul section 3, "Modifiers"). One routine for
/// a sheet kind's values (<see cref="SheetModel"/>) and for 5e's armor class (<see cref="SheetRuleEngine"/>), so the two cannot
/// disagree about what "highest" means.
/// <para>One rule covers every type: <b>a type's modifiers form a group, and the group keeps one member (or all of them).</b>
/// What the kept member does to the target depends on the kind of rule.</para>
/// <list type="bullet">
/// <item><b>Bonus rules</b>, whose kept member is added to the value: <c>sum</c> (the default; every one counts),
/// <c>highest</c> / <c>lowest</c> (of the bonuses of a type the largest (smallest) counts; penalties of the type are a group of
/// their own, ruled by <see cref="ModifierTypesDefinition.PenaltyTypes"/> for that type, else <see cref="ModifierTypesDefinition.Penalties"/>: 3.5's dodge stacks, armor does not; Pathfinder's
/// status bonus is <c>highest</c>: +1 status, +2 status and +1 untyped on 10 make 13).</item>
/// <item><b>Value rules</b>, whose kept member competes with the value the target starts from and replaces it when it wins:
/// <c>max</c> keeps the greatest (a floor: Extra Attack 2, 3, 4 on a start of 1 is 4), <c>min</c> keeps the least (a ceiling:
/// Improved Critical 19 and Superior Critical 18 on a start of 20 is 18). <c>best</c> is <c>max</c> by its older name: 5e's armor
/// class is the best of worn armor, Unarmored Defense and 10 + Dexterity. Penalties are not split off: the group's greatest
/// (least) member counts, whatever its sign.</item>
/// </list>
/// A tie goes to the one listed first.
/// </summary>
public static class ModifierStacking
{
    public const string Best = "best";
    public const string Max = "max";
    public const string Min = "min";

    /// <summary>The rule for a type: the system's, else <paramref name="fallback"/>'s for that type, else the system's default.</summary>
    public static string RuleOf(ModifierTypesDefinition types, string type, IReadOnlyDictionary<string, string>? fallback = null) =>
        types.Types.GetValueOrDefault(type) ?? fallback?.GetValueOrDefault(type) ?? types.Default;

    /// <summary>Whether a rule keeps a value to compete with the start (<c>max</c>, <c>best</c>, <c>min</c>) instead of adding a bonus.</summary>
    public static bool IsValueRule(string? rule) =>
        rule != null && (rule.Equals(Best, StringComparison.OrdinalIgnoreCase) || rule.Equals(Max, StringComparison.OrdinalIgnoreCase) || rule.Equals(Min, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a rule is a value rule that keeps the least (<c>min</c>); the others keep the greatest.</summary>
    public static bool IsCeiling(string? rule) => rule != null && rule.Equals(Min, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a rule is a value rule that keeps the greatest (<c>max</c>, <c>best</c>).</summary>
    public static bool IsFloor(string? rule) => IsValueRule(rule) && !IsCeiling(rule);

    /// <summary>Each modifier with whether it counts, grouped by type (and, except for value rules, bonus or penalty) in the order
    /// the groups first appear, each group in its own order.</summary>
    public static IReadOnlyList<(T Item, string Type, double Amount, bool Counted)> Stack<T>(IEnumerable<(string Type, double Amount, T Item)> reached,
        ModifierTypesDefinition types, IReadOnlyDictionary<string, string>? fallback = null)
    {
        var result = new List<(T, string, double, bool)>();
        var listed = reached.Select((r, index) => (r.Type, r.Amount, r.Item, Index: index)).ToList();
        foreach (var group in listed.GroupBy(r => (r.Type, Penalty: !IsValue(types, r.Type, fallback) && r.Amount < 0)))
        {
            var members = group.ToList();
            var rule = IsValue(types, group.Key.Type, fallback) ? RuleOf(types, group.Key.Type, fallback)
                : group.Key.Penalty ? PenaltyRuleOf(types, group.Key.Type)
                : RuleOf(types, group.Key.Type, fallback);
            // By place, not by value: two equal bonuses of a "highest" type are still one that counts and one that does not.
            var counted = rule.ToLowerInvariant() switch
            {
                Best or Max => new[] { members.MaxBy(r => r.Amount).Index },
                Min => new[] { members.MinBy(r => r.Amount).Index },
                "highest" => new[] { members.MaxBy(r => Math.Abs(r.Amount)).Index },
                "lowest" => new[] { members.MinBy(r => Math.Abs(r.Amount)).Index },
                _ => members.Select(r => r.Index).ToArray()
            };
            foreach (var member in members) result.Add((member.Item, member.Type, member.Amount, counted.Contains(member.Index)));
        }

        return result;
    }

    private static readonly string[] BonusRules = ["sum", "highest", "lowest", "max", "min", "best"];
    private static readonly string[] PenaltyRules = ["sum", "highest", "lowest"];

    private static bool Known(string? rule, string[] known) => rule != null && known.Contains(rule.ToLowerInvariant());

    /// <summary>The rule for the penalties of a type: <c>penaltyTypes</c> for it, else <c>penalties</c>. A blank rule defers to the
    /// next, and one that is not <c>sum</c>, <c>highest</c> or <c>lowest</c> counts as <c>sum</c> (<see cref="Problems"/> warns).</summary>
    public static string PenaltyRuleOf(ModifierTypesDefinition types, string type)
    {
        var rule = types.PenaltyTypes.GetValueOrDefault(type);
        if (string.IsNullOrWhiteSpace(rule)) rule = types.Penalties;
        return Known(rule, PenaltyRules) ? rule!.ToLowerInvariant() : "sum";
    }

    /// <summary>What is wrong with a system's <c>modifierTypes</c> that still lets it load: a rule that is not one the engine knows (it
    /// counts as <c>sum</c>), a penalty rule that is a value rule, and <c>penaltyTypes</c> on a type whose own rule is a value rule
    /// (its penalties are not a group of their own). A blank rule is not a problem: it means the default.</summary>
    public static IEnumerable<string> Problems(SystemDefinition system)
    {
        var types = system.ModifierTypes;
        foreach (var (where, rule) in new[] { ("modifierTypes.default", types.Default) }
                     .Concat(types.Types.Select(t => ($"modifierTypes.types.{t.Key}", t.Value))))
        {
            if (!string.IsNullOrWhiteSpace(rule) && !Known(rule, BonusRules))
                yield return $"{where} is '{rule}', which is not a stacking rule (use {string.Join(", ", BonusRules)}); it counts as sum.";
        }

        foreach (var (where, rule) in new[] { ("modifierTypes.penalties", types.Penalties) }
                     .Concat(types.PenaltyTypes.Select(t => ($"modifierTypes.penaltyTypes.{t.Key}", t.Value))))
        {
            if (!string.IsNullOrWhiteSpace(rule) && !Known(rule, PenaltyRules))
                yield return $"{where} is '{rule}', which is not a penalty rule (use {string.Join(", ", PenaltyRules)}); it counts as sum.";
        }

        foreach (var type in types.PenaltyTypes.Keys)
        {
            if (IsValueRule(RuleOf(types, type)))
                yield return $"modifierTypes.penaltyTypes.{type} is ignored: {type} is a {RuleOf(types, type)} type, and its penalties are not a group of their own.";
        }
    }

    private static bool IsValue(ModifierTypesDefinition types, string type, IReadOnlyDictionary<string, string>? fallback) =>
        IsValueRule(RuleOf(types, type, fallback));
}
