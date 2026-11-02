using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A class or subclass feature gained at a level, as the class entry's <c>features</c> rows give it.</summary>
public sealed record LevelFeature(string Name, string Description, string Source);

/// <summary>A list that may hold more picks after the level (a feat, cantrips, spells): <see cref="Added"/> more than before.</summary>
public sealed record LevelChoice(string Key, string Label, int Added);

/// <summary>
/// What going up one level brings (U4): the features of the class gaining the level, the hit points it adds, the picks it opens,
/// whether a subclass is due, and what the rules say is still missing once it is taken. Built from the sheet's evaluation now and
/// the evaluation of a copy at the next level, so the stepper shows the engine's own numbers.
/// </summary>
public sealed record LevelUpPlan(
    int Level,
    ContentEntry? Class,
    int ClassLevel,
    IReadOnlyList<LevelFeature> Features,
    HitPointLevel? HitPoints,
    int? HitPointsBefore,
    int? HitPointsAfter,
    int? ProficiencyBefore,
    int? ProficiencyAfter,
    IReadOnlyList<LevelChoice> Choices,
    bool NeedsSubclass,
    IReadOnlyList<RuleViolation> NewViolations)
{
    /// <param name="cls">The class gaining the level, as the reader reads it (localized); null when the sheet has none.</param>
    /// <param name="classLevel">That class's level after the level up.</param>
    /// <param name="subclass">That class's subclass, when it has one, for its features at <paramref name="classLevel"/>.</param>
    public static LevelUpPlan Build(SheetRules rules, SheetEvaluation before, SheetEvaluation after, int level, ContentEntry? cls, int classLevel,
        ContentEntry? subclass, string? proficiencyKey)
    {
        var features = FeaturesAt(cls, classLevel).Concat(FeaturesAt(subclass, classLevel)).ToList();

        var choices = new List<LevelChoice>();
        foreach (var list in rules.Lists)
        {
            var was = before.ListLimits.GetValueOrDefault(list.Key);
            var now = after.ListLimits.GetValueOrDefault(list.Key);
            if (now is { } n && n > (was ?? 0)) choices.Add(new LevelChoice(list.Key, list.Label, n - (was ?? 0)));
        }

        var needsSubclass = rules.Multiclass is { SubclassField: not null } multiclass && cls != null
            && classLevel >= multiclass.SubclassLevel && subclass == null;

        // What the level itself breaks: a rule the sheet met before and does not after. A limit that grew is a choice, not one.
        var known = before.Violations.Select(v => (v.FieldKey, v.Message)).ToHashSet();
        var broken = after.Violations.Where(v => !known.Contains((v.FieldKey, v.Message))).ToList();

        return new LevelUpPlan(level, cls, classLevel, features,
            after.HitPoints is { Active: true } hp ? hp.Levels.LastOrDefault(l => l.Level == level) : null,
            before.HitPoints is { Active: true } b ? b.Max : null,
            after.HitPoints is { Active: true } a ? a.Max : null,
            Number(before, proficiencyKey), Number(after, proficiencyKey),
            choices, needsSubclass, broken);
    }

    /// <summary>The <c>features</c> rows of a class or subclass entry at exactly <paramref name="level"/>.</summary>
    public static IReadOnlyList<LevelFeature> FeaturesAt(ContentEntry? entry, int level)
    {
        if (entry == null || !entry.Properties.TryGetValue("features", out var raw) || raw is not IEnumerable<object> rows)
        {
            return Array.Empty<LevelFeature>();
        }

        var result = new List<LevelFeature>();
        foreach (var row in rows.OfType<IDictionary<string, object>>())
        {
            if (!row.TryGetValue("level", out var l) || !int.TryParse(l?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) || at != level) continue;
            var name = row.TryGetValue("name", out var n) ? n?.ToString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new LevelFeature(name, row.TryGetValue("description", out var d) ? d?.ToString() ?? "" : "", entry.Name));
        }

        return result;
    }

    private static int? Number(SheetEvaluation evaluation, string? key) =>
        key != null && evaluation.Derived.TryGetValue(key, out var value) ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;
}
