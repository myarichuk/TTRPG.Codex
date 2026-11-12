using System.Globalization;
using System.Text;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A feature's typed contribution to a number: <c>{ target, amount, type, when }</c>.</summary>
public sealed record FeatureModifier(string Target, string Amount, string? Type, string? When);

/// <summary>Entries a feature puts on a list from <see cref="Level"/> on (always, when null).</summary>
public sealed record FeatureAdd(string To, IReadOnlyList<string> Entries, int? Level);

/// <summary>A feature's counter of uses as its data states it.</summary>
public sealed record FeatureUses(string? Key, string Max, IReadOnlyList<string> RecoversOn);

/// <summary>A feature the sheet has now: a row of a class, subclass or species entry at or below the sheet's level, or a
/// picked entry that is one. <see cref="View"/> is the sheet as that feature's class sees it (its own class level).</summary>
public sealed record ActiveFeature(string Name, string? Description, ContentEntry Source, int? Level, IReadOnlyDictionary<string, object> View, string? LevelKey,
    IReadOnlyList<FeatureModifier> Modifiers, IReadOnlyList<FeatureAdd> Adds, FeatureUses? Uses)
{
    public bool Grants => Modifiers.Count > 0 || Adds.Count > 0 || Uses != null;
}

/// <summary>One feature's contribution that reached a target, for the sheet's working.</summary>
public sealed record GrantedModifier(string Target, ActiveFeature Feature, string Type, int Amount, bool Counted);

/// <summary>A feature's counter on this sheet: its keys, its worked-out maximum and the rests that refill it.</summary>
public sealed record FeatureCounter(ActiveFeature Feature, string Key, string MaxKey, int Max, IReadOnlyList<string> RecoversOn);

public sealed partial class SheetRuleEngine
{
    /// <summary>Sheet keys that remember what features put on the sheet, so it can be taken off again:
    /// <c>granted__darkvision</c> (how much was added to the field) and <c>granted__grantedSpells</c> (the entries added).</summary>
    public const string GrantedPrefix = "granted__";

    /// <summary>A feature counter's keys: <c>uses_{id}</c> (left), <c>uses_{id}_Max</c>, and <c>uses_{id}_Rest</c>, the rests
    /// that refill it, which the table reads without evaluating the sheet.</summary>
    public const string UsesPrefix = "uses_";
    public const string UsesMaxSuffix = "_Max";
    public const string UsesRestSuffix = "_Rest";

    /// <summary>Whether a sheet key is a feature counter's current value.</summary>
    public static bool IsUsesKey(string key) =>
        key.StartsWith(UsesPrefix, StringComparison.Ordinal) && !key.EndsWith(UsesMaxSuffix, StringComparison.Ordinal) && !key.EndsWith(UsesRestSuffix, StringComparison.Ordinal);

    /// <summary>The rests that refill the feature counter <paramref name="key"/>, as the sheet records them.</summary>
    public static IReadOnlyList<string> UsesRests(IReadOnlyDictionary<string, object> sheet, string key) =>
        (YamlValues.GetString(sheet, key + UsesRestSuffix) ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The features the sheet has now, in the order the system lists their sources.</summary>
    public IReadOnlyList<ActiveFeature> ActiveFeatures(IReadOnlyDictionary<string, object> sheet)
    {
        if (Rules is not { Features.Count: > 0 } rules) return Array.Empty<ActiveFeature>();

        var views = ClassViews(sheet, new List<ClassLevel>());
        var perClass = new HashSet<string>(StringComparer.Ordinal);
        if (rules.Multiclass is { } multiclass)
        {
            perClass.Add(multiclass.ClassField);
            if (multiclass.SubclassField != null) perClass.Add(multiclass.SubclassField);
        }

        var found = new List<ActiveFeature>();
        foreach (var source in rules.Features)
        {
            if (source.List != null)
            {
                var list = rules.Lists.FirstOrDefault(l => l.Key == source.List);
                if (list == null) continue;
                foreach (var raw in RawList(sheet, list.Key))
                {
                    if (resolver.Resolve(list.TargetEntityType, ParsePick(raw).Id) is { } entry) AddFeatures(found, source, entry, views[0]);
                }
            }
            else if (source.Field != null)
            {
                foreach (var view in perClass.Contains(source.Field) ? views : views.Take(1))
                {
                    if (ReferencedEntry(source.Field, view) is { } entry) AddFeatures(found, source, entry, view);
                }
            }
        }

        return found;
    }

    private static void AddFeatures(List<ActiveFeature> found, FeatureSource source, ContentEntry entry, IReadOnlyDictionary<string, object> view)
    {
        if (source.Property == null)
        {
            found.Add(Feature(entry.Name, entry.Description, entry, null, view, source.ByField, entry.Properties));
            return;
        }

        if (!entry.Properties.TryGetValue(source.Property, out var raw) || raw is not IEnumerable<object> rows) return;
        var reached = source.ByField == null ? null : ToDouble(view.GetValueOrDefault(source.ByField));
        foreach (var row in rows.OfType<IDictionary<string, object>>())
        {
            var name = Cell(row, "name")?.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var level = ToDouble(Cell(row, source.LevelColumn));
            if (source.ByField != null && level.HasValue && !(reached >= level)) continue;
            found.Add(Feature(name, Cell(row, "description")?.ToString(), entry, level.HasValue ? (int)level.Value : null, view, source.ByField, new Dictionary<string, object>(row)));
        }
    }

    private static ActiveFeature Feature(string name, string? description, ContentEntry entry, int? level, IReadOnlyDictionary<string, object> view, string? levelKey, IReadOnlyDictionary<string, object> data)
    {
        var modifiers = Maps(data.GetValueOrDefault("modifiers"))
            .Select(m => new FeatureModifier(Text(m, "target") ?? string.Empty, Text(m, "amount") ?? "0", Text(m, "type"), Text(m, "when")))
            .Where(m => m.Target.Length > 0)
            .ToList();
        var adds = Maps(data.GetValueOrDefault("adds"))
            .Select(a => new FeatureAdd(Text(a, "to") ?? string.Empty, Texts(Cell(a, "entries")), ToDouble(Cell(a, "level")) is { } at ? (int)at : null))
            .Where(a => a.To.Length > 0 && a.Entries.Count > 0)
            .ToList();
        var uses = data.GetValueOrDefault("uses") switch
        {
            IDictionary<string, object> map when Text(map, "max") is { } max => new FeatureUses(Text(map, "key"), max, Texts(Cell(map, "recoversOn"))),
            // "uses: 2" is a counter of two with nothing that refills it but the GM.
            { } plain when ToDouble(plain) is > 0 => new FeatureUses(null, Convert.ToString(plain, CultureInfo.InvariantCulture)!, Array.Empty<string>()),
            _ => null
        };
        return new ActiveFeature(name, description, entry, level, view, levelKey, modifiers, adds, uses);
    }

    private static IEnumerable<IDictionary<string, object>> Maps(object? value) => value switch
    {
        IDictionary<string, object> one => new[] { one },
        string => Enumerable.Empty<IDictionary<string, object>>(),
        IEnumerable<object> many => many.OfType<IDictionary<string, object>>(),
        _ => Enumerable.Empty<IDictionary<string, object>>()
    };

    private static string? Text(IDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value != null && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text.Trim() : null;

    private static IReadOnlyList<string> Texts(object? value) => value switch
    {
        null => Array.Empty<string>(),
        string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        IEnumerable<object> many => many.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty).Where(v => v.Length > 0).ToList(),
        _ => new[] { Convert.ToString(value, CultureInfo.InvariantCulture)! }
    };

    // ---- Where a modifier lands ----

    private enum TargetKind { Unknown, ArmorClass, HitPoints, Derived, Field }

    private TargetKind KindOf(string target)
    {
        if (Rules?.ArmorClass is { } armor && armor.Key == target) return TargetKind.ArmorClass;
        if (Rules?.HitPoints is { } hitPoints && hitPoints.Key == target) return TargetKind.HitPoints;
        if (Rules?.Derived.Any(d => d.Key == target) == true) return TargetKind.Derived;
        return system.ActorFields.Any(f => f.Key == target && f.Type == FieldType.Number) ? TargetKind.Field : TargetKind.Unknown;
    }

    /// <summary>A feature's amount (or its counter's maximum) worked out on the sheet as the feature's class sees it; null when
    /// its <c>when</c> does not hold or a formula cannot be worked out (reported once, as a warning, by <paramref name="warn"/>).</summary>
    private int? Amount(ActiveFeature feature, string formula, string? when, IReadOnlyDictionary<string, double>? derived, Action<string>? warn)
    {
        try
        {
            var values = FormulaContext(feature.View);
            foreach (var (key, value) in derived ?? new Dictionary<string, double>()) values[key] = value;
            if (!string.IsNullOrWhiteSpace(when) && EvaluateFormula(feature.View, values, $"({when}) ? 1 : 0") == 0) return null;
            return (int)EvaluateFormula(feature.View, values, formula);
        }
        catch (Exception)
        {
            warn?.Invoke(feature.Name);
            return null;
        }
    }

    private bool IsBestType(string? type) =>
        string.Equals(ModifierStacking.RuleOf(system.ModifierTypes, type ?? Untyped), ModifierStacking.Best, StringComparison.OrdinalIgnoreCase);

    /// <summary>What features add to <paramref name="start"/>: types the system stacks as <c>best</c> set a floor, the rest
    /// stack by their rules and add up.</summary>
    private int WithModifiers(int start, IEnumerable<(ActiveFeature Feature, string? Type, int Amount)> modifiers, string target, List<GrantedModifier>? working)
    {
        var all = modifiers.ToList();
        var floor = all.Where(m => IsBestType(m.Type)).ToList();
        var stacked = ModifierStacking.Stack(all.Where(m => !IsBestType(m.Type)).Select(m => (m.Type ?? Untyped, (double)m.Amount, m)), system.ModifierTypes);
        var total = start;
        var best = floor.Count == 0 ? (int?)null : floor.Max(m => m.Amount);
        if (best > total) total = best.Value;
        foreach (var m in floor) working?.Add(new GrantedModifier(target, m.Feature, m.Type!, m.Amount, m.Amount == best && best > start));
        foreach (var s in stacked)
        {
            if (s.Counted) total += (int)s.Amount;
            working?.Add(new GrantedModifier(target, s.Item.Feature, s.Type, (int)s.Amount, s.Counted));
        }

        return total;
    }

    // ---- In the evaluation: derived values, counters, and what armor class and hit points pick up ----

    private void EvaluateFeatures(IReadOnlyDictionary<string, object> sheet, SheetEvaluation result)
    {
        var features = ActiveFeatures(sheet);
        if (features.Count == 0) return;
        result.Features.AddRange(features);

        var derived = result.Derived.Where(d => IsIdentifier(d.Key) && YamlValues.ToNumber(d.Value).HasValue).ToDictionary(d => d.Key, d => YamlValues.ToNumber(d.Value)!.Value);
        var warned = new HashSet<string>(StringComparer.Ordinal);
        void Warn(string name)
        {
            if (warned.Add(name)) result.Warnings.Add(new RuleViolation("features", $"{name}: what this feature grants can't be worked out, so it is left out. The GM can check the entry.", "Actor.Rule.FeatureError", new object?[] { name }));
        }

        foreach (var group in features.SelectMany(f => f.Modifiers.Select(m => (Feature: f, Modifier: m))).GroupBy(x => x.Modifier.Target, StringComparer.Ordinal))
        {
            var kind = KindOf(group.Key);
            if (kind == TargetKind.Unknown)
            {
                foreach (var name in group.Select(x => x.Feature.Name).Distinct()) Warn(name);
                continue;
            }

            var amounts = group.Select(x => (x.Feature, x.Modifier.Type, Amount: Amount(x.Feature, x.Modifier.Amount, x.Modifier.When, derived, Warn)))
                .Where(x => x.Amount.HasValue)
                .Select(x => (x.Feature, x.Type, x.Amount!.Value))
                .ToList();
            if (amounts.Count == 0) continue;

            switch (kind)
            {
                case TargetKind.Derived:
                    if (YamlValues.ToNumber(result.Derived.GetValueOrDefault(group.Key)) is { } value)
                    {
                        var total = WithModifiers((int)value, amounts, group.Key, result.Granted);
                        result.Derived[group.Key] = (long)total;
                        derived[group.Key] = total;
                    }

                    break;
                case TargetKind.Field:
                    // Already on the sheet (ApplyGranted); listed here for the working only.
                    WithModifiers(0, amounts, group.Key, result.Granted);
                    break;
                default:
                    // Armor class and hit points take these in their own stages.
                    result.Pending[group.Key] = amounts;
                    break;
            }
        }

        foreach (var feature in features.Where(f => f.Uses != null))
        {
            if (Amount(feature, feature.Uses!.Max, null, derived, Warn) is not { } max || max <= 0) continue;
            var key = UsesPrefix + Slug(feature.Uses.Key ?? $"{feature.Source.Id}_{feature.Name}");
            if (result.Counters.Any(c => c.Key == key)) continue; // two features naming one counter share it
            result.Counters.Add(new FeatureCounter(feature, key, key + UsesMaxSuffix, max, feature.Uses.RecoversOn));
        }
    }

    private static string Slug(string text)
    {
        var slug = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '_') slug.Append('_');
        }

        return slug.ToString().Trim('_');
    }

    // ---- On the sheet: what features add to fields and lists, and their counters ----

    /// <summary>Takes off what features added to the sheet's Number fields the last time, leaving the player's own numbers:
    /// run before anything that sets those fields afresh (a newly chosen species), with <see cref="ApplyGranted"/> after.</summary>
    public void StripGranted(Dictionary<string, object> sheet)
    {
        foreach (var field in system.ActorFields.Where(f => f.Type == FieldType.Number))
        {
            if (YamlValues.GetInt(sheet, GrantedPrefix + field.Key) is not { } added) continue;
            if (YamlValues.GetInt(sheet, field.Key) is { } current) sheet[field.Key] = (long)(current - added);
            sheet.Remove(GrantedPrefix + field.Key);
        }
    }

    /// <summary>Puts on the sheet what its features grant outside the evaluation's own stages: amounts on Number fields
    /// (remembered, so the next run starts from the player's number), and entries on lists (remembered too, so they leave
    /// when the feature does, while an entry the player picked themself stays).</summary>
    public void ApplyGranted(Dictionary<string, object> sheet)
    {
        if (Rules == null) return;
        var features = Rules.Features.Count == 0 ? Array.Empty<ActiveFeature>() : ActiveFeatures(sheet);

        foreach (var group in features.SelectMany(f => f.Modifiers.Select(m => (Feature: f, Modifier: m))).GroupBy(x => x.Modifier.Target, StringComparer.Ordinal))
        {
            if (KindOf(group.Key) != TargetKind.Field) continue;
            var field = system.ActorFields.First(f => f.Key == group.Key);
            var start = YamlValues.GetInt(sheet, field.Key) ?? (int)(ToDouble(field.DefaultValue) ?? 0);
            var amounts = group.Select(x => (x.Feature, x.Modifier.Type, Amount: Amount(x.Feature, x.Modifier.Amount, x.Modifier.When, null, null)))
                .Where(x => x.Amount.HasValue)
                .Select(x => (x.Feature, x.Type, x.Amount!.Value));
            var total = WithModifiers(start, amounts, group.Key, null);
            if (total == start) continue;
            sheet[field.Key] = (long)total;
            sheet[GrantedPrefix + field.Key] = (long)(total - start);
        }

        var targets = features.SelectMany(f => f.Adds.Select(a => a.To))
            .Concat(sheet.Keys.Where(k => k.StartsWith(GrantedPrefix, StringComparison.Ordinal) && sheet[k] is not (long or int or double)).Select(k => k[GrantedPrefix.Length..]))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var to in targets)
        {
            var list = Rules.Lists.FirstOrDefault(l => l.Key == to);
            var granted = new List<string>();
            foreach (var feature in features)
            {
                foreach (var add in feature.Adds.Where(a => a.To == to))
                {
                    if (add.Level.HasValue && !((feature.LevelKey == null ? null : ToDouble(feature.View.GetValueOrDefault(feature.LevelKey))) >= add.Level)) continue;
                    granted.AddRange(add.Entries.Select(e => list == null ? e : EntryId(list.TargetEntityType, e)).OfType<string>());
                }
            }

            granted = granted.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var before = RawList(sheet, GrantedPrefix + to);
            var held = RawList(sheet, to);
            string IdOf(string raw) => list == null ? raw : ParsePick(raw).Id;
            // Off: what was added before and is granted no more. On: what is granted and not there yet.
            held.RemoveAll(raw => before.Contains(IdOf(raw), StringComparer.OrdinalIgnoreCase) && !granted.Contains(IdOf(raw), StringComparer.OrdinalIgnoreCase));
            var added = before.Where(id => granted.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var id in granted.Where(id => !held.Any(raw => IdOf(raw).Equals(id, StringComparison.OrdinalIgnoreCase))))
            {
                held.Add(id);
                added.Add(id);
            }

            if (held.Count > 0 || sheet.ContainsKey(to)) sheet[to] = held.Cast<object>().ToList();
            if (added.Count > 0) sheet[GrantedPrefix + to] = added.Cast<object>().ToList();
            else sheet.Remove(GrantedPrefix + to);
        }
    }

    // An entry a feature names by id, or by the name a writer knows it by.
    private string? EntryId(string type, string named) =>
        resolver.Resolve(type, named)?.Id
        ?? resolver.Candidates(type).FirstOrDefault(c => c.Id.Equals(named, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(named, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>Writes the features' counters to the sheet, as the slot pools are written: a counter that just appeared or grew
    /// starts with the new uses available, and one whose feature is gone leaves the sheet.</summary>
    public void ApplyCounters(Dictionary<string, object> sheet, SheetEvaluation evaluation)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var counter in evaluation.Counters)
        {
            keep.Add(counter.Key);
            keep.Add(counter.MaxKey);
            var previousMax = YamlValues.GetInt(sheet, counter.MaxKey);
            var current = YamlValues.GetInt(sheet, counter.Key);
            sheet[counter.MaxKey] = (long)counter.Max;
            if (current == null) current = counter.Max;
            else if (previousMax.HasValue && counter.Max > previousMax.Value) current += counter.Max - previousMax.Value;
            sheet[counter.Key] = (long)Math.Clamp(current.Value, 0, counter.Max);
            if (counter.RecoversOn.Count > 0)
            {
                sheet[counter.Key + UsesRestSuffix] = string.Join("|", counter.RecoversOn);
                keep.Add(counter.Key + UsesRestSuffix);
            }
        }

        foreach (var stale in sheet.Keys.Where(k => k.StartsWith(UsesPrefix, StringComparison.Ordinal) && !keep.Contains(k)).ToList()) sheet.Remove(stale);
    }
}
