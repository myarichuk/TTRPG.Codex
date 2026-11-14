using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// The check a written entry (a model's draft, a homebrew author's) passes before it is kept, for what its features add to the ranked lists
/// (<see cref="GrantList.IsRanked"/>): a choice needs an id and a count, its ids are unique within the entry (the picks are stored under
/// them), a rank is one the list has, and <c>rank</c> and <c>raise</c> are not both given. The engine only warns of these when it reads the
/// entry; a draft is refused.
/// </summary>
public static class FeatureGrantCheck
{
    /// <summary>What is wrong with the grants of <paramref name="entry"/>, one sentence each; empty when nothing is, or the system reads no
    /// features from the entry's type.</summary>
    /// <param name="requireId">Whether a choice must name its own id. A model's draft must; a person's entry may leave it to the default the
    /// engine makes (the row's slug and level), where it is only refused if two ids come out the same.</param>
    public static IReadOnlyList<string> Problems(SystemDefinition system, ContentEntry entry, bool requireId = true)
    {
        var problems = new List<string>();
        if (FeatureGrantVocabulary.For(system) is not { } vocabulary || vocabulary.CarrierFor(entry.Type) is not { } carrier) return problems;

        var features = new List<(string Name, IDictionary<string, object> Data)>();
        if (carrier.WholeEntry) features.Add((entry.Name, entry.Properties));
        foreach (var property in carrier.RowProperties)
        {
            if (!entry.Properties.TryGetValue(property, out var raw) || raw is not IEnumerable<object> rows) continue;
            foreach (var row in rows.OfType<IDictionary<string, object>>())
            {
                features.Add((row.TryGetValue("name", out var name) ? name?.ToString() ?? string.Empty : string.Empty, row));
            }
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in features) Feature(name, data, ids);
        return problems;

        // One feature row: its adds, and its options (each an inner row with its own namespace of choice ids, as the sheet stores them).
        void Feature(string name, IDictionary<string, object> data, HashSet<string> used)
        {
            OptionsProblems(name, data, used);
            ModifierProblems(name, data);
            var earlier = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // the choices of this feature written so far, which a later condition may read
            foreach (var add in AddsOf(data))
            {
                var to = Text(add, "to");
                var list = vocabulary.Lists.FirstOrDefault(l => l.IsRanked && l.Key.Equals(to, StringComparison.OrdinalIgnoreCase));
                var isChoice = add.ContainsKey("choice") || add.ContainsKey("choose");
                var shown = string.IsNullOrWhiteSpace(name) ? entry.Name : name;

                // A spell cast without a slot: free is "atWill" or a counter ({ max, recoversOn... }), on a list of Library entries (spells).
                if (add.TryGetValue("free", out var free) && free != null)
                {
                    // A mark-only choice (no "to", a "fromList") puts the spell on no list, but picks among the entries of one.
                    var heldFrom = to.Length == 0 ? Text(add, "fromList") : string.Empty;
                    var entryList = vocabulary.Lists.FirstOrDefault(l => l.EntityType != null && l.Key.Equals(to, StringComparison.OrdinalIgnoreCase))
                        ?? (heldFrom.Length == 0 ? null : (system.SheetRules?.Lists ?? new List<ListRule>()).Where(l => l.TargetEntityType != null && l.Key.Equals(heldFrom, StringComparison.OrdinalIgnoreCase))
                            .Select(l => new GrantList(l.Key, l.Label, l.TargetEntityType)).FirstOrDefault());
                    if (entryList == null) problems.Add($"{shown}: \"free\" goes on entries added to a list of Library entries (the granted spells), not \"{to}\".");
                    var atWill = free is string freeText && freeText.Trim().Equals("atWill", StringComparison.OrdinalIgnoreCase);
                    if (!atWill && !(free is IDictionary<string, object> freeMap && freeMap.ContainsKey("max")))
                    {
                        problems.Add($"{shown}: \"free\" is atWill, or a counter with a \"max\" (and \"recoversOn\" or \"recovers\").");
                    }
                }

                // A choice over Library entries (the list is one of entries, with a "where" or a "fromList" to narrow them), or of a value (no "to").
                var pickable = vocabulary.Lists.FirstOrDefault(l => !l.IsRanked && l.EntityType != null && l.Key.Equals(to, StringComparison.OrdinalIgnoreCase));
                var fromListName = Text(add, "fromList");
                var markOnly = isChoice && to.Length == 0 && fromListName.Length > 0; // picks among what the sheet holds, putting nothing anywhere
                var isValue = isChoice && to.Length == 0 && !markOnly;
                if (isChoice)
                {
                    if (list == null && pickable == null && !isValue && !markOnly)
                    {
                        problems.Add($"{shown}: a choice can only go to {string.Join(" or ", vocabulary.Lists.Where(l => l.IsRanked || l.EntityType != null).Select(l => l.Key))}, or pick a value (give no \"to\"), not \"{to}\".");
                    }

                    if (!(Number(add, "choose") >= 1)) problems.Add($"{shown}: a choice needs \"choose\", how many the player picks (1 or more).");
                    var id = Text(add, "choice");
                    if (id.Length == 0 && !requireId) id = SheetRuleEngine.DefaultChoiceId(shown, Number(data, "level") >= 1 ? (int)Number(data, "level") : null);
                    if (id.Length == 0) problems.Add($"{shown}: a choice needs a \"choice\" id, unique in the entry.");
                    else if (!used.Add(id)) problems.Add($"{shown}: the choice id \"{id}\" is used more than once in this entry; each choice needs its own.");

                    if (fromListName.Length > 0 && fromListName.Equals(to, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{shown}: a choice from a list you already hold adds nothing; give no \"to\" (the picks only mark what you hold).");
                    }

                    if (pickable != null || markOnly)
                    {
                        // Entries are offered by the conditions: with none, the whole Library would be.
                        var conditions = RowsOf(add, "where").ToList();
                        if (add.ContainsKey("entries")) problems.Add($"{shown}: a choice over entries has no \"entries\"; the conditions in \"where\" say which may be picked.");
                        if (conditions.Count == 0 && Text(add, "fromList").Length == 0) problems.Add($"{shown}: a choice over entries needs \"where\" conditions or a \"fromList\" (it would offer everything in the Library).");
                        if (Text(add, "fromList") is { Length: > 0 } fromList && (system.SheetRules?.Lists ?? new List<ListRule>()).All(l => !l.Key.Equals(fromList, StringComparison.OrdinalIgnoreCase))) problems.Add($"{shown}: \"fromList\" names \"{fromList}\", which is not a list of this system.");
                        foreach (var condition in conditions)
                        {
                            if (Text(condition, "property").Length == 0) problems.Add($"{shown}: a \"where\" condition needs a \"property\".");
                            foreach (var read in new[] { "containsChoice", "equalsChoice" }.Select(k => Text(condition, k)).Where(r => r.Length > 0))
                            {
                                if (!earlier.Contains(read)) problems.Add($"{shown}: a condition reads the choice \"{read}\", which is not a choice written before it in this feature (a choice must come first).");
                            }
                        }
                    }
                    else if (isValue)
                    {
                        // The values are the player's menu: a non-empty list, written as the entries write them (a class's name as its spells name it).
                        var values = add.TryGetValue("from", out var listedValues) && listedValues is not string ? listedValues as IEnumerable<object> : null;
                        if (values == null || !values.Any()) problems.Add($"{shown}: a choice of a value needs \"from\", a list of the values the player picks between.");
                    }

                    if (id.Length > 0) earlier.Add(id);
                }

                if (list == null) continue;
                if (add.ContainsKey("rank") && add.ContainsKey("raise")) problems.Add($"{shown}: \"rank\" and \"raise\" cannot both be given.");
                foreach (var key in new[] { "rank", "minRank", "maxRank" })
                {
                    var rank = Text(add, key);
                    if (rank.Length > 0 && !IsRank(list, rank)) problems.Add($"{shown}: \"{rank}\" is not a rank of {list.Key} ({string.Join(", ", list.Ranks!.Select(r => r.Key))}).");
                }
            }
        }

        // A modifier's optIn (an "ends" has no condition to end here) and a dice amount on a target a pool roll reads.
        void ModifierProblems(string name, IDictionary<string, object> data)
        {
            var shown = string.IsNullOrWhiteSpace(name) ? entry.Name : name;
            foreach (var modifier in RowsOf(data, "modifiers"))
            {
                var optIn = SheetRuleEngine.ParseOptIn(modifier.TryGetValue("optIn", out var raw) ? raw : null);
                if (modifier.TryGetValue("optIn", out var rawOptIn) && rawOptIn is IDictionary<string, object> { } map && map.TryGetValue("spends", out var rawSpend)
                    && rawSpend is IDictionary<string, object> spendMap && !spendMap.ContainsKey("key"))
                {
                    problems.Add($"{shown}: an opt-in that spends needs the \"key\" of the pool.");
                }

                var amount = modifier.TryGetValue("amount", out var rawAmount) ? Convert.ToString(rawAmount, CultureInfo.InvariantCulture) : null;
                problems.AddRange(OptInChecks.Problems(system, shown, Text(modifier, "target"), amount, optIn, inCondition: false));
            }
        }

        // "Choose one of the following" inside a feature: options need a choose with an id (unique in the entry), a count, a known onChange and
        // changesOn naming only this system's rests or a moment the table triggers; and a choose without options asks the player for nothing.
        void OptionsProblems(string name, IDictionary<string, object> data, HashSet<string> used)
        {
            var shown = string.IsNullOrWhiteSpace(name) ? entry.Name : name;
            var options = RowsOf(data, "options").ToList();
            var hasChoose = data.TryGetValue("choose", out var chooseRaw) && chooseRaw != null;
            if (options.Count == 0)
            {
                if (hasChoose && data.ContainsKey("options")) problems.Add($"{shown}: \"options\" has no rows to choose between.");
                else if (hasChoose && chooseRaw is IDictionary<string, object>) problems.Add($"{shown}: \"choose\" needs \"options\", the rows to choose between.");
                return;
            }

            if (!hasChoose)
            {
                problems.Add($"{shown}: \"options\" needs \"choose\", with an \"id\" (and optionally \"count\", \"onChange\", \"changesOn\"), so the player is asked to pick.");
            }
            else
            {
                var map = chooseRaw as IDictionary<string, object>;
                var id = map != null ? Text(map, "id") : Convert.ToString(chooseRaw, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (id.Length == 0) problems.Add($"{shown}: \"choose\" needs an \"id\", unique in the entry (a short slug of the feature's name).");
                else if (!used.Add(id)) problems.Add($"{shown}: the choice id \"{id}\" is used more than once in this entry; each choice needs its own.");
                if (map != null)
                {
                    if (map.ContainsKey("count") && !(Number(map, "count") >= 1)) problems.Add($"{shown}: \"count\" must be 1 or more.");
                    var onChange = Text(map, "onChange");
                    if (onChange.Length > 0 && onChange.ToLowerInvariant() is not (RowChoose.Keep or RowChoose.Refill or RowChoose.Empty))
                    {
                        problems.Add($"{shown}: \"onChange\" must be keep, refill or empty, not \"{onChange}\".");
                    }

                    var moments = map.TryGetValue("changesOn", out var rawMoments) ? rawMoments : null;
                    foreach (var moment in moments is IEnumerable<object> listed ? listed.Select(m => Convert.ToString(m, CultureInfo.InvariantCulture) ?? string.Empty) : Array.Empty<string>())
                    {
                        if (!vocabulary.ChangeMoments.Contains(moment, StringComparer.OrdinalIgnoreCase))
                        {
                            problems.Add($"{shown}: \"changesOn\" names \"{moment}\", which is not one of this system's rests ({string.Join(", ", vocabulary.Rests)}), nor one of its other moments ({string.Join(", ", vocabulary.ChangeMoments.Except(vocabulary.Rests, StringComparer.OrdinalIgnoreCase))}).");
                        }
                    }
                }
            }

            // Each option is a row of its own: its choices are stored under this one's id, so they may repeat an id used elsewhere in the entry.
            foreach (var option in options)
            {
                Feature($"{shown}: {Text(option, "name")}", option, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    private static IEnumerable<IDictionary<string, object>> RowsOf(IDictionary<string, object> data, string key) =>
        (data.TryGetValue(key, out var raw) ? raw : null) switch
        {
            IEnumerable<object> many => many.OfType<IDictionary<string, object>>(),
            _ => Enumerable.Empty<IDictionary<string, object>>()
        };

    private static bool IsRank(GrantList list, string rank) =>
        list.Ranks!.Any(r => r.Key.Equals(rank, StringComparison.OrdinalIgnoreCase))
        || (int.TryParse(rank, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= list.Ranks!.Count)
        || rank.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<IDictionary<string, object>> AddsOf(IDictionary<string, object> data) => (data.TryGetValue("adds", out var raw) ? raw : null) switch
    {
        IDictionary<string, object> one => new[] { one },
        string => Enumerable.Empty<IDictionary<string, object>>(),
        IEnumerable<object> many => many.OfType<IDictionary<string, object>>(),
        _ => Enumerable.Empty<IDictionary<string, object>>()
    };

    private static string Text(IDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;

    private static double Number(IDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
