using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

public sealed partial class SheetRuleEngine
{
    // ---- Row choices: pick one row of an entry's collection, or one of the options of a feature row ----
    //
    // Both levels share the storage of the other choices: choice__{entryId}__{id}, a list of the rows' names as the entry writes them
    // (its own language; the UI shows them translated). A choice nested in a chosen row is stored under the parent's id first:
    // choice__{entryId}__{parentId}__{id}, so ids of different rows never clash. Only the rows picked are features (CollectFeatures); the
    // rest give nothing, counters included. The warning for an unmade one is a notice like the others (IsChoiceNotice): it never blocks a save.

    /// <summary>The <see cref="PendingChoice.Target"/> of a choice among rows.</summary>
    public const string RowsTarget = "rows";

    /// <summary>The key of the warning for a row choice the player has not made (or made in part).</summary>
    public const string ChoiceRowPendingKey = "Actor.Rule.ChoiceRowPending";

    /// <summary>Turns the row choices found among the sheet's features into pending and made choices, with the warnings and violations they bring.</summary>
    private void EvaluateRowChoices(FeatureCollection collected, SheetEvaluation result)
    {
        var warned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in collected.Broken.Distinct(StringComparer.Ordinal)) WarnFeature(result, warned, name); // options without a usable choose

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in collected.Choices)
        {
            if (!seen.Add(choice.Key))
            {
                WarnFeature(result, warned, choice.Owner.Name); // two choices of one entry with one id would share their picks
                continue;
            }

            foreach (var name in choice.Stale)
            {
                result.Violations.Add(new RuleViolation(choice.Key, $"{choice.Label} ({choice.Source}): \"{name}\" no longer fits this choice. Choose again.", ChoiceStaleKey,
                    new object?[] { choice.Label, choice.Owner.Source, name }));
            }

            var count = Math.Min(choice.Choose.Count, choice.Options.Count);
            var info = new RowChoiceInfo(choice.Choose.OnChange.ToLowerInvariant(), choice.Choose.ChangesOn);
            var pending = new PendingChoice(choice.Key, choice.Label, choice.Source, count, choice.Valid.Select(v => v.Name).ToList(),
                choice.Options.Select(o => new ChoiceOption(o.Name, o.Name, o.Description)).ToList(), null, RowsTarget, false, false, choice.Owner, Row: info);
            if (choice.Valid.Count < count)
            {
                result.PendingChoices.Add(pending);
                result.Warnings.Add(new RuleViolation(choice.Key, $"{choice.Label} ({choice.Source}): choose {count - choice.Valid.Count} more.", ChoiceRowPendingKey,
                    new object?[] { choice.Label, choice.Owner.Source, count - choice.Valid.Count }));
            }
            else
            {
                result.MadeChoices.Add(pending);
            }
        }
    }

    /// <summary>
    /// Writes a new pick for a choice and settles the counters of the rows that change, by the choice's <c>onChange</c>. Keep (the default):
    /// counters whose key both the old and the new row have keep what was spent; <c>refill</c>: the new row's counters start full;
    /// <c>empty</c>: they start at 0. A counter only the old row had leaves the sheet, and one only the new row has starts full. This is
    /// the one way a pick is changed, so the sheet, the level-up and the table all settle counters the same way, through
    /// <see cref="ApplyCounters"/>. Making a pick for the first time is not a change. Returns the sheet as it stands afterwards.
    /// </summary>
    public SheetEvaluation ChangeChoice(Dictionary<string, object> sheet, string choiceKey, IEnumerable<string> picks)
    {
        var picked = picks.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var before = Evaluate(sheet);
        var choice = before.PendingChoices.Concat(before.MadeChoices).FirstOrDefault(c => c.Key == choiceKey);
        var changed = choice is { IsRow: true } && choice.Made.Count > 0
            && !choice.Made.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(picked.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        SetChoice(sheet, choiceKey, picked);
        var after = Recompute(sheet);
        if (!changed || choice!.Row!.OnChange is not (RowChoose.Refill or RowChoose.Empty)) return after;

        foreach (var counter in after.Counters.Where(c => IsChosenThrough(c.Feature, choiceKey)))
        {
            sheet[counter.Key] = choice.Row.OnChange == RowChoose.Refill ? (long)counter.Max : 0L;
        }

        return after;
    }

    /// <summary>Whether a feature was picked through the row choice <paramref name="choiceKey"/>, or through a choice nested in a row it picked.</summary>
    public static bool IsChosenThrough(ActiveFeature feature, string choiceKey) =>
        feature.ChosenBy != null && (feature.ChosenBy == choiceKey || feature.ChosenBy.StartsWith(choiceKey + "__", StringComparison.Ordinal));

    /// <summary>The sheet keys that tell the table which moments a row choice is usually changed at, so it can offer the change after a rest
    /// without evaluating the sheet: <c>changes_on__{choiceKey}</c> holds the moments (<c>Long Rest|dawn</c>) and <c>…_Label</c> the choice's name.</summary>
    public const string ChangesOnPrefix = "changes_on__";

    /// <summary>Writes the moments of the row choices to the sheet (<see cref="ChangesOnPrefix"/>) and takes off those of choices that are gone.</summary>
    public void ApplyChoiceMoments(Dictionary<string, object> sheet, SheetEvaluation evaluation)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in evaluation.PendingChoices.Concat(evaluation.MadeChoices).Where(c => c.Row is { ChangesOn.Count: > 0 }))
        {
            var key = ChangesOnPrefix + choice.Key;
            sheet[key] = string.Join("|", choice.Row!.ChangesOn);
            sheet[key + UsesLabelSuffix] = choice.Label;
            keep.Add(key);
            keep.Add(key + UsesLabelSuffix);
        }

        foreach (var stale in sheet.Keys.Where(k => k.StartsWith(ChangesOnPrefix, StringComparison.Ordinal) && !keep.Contains(k)).ToList()) sheet.Remove(stale);
    }

    /// <summary>The row choices of a sheet that <paramref name="moment"/> (a rest's name, <c>initiative</c>, <c>dawn</c>) is usually the time to change,
    /// read from the keys <see cref="ApplyChoiceMoments"/> wrote: the choice's sheet key and its name.</summary>
    public static IReadOnlyList<(string Key, string Label)> ChoicesToChangeAfter(IReadOnlyDictionary<string, object> sheet, string moment) =>
        sheet.Keys.Where(k => k.StartsWith(ChangesOnPrefix, StringComparison.Ordinal) && !k.EndsWith(UsesLabelSuffix, StringComparison.Ordinal))
            .Where(k => (YamlValues.GetString(sheet, k) ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries).Contains(moment, StringComparer.OrdinalIgnoreCase))
            .Select(k => (k[ChangesOnPrefix.Length..], YamlValues.GetString(sheet, k + UsesLabelSuffix) ?? k[ChangesOnPrefix.Length..]))
            .ToList();

    /// <summary>The row choices a moment (a rest's name, <c>initiative</c>, <c>dawn</c>) is usually the time to change: those whose
    /// <c>changesOn</c> names it, made or not. The rest flow offers them after the moment; nothing forces a change.</summary>
    public static IReadOnlyList<PendingChoice> ChoicesChangingOn(SheetEvaluation evaluation, string moment) =>
        evaluation.MadeChoices.Concat(evaluation.PendingChoices)
            .Where(c => c.Row != null && c.Row.ChangesOn.Contains(moment, StringComparer.OrdinalIgnoreCase)).ToList();
}
