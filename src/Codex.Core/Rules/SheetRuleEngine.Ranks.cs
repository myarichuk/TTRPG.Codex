using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>Where a rank a key holds came from: <see cref="Kind"/> is one of <see cref="RankSourceKinds"/>; <see cref="Name"/> the entry or
/// feature that gave it ("Sage", "Rogue: Expertise"); <see cref="Feature"/> the feature, when it was one.</summary>
public sealed record RankSource(int Rank, string Kind, string Name, ActiveFeature? Feature = null);

/// <summary>The kinds of <see cref="RankSource"/>.</summary>
public static class RankSourceKinds
{
    /// <summary>The background's list (skills).</summary>
    public const string Background = "background";

    /// <summary>The first class's list (saving throws).</summary>
    public const string Class = "class";

    /// <summary>A feature's fixed <c>adds</c>.</summary>
    public const string Feature = "feature";

    /// <summary>A key the player picked for a choice grant.</summary>
    public const string Choice = "choice";

    /// <summary>A rank step up (<c>raise</c>), fixed or chosen.</summary>
    public const string Raise = "raise";
}

/// <summary>A key a choice offers, with the name to show.</summary>
public sealed record ChoiceOption(string Key, string Label, string? Description = null);

/// <summary>What a row choice adds to a <see cref="PendingChoice"/>: what happens to counters when the pick changes (<see cref="RowChoose.Keep"/>,
/// <see cref="RowChoose.Refill"/>, <see cref="RowChoose.Empty"/>) and the moments the pick is usually changed at (a rest's name, or initiative, dawn).</summary>
public sealed record RowChoiceInfo(string OnChange, IReadOnlyList<string> ChangesOn);

/// <summary>
/// A choice a feature leaves to the player (<see cref="FeatureChoice"/>), as the sheet stands. <see cref="Key"/> is the sheet key the picks are
/// stored under (<see cref="SheetRuleEngine.ChoiceKey"/>), a list of keys of <see cref="Target"/>; <see cref="Made"/> the picks that count now;
/// <see cref="Options"/> the keys the player may pick (the picks made are among them); <see cref="Rank"/> the rank the picks are put at, null for a
/// <see cref="Raise"/>. <see cref="Adopted"/>: nothing is stored yet, but the player's own ticks already cover the choice, so <see cref="Made"/> holds
/// them and confirming means writing them (<see cref="SheetRuleEngine.SetChoice"/>).
/// </summary>
public sealed record PendingChoice(string Key, string Label, string Source, int Count, IReadOnlyList<string> Made, IReadOnlyList<ChoiceOption> Options,
    int? Rank, string Target, bool Raise, bool Adopted, ActiveFeature Feature, int? MinRank = null, int? MaxRank = null, RowChoiceInfo? Row = null)
{
    /// <summary>The choice picks among rows (an entry's rows, or a feature's options), not keys of a ranked list. <see cref="Target"/> is then
    /// <see cref="SheetRuleEngine.RowsTarget"/>, and the picks are the rows' names as the entry writes them.</summary>
    public bool IsRow => Row != null;

    /// <summary>How many more picks the choice asks for.</summary>
    public int Remaining => Math.Max(Count - Made.Count, 0);

    /// <summary>The picks as they would stand with <paramref name="key"/> ticked or unticked. A tick beyond <see cref="Count"/> is not taken.</summary>
    public IReadOnlyList<string> Toggled(string key, bool on)
    {
        // Picking among rows when only one is wanted replaces the pick (a radio), so changing it needs no unpicking first.
        if (on && IsRow && Count == 1) return Options.Any(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ? new[] { key } : Made;
        var picks = Made.Where(m => !m.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on && picks.Count < Count && Options.Any(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) picks.Add(key);
        return picks;
    }

    /// <summary>The picks made, by the names the sheet shows (the key when an option has no name).</summary>
    public IReadOnlyList<string> MadeLabels =>
        Made.Select(key => Options.FirstOrDefault(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Label ?? key).ToList();
}

public sealed partial class SheetRuleEngine
{
    // ---- Ranked lists: skills and saving throws ----

    /// <summary>The target a grant names for the skills (<c>adds: [{ to: skills, ... }]</c>).</summary>
    public const string SkillsTarget = "skills";

    /// <summary>The target a grant names for the saving throws.</summary>
    public const string SavesTarget = "saves";

    /// <summary>The prefix of the sheet keys that hold a choice's picks: <c>choice__{entryId}__{choiceId}</c>.</summary>
    public const string ChoicePrefix = "choice__";

    /// <summary>The key of the warning for a choice the player has not made (or made in part).</summary>
    public const string ChoicePendingKey = "Actor.Rule.ChoicePending";

    /// <summary>The key of the warning for a choice the player's own ticks already cover but who has not confirmed it.</summary>
    public const string ChoiceConfirmKey = "Actor.Rule.ChoiceConfirm";

    /// <summary>The key of the violation for a pick that no longer fits its choice, or one too many.</summary>
    public const string ChoiceStaleKey = "Actor.Rule.ChoiceStale";

    /// <summary>Whether a warning or violation says a choice is open (made in part, or unconfirmed): never a reason to refuse a save.</summary>
    public static bool IsChoiceNotice(RuleViolation notice) => notice.Key is ChoicePendingKey or ChoiceConfirmKey or ChoiceRowPendingKey;

    /// <summary>The sheet key holding the picks of choice <paramref name="choiceId"/> of entry <paramref name="entryId"/>. The second copy
    /// of a repeatable pick (a feat taken twice) has its own: <c>choice__{entryId}#2__{choiceId}</c>.</summary>
    public static string ChoiceKey(string entryId, int copy, string choiceId) =>
        ChoicePrefix + entryId + (copy > 1 ? "#" + copy.ToString(CultureInfo.InvariantCulture) : string.Empty) + "__" + choiceId;

    /// <summary>The entry, copy and choice id a <see cref="ChoiceKey"/> was made from; null when <paramref name="key"/> is not one.</summary>
    public static (string EntryId, int Copy, string ChoiceId)? ParseChoiceKey(string key)
    {
        if (!key.StartsWith(ChoicePrefix, StringComparison.Ordinal)) return null;
        var rest = key[ChoicePrefix.Length..];
        var split = rest.IndexOf("__", StringComparison.Ordinal);
        if (split <= 0 || split + 2 >= rest.Length) return null;
        var entry = rest[..split];
        var copy = 1;
        var hash = entry.LastIndexOf('#');
        if (hash > 0 && int.TryParse(entry[(hash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 1)
        {
            copy = n;
            entry = entry[..hash];
        }

        return (entry, copy, rest[(split + 2)..]);
    }

    /// <summary>Writes the player's picks for a choice to the sheet (<see cref="PendingChoice.Key"/>): the keys as a list, or nothing when
    /// there are none. This is how a pick is made, changed or confirmed.</summary>
    public static void SetChoice(Dictionary<string, object> sheet, string choiceKey, IEnumerable<string> picks)
    {
        var list = picks.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToList();
        if (list.Count == 0) sheet.Remove(choiceKey);
        else sheet[choiceKey] = list;
    }

    /// <summary>Sets the rank the player gives a key themselves: it goes in the sheet list of that rank only, and comes out of the lists of the
    /// others (0 takes it out of all). <paramref name="names"/> are the ways the sheet may hold it (its key, its label).</summary>
    public static void SetOwnRank(Dictionary<string, object> sheet, IReadOnlyList<RankDefinition> ranks, int rank, params string[] names)
    {
        for (var i = 0; i < ranks.Count; i++)
        {
            var list = RawList(sheet, ranks[i].SheetKey);
            list.RemoveAll(n => names.Contains(n, StringComparer.OrdinalIgnoreCase));
            if (i + 1 == rank && names.Length > 0) list.Add(names[0]);
            if (list.Count == 0) sheet.Remove(ranks[i].SheetKey);
            else sheet[ranks[i].SheetKey] = list.Cast<object>().ToList();
        }
    }

    /// <summary>The ranked list and rank a grant's <c>to</c> names: <c>skills</c> or <c>saves</c> (no rank), or a rank's own sheet list
    /// (<c>skillExpertise</c>: that rank). Null when <paramref name="to"/> is no ranked list.</summary>
    private (string Target, int? Rank)? RankTargetOf(string to)
    {
        if (Rules?.Skills is not { } skills) return null;
        if (to.Equals(SkillsTarget, StringComparison.OrdinalIgnoreCase)) return (SkillsTarget, null);
        if (to.Equals(SavesTarget, StringComparison.OrdinalIgnoreCase)) return (SavesTarget, null);
        var skill = skills.SkillRanks().ToList().FindIndex(r => r.SheetKey.Equals(to, StringComparison.OrdinalIgnoreCase));
        if (skill >= 0) return (SkillsTarget, skill + 1);
        var save = skills.SaveRanks().ToList().FindIndex(r => r.SheetKey.Equals(to, StringComparison.OrdinalIgnoreCase));
        return save >= 0 ? (SavesTarget, save + 1) : null;
    }

    /// <summary>One-time: the entries an earlier version recorded in <c>granted__{rank list}</c> when a feature added them to a rank list on
    /// the sheet are taken off the list again, and the record dropped. Grants to ranked lists live only in the evaluation now; a skill the
    /// player ticked themself was never recorded, so it stays.</summary>
    private void CleanUpGrantedRanks(Dictionary<string, object> sheet)
    {
        if (Rules?.Skills is not { } skills) return;
        foreach (var rank in skills.SkillRanks().Concat(skills.SaveRanks()))
        {
            var marker = GrantedPrefix + rank.SheetKey;
            if (!sheet.TryGetValue(marker, out var recorded)) continue;
            if (recorded is not (long or int or double))
            {
                var added = RawList(sheet, marker);
                var held = RawList(sheet, rank.SheetKey);
                if (held.RemoveAll(raw => added.Contains(raw, StringComparer.OrdinalIgnoreCase)) > 0)
                {
                    if (held.Count > 0) sheet[rank.SheetKey] = held.Cast<object>().ToList();
                    else sheet.Remove(rank.SheetKey);
                }
            }

            sheet.Remove(marker);
        }
    }

    /// <summary>Takes off the sheet the picks of choices whose entry is no longer on it (a class, a feat taken off), and those of a copy of
    /// a repeatable pick that is gone. A choice of a feature that merely does not apply now (a level lost) is kept, for if it comes back.
    /// Run on save, not on every edit (<see cref="Recompute"/> leaves them alone), so taking a feat off and putting it back while editing loses nothing.</summary>
    public void PruneChoices(Dictionary<string, object> sheet)
    {
        if (Rules is not { Features.Count: > 0 } rules) return;
        var stored = sheet.Keys.Where(k => k.StartsWith(ChoicePrefix, StringComparison.Ordinal)).ToList();
        if (stored.Count == 0) return;

        // How many times each entry that carries features is on the sheet, by the id the sheet stores and the id it resolves to.
        var held = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Count(string type, string raw)
        {
            held[raw] = held.GetValueOrDefault(raw) + 1;
            if (resolver.Resolve(type, raw) is { } entry && !entry.Id.Equals(raw, StringComparison.OrdinalIgnoreCase)) held[entry.Id] = held.GetValueOrDefault(entry.Id) + 1;
        }

        var views = ClassViews(sheet, new List<ClassLevel>());
        var perClass = new HashSet<string>(StringComparer.Ordinal);
        if (rules.Multiclass is { } multiclass)
        {
            perClass.Add(multiclass.ClassField);
            if (multiclass.SubclassField != null) perClass.Add(multiclass.SubclassField);
        }

        foreach (var source in rules.Features)
        {
            if (source.List != null)
            {
                if (rules.Lists.FirstOrDefault(l => l.Key == source.List) is not { } list) continue;
                foreach (var raw in RawList(sheet, list.Key)) Count(list.TargetEntityType, ParsePick(raw).Id);
            }
            else if (source.Field != null && system.ActorFields.FirstOrDefault(f => f.Key == source.Field)?.TargetEntityType is { } type)
            {
                foreach (var view in perClass.Contains(source.Field) ? views : views.Take(1))
                {
                    if (YamlValues.GetString(view, source.Field) is { Length: > 0 } id) Count(type, id);
                }
            }
        }

        foreach (var key in stored)
        {
            if (ParseChoiceKey(key) is not { } parsed) continue;
            if (held.GetValueOrDefault(parsed.EntryId) < parsed.Copy) sheet.Remove(key);
        }
    }

    // One ranked list while the engine works on it: the keys it ranks, the ranks the player set (Own), and the ranks features, the
    // background, the class and choices gave (Granted), with where each came from.
    private sealed class RankedList(string target, string label, IReadOnlyList<RankDefinition> ranks, IReadOnlyList<RankedKey> keys)
    {
        public string Target { get; } = target;
        public string Label { get; } = label;
        public IReadOnlyList<RankDefinition> Ranks { get; } = ranks;
        public IReadOnlyList<RankedKey> Keys { get; } = keys;
        public Dictionary<string, int> Own { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Granted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<RankSource>> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The keys the player ticked at the first rank.</summary>
        public HashSet<string> FirstRankTicks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Top => Ranks.Count;

        /// <summary>The rank a key holds at this point: the player's or a source's, whichever is higher.</summary>
        public int Now(string key) => Math.Max(Own.GetValueOrDefault(key), Granted.GetValueOrDefault(key));

        public RankedKey? Match(string name) => Keys.FirstOrDefault(k => k.Names.Contains(name, StringComparer.OrdinalIgnoreCase));

        /// <summary>The keys a source gives a rank of 1 or more without needing the player's own ranks: the background, fixed ranks, choices with
        /// no <c>minRank</c>. Only these excuse a tick from the class's skill count; an expertise pick needs the proficiency it sits on.</summary>
        public HashSet<string> Independent { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gives <paramref name="key"/> at least <paramref name="rank"/>, noting where from. <paramref name="independent"/>: the
        /// source holds whatever the player has ticked.</summary>
        public void Give(string key, int rank, string kind, string name, ActiveFeature? feature, bool independent = false)
        {
            if (independent && rank >= 1) Independent.Add(key);
            if (rank > Granted.GetValueOrDefault(key)) Granted[key] = rank;
            if (!Sources.TryGetValue(key, out var sources)) Sources[key] = sources = new List<RankSource>();
            sources.Add(new RankSource(rank, kind, name, feature));
        }

        /// <summary>A rank step up from where the key stands, capped at the top.</summary>
        public void Raise(string key, int steps, string name, ActiveFeature? feature)
        {
            var rank = Math.Min(Top, Now(key) + steps);
            Give(key, rank, RankSourceKinds.Raise, name, feature, independent: Now(key) == 0); // a raise from nothing is a rank of its own
        }
    }

    private sealed record RankedKey(string Key, string Label, string? Attribute, IReadOnlyList<string> Names);

    private sealed record ChoiceInstance(ActiveFeature Feature, FeatureAdd Add, FeatureChoice Choice, string Key);

    private IReadOnlyList<RankedList> BuildRankedLists(SkillRules rules)
    {
        var skills = new RankedList(SkillsTarget, rules.Label, rules.SkillRanks(),
            rules.List.Select(s => new RankedKey(s.Key, s.Label, s.Attribute, new[] { s.Key, s.Label, s.CanonicalLabel ?? s.Label })).ToList());
        var saves = new RankedList(SavesTarget, "Saving throws", rules.SaveRanks(),
            system.Attributes.Select(a => new RankedKey(a.Key, a.Label, a.Key,
                new[] { a.Key, a.Abbreviation, a.Label, a.CanonicalAbbreviation, a.CanonicalLabel }.Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList())).ToList());
        return new[] { skills, saves };
    }

    /// <summary>A rank named by key or number: 0 is none, 1 the first rank listed. <paramref name="fallback"/> when nothing is named; null
    /// when the name is no rank of the list.</summary>
    private static int? ResolveRank(RankedList list, string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return number <= list.Top ? number : null;
        var index = list.Ranks.ToList().FindIndex(r => r.Key.Equals(text, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index + 1 : text.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0 : null;
    }

    private void WarnFeature(SheetEvaluation result, HashSet<string> warned, string name)
    {
        if (!warned.Add(name)) return;
        result.Warnings.Add(new RuleViolation("features", $"{name}: what this feature grants can't be worked out, so it is left out. The GM can check the entry.", "Actor.Rule.FeatureError", new object?[] { name }));
    }

    /// <summary>
    /// The skills' and the saving throws' ranks, in one pass per list (no loop): the player's own ranks, then fixed sources (the background, the
    /// class's saves, fixed <c>adds</c>), then the choices that put keys at a rank (lowest <c>minRank</c> first, each checked against the ranks so
    /// far without its own picks, so Skilled's picks can feed Expertise), then <c>raise</c> grants, fixed and chosen. The highest rank wins; ranks
    /// are not modifiers, so <c>sheetRules.stacking</c> does not touch them. Fills <see cref="SheetEvaluation.Skills"/>,
    /// <see cref="SheetEvaluation.SavingThrows"/> and the choice lists; returns the skills' list for the checks that follow.
    /// </summary>
    private RankedList EvaluateRanks(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result, SkillRules rules)
    {
        var lists = BuildRankedLists(rules);
        var warned = new HashSet<string>(StringComparer.Ordinal);

        foreach (var list in lists)
        {
            // The player's own ranks: the highest sheet list that names a key. A name that is no key of the list is said once.
            var unknown = new List<(string Name, string SheetKey)>();
            for (var i = 0; i < list.Ranks.Count; i++)
            {
                foreach (var name in RawList(sheet, list.Ranks[i].SheetKey))
                {
                    if (list.Match(name) is not { } key)
                    {
                        unknown.Add((name, list.Ranks[i].SheetKey));
                        continue;
                    }

                    if (i + 1 > list.Own.GetValueOrDefault(key.Key)) list.Own[key.Key] = i + 1;
                    if (i == 0) list.FirstRankTicks.Add(key.Key);
                }
            }

            foreach (var (name, sheetKey) in unknown.DistinctBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Warnings.Add(new RuleViolation(sheetKey, $"{list.Label}: \"{name}\" is not a {(list.Target == SkillsTarget ? "skill" : "saving throw")} this system has."));
            }
        }

        var skills = lists[0];
        var saves = lists[1];

        // Fixed sources: the background gives skills, the first class gives saving throws, both at the first rank.
        if (ReferencedEntry(rules.BackgroundField, sheet) is { } background)
        {
            foreach (var name in Names(background.Properties.GetValueOrDefault(rules.BackgroundProperty)))
            {
                if (skills.Match(name) is { } key) skills.Give(key.Key, 1, RankSourceKinds.Background, background.Name, null, independent: true);
            }
        }

        if (FirstClass(sheet, result, rules.ClassField) is { } firstClass)
        {
            foreach (var attribute in AttributesNamedIn(firstClass.Properties.GetValueOrDefault(rules.SavingThrowsProperty)))
            {
                if (saves.Match(attribute) is { } key) saves.Give(key.Key, 1, RankSourceKinds.Class, firstClass.Name, null, independent: true);
            }
        }

        // Grants: fixed ranks now, raises and choices in their turn below.
        var fixedRanks = new List<(RankedList List, ActiveFeature Feature, FeatureAdd Add, int Rank)>();
        var fixedRaises = new List<(RankedList List, ActiveFeature Feature, FeatureAdd Add)>();
        var choices = new List<(RankedList List, ChoiceInstance Choice)>();
        // A row choice of the entry with the same id would share its picks: the keys of the row choices are taken already.
        var keysSeen = result.PendingChoices.Concat(result.MadeChoices).Where(c => c.IsRow).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var feature in result.Features)
        {
            foreach (var add in feature.Adds)
            {
                if (RankTargetOf(add.To) is not { } target)
                {
                    if (add.Choice != null) WarnFeature(result, warned, feature.Name); // a choice into a list that is not ranked is not supported
                    continue;
                }

                if (!AddApplies(feature, add)) continue;
                var list = target.Target == SkillsTarget ? skills : saves;
                if (add.Raise != 0 && add.Rank != null)
                {
                    WarnFeature(result, warned, feature.Name); // rank and raise exclude each other
                    continue;
                }

                if (add.Choice is { } choice)
                {
                    var key = ChoiceKey(feature.Source.Id, feature.Copy, feature.ScopedChoiceId(choice.Id));
                    if (!keysSeen.Add(key))
                    {
                        WarnFeature(result, warned, feature.Name); // two choices of one entry with one id would share their picks
                        continue;
                    }

                    choices.Add((list, new ChoiceInstance(feature, add, choice, key)));
                }
                else if (add.Raise > 0)
                {
                    fixedRaises.Add((list, feature, add));
                }
                else if (ResolveRank(list, add.Rank, target.Rank ?? 1) is { } rank and > 0)
                {
                    fixedRanks.Add((list, feature, add, rank));
                }
                else
                {
                    WarnFeature(result, warned, feature.Name); // a rank the list does not have
                }
            }
        }

        string SourceName(ActiveFeature f) => f.Name.Equals(f.Source.Name, StringComparison.OrdinalIgnoreCase) ? f.Name : $"{f.Source.Name}: {f.Name}";

        foreach (var (list, feature, add, rank) in fixedRanks)
        {
            foreach (var name in add.Entries)
            {
                if (list.Match(name) is { } key) list.Give(key.Key, rank, RankSourceKinds.Feature, SourceName(feature), feature, independent: true);
                else WarnFeature(result, warned, feature.Name);
            }
        }

        foreach (var list in lists)
        {
            // Choices that set a rank, lowest minRank first; the sort is stable, so equals keep the order of the features.
            var ordered = choices.Where(c => c.List == list && c.Choice.Add.Raise == 0)
                .OrderBy(c => ResolveRank(list, c.Choice.Choice.MinRank, 0) ?? 0).Select(c => c.Choice);
            foreach (var choice in ordered) ApplyChoice(list, choice, sheet, result, warned, SourceName(choice.Feature));

            foreach (var (_, feature, add) in fixedRaises.Where(r => r.List == list))
            {
                foreach (var name in add.Entries.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (list.Match(name) is { } key) list.Raise(key.Key, add.Raise, SourceName(feature), feature);
                    else WarnFeature(result, warned, feature.Name);
                }
            }

            var raises = choices.Where(c => c.List == list && c.Choice.Add.Raise > 0)
                .OrderBy(c => ResolveRank(list, c.Choice.Choice.MinRank, 0) ?? 0).Select(c => c.Choice);
            foreach (var choice in raises) ApplyChoice(list, choice, sheet, result, warned, SourceName(choice.Feature));
        }

        // Values: what each rank is worth, worked out once and only if a key holds it.
        var values = new Dictionary<(string Target, int Rank), int>();
        int ValueOf(RankedList list, int rank)
        {
            if (rank <= 0) return 0;
            if (values.TryGetValue((list.Target, rank), out var known)) return known;
            var definition = list.Ranks[rank - 1];
            var worked = rank;
            if (!string.IsNullOrWhiteSpace(definition.Value))
            {
                try
                {
                    var numbers = FormulaContext(sheet);
                    foreach (var (name, number) in DerivedNumbers(result)) numbers[name] = number;
                    worked = (int)EvaluateFormula(sheet, numbers, definition.Value);
                }
                catch (Exception)
                {
                    result.Warnings.Add(FormulaError(definition.Key, string.IsNullOrWhiteSpace(definition.Label) ? definition.Key : definition.Label));
                    worked = 0;
                }
            }

            return values[(list.Target, rank)] = worked;
        }

        SkillScore ScoreOf(RankedList list, RankedKey key)
        {
            var rank = list.Now(key.Key);
            var label = rank > 0 ? (string.IsNullOrWhiteSpace(list.Ranks[rank - 1].Label) ? list.Ranks[rank - 1].Key : list.Ranks[rank - 1].Label) : null;
            var rankBonus = ValueOf(list, rank);
            return new SkillScore(key.Key, key.Label, key.Attribute ?? string.Empty, rank >= 1, rank >= 2,
                ModifierOf(sheet, result, key.Attribute ?? string.Empty) + rankBonus, rank, label,
                list.Sources.TryGetValue(key.Key, out var sources) ? sources : Array.Empty<RankSource>(), list.Own.GetValueOrDefault(key.Key), rankBonus);
        }

        foreach (var key in skills.Keys) result.Skills.Add(ScoreOf(skills, key));
        foreach (var key in saves.Keys) result.SavingThrows.Add(ScoreOf(saves, key));

        // What each rank is worth now, held or not, so the level-up can say how the numbers move.
        foreach (var list in lists)
        {
            for (var i = 0; i < list.Ranks.Count; i++)
            {
                var definition = list.Ranks[i];
                result.RankValues.Add(new RankValue(list.Target, i + 1, definition.Key, string.IsNullOrWhiteSpace(definition.Label) ? definition.Key : definition.Label, ValueOf(list, i + 1)));
            }
        }

        return skills;
    }

    /// <summary>One choice, in its turn: which of the stored picks still fit (the rest are violations: choose again), which keys the player
    /// may pick now, the ranks the valid picks give, and whether the choice is open. A choice with nothing stored adopts the player's own ticks
    /// when they already cover it.</summary>
    private void ApplyChoice(RankedList list, ChoiceInstance instance, IReadOnlyDictionary<string, object> sheet, SheetEvaluation result, HashSet<string> warned, string sourceName)
    {
        var (feature, add, choice, storeKey) = instance;
        var raise = add.Raise > 0;
        var rank = raise ? (int?)null : ResolveRank(list, add.Rank, 1);
        var min = ResolveRank(list, choice.MinRank, 0);
        var max = ResolveRank(list, choice.MaxRank, list.Top);
        if (min == null || max == null || (rank == null && !raise) || rank == 0)
        {
            WarnFeature(result, warned, feature.Name);
            return;
        }

        var from = choice.From?.Select(n => list.Match(n)?.Key).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Whether a key fits, by the ranks so far: this choice's own picks are not in them yet, so a pick never props up itself.
        bool Fits(RankedKey key) =>
            (from == null || from.Contains(key.Key)) && FilterAllows(key, choice)
            && list.Now(key.Key) >= min.Value && list.Now(key.Key) <= max.Value && (!raise || list.Now(key.Key) < list.Top);

        var offered = list.Keys.Where(k => Fits(k) && list.Now(k.Key) < (raise ? list.Top : rank!.Value)).ToList();

        var stored = RawList(sheet, storeKey);
        var valid = new List<RankedKey>();
        var stale = new List<string>();
        foreach (var name in stored)
        {
            if (list.Match(name) is { } key && Fits(key) && valid.All(v => v.Key != key.Key) && valid.Count < choice.Choose) valid.Add(key);
            else stale.Add(list.Match(name)?.Label ?? name);
        }

        var adopted = false;
        // Only a choice that builds on a rank the key already has (expertise on a proficiency) adopts. A plain "choose N" at the
        // first rank would take the player's class-skill ticks as its picks and hide a real over-pick, so it stays open.
        if (stored.Count == 0 && !raise && choice.MinRank != null)
        {
            // The player's own ticks, made before the choice existed, already put enough eligible keys at its rank: count them as the picks.
            var covered = list.Keys.Where(k => Fits(k) && list.Own.GetValueOrDefault(k.Key) >= rank!.Value && list.Granted.GetValueOrDefault(k.Key) < rank.Value).ToList();
            if (covered.Count >= choice.Choose)
            {
                valid = covered.Take(choice.Choose).ToList();
                adopted = true;
            }
        }

        foreach (var name in stale)
        {
            result.Violations.Add(new RuleViolation(storeKey, $"{feature.Name} ({feature.Source.Name}): \"{name}\" no longer fits this choice. Choose again.", ChoiceStaleKey,
                new object?[] { feature.Name, feature.Source, name }));
        }

        foreach (var key in valid)
        {
            if (raise) list.Raise(key.Key, add.Raise, sourceName, feature);
            else list.Give(key.Key, rank!.Value, RankSourceKinds.Choice, sourceName, feature, independent: min.Value == 0);
        }

        var open = offered.Count(k => valid.All(v => v.Key != k.Key));
        var remaining = choice.Choose - valid.Count;
        var options = list.Keys.Where(k => offered.Contains(k) || valid.Contains(k)).Select(k => new ChoiceOption(k.Key, k.Label)).ToList();
        var pending = new PendingChoice(storeKey, feature.Name, feature.Source.Name, choice.Choose, valid.Select(v => v.Key).ToList(), options,
            raise ? null : rank, list.Target, raise, adopted, feature, choice.MinRank == null ? null : min, choice.MaxRank == null ? null : max);

        if (adopted)
        {
            result.PendingChoices.Add(pending);
            result.Warnings.Add(new RuleViolation(storeKey, $"{feature.Name} ({feature.Source.Name}): your {valid.Count} {list.Label.ToLowerInvariant()} already match this choice. Confirm them.",
                ChoiceConfirmKey, new object?[] { feature.Name, feature.Source, valid.Count, list.Label }));
        }
        else if (remaining > 0 && open > 0)
        {
            result.PendingChoices.Add(pending);
            result.Warnings.Add(new RuleViolation(storeKey, $"{feature.Name} ({feature.Source.Name}): choose {Math.Min(remaining, open)} more {list.Label.ToLowerInvariant()}.",
                ChoicePendingKey, new object?[] { feature.Name, feature.Source, Math.Min(remaining, open), list.Label }));
        }
        else if (valid.Count > 0)
        {
            result.MadeChoices.Add(pending);
        }
    }

    /// <summary>Whether a key passes a choice's filter on the key's own fields (<c>from: { attribute: [str, dex] }</c>).</summary>
    private bool FilterAllows(RankedKey key, FeatureChoice choice)
    {
        if (choice.Filter == null) return true;
        foreach (var (property, values) in choice.Filter)
        {
            var own = property.ToLowerInvariant() switch { "attribute" => key.Attribute, "key" => key.Key, "label" => key.Label, _ => null };
            if (own == null) return false;
            var named = property.Equals("attribute", StringComparison.OrdinalIgnoreCase) ? AttributesNamedIn(values.Cast<object>().ToList()) : values;
            if (!named.Contains(own, StringComparer.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
