using System.Collections;
using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One modifier that reached a target, and whether it counted (a lower bonus of a type that does not stack does not).</summary>
/// <summary>A track as it stands on one sheet: its definition, its bounds and declared start worked out (null where there are
/// none or they cannot be worked out), and for slots in levels, the levels.</summary>
public sealed record SheetTrack(string Key, string? Label, TrackDefinition Definition, double? Min, double? Max, double? Start, IReadOnlyList<SheetSlotLevel> Levels)
{
    public bool IsSlots => Definition.Display.Equals("slots", StringComparison.OrdinalIgnoreCase) || Definition.Holds != null;
}

/// <summary>One level of a slot track in levels: how many slots, and the condition anything in it brings.</summary>
public sealed record SheetSlotLevel(int Level, int Slots, string? Condition);

/// <summary>A row of a list or keyed list as a layout shows it: the entry's id and label, its group, its place in the sheet's raw
/// list (picks only), and a cell per column asked for. A cell that cannot be worked out is null.</summary>
public sealed record SheetRow(string Id, string Label, string? Group, int? Index, IReadOnlyDictionary<string, object?> Cells);

public sealed record AppliedModifier(string Type, double Amount, string? Source, bool Counted);

/// <summary>What <see cref="SheetModel"/> works out for a sheet.</summary>
public sealed class SheetModelResult
{
    /// <summary>Every value by key (a double, or a string for a choice), keyed-list entries as <c>skills.climb</c>.</summary>
    public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

    public List<RuleViolation> Violations { get; } = new();

    /// <summary>The conditions that hold: the sheet's own, and those its tracks' thresholds and bands bring.</summary>
    public List<string> Conditions { get; } = new();

    /// <summary>Formulas that could not be worked out: the GM's to fix, never the player's.</summary>
    public List<RuleViolation> Warnings { get; } = new();

    /// <summary>For each value with modifiers, every modifier that reached it, for the working breakdown.</summary>
    public Dictionary<string, IReadOnlyList<AppliedModifier>> Modifiers { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A sheet as the primitives see it (overhaul section 3): the values, lists and keyed lists of its sheet kind, worked out
/// lazily. A value is computed the first time something reads it, so the order the YAML lists them in does not matter, and
/// a value that ends up reading itself is reported as a formula problem, not a stack overflow.
/// <para>
/// Modifiers come from the sheet kind's own list, from picks whose entries carry them (<c>modifiersFrom</c>) and from the
/// conditions on the sheet (<c>conditionEffects</c>). On one target they combine by type: each type's bonuses by its
/// stacking rule (<c>sum</c>, <c>highest</c>, <c>lowest</c>; <see cref="ModifierTypesDefinition"/>), penalties by
/// <see cref="ModifierTypesDefinition.Penalties"/>.
/// </para>
/// </summary>
public sealed class SheetModel
{
    [ThreadStatic] private static SheetModel? _current;

    /// <summary>The model whose formula is running on this thread, for the built-in functions that read the sheet.</summary>
    internal static SheetModel Need() => _current ?? throw new FormulaException("no sheet is being worked out");

    public const string ConditionsKey = "conditions";
    public const string MaxSuffix = "_Max";
    public const string KindKey = "kind";

    private readonly SystemDefinition _system;
    private readonly IEntryResolver _resolver;
    private readonly IReadOnlyDictionary<string, object> _sheet;
    private readonly SheetFormulas _formulas;
    private readonly bool _checkPrerequisites;

    /// <summary>The inputs of the roll being made, as <c>input.</c> reads them; empty on the sheet.</summary>
    private readonly FormulaItem _input;
    private readonly Dictionary<string, ValueDefinition> _valueDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormulaList> _lists = new(StringComparer.Ordinal);

    /// <summary>For each pick list, where each item is in the sheet's raw list: a raw element that is neither a map nor an id is
    /// skipped, so the two need not line up.</summary>
    private readonly Dictionary<string, List<int>> _rawIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _working = new(StringComparer.Ordinal);
    private readonly List<string> _conditions;
    private List<Modifier>? _modifiers;
    private readonly SheetModelResult _result = new();

    /// <summary>The sheet kind in force: the sheet's <c>kind</c>, or the system's first.</summary>
    public SheetKindDefinition? Kind { get; }

    private sealed record Modifier(string Target, string Amount, string Type, string? Source, string? When, FormulaItem? Item);

    public SheetModel(SystemDefinition system, IEntryResolver resolver, IReadOnlyDictionary<string, object> sheet)
        : this(system, resolver, sheet, checkPrerequisites: true)
    {
    }

    /// <summary>The sheet as a roll sees it: <c>input.</c> reads <paramref name="input"/>, so modifiers whose <c>when</c> reads
    /// an input hold or not by the roll's inputs.</summary>
    public SheetModel(SystemDefinition system, IEntryResolver resolver, IReadOnlyDictionary<string, object> sheet, IReadOnlyDictionary<string, object?> input)
        : this(system, resolver, sheet, checkPrerequisites: true, input)
    {
    }

    private SheetModel(SystemDefinition system, IEntryResolver resolver, IReadOnlyDictionary<string, object> sheet, bool checkPrerequisites,
        IReadOnlyDictionary<string, object?>? input = null)
    {
        _input = input == null ? FormulaItem.Empty : new FormulaItem(name => input.TryGetValue(name, out var value) ? Plain(value) : null);
        _system = system;
        _resolver = resolver;
        _sheet = sheet;
        _formulas = SheetFormulas.For(system);
        _checkPrerequisites = checkPrerequisites;
        var kind = YamlValues.GetString(sheet, KindKey);
        Kind = system.SheetKinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase)) ?? system.SheetKinds.FirstOrDefault();
        _conditions = SheetRuleEngine.RawList(sheet, ConditionsKey);
        if (Kind == null) return;

        foreach (var value in Kind.Values) _valueDefinitions.TryAdd(value.Key, value);
        foreach (var list in Kind.Lists) _lists[list.Key] = PickList(list);
        foreach (var list in Kind.KeyedLists) _lists[list.Key] = KeyedList(list);
    }

    /// <summary>Works out every value, keyed-list entry and rule of the sheet kind.</summary>
    public SheetModelResult Evaluate()
    {
        if (Kind == null) return _result;
        using var _ = Enter();

        foreach (var value in Kind.Values)
        {
            if (Value(value.Key) is { } result) _result.Values[value.Key] = result;
        }

        foreach (var list in Kind.KeyedLists.Where(l => l.Derive.ContainsKey("value")))
        {
            foreach (var entry in _lists[list.Key])
            {
                if (Guard($"{list.Key}.{entry.Get("id")}", entry.Get("label")?.ToString(), () => entry.Get("value")) is { } value) _result.Values[$"{list.Key}.{entry.Get("id")}"] = value;
            }
        }

        foreach (var track in Tracks())
        {
            if (track.Definition.Max != null && Guard(track.Key + MaxSuffix, track.Label, () => TrackMax(track)) is { } max) _result.Values[track.Key + MaxSuffix] = max;
            if (track.Definition.Holds is { } holds && !holds.Type.Equals("text", StringComparison.OrdinalIgnoreCase)) CheckSlots(track, holds);
            CheckRange(track);
        }

        _result.Conditions.AddRange(Conditions());

        foreach (var list in Kind.Lists) Check(list.Key, list.Rules);
        foreach (var list in Kind.KeyedLists) Check(list.Key, list.Rules);
        if (_checkPrerequisites)
        {
            foreach (var list in Kind.Lists.Where(l => l.Prerequisites != null)) CheckPrerequisites(list);
        }

        return _result;
    }

    // ---- Names ----

    private Scope Enter() => new(this);

    private readonly struct Scope : IDisposable
    {
        private readonly SheetModel? _previous;

        public Scope(SheetModel model)
        {
            _previous = _current;
            _current = model;
        }

        public void Dispose() => _current = _previous;
    }

    /// <summary>What a name in a formula stands for: a value, a list, an attribute's score or a number on the sheet.</summary>
    private object? Resolve(string name)
    {
        if (_valueDefinitions.ContainsKey(name)) return Value(name);
        if (_lists.TryGetValue(name, out var list)) return list;
        // A track whose maximum is the sheet's own number under that name (5e's HP_Max) reads the sheet.
        if (name.EndsWith(MaxSuffix, StringComparison.Ordinal) && Tracks().FirstOrDefault(t => t.Key + MaxSuffix == name) is { } track && track.Definition.Max != name) return TrackMax(track);
        if (name == "input") return _input;
        if (_system.Attributes.FirstOrDefault(a => a.Key == name) is { } attribute) return ScoreOf(attribute);
        return _sheet.TryGetValue(name, out var raw) ? Plain(raw) : null;
    }

    /// <summary>Runs one formula on the sheet, its names read from <paramref name="numbers"/> first. For the formulas of
    /// <c>sheetRules</c>, which predate sheet kinds.</summary>
    public object? Formula(string formula, IReadOnlyDictionary<string, double> numbers)
    {
        using var _ = Enter();
        var arguments = _formulas.Names(formula)
            .Select(n => (n, numbers.TryGetValue(n, out var number) ? number : Resolve(n) ?? throw new FormulaException($"no number called \"{n}\"")))
            .ToList();
        return _formulas.Run(formula, arguments);
    }

    /// <summary>Runs one formula on the sheet, <paramref name="names"/> read first (a roll's <c>natural</c>, <c>total</c>,
    /// <c>dice</c>). A name neither binds nor the sheet has, or a formula that fails, throws <see cref="FormulaException"/>.</summary>
    public object? Run(string formula, IReadOnlyDictionary<string, object> names)
    {
        try
        {
            return Run(formula, extra: names);
        }
        catch (Exception ex) when (ex is not FormulaException and not StackOverflowException)
        {
            throw new FormulaException(ex.Message);
        }
    }

    /// <summary>The pick on the sheet with this id, in any of its lists (the weapon a roll is made with), or null.</summary>
    public FormulaItem? Item(string id)
    {
        using var _ = Enter();
        return Kind?.Lists.Select(l => _lists[l.Key]).SelectMany(l => l)
            .FirstOrDefault(i => string.Equals(i.Get("id")?.ToString(), id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A track's least and greatest value (null where it has none), or null when the sheet kind has no such track.</summary>
    public (double? Min, double? Max)? TrackBounds(string key) => TrackOf(key) is { } track ? (track.Min, track.Max) : null;

    /// <summary>The sheet kind's track with this key (one an <c>each</c> makes included) as it stands on this sheet, or null.
    /// A bound that cannot be worked out is null (and a warning).</summary>
    public SheetTrack? TrackOf(string key)
    {
        using var _ = Enter();
        if (Tracks().FirstOrDefault(t => t.Key == key) is not { } track) return null;
        var min = track.Definition.Min is { } low ? Guard(track.Key + ":min", track.Label, () => Run(low, extra: track.Bindings)) is { } m ? SheetFormulas.Number(m) : (double?)null : 0d;
        var max = track.Definition.Max != null ? Guard(track.Key + MaxSuffix, track.Label, () => TrackMax(track)) as double? : null;
        double? start = track.Definition.Start switch
        {
            null => null,
            "max" => max,
            "min" => min ?? 0,
            { } formula => Guard(track.Key + ":start", track.Label, () => Run(formula, extra: track.Bindings)) is { } s ? SheetFormulas.Number(s) : null
        };
        var levels = new List<SheetSlotLevel>();
        if (track.Definition.Each == null && track.Definition.Levels is IEnumerable list and not string)
        {
            foreach (var level in list.Cast<object?>())
            {
                var map = level as IDictionary<object, object>;
                if (map == null || YamlValues.ToNumber(At(map, "level")) is not { } number) continue;
                levels.Add(new SheetSlotLevel((int)number, (int)(YamlValues.ToNumber(At(map, "slots")) ?? 1), At(map, "condition")?.ToString()));
            }
        }

        return new SheetTrack(track.Key, track.Label, track.Definition, min, max, start, levels);
    }

    /// <summary>The keys of the sheet kind's tracks, those an <c>each</c> makes included, in declaration order.</summary>
    public IReadOnlyList<string> TrackKeys()
    {
        using var _ = Enter();
        return Tracks().Select(t => t.Key).ToList();
    }

    /// <summary>The rows of a list or keyed list, each with the <paramref name="columns"/> asked for (fields, entry properties or
    /// derived values) and its group read from <paramref name="groupBy"/>. Empty when the kind has no such list.</summary>
    public IReadOnlyList<SheetRow> Rows(string listKey, IEnumerable<string> columns, string? groupBy = null)
    {
        using var _ = Enter();
        if (!_lists.TryGetValue(listKey, out var list)) return Array.Empty<SheetRow>();
        var asked = columns.ToList();
        var index = _rawIndex.GetValueOrDefault(listKey);
        var rows = new List<SheetRow>();
        var position = 0;
        foreach (var item in list)
        {
            object? Cell(string name)
            {
                try
                {
                    return item.Get(name);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            var id = Cell("id")?.ToString() ?? "";
            var label = Cell("label")?.ToString() ?? Cell("name")?.ToString() ?? id;
            var cells = asked.ToDictionary(c => c, c => Cell(c) is FormulaItem or FormulaList ? null : Cell(c), StringComparer.Ordinal);
            rows.Add(new SheetRow(id, label, groupBy == null ? null : Cell(groupBy)?.ToString(), index != null && position < index.Count ? index[position] : null, cells));
            position++;
        }

        return rows;
    }

    /// <summary>A path into the sheet (<c>BAB</c>, <c>skills.knowledge.arcana.ranks</c>), or null.</summary>
    public object? Path(string path)
    {
        using var _ = Enter();
        try
        {
            var parts = path.Split('.');
            var at = Resolve(parts[0]);
            foreach (var part in parts.Skip(1)) at = (at as FormulaItem)?.Get(part);
            return at;
        }
        catch (Exception)
        {
            // A derive along the path that cannot be worked out: there is no value there (its own formula reports why).
            return null;
        }
    }

    private object? Value(string key)
    {
        if (_values.TryGetValue(key, out var known)) return known;
        var definition = _valueDefinitions[key];
        object? value = null;
        if (!_working.Add(key)) throw new FormulaException($"\"{key}\" depends on itself");
        try
        {
            value = Guard(key, definition.Label ?? key, () =>
            {
                if (definition.Input) return _sheet.TryGetValue(key, out var raw) ? Plain(raw) : Plain(YamlValues.Normalize(definition.Default));
                var result = definition.Formula == null ? 0d : Run(definition.Formula);
                var targets = Strings(definition.Modifiers);
                if (targets.Count == 0) return result;
                return SheetFormulas.Number(result) + Stack(key, targets, definition.Without);
            });
        }
        finally
        {
            _working.Remove(key);
        }

        _values[key] = value;
        return value;
    }

    /// <summary>Runs <paramref name="evaluate"/>; a formula that fails becomes a warning (once per key) and no value.</summary>
    private object? Guard(string key, string? label, Func<object?> evaluate)
    {
        try
        {
            var value = evaluate();
            if (value is double d && (!double.IsFinite(d) || Math.Abs(d) > 1e15)) throw new FormulaException("not a number a sheet can hold");
            return value;
        }
        catch (Exception ex) when (ex is not StackOverflowException)
        {
            if (_result.Warnings.All(w => w.FieldKey != key))
            {
                var name = label ?? key;
                _result.Warnings.Add(ScriptFailure(ex) is { } script
                    ? new RuleViolation(key, $"{name}: the system's script failed, so it is left out. {script.Message}", "Actor.Rule.ScriptError", new object?[] { name, script.Message })
                    : new RuleViolation(key,
                        $"{name}: the system's formula for this can't be worked out, so it is left out. The GM can check the system's rules.",
                        "Actor.Rule.FormulaError", new object?[] { name }));
            }

            return null;
        }
    }

    /// <summary>The script failure behind <paramref name="ex"/>, through the wrapping a formula's call puts round it.</summary>
    internal static ScriptException? ScriptFailure(Exception? ex)
    {
        for (var at = ex; at != null; at = at.InnerException)
        {
            if (at is ScriptException script) return script;
        }

        return null;
    }

    /// <summary>Runs a formula, with <paramref name="item"/> bound to <paramref name="parameter"/> when it is a lambda.</summary>
    private object? Run(string formula, FormulaItem? item = null, string? parameter = null, IReadOnlyDictionary<string, object>? extra = null)
    {
        var arguments = new List<(string, object)>();
        foreach (var name in _formulas.Names(formula))
        {
            if (name == parameter && item != null)
            {
                arguments.Add((name, item));
                continue;
            }

            if (extra != null && extra.TryGetValue(name, out var bound))
            {
                arguments.Add((name, bound));
                continue;
            }

            var value = Resolve(name) ?? throw new FormulaException($"nothing called \"{name}\"");
            arguments.Add((name, value));
        }

        using var _ = Enter();
        return _formulas.Run(formula, arguments);
    }

    /// <summary>Runs a lambda (<c>"s => s.points"</c>) on <paramref name="item"/>, or a plain formula.</summary>
    private object? RunOn(string formula, FormulaItem item)
    {
        var (parameter, body) = SheetFormulas.SplitLambda(formula);
        return Run(body, item, parameter);
    }

    // ---- Lists ----

    private FormulaList PickList(PickListDefinition list)
    {
        var items = new List<FormulaItem>();
        var rawIndex = _rawIndex[list.Key] = new List<int>();
        if (_sheet.TryGetValue(list.Key, out var raw) && raw is IEnumerable picks and not string)
        {
            var position = -1;
            foreach (var pick in picks)
            {
                position++;
                var fields = pick switch
                {
                    IDictionary<string, object> map => map,
                    string text => new Dictionary<string, object> { ["id"] = text },
                    _ => null
                };
                if (fields == null) continue;
                var id = fields.TryGetValue("id", out var rawId) ? rawId?.ToString() : null;
                var entry = list.Type != null && id != null ? _resolver.Resolve(list.Type, id) : null;
                if (list.Type != null && id != null && entry == null)
                {
                    _result.Violations.Add(new RuleViolation(list.Key, $"{list.Label}: \"{id}\" is not in the Library.", "Actor.Rule.NotInLibrary", new object?[] { list.Label, id }));
                }

                rawIndex.Add(position);
                items.Add(new FormulaItem(name =>
                {
                    if (fields.TryGetValue(name, out var value)) return Plain(value);
                    if (name == "name" && entry != null) return entry.Name;
                    if (entry != null && entry.Properties.TryGetValue(name, out var property)) return Plain(property);
                    return list.ItemFields.FirstOrDefault(f => f.Key == name) is { } field ? FieldDefault(field) : null;
                }));
            }
        }

        return new FormulaList(items);
    }

    private FormulaList KeyedList(KeyedListDefinition list)
    {
        var stored = _sheet.TryGetValue(list.Key, out var raw) && raw is IDictionary<string, object> map ? map : new Dictionary<string, object>();
        var items = new List<FormulaItem>();
        var byKey = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in list.Keys)
        {
            if (key.Parameter == null)
            {
                var item = KeyedItem(list, key, key.Key, null, At(stored, key.Key));
                items.Add(item);
                byKey[key.Key] = item;
                continue;
            }

            // One entry per subject the sheet has: "knowledge.arcana".
            var subjects = new Dictionary<string, FormulaItem>(StringComparer.Ordinal);
            foreach (var (id, fields) in stored.Where(s => s.Key.StartsWith(key.Key + ".", StringComparison.Ordinal)))
            {
                var subject = id[(key.Key.Length + 1)..];
                var item = KeyedItem(list, key, id, subject, fields);
                items.Add(item);
                subjects[subject] = item;
            }

            byKey[key.Key] = new FormulaList(subjects.Values.ToList(), name => subjects.GetValueOrDefault(name));
        }

        return new FormulaList(items, name => byKey.GetValueOrDefault(name));
    }

    private FormulaItem KeyedItem(KeyedListDefinition list, KeyedEntryDefinition key, string id, string? subject, object? stored)
    {
        var fields = stored as IDictionary<string, object> ?? new Dictionary<string, object>();
        var derived = new Dictionary<string, object?>(StringComparer.Ordinal);
        var working = new HashSet<string>(StringComparer.Ordinal);
        FormulaItem? self = null;
        self = new FormulaItem(name =>
        {
            switch (name)
            {
                case "id": return id;
                case "key": return key.Key;
                case "label": return subject == null ? key.Label : $"{key.Label} ({subject})";
                case "attribute": return key.Attribute;
                case "group": return key.Group ?? key.Groups?.FirstOrDefault();
                case "groups": return Groups(key);
                case "trainedOnly": return key.TrainedOnly;
            }

            if (key.Parameter != null && name == key.Parameter) return subject;
            if (fields.TryGetValue(name, out var value)) return Plain(value);
            if (list.Derive.TryGetValue(name, out var formula))
            {
                if (derived.TryGetValue(name, out var known)) return known;
                if (!working.Add(name)) throw new FormulaException($"\"{id}.{name}\" depends on itself");
                try
                {
                    var result = RunOn(formula, self!);
                    if (name == "value") result = SheetFormulas.Number(result) + Stack($"{list.Key}.{id}", Targets(list, key, id), null);
                    derived[name] = result;
                    return result;
                }
                finally
                {
                    working.Remove(name);
                }
            }

            return list.ItemFields.FirstOrDefault(f => f.Key == name) is { } field ? FieldDefault(field) : null;
        });
        return self;
    }

    private static List<object> Groups(KeyedEntryDefinition key) =>
        (key.Groups ?? new List<string>()).Concat(key.Group != null ? new[] { key.Group } : Array.Empty<string>()).Distinct().Cast<object>().ToList();

    /// <summary>What reaches a keyed entry: modifiers on <c>skills.knowledge.arcana</c>, on <c>skills.knowledge</c>, and on each of
    /// its groups (<c>'@armorPenalty'</c>).</summary>
    private static List<string> Targets(KeyedListDefinition list, KeyedEntryDefinition key, string id)
    {
        var targets = new List<string> { $"{list.Key}.{id}" };
        if (id != key.Key) targets.Add($"{list.Key}.{key.Key}");
        targets.AddRange(Groups(key).Select(g => "@" + g));
        return targets;
    }

    // YAML hands an untyped default over as text ("0"), so a field's type says how to read it.
    private static object? FieldDefault(ItemFieldDefinition field) =>
        field.Type.Equals("Number", StringComparison.OrdinalIgnoreCase) ? YamlValues.ToNumber(field.Default) ?? 0d
        : field.Type.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ? Plain(field.Default) is true
        : Plain(field.Default);

    /// <summary>A sheet or YAML value as formulas see it: numbers as doubles, "true"/"false" as booleans, the rest as it is.</summary>
    internal static object? Plain(object? value) => value switch
    {
        null => null,
        double or string or bool or FormulaItem => value is string s && bool.TryParse(s, out var flag) ? flag : value,
        IDictionary<string, object> => value,
        IConvertible c and not string => c.ToDouble(CultureInfo.InvariantCulture),
        _ => value
    };

    private static List<string> Strings(object? value) => value switch
    {
        null => new List<string>(),
        string s => new List<string> { s },
        IEnumerable items => items.Cast<object?>().Select(i => i?.ToString()).Where(i => !string.IsNullOrEmpty(i)).Select(i => i!).ToList(),
        _ => new List<string> { value.ToString()! }
    };

    // ---- Modifiers ----

    private List<Modifier> Modifiers()
    {
        if (_modifiers != null) return _modifiers;
        var modifiers = new List<Modifier>();
        foreach (var m in Kind?.Modifiers ?? new List<ModifierDefinition>())
        {
            modifiers.Add(new Modifier(m.Target, m.Amount, m.Type ?? "untyped", m.Source, m.When, null));
        }

        foreach (var list in Kind?.Lists.Where(l => l.ModifiersFrom != null) ?? Enumerable.Empty<PickListDefinition>())
        {
            foreach (var item in _lists[list.Key])
            {
                if (list.ModifiersFrom!.When is { } when && Guard(list.Key, list.Label, () => RunOn(when, item)) is not true) continue;
                if (item.Get(list.ModifiersFrom.Property) is not IEnumerable granted || granted is string) continue;
                foreach (var raw in granted)
                {
                    if (raw is not IDictionary<string, object> m) continue;
                    var source = item.Get("name")?.ToString() ?? item.Get("id")?.ToString();
                    modifiers.Add(new Modifier(At(m, "target")?.ToString() ?? "", At(m, "amount")?.ToString() ?? "0",
                        At(m, "type")?.ToString() ?? "untyped", At(m, "source")?.ToString() ?? source,
                        At(m, "when")?.ToString(), item));
                }
            }
        }

        foreach (var condition in Conditions())
        {
            if (_system.ConditionEffects.FirstOrDefault(e => string.Equals(e.Key, condition, StringComparison.OrdinalIgnoreCase)).Value is not { } effect) continue;
            foreach (var m in effect.Modifiers) modifiers.Add(new Modifier(m.Target, m.Amount, m.Type ?? "untyped", m.Source ?? condition, m.When, null));
        }

        return _modifiers = modifiers;
    }

    private static object? At<TKey>(IDictionary<TKey, object> map, TKey key) where TKey : notnull =>
        map.TryGetValue(key, out var value) ? value : null;

    private static bool SheetFormulasTruthy(object? value) => value is true;

    /// <summary>The stacked total of the modifiers on <paramref name="targets"/>, leaving out the types in
    /// <paramref name="without"/>; every one that reached is recorded under <paramref name="key"/>.</summary>
    private double Stack(string key, IReadOnlyCollection<string> targets, List<object>? without)
    {
        var left = new List<(string Type, bool PositiveOnly)>();
        foreach (var w in without ?? new List<object>())
        {
            if (w is IDictionary<object, object> map) left.Add((At(map, "type")?.ToString() ?? "", At(map, "positiveOnly") is true or "true"));
            else if (w is IDictionary<string, object> named) left.Add((At(named, "type")?.ToString() ?? "", At(named, "positiveOnly") is true or "true"));
            else left.Add((w.ToString() ?? "", false));
        }

        var reached = new List<(Modifier Modifier, double Amount)>();
        foreach (var modifier in Modifiers().Where(m => targets.Contains(m.Target, StringComparer.Ordinal)))
        {
            if (modifier.When is { } when && !Holds(when, modifier.Item)) continue;
            var amount = SheetFormulas.Number(modifier.Item != null ? RunOn(modifier.Amount, modifier.Item) : Run(modifier.Amount));
            if (amount == 0) continue;
            if (left.Any(l => l.Type == modifier.Type && (!l.PositiveOnly || amount > 0))) continue;
            reached.Add((modifier, amount));
        }

        var applied = new List<AppliedModifier>();
        var total = 0d;
        foreach (var (modifier, type, amount, counts) in ModifierStacking.Stack(reached.Select(r => (r.Modifier.Type, r.Amount, r.Modifier)), _system.ModifierTypes))
        {
            if (counts) total += amount;
            applied.Add(new AppliedModifier(type, amount, modifier.Source, counts));
        }

        _result.Modifiers[key] = applied;
        return total;
    }

    /// <summary>A modifier's or rule's condition. One that reads what only a roll has (<c>input.defensive</c>) does not hold on
    /// the sheet; one that fails otherwise is a formula problem, and does not hold either.</summary>
    private bool Holds(string when, FormulaItem? item)
    {
        try
        {
            return SheetFormulasTruthy(item != null ? RunOn(when, item) : Run(when));
        }
        catch (Exception) when (when.Contains("input.", StringComparison.Ordinal))
        {
            return false;
        }
    }

    // ---- Tracks ----

    /// <summary>A track as the sheet has it: a declared one, or one of those an <c>each</c> makes, with what its formulas read
    /// besides the sheet (<c>item</c>, <c>level</c>).</summary>
    private sealed record Track(string Key, string? Label, TrackDefinition Definition, IReadOnlyDictionary<string, object> Bindings);

    private List<Track>? _tracks;
    private bool _expandingTracks;
    private List<string>? _allConditions;
    private bool _workingConditions;

    private List<Track> Tracks()
    {
        if (_tracks != null) return _tracks;
        if (_expandingTracks) throw new FormulaException("a track's \"each\" depends on the tracks it makes");
        _expandingTracks = true;
        var tracks = new List<Track>();
        try
        {
            if (Kind == null) tracks.AddRange(ResourceTracks());
            foreach (var definition in Kind?.Tracks ?? new List<TrackDefinition>())
            {
                if (definition.Each == null)
                {
                    tracks.Add(new Track(definition.Key, definition.Label, definition, new Dictionary<string, object>()));
                    continue;
                }

                if (Guard(definition.Key, definition.Label, () => Run(definition.Each)) is not FormulaList items) continue;
                foreach (var item in items)
                {
                    foreach (var level in Levels(definition.Levels).DefaultIfEmpty(-1))
                    {
                        var bindings = new Dictionary<string, object> { ["item"] = item };
                        if (level >= 0) bindings["level"] = (double)level;
                        tracks.Add(new Track(Template(definition.Key, item, level, key: true), definition.Label is { } label ? Template(label, item, level) : null, definition, bindings));
                    }
                }
            }
        }
        finally
        {
            _expandingTracks = false;
        }

        return _tracks = tracks;
    }

    /// <summary>
    /// A system without sheet kinds (srd5e) still has tracks the model knows, so intents can act on them (overhaul step 6): its
    /// resources (hit points up to <c>HP_Max</c>, temporary hit points) and the slot pools the sheet has (<c>slots_3</c> up to
    /// <c>slots_3_Max</c>), each from 0. What reaching 0 does stays the table's vitals rules.
    /// </summary>
    private IEnumerable<Track> ResourceTracks()
    {
        var none = new Dictionary<string, object>();
        foreach (var resource in _system.Resources)
        {
            yield return new Track(resource.Key, resource.Label, new TrackDefinition { Key = resource.Key, Label = resource.Label, Max = resource.MaxKey, Min = "0" }, none);
        }

        foreach (var pool in _system.SheetRules?.SlotPools ?? new List<SlotPoolRule>())
        {
            foreach (var key in _sheet.Keys.Where(k => pool.Owns(k) && !k.EndsWith(MaxSuffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                yield return new Track(key, null, new TrackDefinition { Key = key, Max = key + MaxSuffix, Min = "0" }, none);
            }
        }
    }

    /// <summary><c>"0..9"</c>, or a list of numbers or of <c>{ level }</c> maps.</summary>
    private static IEnumerable<int> Levels(object? levels)
    {
        switch (levels)
        {
            case string range when range.Split("..") is [var from, var to] && int.TryParse(from, out var a) && int.TryParse(to, out var b):
                return Enumerable.Range(a, Math.Max(0, b - a + 1));
            case IEnumerable list and not string:
                return list.Cast<object?>().Select(l => l is IDictionary<object, object> m ? At(m, "level") : l is IDictionary<string, object> n ? At(n, "level") : l)
                    .Select(YamlValues.ToNumber).Where(n => n.HasValue).Select(n => (int)n!.Value).ToList();
            default:
                return Enumerable.Empty<int>();
        }
    }

    /// <summary><c>"slots_{item.id}_{level}"</c> filled in: <c>slots_cleric_1</c>.</summary>
    private static string Template(string template, FormulaItem item, int level, bool key = false) =>
        System.Text.RegularExpressions.Regex.Replace(template, @"\{(item\.(\w+)|level)\}", m =>
            m.Groups[2].Success ? (item.Get(m.Groups[2].Value)?.ToString() is { } text ? key ? text.ToLowerInvariant() : text : "")
            : level.ToString(CultureInfo.InvariantCulture));

    private double TrackMax(Track track) => SheetFormulas.Number(Run(track.Definition.Max!, extra: track.Bindings));

    /// <summary>The conditions that hold: the sheet's own, and those the tracks' thresholds and bands bring.</summary>
    private List<string> Conditions()
    {
        if (_allConditions != null) return _allConditions;
        // A threshold reads values, a value may take modifiers, and a condition brings modifiers: going round is a formula problem.
        if (_workingConditions) throw new FormulaException("a track's threshold depends on the conditions it brings");
        _workingConditions = true;
        var conditions = new List<string>(_conditions);
        try
        {
            foreach (var track in Tracks())
            {
                if (YamlValues.ToNumber(_sheet.GetValueOrDefault(track.Key)) is not { } current) continue;
                foreach (var threshold in track.Definition.Thresholds)
                {
                    if (threshold.Condition != null && Matches(track, threshold, current)) conditions.Add(threshold.Condition);
                }

                if (track.Definition.Bands.FirstOrDefault(b => Matches(track, b, current)) is { Condition: { } band }) conditions.Add(band);
            }

            // Slots in levels (harm): each level with something in it brings its condition.
            foreach (var track in Tracks().Where(t => t.Definition.Each == null && t.Definition.Levels is IEnumerable and not string))
            {
                if (_sheet.GetValueOrDefault(track.Key) is not IDictionary<string, object> filled) continue;
                foreach (var level in ((IEnumerable)track.Definition.Levels!).Cast<object?>())
                {
                    var map = level as IDictionary<object, object>;
                    if (map == null || YamlValues.ToNumber(At(map, "level")) is not { } number || At(map, "condition")?.ToString() is not { } condition) continue;
                    var key = number.ToString(CultureInfo.InvariantCulture);
                    if (filled.TryGetValue(key, out var texts) && texts is IEnumerable list and not string && list.Cast<object?>().Any(t => !string.IsNullOrWhiteSpace(t?.ToString())))
                    {
                        conditions.Add(condition);
                    }
                }
            }
        }
        finally
        {
            _workingConditions = false;
        }

        return _allConditions = conditions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether <paramref name="current"/> meets every comparison of <paramref name="threshold"/>; one that cannot be
    /// worked out does not hold (and is a warning).</summary>
    private bool Matches(Track track, TrackThresholdDefinition threshold, double current)
    {
        bool Compare(string? formula, Func<double, bool> test) =>
            formula == null || (Guard($"{track.Key}:{formula}", track.Label, () => Run(formula, extra: track.Bindings)) is { } bound && test(SheetFormulas.Number(bound)));

        return Compare(threshold.AtOrBelow, x => current <= x) && Compare(threshold.Below, x => current < x)
            && Compare(threshold.AtOrAbove, x => current >= x) && Compare(threshold.Above, x => current > x)
            && Compare(threshold.Equals, x => current == x);
    }

    /// <summary>A slot track's contents: each must be an entry of the type its slots hold that passes their <c>where</c>
    /// (<see cref="TrackHoldsDefinition"/>), and there must be no more than the track's maximum. The same entry may fill
    /// several slots (a prepared spell, twice).</summary>
    private void CheckSlots(Track track, TrackHoldsDefinition holds)
    {
        var held = SheetRuleEngine.RawList(_sheet, track.Key);
        if (held.Count == 0) return;
        var label = track.Label ?? track.Key;
        foreach (var id in held.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = _resolver.Resolve(holds.Type, id);
            var fits = entry != null && (holds.Where == null || Guard(track.Key + ":holds", label, () =>
            {
                var (parameter, body) = SheetFormulas.SplitLambda(holds.Where);
                return Run(body, EntryItem(entry), parameter, track.Bindings);
            }) is true);
            if (fits) continue;
            var name = entry?.Name ?? id;
            var message = $"{label}: {name} is {Fill(holds.Message ?? "not something these slots can hold.", track)}";
            _result.Violations.Add(new RuleViolation(track.Key, message, "Actor.Rule.SlotHolds", new object?[] { label, name }));
        }

        if (track.Definition.Max != null && Guard(track.Key + MaxSuffix, label, () => TrackMax(track)) is double max && held.Count > max)
        {
            _result.Violations.Add(new RuleViolation(track.Key, $"{label}: {held.Count} in {max:0} slots.", "Actor.Rule.TrackFull", new object?[] { label, held.Count, max }));
        }
    }

    /// <summary>A numeric track's current value must be within its minimum (0 unless it says; it may be negative, 3.5's -10) and
    /// its maximum. A track the sheet has no number for, or whose maximum cannot be worked out, is not judged.</summary>
    private void CheckRange(Track track)
    {
        if (YamlValues.ToNumber(_sheet.GetValueOrDefault(track.Key)) is not { } current) return;
        var label = track.Label ?? track.Key;
        var min = track.Definition.Min is { } low ? Guard(track.Key + ":min", label, () => Run(low, extra: track.Bindings)) is { } m ? SheetFormulas.Number(m) : (double?)null : 0d;
        var max = track.Definition.Max != null ? Guard(track.Key + MaxSuffix, label, () => TrackMax(track)) as double? : null;
        if ((min is { } floor && current < floor) || (max is { } ceiling && current > ceiling))
        {
            var range = max is { } top ? $"{min ?? 0:0} to {top:0}" : $"at least {min ?? 0:0}";
            _result.Violations.Add(new RuleViolation(track.Key, $"{label}: {current:0} is outside {range}.", "Actor.Rule.TrackRange", new object?[] { label, current, min, max }));
        }
    }

    private static FormulaItem EntryItem(ContentEntry entry) => new(name =>
        name switch
        {
            "id" => entry.Id,
            "name" => entry.Name,
            _ => entry.Properties.TryGetValue(name, out var value) ? Plain(value) : null
        });

    /// <summary>A track's message with <c>{item.name}</c> and <c>{level}</c> filled in.</summary>
    private static string Fill(string message, Track track) =>
        System.Text.RegularExpressions.Regex.Replace(message, @"\{(item\.(\w+)|level)\}", m =>
            m.Groups[2].Success ? (track.Bindings.GetValueOrDefault("item") as FormulaItem)?.Get(m.Groups[2].Value)?.ToString() ?? m.Value
            : track.Bindings.GetValueOrDefault("level") is double level ? level.ToString(CultureInfo.InvariantCulture) : m.Value);

    // ---- Rules and prerequisites ----

    private void Check(string field, IEnumerable<SheetCheckDefinition> rules)
    {
        foreach (var rule in rules)
        {
            var (parameter, _) = SheetFormulas.SplitLambda(rule.Check);
            if (parameter == null)
            {
                if (Guard(field + ":" + rule.Key, rule.Key, () => Run(rule.Check)) is false) Violation(field, rule, null);
                continue;
            }

            foreach (var item in _lists[field])
            {
                if (Guard(field + ":" + rule.Key, rule.Key, () => RunOn(rule.Check, item)) is false) Violation(field, rule, item);
            }
        }
    }

    private void Violation(string field, SheetCheckDefinition rule, FormulaItem? item)
    {
        var message = System.Text.RegularExpressions.Regex.Replace(rule.Message ?? rule.Key, @"\{(\w+)\}", m =>
        {
            var name = m.Groups[1].Value;
            object? value;
            try
            {
                value = item?.Get(name) ?? (_valueDefinitions.ContainsKey(name) ? Value(name) : null);
            }
            catch (Exception)
            {
                value = null;
            }
            return value switch
            {
                double d => d.ToString("0.##", CultureInfo.InvariantCulture),
                null => m.Value,
                _ => value.ToString()!
            };
        });
        _result.Violations.Add(new RuleViolation(field, message, rule.Key, new object?[] { message }));
    }

    /// <summary>Each pick's entry prerequisites (<c>{ value: BAB, atLeast: 5 }</c>, <c>{ list: feats, contains: id }</c>), judged on
    /// the sheet without that pick: a prestige class's own levels do not count towards entering it.</summary>
    private void CheckPrerequisites(PickListDefinition list)
    {
        var picks = _sheet.TryGetValue(list.Key, out var raw) && raw is IEnumerable all and not string ? all.Cast<object>().ToList() : new List<object>();
        var items = _lists[list.Key];
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Get(list.Prerequisites!) is not IEnumerable prerequisites || prerequisites is string) continue;
            var position = _rawIndex[list.Key][index];
            var without = new Dictionary<string, object>(_sheet) { [list.Key] = picks.Where((_, i) => i != position).ToList() };
            var before = new SheetModel(_system, _resolver, without, checkPrerequisites: false);
            var name = items[index].Get("name")?.ToString() ?? items[index].Get("id")?.ToString();
            foreach (var prerequisite in prerequisites.OfType<IDictionary<string, object>>())
            {
                bool met;
                if (At(prerequisite, "value")?.ToString() is { } path)
                {
                    var atLeast = SheetFormulas.Number(At(prerequisite, "atLeast") ?? 0);
                    met = before.Path(path) is { } value and not FormulaItem && SheetFormulas.Number(value) >= atLeast;
                }
                else if (At(prerequisite, "list")?.ToString() is { } other)
                {
                    met = before.Path(other) is FormulaList chosen && chosen.Any(i => string.Equals(i.Get("id")?.ToString(), At(prerequisite, "contains")?.ToString(), StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    continue;
                }

                if (met) continue;
                var message = $"{name}: {At(prerequisite, "message")?.ToString() ?? "a prerequisite is not met."}";
                _result.Violations.Add(new RuleViolation(list.Key, message, "Actor.Rule.Prerequisite", new object?[] { name, message }));
            }
        }
    }

    // ---- What the built-in functions read ----

    private double ScoreOf(AttributeDefinition attribute) => YamlValues.ToNumber(_sheet.GetValueOrDefault(attribute.Key)) ?? attribute.Default;

    internal double Score(object? key)
    {
        // An attribute's score, or a value of the sheet kind named by key (Blades' insight is a count of actions, not an attribute).
        var name = key?.ToString();
        if (_system.Attributes.FirstOrDefault(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase)) is { } attribute) return ScoreOf(attribute);
        if (name != null && _valueDefinitions.ContainsKey(name)) return SheetFormulas.Number(Value(name));
        throw new FormulaException($"no attribute or value called \"{name}\"");
    }

    /// <summary><c>mod(dex)</c> (a score) or <c>mod('dex')</c>, <c>mod(s.attribute)</c> (an attribute's key).</summary>
    internal double AttributeModifier(object? scoreOrKey)
    {
        var score = scoreOrKey is string ? Score(scoreOrKey) : SheetFormulas.Number(scoreOrKey);
        return AttributeDefinition.HalfMinusFive((int)Math.Floor(score));
    }

    internal double Progression(object? name, object? level)
    {
        var key = name?.ToString() ?? "";
        if (!_system.Progressions.TryGetValue(key, out var formula)) throw new FormulaException($"no progression called \"{key}\"");
        var levelValue = SheetFormulas.Number(level);
        return SheetFormulas.Number(_formulas.Run(formula, _formulas.Names(formula).Select(n => (n, n == "level" ? (object)levelValue : Resolve(n) ?? throw new FormulaException($"nothing called \"{n}\""))).ToList()));
    }

    /// <summary>A named function called from outside a formula (a script handler's <c>ctx.call</c>), with this sheet as the one
    /// being worked out.</summary>
    internal double CallNamed(string name, IReadOnlyList<object?> arguments)
    {
        if (!_system.Functions.ContainsKey(name)) throw new FormulaException($"no function called \"{name}\"");
        using var _ = Enter();
        var expected = _system.Functions[name].Script != null ? arguments.Count : _formulas.Function(name)?.Parameters.Length ?? 0;
        if (arguments.Count != expected) throw new FormulaException($"{name} takes {expected} arguments, not {arguments.Count}");
        return CallFunction(name, arguments.ToArray());
    }

    internal double CallFunction(string name, params object?[] arguments)
    {
        if (_system.Functions.TryGetValue(name, out var definition) && definition.Script is { } script)
        {
            return SystemScripts.For(_system).Function(name, script, arguments.Select(a => Plain(a) switch
            {
                FormulaList => throw new FormulaException($"{name} is a script function, which takes numbers, text and flags, not a list"),
                FormulaText text => text.ToString(),
                FormulaItem item => item.Value ?? (object?)item.Flag,
                var plain => plain
            }).ToList()).Value;
        }

        if (_formulas.Function(name) is not { } function) throw new FormulaException($"no function called \"{name}\"");
        // Every parameter is bound, even one the body's identifiers do not list: a parameter may share its name with a built-in
        // (bonusSpells(score, level) and score(key)), and then the parser takes it for the function.
        var bound = function.Parameters.Select((p, i) => (p, Plain(arguments[i]) ?? throw new FormulaException($"no value for \"{p}\""))).ToList();
        foreach (var n in _formulas.Names(function.Body).Where(n => !function.Parameters.Contains(n)))
        {
            bound.Add((n, Resolve(n) ?? throw new FormulaException($"nothing called \"{n}\"")));
        }

        return SheetFormulas.Number(_formulas.Run(function.Body, bound));
    }

    internal double ModifierTotal(string target) => Stack("modifiers:" + target, new[] { target }, null);

    internal bool Has(string? condition) => Conditions().Any(c => string.Equals(c, condition, StringComparison.OrdinalIgnoreCase));
}
