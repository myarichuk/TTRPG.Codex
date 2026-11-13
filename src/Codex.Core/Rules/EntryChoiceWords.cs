using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>What a choice asks for, in parts a title is built from: how many, the level its <c>where</c> asks for (a <c>level</c> property equal to N),
/// and the noun, already singular or plural by the count (<see cref="Noun"/> is null for a value choice, and when the list's type has no name).</summary>
public sealed record ChoiceWant(int Count, int? Level, string? Noun, bool IsValue);

/// <summary>
/// Words for choices over Library entries and values (docs/ENTRY_CHOICES_DESIGN.md): what a picker's title says the choice wants, built from the
/// choice (its count and <c>where</c> conditions), never from the list's label. Pure, so it is tested without the page; the page puts the parts in
/// the reader's language.
/// </summary>
public static class EntryChoiceWords
{
    /// <summary>What <paramref name="choice"/> wants. <paramref name="singular"/> and <paramref name="plural"/> name the list's entity type ("spell",
    /// "spells"); the noun is the one that fits the count, and a plural that is missing falls back to the singular.</summary>
    public static ChoiceWant Wants(PendingChoice choice, string? singular, string? plural)
    {
        if (choice.Target == SheetRuleEngine.ValueTarget) return new ChoiceWant(choice.Count, null, null, true);

        var noun = choice.Count == 1 ? singular : string.IsNullOrWhiteSpace(plural) ? singular : plural;
        return new ChoiceWant(choice.Count, LevelOf(ConditionsOf(choice)), string.IsNullOrWhiteSpace(noun) ? null : noun.Trim().ToLowerInvariant(), false);
    }

    /// <summary>The <c>where</c> conditions of the choice stored under <paramref name="choice"/>'s key, or none.</summary>
    public static IReadOnlyList<RuleCondition> ConditionsOf(PendingChoice choice)
    {
        var feature = choice.Feature;
        foreach (var add in feature.Adds)
        {
            if (add.Choice is not { } own) continue;
            var key = SheetRuleEngine.ChoiceKey(feature.Source.Id, feature.Copy, feature.ScopedChoiceId(own.Id));
            if (key.Equals(choice.Key, StringComparison.Ordinal)) return own.Where ?? (IReadOnlyList<RuleCondition>)Array.Empty<RuleCondition>();
        }

        return Array.Empty<RuleCondition>();
    }

    /// <summary>The level a list of conditions asks for: a condition on the <c>level</c> property that equals a whole number.</summary>
    public static int? LevelOf(IEnumerable<RuleCondition> conditions)
    {
        foreach (var condition in conditions)
        {
            if (!condition.Property.Equals("level", StringComparison.OrdinalIgnoreCase) || condition.Equals is not { } value) continue;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var level)
                && level >= 0 && level == Math.Floor(level)) return (int)level;
        }

        return null;
    }

    /// <summary>An entry's level as the small print of an option ("Level 3"): <paramref name="format"/> for a whole number, else the value as it is.</summary>
    public static string? LevelText(object? value, Func<int, string> format) =>
        value is null ? null
        : double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n == Math.Floor(n) ? format((int)n)
        : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>The options whose name contains <paramref name="search"/>, ignoring case and accents; all of them when the search is empty. Keeps the
    /// order given. <paramref name="name"/> gives the name the reader sees (the option's label when not given).</summary>
    public static IReadOnlyList<ChoiceOption> Filter(IReadOnlyList<ChoiceOption> options, string? search, Func<ChoiceOption, string>? name = null)
    {
        var needle = search?.Trim();
        if (string.IsNullOrEmpty(needle)) return options;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return options.Where(o => compare.IndexOf(name?.Invoke(o) ?? o.Label, needle, Loose) >= 0).ToList();
    }

    /// <summary>A choice id as words ("spell-list" is "spell list"), for "Choose {0} first". The id is the author's, so this is as good as the data.</summary>
    public static string Humanize(string choiceId)
    {
        var last = choiceId.Split("__", StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? choiceId;
        return last.Replace('-', ' ').Replace('_', ' ').Trim();
    }

    /// <summary>The picks as they would stand with <paramref name="key"/> ticked or unticked in an entry or value choice. One wanted: a tick replaces the
    /// pick. Several: a tick beyond the count is not taken. A key the choice does not offer is ignored.</summary>
    public static IReadOnlyList<string> Toggled(PendingChoice choice, string key, bool on)
    {
        if (!choice.Options.Any(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) return choice.Made;
        if (on && choice.Count == 1) return new[] { key };
        var picks = choice.Made.Where(m => !m.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on && picks.Count < choice.Count) picks.Add(key);
        return picks;
    }
}
