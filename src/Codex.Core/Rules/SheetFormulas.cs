using System.Collections;
using System.Collections.Concurrent;
using System.Dynamic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Codex.Plugin.Abstractions;
using DynamicExpresso;

namespace Codex.Core.Rules;

/// <summary>
/// Something a formula reads members of: a list pick (<c>c.classLevel</c>), a keyed-list entry (<c>s.ranks</c>), an entry's
/// map-valued property. A member it does not have reads as null, which the aggregates skip (an item without <c>maxDex</c> sets
/// no cap). It is a <see cref="DynamicObject"/> because DynamicExpresso binds member access dynamically only on a parameter
/// whose static type is one; lambda parameters are typed by the delegate, so a plain <c>object</c> would not do.
/// </summary>
public class FormulaItem(Func<string, object?> member) : DynamicObject
{
    public static readonly FormulaItem Empty = new(_ => null);

    public object? Get(string name) => member(name);

    /// <summary>The number this item stands for, when it stands for one (a die's face); null otherwise.</summary>
    public virtual double? Value => null;

    /// <summary>The flag this item stands for, when it stands for one (an answer in a list of answers); null otherwise.</summary>
    public virtual bool? Flag => null;

    public static implicit operator double?(FormulaItem? item) => item?.Value;
    public static implicit operator bool?(FormulaItem? item) => item?.Flag;
    public static bool operator ==(FormulaItem? a, bool b) => a?.Flag == b;
    public static bool operator !=(FormulaItem? a, bool b) => a?.Flag != b;
    public static bool operator ==(FormulaItem? a, double b) => a?.Value == b;
    public static bool operator !=(FormulaItem? a, double b) => a?.Value != b;
    public static bool operator <(FormulaItem? a, double b) => a?.Value < b;
    public static bool operator >(FormulaItem? a, double b) => a?.Value > b;
    public static bool operator <=(FormulaItem? a, double b) => a?.Value <= b;
    public static bool operator >=(FormulaItem? a, double b) => a?.Value >= b;
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);
    public override int GetHashCode() => base.GetHashCode();

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        result = member(binder.Name);
        return true;
    }

    /// <summary>A member by a name worked out in the formula: <c>skills[input.skill]</c>.</summary>
    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        result = indexes is [{ } key] ? member(Convert.ToString(key, CultureInfo.InvariantCulture) ?? "") : null;
        return true;
    }
}

/// <summary>A number in a list formulas aggregate over: a die's face in <c>count(dice, d => d == 6)</c>.</summary>
public sealed class FormulaNumber(double value) : FormulaItem(_ => null)
{
    public override double? Value => value;
}

/// <summary>A flag in a list formulas aggregate over: an answer in <c>count(ev.answers, a => a)</c>.</summary>
public sealed class FormulaFlag(bool value) : FormulaItem(_ => null)
{
    public override bool? Flag => value;
}

/// <summary>Text in a list formulas read: a name in <c>contains(ev.names, 'x')</c>.</summary>
public sealed class FormulaText(string? value) : FormulaItem(_ => null)
{
    public override string? ToString() => value;
}

/// <summary>A list as formulas see it: its items to aggregate over, and (for a keyed list) its entries by key as members
/// (<c>skills.tumble</c>).</summary>
public sealed class FormulaList(IReadOnlyList<FormulaItem> items, Func<string, object?>? member = null)
    : FormulaItem(member ?? (_ => null)), IReadOnlyList<FormulaItem>
{
    public FormulaItem this[int index] => items[index];
    public int Count => items.Count;
    public IEnumerator<FormulaItem> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A formula that names something the sheet doesn't have, or comes out as no number.</summary>
public sealed class FormulaException(string message) : Exception(message);

/// <summary>
/// Parses the primitives' formulas once per system and formula (R7: content packs are anyone's, and parsing on every render
/// of every viewer is waste). The interpreter knows no types (no <c>new</c>, no <c>Math</c>, no reflection); its whole
/// surface is the functions registered here, which take <c>object</c> so a dynamic argument binds, and read the sheet being
/// evaluated through <see cref="SheetModel.Current"/>.
/// <para>Before parsing, <c>'text'</c> becomes <c>"text"</c> (YAML authors quote with single quotes inside double-quoted
/// strings) and every integer literal becomes a double, so <c>cond ? x : 0</c> and <c>points / 2</c> never fall into integer
/// arithmetic. Integer division is <c>div</c>; rounding is <c>floor</c> and <c>ceil</c> (overhaul section 4, rule 4).</para>
/// </summary>
public sealed partial class SheetFormulas
{
    private static readonly ConditionalWeakTable<SystemDefinition, SheetFormulas> BySystem = new();

    private readonly Interpreter _interpreter;
    private readonly ConcurrentDictionary<(string Formula, string Signature), Lambda> _compiled = new();
    private readonly ConcurrentDictionary<string, (string Body, string[] Parameters)> _functions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _names = new(StringComparer.Ordinal);

    public static SheetFormulas For(SystemDefinition system) => BySystem.GetValue(system, s => new SheetFormulas(s));

    private SheetFormulas(SystemDefinition system)
    {
        // Keywords (true, false, null) and lambdas; no types, so no `new`, `typeof` or static members.
        _interpreter = new Interpreter(InterpreterOptions.LambdaExpressions | InterpreterOptions.SystemKeywords);
        Builtins(_interpreter);
        foreach (var (name, function) in system.Functions)
        {
            if (function.Formula == null || LambdaHead().Match(function.Formula) is not { Success: true } head) continue;
            var parameters = head.Groups["params"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            // The parser takes a name it knows as a function before a parameter of that name (bonusSpells(score, level) and
            // score(key)), so the parameters are renamed in the body: __p0, __p1... A name followed by '(' stays the function.
            var renamed = parameters.Select((_, i) => "__p" + i).ToArray();
            _functions[name] = (RenameParameters(function.Formula[head.Length..], parameters, renamed), renamed);
            var captured = name;
            switch (parameters.Length)
            {
                case 1: _interpreter.SetFunction(name, (Func<object?, double>)(a => SheetModel.Need().CallFunction(captured, a))); break;
                case 2: _interpreter.SetFunction(name, (Func<object?, object?, double>)((a, b) => SheetModel.Need().CallFunction(captured, a, b))); break;
                case 3: _interpreter.SetFunction(name, (Func<object?, object?, object?, double>)((a, b, c) => SheetModel.Need().CallFunction(captured, a, b, c))); break;
                case 4: _interpreter.SetFunction(name, (Func<object?, object?, object?, object?, double>)((a, b, c, d) => SheetModel.Need().CallFunction(captured, a, b, c, d))); break;
            }
        }
    }

    /// <summary>Replaces each of <paramref name="from"/> with the matching <paramref name="to"/> where it stands as a name: not
    /// inside a string, not after a '.', not followed by '('.</summary>
    internal static string RenameParameters(string body, string[] from, string[] to)
    {
        var result = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length;)
        {
            var c = body[i];
            if (c is '\'' or '"')
            {
                var end = i + 1;
                while (end < body.Length && body[end] != c) end += body[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, body.Length);
                result.Append(body, i, end - i);
                i = end;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] == '_')) i++;
                var word = body[start..i];
                var next = i;
                while (next < body.Length && char.IsWhiteSpace(body[next])) next++;
                var member = start > 0 && body[start - 1] == '.';
                var call = next < body.Length && body[next] == '(';
                var index = Array.IndexOf(from, word);
                result.Append(index >= 0 && !member && !call ? to[index] : word);
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>A named function's body and parameter names, or null. A named function takes up to four arguments and returns a
    /// number (a condition gives 1 or 0), so its result can be added to.</summary>
    internal (string Body, string[] Parameters)? Function(string name) => _functions.TryGetValue(name, out var f) ? f : null;

    /// <summary><c>"s => body"</c> or <c>"(a, b) => body"</c> at the start of a formula.</summary>
    [GeneratedRegex(@"^\s*\(?\s*(?<params>[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*\)?\s*=>\s*")]
    private static partial Regex LambdaHead();

    /// <summary>Splits a top-level lambda (<c>"s => s.points"</c>) into its parameter and body; (null, formula) otherwise.</summary>
    public static (string? Parameter, string Body) SplitLambda(string formula) =>
        LambdaHead().Match(formula) is { Success: true } head && !head.Groups["params"].Value.Contains(',')
            ? (head.Groups["params"].Value, formula[head.Length..])
            : (null, formula);

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*=>")]
    private static partial Regex InnerLambdaParameter();

    /// <summary>The names a formula reads that are not its own lambdas' parameters. Worked out once per formula.</summary>
    public IReadOnlyList<string> Names(string formula) => _names.GetOrAdd(formula, f =>
    {
        var text = Prepare(f);
        var own = InnerLambdaParameter().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        lock (_interpreter)
        {
            return _interpreter.DetectIdentifiers(text).UnknownIdentifiers.Where(n => !own.Contains(n)).Distinct().ToList();
        }
    });

    /// <summary>Runs <paramref name="formula"/> with each name bound to a value (a double, string, bool or <see cref="FormulaItem"/>).</summary>
    public object? Run(string formula, IReadOnlyList<(string Name, object Value)> arguments)
    {
        var signature = string.Join(",", arguments.Select(a => a.Name + ":" + TypeOf(a.Value).Name));
        var lambda = _compiled.GetOrAdd((formula, signature), key =>
        {
            var parameters = arguments.Select(a => new Parameter(a.Name, TypeOf(a.Value))).ToArray();
            Lambda parsed;
            lock (_interpreter)
            {
                parsed = _interpreter.Parse(Prepare(key.Formula), parameters);
            }

            FormulaSandbox.Check(parsed.Expression);
            return parsed;
        });
        return lambda.Invoke(arguments.Select(a => a.Value).ToArray());
    }

    private static Type TypeOf(object value) => value switch
    {
        double => typeof(double),
        string => typeof(string),
        bool => typeof(bool),
        FormulaList => typeof(FormulaList),
        FormulaItem => typeof(FormulaItem),
        _ => typeof(object)
    };

    /// <summary>The longest formula, and the deepest nesting of brackets, a formula may have. The parser is recursive, and a
    /// stack overflow cannot be caught: a pathological formula must be refused before it reaches it.</summary>
    public const int MaxLength = 2000;
    public const int MaxDepth = 32;

    /// <summary>Single-quoted strings become double-quoted, integer literals doubles. A backslash escapes the next character
    /// inside a string (<c>'it\'s'</c>). Refuses a formula longer than <see cref="MaxLength"/> or nested deeper than
    /// <see cref="MaxDepth"/>.</summary>
    internal static string Prepare(string formula)
    {
        if (formula.Length > MaxLength) throw new FormulaException($"longer than {MaxLength} characters");
        var text = new StringBuilder(formula.Length + 8);
        var code = new StringBuilder();
        var depth = 0;
        void Flush()
        {
            text.Append(IntegerLiteral().Replace(code.ToString(), "$1.0"));
            code.Clear();
        }

        for (var i = 0; i < formula.Length; i++)
        {
            var c = formula[i];
            if (c is not ('\'' or '"'))
            {
                if (c is '(' or '[' or '{' && ++depth > MaxDepth) throw new FormulaException($"nested deeper than {MaxDepth}");
                if (c is ')' or ']' or '}') depth--;
                code.Append(c);
                continue;
            }

            Flush();
            var content = new StringBuilder();
            var j = i + 1;
            for (; j < formula.Length && formula[j] != c; j++)
            {
                if (formula[j] == '\\' && j + 1 < formula.Length)
                {
                    var next = formula[++j];
                    // \' and \" are the quote itself; any other escape is kept for the parser (\n, \\).
                    if (next is '\'' or '"') content.Append(next);
                    else content.Append('\\').Append(next);
                    continue;
                }

                content.Append(formula[j]);
            }

            text.Append('"').Append(content.ToString().Replace("\"", "\\\"")).Append('"');
            i = j;
        }

        Flush();
        return text.ToString();
    }

    [GeneratedRegex(@"(?<![\w.])(\d+)(?![\w.])")]
    private static partial Regex IntegerLiteral();

    // ---- Built-in functions (overhaul section 4: built in, C#) ----

    internal static double Number(object? value) => value switch
    {
        double d => d,
        null => throw new FormulaException("no number"),
        bool b => b ? 1 : 0,
        IConvertible c when value is not string => c.ToDouble(CultureInfo.InvariantCulture),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new FormulaException($"\"{value}\" is not a number")
    };

    /// <summary>What an aggregate goes over: a list; null (a member an item does not have) is none.</summary>
    private static IEnumerable<FormulaItem> Items(object? list) => list switch
    {
        null => Array.Empty<FormulaItem>(),
        IEnumerable<FormulaItem> items => items,
        _ => throw new FormulaException($"\"{list}\" is not a list")
    };

    private static IEnumerable<double> Selected(IEnumerable<FormulaItem> list, Func<FormulaItem, double?> select) =>
        list.Select(select).Where(v => v.HasValue).Select(v => v!.Value);

    private static void Builtins(Interpreter it)
    {
        it.SetFunction("floor", (Func<object?, double>)(x => Math.Floor(Number(x))));
        it.SetFunction("ceil", (Func<object?, double>)(x => Math.Ceiling(Number(x))));
        it.SetFunction("abs", (Func<object?, double>)(x => Math.Abs(Number(x))));
        it.SetFunction("div", (Func<object?, object?, double>)((a, b) => Math.Floor(Number(a) / Number(b))));
        it.SetFunction("max", (Func<object?, object?, double>)((a, b) => Math.Max(Number(a), Number(b))));
        it.SetFunction("min", (Func<object?, object?, double>)((a, b) => Math.Min(Number(a), Number(b))));
        // One delegate type per function: DynamicExpresso parses a lambda's body against the parameter's type, and given
        // overloads it takes one of them, not the one that fits. Selectors return double? and predicates bool?, so a member an
        // item does not have (null) is skipped, or does not hold, instead of failing the formula.
        // The list is object, not IEnumerable<FormulaItem>: a list reached through a member (ev.answers, item.levels) is
        // dynamic, and the parser will not pass a dynamic argument to a parameter of another type.
        it.SetFunction("max", (Func<object?, Func<FormulaItem, double?>, object?, double>)((l, f, d) => Selected(Items(l), f).DefaultIfEmpty(Number(d)).Max()));
        it.SetFunction("min", (Func<object?, Func<FormulaItem, double?>, object?, double>)((l, f, d) => Selected(Items(l), f).DefaultIfEmpty(Number(d)).Min()));
        it.SetFunction("sum", (Func<object?, Func<FormulaItem, double?>, double>)((l, f) => Selected(Items(l), f).Sum()));
        it.SetFunction("count", (Func<object?, Func<FormulaItem, bool?>, double>)((l, f) => Items(l).Count(i => f(i) == true)));
        it.SetFunction("any", (Func<object?, Func<FormulaItem, bool?>, bool>)((l, f) => Items(l).Any(i => f(i) == true)));
        it.SetFunction("all", (Func<object?, Func<FormulaItem, bool?>, bool>)((l, f) => Items(l).All(i => f(i) == true)));
        it.SetFunction("where", (Func<object?, Func<FormulaItem, bool?>, FormulaList>)((l, f) => new FormulaList(Items(l).Where(i => f(i) == true).ToList())));
        it.SetFunction("first", (Func<object?, FormulaItem>)(l => Items(l).FirstOrDefault() ?? FormulaItem.Empty));
        it.SetFunction("contains", (Func<object?, object?, bool>)Contains);
        it.SetFunction("levelFor", (Func<object?, object?, double>)LevelFor);
        it.SetFunction("table", (Func<object?, object?, object?, double>)Table);
        it.SetFunction("mod", (Func<object?, double>)(x => SheetModel.Need().AttributeModifier(x)));
        it.SetFunction("score", (Func<object?, double>)(x => SheetModel.Need().Score(x)));
        it.SetFunction("progression", (Func<object?, object?, double>)((name, level) => SheetModel.Need().Progression(name, level)));
        it.SetFunction("modifiers", (Func<object?, double>)(target => SheetModel.Need().ModifierTotal(target?.ToString() ?? "")));
        it.SetFunction("has", (Func<object?, bool>)(condition => SheetModel.Need().Has(condition?.ToString())));
    }

    private static bool Contains(object? collection, object? value)
    {
        var wanted = value?.ToString();
        return collection switch
        {
            null or string => false,
            IEnumerable<FormulaItem> items => items.Any(i => string.Equals((i is FormulaText ? i : i.Get("id"))?.ToString(), wanted, StringComparison.OrdinalIgnoreCase)),
            IEnumerable items => items.Cast<object?>().Any(i => string.Equals(i?.ToString(), wanted, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    /// <summary>The level a map-valued property gives a class (<c>levels: { Cleric: 1 }</c>), or -1 when it gives none.</summary>
    private static double LevelFor(object? item, object? className)
    {
        var levels = item is FormulaItem formulaItem ? formulaItem.Get("levels") : item;
        if (levels is not IDictionary map || className?.ToString() is not { } name) return -1;
        foreach (DictionaryEntry entry in map)
        {
            if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase)) return Number(entry.Value);
        }

        return -1;
    }

    /// <summary>The <paramref name="column"/> of the row whose <c>level</c> is <paramref name="row"/>; 0 when there is none.</summary>
    private static double Table(object? rows, object? row, object? column)
    {
        if (rows is not IEnumerable list || rows is string) return 0;
        var level = Number(row);
        var key = Number(column).ToString(CultureInfo.InvariantCulture);
        foreach (var candidate in list)
        {
            if (candidate is not IDictionary map) continue;
            if (map["level"] is not { } at || Number(at) != level) continue;
            return map.Contains(key) && map[key] is { } value ? Number(value) : 0;
        }

        return 0;
    }
}
