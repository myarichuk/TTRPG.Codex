using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

public sealed partial class SheetRuleEngine
{
    // ---- Key choices on a sheet kind: a feature leaves some keys of the kind's own keyed list to the player ----
    //
    // `adds: [{ to: actions, choice: expertise, choose: 1 }]` on an entry that a kind's feature source reaches. The picks are stored as
    // choice__{entryId}__{id}, a list of keys of that keyed list, as the picks of a ranked list's choice are. They change no number yet
    // (that is the later "+1 action rating" hook), so the choice is a PendingChoice with a Target and no Rank, a warning while open, and
    // a violation (ChoiceStale) for a pick the list no longer has.

    /// <summary>Turns the key choices of a sheet kind's features into pending and made choices, with the warnings and violations they bring.
    /// A grant of a kind's feature that this does not read (a rank or raise, or entries put on a list) is warned as a feature that can't be
    /// worked out, since it would otherwise silently do nothing.</summary>
    private void EvaluateKeyedChoices(IReadOnlyList<ActiveFeature> features, IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        if (SheetLayout.KindOf(system, sheet) is not { } kind) return;
        var warned = new HashSet<string>(StringComparer.Ordinal);
        var keysSeen = result.PendingChoices.Concat(result.MadeChoices).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var feature in features.Where(f => f.OnKind))
        {
            foreach (var add in feature.Adds)
            {
                if (add.Choice is not { } choice || add.Rank != null || add.Raise != 0 || kind.KeyedLists.FirstOrDefault(l => l.Key == add.To) is not { } list)
                {
                    WarnFeature(result, warned, feature.Name);
                    continue;
                }

                var key = ChoiceKey(feature.Source.Id, feature.Copy, feature.ScopedChoiceId(choice.Id));
                if (!keysSeen.Add(key))
                {
                    WarnFeature(result, warned, feature.Name); // two choices of one entry with one id would share their picks
                    continue;
                }

                var offered = list.Keys.Where(k => k.Parameter == null).Select(k => new ChoiceOption(k.Key, string.IsNullOrWhiteSpace(k.Label) ? k.Key : k.Label)).ToList();
                if (choice.From != null) offered = offered.Where(o => choice.From.Contains(o.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                if (offered.Count == 0) continue;

                var valid = new List<string>();
                var stale = new List<string>();
                foreach (var name in RawList(sheet, key))
                {
                    var match = offered.FirstOrDefault(o => o.Key.Equals(name, StringComparison.OrdinalIgnoreCase) || o.Label.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !valid.Contains(match.Key, StringComparer.OrdinalIgnoreCase) && valid.Count < choice.Choose) valid.Add(match.Key);
                    else stale.Add(match?.Label ?? name);
                }

                foreach (var name in stale)
                {
                    result.Violations.Add(new RuleViolation(key, $"{feature.Name} ({feature.Source.Name}): \"{name}\" no longer fits this choice. Choose again.", ChoiceStaleKey,
                        new object?[] { feature.Name, feature.Source, name }));
                }

                var count = Math.Min(choice.Choose, offered.Count);
                var pending = new PendingChoice(key, feature.Name, feature.Source.Name, count, valid, offered, null, list.Key, false, false, feature);
                var label = string.IsNullOrWhiteSpace(list.Label) ? list.Key : list.Label;
                if (valid.Count < count)
                {
                    result.PendingChoices.Add(pending);
                    result.Warnings.Add(new RuleViolation(key, $"{feature.Name} ({feature.Source.Name}): choose {count - valid.Count} more {label.ToLowerInvariant()}.", ChoicePendingKey,
                        new object?[] { feature.Name, feature.Source, count - valid.Count, label }));
                }
                else if (valid.Count > 0)
                {
                    result.MadeChoices.Add(pending);
                }
            }
        }
    }
}
