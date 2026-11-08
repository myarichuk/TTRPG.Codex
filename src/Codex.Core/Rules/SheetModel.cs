using System.Collections;
using System.Globalization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>One modifier that reached a target, and whether it counted (a lower bonus of a type that does not stack does not).</summary>
public sealed record AppliedModifier(string Type, double Amount, string? Source, bool Counted);

/// <summary>What <see cref="SheetModel"/> works out for a sheet.</summary>
public sealed class SheetModelResult
{
    /// <summary>Every value by key (a double, or a string for a choice), keyed-list entries as <c>skills.climb</c>.</summary>
    public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

    public List<RuleViolation> Violations { get; } = new();

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
    public const string KindKey = "kind";

    private readonly SystemDefinition _system;
    private readonly IEntryResolver _resolver;
    private readonly IReadOnlyDictionary<string, object> _sheet;
    private readonly SheetFormulas _formulas;
    private readonly bool _checkPrerequisites;
    private readonly Dictionary<string, ValueDefinition> _valueDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FormulaList> _lists = new(StringComparer.Ordinal);
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

    private SheetModel(SystemDefinition system, IEntryResolver resolver, IReadOnlyDictionary<string, object> sheet, bool checkPrerequisites)
    {
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
        if (name == "input") return FormulaItem.Empty;
        if (_system.Attributes.FirstOrDefault(a => a.Key == name) is { } attribute) return ScoreOf(attribute);
        return _sheet.TryGetValue(name, out var raw) ? Plain(raw) : null;
    }

    /// <summary>A path into the sheet (<c>BAB</c>, <c>skills.knowledge.arcana.ranks</c>), or null.</summary>
    public object? Path(string path)
    {
        using var _ = Enter();
        var parts = path.Split('.');
        var at = Resolve(parts[0]);
        foreach (var part in parts.Skip(1)) at = (at as FormulaItem)?.Get(part);
        return at;
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
                if (definition.Input) return _sheet.TryGetValue(key, out var raw) ? Plain(raw) : Plain(definition.Default);
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
                _result.Warnings.Add(new RuleViolation(key,
                    $"{name}: the system's formula for this can't be worked out, so it is left out. The GM can check the system's rules.",
                    "Actor.Rule.FormulaError", new object?[] { name }));
            }

            return null;
        }
    }

    /// <summary>Runs a formula, with <paramref name="item"/> bound to <paramref name="parameter"/> when it is a lambda.</summary>
    private object? Run(string formula, FormulaItem? item = null, string? parameter = null)
    {
        var arguments = new List<(string, object)>();
        foreach (var name in _formulas.Names(formula))
        {
            if (name == parameter && item != null)
            {
                arguments.Add((name, item));
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
        if (_sheet.TryGetValue(list.Key, out var raw) && raw is IEnumerable picks and not string)
        {
            foreach (var pick in picks)
            {
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

    private static object? FieldDefault(ItemFieldDefinition field) =>
        field.Default != null ? Plain(field.Default)
        : field.Type.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ? false
        : field.Type.Equals("Number", StringComparison.OrdinalIgnoreCase) ? 0d
        : null;

    /// <summary>A sheet or YAML value as formulas see it: numbers as doubles, "true"/"false" as booleans, the rest as it is.</summary>
    private static object? Plain(object? value) => value switch
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

        foreach (var condition in _conditions)
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
        foreach (var group in reached.GroupBy(r => (r.Modifier.Type, Penalty: r.Amount < 0)))
        {
            var rule = group.Key.Penalty ? _system.ModifierTypes.Penalties
                : _system.ModifierTypes.Types.GetValueOrDefault(group.Key.Type) ?? _system.ModifierTypes.Default;
            var counted = rule.ToLowerInvariant() switch
            {
                "highest" => new[] { group.MaxBy(r => Math.Abs(r.Amount)) },
                "lowest" => new[] { group.MinBy(r => Math.Abs(r.Amount)) },
                _ => group.ToArray()
            };
            foreach (var r in group)
            {
                var counts = counted.Contains(r);
                if (counts) total += r.Amount;
                applied.Add(new AppliedModifier(r.Modifier.Type, r.Amount, r.Modifier.Source, counts));
            }
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
            var value = item?.Get(name) ?? (_valueDefinitions.ContainsKey(name) ? Value(name) : null);
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
            var without = new Dictionary<string, object>(_sheet) { [list.Key] = picks.Where((_, i) => i != index).ToList() };
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
        var name = key?.ToString();
        return _system.Attributes.FirstOrDefault(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase)) is { } attribute
            ? ScoreOf(attribute)
            : throw new FormulaException($"no attribute called \"{name}\"");
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

    internal double CallFunction(string name, params object?[] arguments)
    {
        if (_formulas.Function(name) is not { } function) throw new FormulaException($"no function called \"{name}\"");
        var bound = new List<(string, object)>();
        foreach (var n in _formulas.Names(function.Body))
        {
            var index = Array.IndexOf(function.Parameters, n);
            bound.Add((n, index >= 0 ? Plain(arguments[index]) ?? throw new FormulaException($"no value for \"{n}\"") : Resolve(n) ?? throw new FormulaException($"nothing called \"{n}\"")));
        }

        return SheetFormulas.Number(_formulas.Run(function.Body, bound));
    }

    internal double ModifierTotal(string target) => Stack("modifiers:" + target, new[] { target }, null);

    internal bool Has(string? condition) => _conditions.Any(c => string.Equals(c, condition, StringComparison.OrdinalIgnoreCase));
}
