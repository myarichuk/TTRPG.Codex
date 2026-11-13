using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

public sealed partial class SheetRuleEngine
{
    // ---- Choices over Library entries (docs/ENTRY_CHOICES_DESIGN.md) ----
    //
    // `adds: [{ to: grantedSpells, choice: mystic-arcanum-6, choose: 1, where: [...] }]`: the player picks entries of a list of Library entries
    // (spells), among the candidates that meet every `where` condition (the conditions of a list's own `where`, plus `contains`, `containsChoice`
    // and `equalsChoice`). Or `adds: [{ choice: spell-list, choose: 1, from: [Cleric, Druid, Wizard] }]`: a choice of a value, no list, which later
    // choices of the same feature read. The picks are stored as choice__{entryId}[#copy]__{id}, entry ids or values; the valid picks of an entry
    // choice are put on its list like fixed entries (ApplyGranted), and a pick that no longer fits is stale (a violation, choose again).

    /// <summary>The <see cref="PendingChoice.Target"/> of a choice of a value, which puts nothing on a list.</summary>
    public const string ValueTarget = "value";

    /// <summary>How much of an entry's text an option carries.</summary>
    private const int OptionTextLength = 140;

    /// <summary>The list of Library entries <paramref name="to"/> names (one with a target entity type), or null when it names none.</summary>
    private ListRule? EntryList(string to) =>
        to.Length == 0 ? null : Rules?.Lists.FirstOrDefault(l => l.Key.Equals(to, StringComparison.Ordinal) && l.TargetEntityType != null);

    /// <summary>Whether an <c>adds</c> item leaves entries of a list of Library entries to the player.</summary>
    private bool IsEntryChoice(FeatureAdd add) => add.Choice != null && add.Rank == null && add.Raise == 0 && EntryList(add.To) != null;

    /// <summary>One entry choice or value choice of a feature, worked out against the sheet: what it offers, what is picked and fits
    /// (<see cref="Valid"/>), what is stored and does not (<see cref="Stale"/>), and the choice it waits on, when it does.</summary>
    private sealed record EntryChoiceState(ActiveFeature Feature, FeatureAdd Add, FeatureChoice Choice, string Key, ListRule? List,
        IReadOnlyList<ChoiceOption> Options, IReadOnlyList<string> Valid, IReadOnlyList<string> Stale, string? BlockedBy);

    /// <summary>
    /// Works out the entry choices and value choices of <paramref name="features"/>, each feature's in the order its <c>adds</c> are written, so a choice
    /// reads what the ones before it hold (<c>containsChoice</c>). A choice that reads a choice with no valid pick offers nothing. Reads the sheet
    /// only, so <see cref="ApplyGranted"/> can call it before the evaluation.
    /// </summary>
    private List<EntryChoiceState> ResolveEntryChoices(IEnumerable<ActiveFeature> features, IReadOnlyDictionary<string, object> sheet)
    {
        var states = new List<EntryChoiceState>();
        foreach (var feature in features)
        {
            var picked = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var add in feature.Adds)
            {
                if (add.Choice is not { } choice || add.Rank != null || add.Raise != 0) continue;
                var list = EntryList(add.To);
                if (list == null && !add.IsValueChoice) continue; // a ranked list's, or a kind's, choice: not this one's
                if (!AddApplies(feature, add)) continue;

                var key = ChoiceKey(feature.Source.Id, feature.Copy, feature.ScopedChoiceId(choice.Id));
                var blocked = list == null ? null : BlockedBy(choice, picked);
                var options = list == null
                    ? (choice.From ?? Array.Empty<string>()).Select(v => new ChoiceOption(v, v)).ToList()
                    : blocked != null ? new List<ChoiceOption>() : EntryOptions(feature, choice, list, sheet, picked);

                var valid = new List<string>();
                var stale = new List<string>();
                foreach (var raw in RawList(sheet, key))
                {
                    var match = options.FirstOrDefault(o => o.Key.Equals(raw, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !valid.Contains(match.Key, StringComparer.OrdinalIgnoreCase) && valid.Count < choice.Choose) valid.Add(match.Key);
                    else stale.Add(raw);
                }

                picked[choice.Id] = valid;
                states.Add(new EntryChoiceState(feature, add, choice, key, list, options, valid, stale, blocked));
            }
        }

        return states;
    }

    /// <summary>The id of the first choice <paramref name="choice"/>'s conditions read that has no valid pick yet, or null.</summary>
    private static string? BlockedBy(FeatureChoice choice, IReadOnlyDictionary<string, IReadOnlyList<string>> picked)
    {
        foreach (var condition in choice.Where ?? Array.Empty<RuleCondition>())
        {
            foreach (var read in new[] { condition.ContainsChoice, condition.EqualsChoice }.OfType<string>())
            {
                if (!picked.TryGetValue(read, out var made) || made.Count == 0) return read;
            }
        }

        return null;
    }

    /// <summary>The entries a choice offers: every candidate of the list's type that meets each <c>where</c> condition, narrowed to what the sheet already
    /// holds on <c>fromList</c> when that is named. Candidates are what the resolver gives, so a campaign's homebrew counts. By name.</summary>
    private List<ChoiceOption> EntryOptions(ActiveFeature feature, FeatureChoice choice, ListRule list, IReadOnlyDictionary<string, object> sheet,
        IReadOnlyDictionary<string, IReadOnlyList<string>> picked)
    {
        var held = choice.FromList == null
            ? null
            : RawList(sheet, choice.FromList).Select(raw => ParsePick(raw).Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return resolver.Candidates(list.TargetEntityType)
            .Where(c => held == null || held.Contains(c.Id))
            .Where(c => (choice.Where ?? Array.Empty<RuleCondition>()).All(w => Holds(w, c, feature.View, picked)))
            .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ChoiceOption(c.Id, c.Name, ShortText(c.Description)))
            .ToList();
    }

    /// <summary>The first line of an entry's text without its markdown, cut short: what an option says about itself in a long list.</summary>
    private static string? ShortText(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var line = description.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => !l.StartsWith('|') && !l.StartsWith('#'));
        if (line == null) return null;
        line = line.Replace("*", string.Empty, StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal).Trim();
        return line.Length <= OptionTextLength ? line : line[..OptionTextLength].TrimEnd() + "…";
    }

    /// <summary>Turns the entry choices and value choices of the features into pending and made choices, with the warnings and violations they bring: a
    /// warning while a choice is open (never a reason to refuse a save), a violation for a pick that no longer fits.</summary>
    private void EvaluateEntryChoices(IReadOnlyList<ActiveFeature> features, IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (!features.Any(f => f.Adds.Any(a => a.Choice != null && (a.IsValueChoice || IsEntryChoice(a))))) return;
        var keysSeen = result.PendingChoices.Concat(result.MadeChoices).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var warned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in ResolveEntryChoices(features.Where(f => !f.OnKind), sheet))
        {
            var (feature, _, choice, key, list, options, valid, stale, blocked) = state;
            if (!keysSeen.Add(key))
            {
                WarnFeature(result, warned, feature.Name); // two choices of one entry with one id would share their picks
                continue;
            }

            foreach (var name in stale)
            {
                result.Violations.Add(new RuleViolation(key, $"{feature.Name} ({feature.Source.Name}): \"{name}\" no longer fits this choice. Choose again.", ChoiceStaleKey,
                    new object?[] { feature.Name, feature.Source, options.FirstOrDefault(o => o.Key.Equals(name, StringComparison.OrdinalIgnoreCase))?.Label ?? name }));
            }

            var remaining = choice.Choose - valid.Count;
            var open = options.Count(o => !valid.Contains(o.Key, StringComparer.OrdinalIgnoreCase));
            var pending = new PendingChoice(key, feature.Name, feature.Source.Name, choice.Choose, valid, options, null, list?.Key ?? ValueTarget, false, false, feature, BlockedBy: blocked);
            if (blocked != null)
            {
                result.PendingChoices.Add(pending); // the picker says what to choose first; no warning of its own, the choice it waits on has one
            }
            else if (remaining > 0 && open > 0)
            {
                result.PendingChoices.Add(pending);
                var label = list == null ? choice.Id : string.IsNullOrWhiteSpace(list.Label) ? list.Key : list.Label;
                result.Warnings.Add(new RuleViolation(key, $"{feature.Name} ({feature.Source.Name}): choose {Math.Min(remaining, open)} more {label.ToLowerInvariant()}.", ChoicePendingKey,
                    new object?[] { feature.Name, feature.Source, Math.Min(remaining, open), label }));
            }
            else if (valid.Count > 0)
            {
                result.MadeChoices.Add(pending);
            }
        }
    }
}
