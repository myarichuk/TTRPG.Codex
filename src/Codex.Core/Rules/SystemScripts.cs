using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Acornima.Ast;
using Codex.Plugin.Abstractions;
using Jint;
using Jint.Native;
using Jint.Runtime;
using JsFunction = Jint.Native.Function.Function;
using Jint.Native.Object;

namespace Codex.Core.Rules;

/// <summary>A script that failed: it threw, ran too long or too far, or returned what the host cannot read. The message names
/// the system, what was being worked out, the script and (when the engine knows it) the line, for the GM.</summary>
public sealed class ScriptException(string message) : FormulaException(message);

/// <summary>One number a script function worked out, with its working: <c>{ value, parts: [[label, amount], ...] }</c>, or a
/// bare number (no parts).</summary>
public sealed record ScriptValue(double Value, IReadOnlyList<(string Label, double Amount)> Parts);

/// <summary>
/// The script supplier (overhaul section 4): a system's <c>.js</c> files, run by Jint. A script is an ES module with named
/// exports, named from system.yaml as <c>file#export</c>. It is handed plain data (numbers, text, flags, and objects and arrays
/// of them, made afresh for each call, so changing them changes nothing) and its result is read back as plain data. It has no
/// .NET access (<c>AllowClr</c> is off), no file or module loading, no <c>Math.random</c> (the host owns randomness:
/// <c>ctx.d(sides)</c> in a handler), and limits on time, statements, memory, recursion, array size and regular expressions.
/// <para>Each file is parsed once. A Jint engine is not thread-safe, so calls rent one from a small pool; an engine that
/// failed is thrown away rather than returned. A module's own variables therefore do not carry from one call to the next:
/// a script keeps no state.</para>
/// <para>A function's result depends on its arguments alone (it is given nothing else), so results are cached by them.</para>
/// </summary>
public sealed class SystemScripts
{
    /// <summary>The limits on one call. Low: a rule is a few lines of arithmetic.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    public const int MaxStatements = 100_000;
    public const long MaxMemoryBytes = 16 * 1024 * 1024;
    public const int MaxRecursion = 64;
    public const uint MaxArraySize = 10_000;

    /// <summary>How much plain data a script may return: values, entries and elements counted together.</summary>
    public const int MaxResultSize = 10_000;

    private const int PoolSize = 4;
    private const int CacheSize = 2048;

    // Keyed by the sources, which a system's localized copies share, so they share engines and the cache too.
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, string>, SystemScripts> BySources = new();

    private readonly string _systemId;
    private readonly Dictionary<string, Prepared<Module>> _prepared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parseProblems = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Lease> _pool = new();
    private readonly ConcurrentDictionary<string, ScriptValue> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int?> _arity = new(StringComparer.Ordinal);

    public static SystemScripts For(SystemDefinition system) => BySources.GetValue(system.Scripts, sources => new SystemScripts(system.Id, sources));

    private SystemScripts(string systemId, IReadOnlyDictionary<string, string> sources)
    {
        _systemId = systemId;
        foreach (var (file, source) in sources)
        {
            try
            {
                _prepared[file] = Engine.PrepareModule(source, file);
            }
            catch (Exception ex)
            {
                _parseProblems[file] = ex.InnerException is Acornima.ParseErrorException parse
                    ? $"line {parse.LineNumber}: {parse.Message}"
                    : Located(ex) is { } line ? $"line {line}: {ex.Message}" : ex.Message;
            }
        }
    }

    /// <summary>The script files a system names, from <c>functions</c> and <c>events</c>.</summary>
    public static IReadOnlyList<string> FilesNamed(SystemDefinition system) =>
        References(system).Select(r => Split(r.Reference).File).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Every <c>file#export</c> the system names, with what names it.</summary>
    private static IEnumerable<(string What, string Reference)> References(SystemDefinition system)
    {
        foreach (var (name, function) in system.Functions)
        {
            if (function.Script != null) yield return ($"The function \"{name}\"", function.Script);
        }

        foreach (var (type, raw) in system.Events)
        {
            if (YamlValues.Normalize(raw) is IDictionary<string, object> map && map.TryGetValue("script", out var script) && script != null)
            {
                yield return ($"The {type} handler", script.ToString()!);
            }
        }
    }

    internal static (string File, string Export) Split(string reference)
    {
        var hash = reference.IndexOf('#');
        return hash < 0 ? (reference.Trim(), "") : (reference[..hash].Trim(), reference[(hash + 1)..].Trim());
    }

    // ---- Load problems ----

    /// <summary>What is wrong with the system's scripts as the loader reports it: a reference that is not <c>file#export</c>,
    /// a file that is not there (or not one a system may have), a file that does not parse, an export that is not a function.
    /// Each one's function has no value, or its event does nothing.</summary>
    public static IReadOnlyList<string> Problems(SystemDefinition system)
    {
        var problems = new List<string>();
        var references = References(system).ToList();
        if (references.Count == 0) return problems;
        var scripts = For(system);
        foreach (var (file, problem) in scripts._parseProblems) problems.Add($"The script {file} does not parse ({problem}).");
        foreach (var (what, reference) in references)
        {
            var (file, export) = Split(reference);
            if (file.Length == 0 || export.Length == 0)
            {
                problems.Add($"{what} names the script \"{reference}\", which is not file#export (rules.js#onDamage).");
            }
            else if (!system.Scripts.ContainsKey(file))
            {
                problems.Add($"{what} names the script {file}, which is not in the system's folder (a .js file beside system.yaml, at most {Systems.SystemDefinitionLoader.MaxScriptBytes / 1024} KB).");
            }
            else if (!scripts._parseProblems.ContainsKey(file) && scripts.ExportProblem(file, export) is { } problem)
            {
                problems.Add($"{what} names {reference}: {problem}");
            }
        }

        foreach (var (name, function) in system.Functions.Where(f => f.Value.Script != null))
        {
            if (function.Formula != null) problems.Add($"The function \"{name}\" has both a formula and a script; the script is used.");
            if (scripts.Arity(function.Script!) is > SheetFormulas.MaxFunctionArguments)
            {
                problems.Add($"The function \"{name}\" ({function.Script}) takes more than {SheetFormulas.MaxFunctionArguments} arguments, so formulas cannot call it.");
            }
        }

        return problems;
    }

    private string? ExportProblem(string file, string export)
    {
        try
        {
            return Use(lease => lease.Export(file, export) is JsFunction ? null : $"{file} has no exported function \"{export}\".");
        }
        catch (ScriptException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"{file} fails as it loads ({Describe(ex)}).";
        }
    }

    // ---- Functions ----

    /// <summary>How many parameters the exported function declares, or null when it cannot be had.</summary>
    public int? Arity(string reference) => _arity.GetOrAdd(reference, r =>
    {
        var (file, export) = Split(r);
        if (!_prepared.ContainsKey(file)) return null;
        try
        {
            return Use(lease => lease.Export(file, export) is JsFunction f ? (int?)(int)TypeConverter.ToNumber(f.Get("length")) : null);
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>Calls a script function with plain arguments (numbers, text, flags) and reads its number back.</summary>
    /// <param name="name">The function's name in system.yaml, for the message when it fails.</param>
    /// <exception cref="ScriptException">It failed, or returned something that is not a number.</exception>
    public ScriptValue Function(string name, string reference, IReadOnlyList<object?> arguments)
    {
        var key = CacheKey(reference, arguments);
        if (key != null && _cache.TryGetValue(key, out var known)) return known;
        var value = Call($"the function \"{name}\"", reference, engine => arguments.Select(a => ToJs(engine, a)).ToArray(), (_, result) => ReadValue(result));
        if (key != null)
        {
            if (_cache.Count >= CacheSize) _cache.Clear();
            _cache[key] = value;
        }

        return value;
    }

    internal static string? CacheKey(string reference, IReadOnlyList<object?> arguments)
    {
        var key = new System.Text.StringBuilder(reference);
        foreach (var argument in arguments)
        {
            key.Append('\u0001');
            switch (argument)
            {
                case null: key.Append('n'); break;
                case bool flag: key.Append(flag ? "t" : "f"); break;
                case double number: key.Append('d').Append(number.ToString("R", CultureInfo.InvariantCulture)); break;
                case string text: key.Append('s').Append(text); break;
                default: return null;
            }
        }

        return key.ToString();
    }

    private static ScriptValue ReadValue(JsValue result)
    {
        if (result.IsNumber()) return new ScriptValue(Finite(result.AsNumber()), Array.Empty<(string, double)>());
        if (result is ObjectInstance o && !o.IsArray() && o.Get("value") is { } value && value.IsNumber())
        {
            var parts = new List<(string, double)>();
            if (o.Get("parts") is JsArray list)
            {
                for (uint i = 0; i < Math.Min(list.Length, 64u); i++)
                {
                    if (list[i] is JsArray { Length: 2 } pair && pair[1].IsNumber()) parts.Add((TypeConverter.ToString(pair[0]), Finite(pair[1].AsNumber())));
                }
            }

            return new ScriptValue(Finite(value.AsNumber()), parts);
        }

        throw new ResultException($"it returned {Shape(result)}, not a number or {{ value, parts }}");
    }

    private static double Finite(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= 1e15 ? value : throw new ResultException($"it returned {value.ToString(CultureInfo.InvariantCulture)}, which is not a number a sheet can hold");

    // ---- Handlers ----

    /// <summary>What a handler gets besides the event and the sheet: <c>ctx.call(name, ...args)</c> calls one of the system's
    /// named functions (formula or script), <c>ctx.d(sides)</c> rolls a die through the host.</summary>
    public sealed record HandlerContext(Func<string, object?[], double> CallFunction, Func<int, int> Roll);

    /// <summary>Calls a script handler as <c>export(ev, sheet, ctx)</c> and reads back what it returned: plain data (lists,
    /// maps by key, numbers, text, flags, nulls), its intents for the caller to read.</summary>
    /// <exception cref="ScriptException">It failed, or returned what cannot be read.</exception>
    public object? Handler(string type, string reference, IReadOnlyDictionary<string, object?> ev, IReadOnlyDictionary<string, object?> sheet, HandlerContext context) =>
        Call($"the {type} handler", reference, engine => new[] { ToJs(engine, ev), ToJs(engine, sheet), Context(engine, context) }, (_, result) =>
        {
            var budget = MaxResultSize;
            return FromJs(result, ref budget);
        });

    private static JsValue Context(Engine engine, HandlerContext context)
    {
        var ctx = new JsObject(engine);
        ctx.FastSetDataProperty("call", new Jint.Runtime.Interop.ClrFunction(engine, "call", (_, args) =>
        {
            if (args.Length == 0 || !args[0].IsString()) throw new ResultException("ctx.call takes a function's name first");
            var budget = MaxResultSize;
            var rest = args.Skip(1).Select(a => FromJs(a, ref budget)).ToArray();
            return new JsNumber(context.CallFunction(args[0].AsString(), rest));
        }, 1));
        ctx.FastSetDataProperty("d", new Jint.Runtime.Interop.ClrFunction(engine, "d", (_, args) =>
        {
            var sides = args.Length > 0 && args[0].IsNumber() ? args[0].AsNumber() : 0;
            if (sides is < 2 or > 1000 || sides != Math.Floor(sides)) throw new ResultException("ctx.d takes a whole number of sides, from 2 to 1000");
            return new JsNumber(context.Roll((int)sides));
        }, 1));
        return ctx;
    }

    // ---- Calls ----

    /// <summary>Thrown by the host while reading what a script gave it; becomes a <see cref="ScriptException"/>.</summary>
    private sealed class ResultException(string message) : Exception(message);

    private T Call<T>(string what, string reference, Func<Engine, JsValue[]> arguments, Func<Engine, JsValue, T> read)
    {
        var (file, export) = Split(reference);
        var named = $"{_systemId}: {what} (script {file}#{export})";
        if (_parseProblems.TryGetValue(file, out var parse)) throw new ScriptException($"{named}: {file} does not parse ({parse})");
        if (!_prepared.ContainsKey(file)) throw new ScriptException($"{named}: there is no script {file}");
        try
        {
            return Use(lease =>
            {
                if (lease.Export(file, export) is not JsFunction function) throw new ResultException($"{file} has no exported function \"{export}\"");
                var result = lease.Engine.Call(function, JsValue.Undefined, arguments(lease.Engine));
                return read(lease.Engine, result);
            });
        }
        catch (ScriptException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
        {
            var message = $"{named}{(Located(ex) is { } line ? $", line {line}" : "")}: {Describe(ex)}";
            System.Diagnostics.Trace.TraceWarning(message);
            throw new ScriptException(message);
        }
    }

    /// <summary>Runs <paramref name="use"/> on a pooled engine with its limits reset. An engine that failed is not returned:
    /// a script stopped half-way may have left its module in any state.</summary>
    private T Use<T>(Func<Lease, T> use)
    {
        var lease = _pool.TryTake(out var pooled) ? pooled : new Lease(this);
        lease.Engine.Constraints.Reset();
        var result = use(lease);
        if (_pool.Count < PoolSize) _pool.Add(lease);
        return result;
    }

    /// <summary>One engine with the system's modules added; each is imported (run) the first time an export of it is asked for.</summary>
    private sealed class Lease
    {
        private readonly SystemScripts _owner;
        private readonly Dictionary<string, ObjectInstance> _imported = new(StringComparer.Ordinal);

        public Engine Engine { get; }

        public Lease(SystemScripts owner)
        {
            _owner = owner;
            Engine = new Engine(options =>
            {
                options.Strict();
                options.TimeoutInterval(Timeout);
                options.MaxStatements(MaxStatements);
                options.LimitMemory(MaxMemoryBytes);
                options.LimitRecursion(MaxRecursion);
                options.Constraints.MaxArraySize = MaxArraySize;
                options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(100);
            });
            Engine.Execute("""
                Object.defineProperty(Math, "random", { value: function () { throw new Error("Math.random is not for rules: return a roll intent, or roll with ctx.d(sides)"); } });
                """);
            foreach (var (file, prepared) in owner._prepared)
            {
                var module = prepared;
                Engine.Modules.Add(file, builder => builder.AddModule(module));
            }
        }

        public JsValue Export(string file, string export)
        {
            if (!_imported.TryGetValue(file, out var exports))
            {
                if (!_owner._prepared.ContainsKey(file)) return JsValue.Undefined;
                exports = Engine.Modules.Import(file);
                _imported[file] = exports;
            }

            return exports.Get(export);
        }
    }

    private static int? Located(Exception ex) =>
        JintException.TryGetJavaScriptLocation(ex, out var location) && location.Start.Line > 0 ? location.Start.Line : null;

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => $"it ran longer than {Timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms",
        Jint.Runtime.StatementsCountOverflowException => $"it ran more than {MaxStatements} statements",
        Jint.Runtime.MemoryLimitExceededException => $"it used more than {MaxMemoryBytes / (1024 * 1024)} MB",
        Jint.Runtime.RecursionDepthOverflowException => $"it called itself deeper than {MaxRecursion} times",
        _ => ex.Message
    };

    // ---- Plain data both ways ----

    /// <summary>Plain .NET data as a fresh JS value: maps as objects, lists as arrays, numbers, text and flags as themselves.</summary>
    internal static JsValue ToJs(Engine engine, object? value)
    {
        switch (value)
        {
            case null:
                return JsValue.Null;
            case JsValue js:
                return js;
            case bool flag:
                return flag ? JsBoolean.True : JsBoolean.False;
            case string text:
                return new JsString(text);
            case IDictionary<string, object?> map:
                var o = new JsObject(engine);
                foreach (var (key, entry) in map) o.FastSetDataProperty(key, ToJs(engine, entry));
                return o;
            case IReadOnlyDictionary<string, object?> map:
                var r = new JsObject(engine);
                foreach (var (key, entry) in map) r.FastSetDataProperty(key, ToJs(engine, entry));
                return r;
            case IEnumerable list:
                return new JsArray(engine, list.Cast<object?>().Select(e => ToJs(engine, e)).ToArray());
            case IConvertible number:
                return new JsNumber(number.ToDouble(CultureInfo.InvariantCulture));
            default:
                return new JsString(value.ToString() ?? "");
        }
    }

    /// <summary>A JS value as plain .NET data: objects as maps (own enumerable keys), arrays as lists, numbers as doubles.
    /// Functions, symbols and big integers are not data, and more than <paramref name="budget"/> values is too much.</summary>
    internal static object? FromJs(JsValue value, ref int budget)
    {
        if (--budget < 0) throw new ResultException($"it returned more than {MaxResultSize} values");
        if (value.IsNull() || value.IsUndefined()) return null;
        if (value.IsBoolean()) return value.AsBoolean();
        if (value.IsNumber()) return Finite(value.AsNumber());
        if (value.IsString()) return value.AsString();
        if (value is JsArray array)
        {
            var list = new List<object?>((int)Math.Min(array.Length, MaxArraySize));
            for (uint i = 0; i < array.Length; i++) list.Add(FromJs(array[i], ref budget));
            return list;
        }

        if (value is ObjectInstance o && value is not JsFunction)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in o.GetOwnPropertyKeys(Types.String))
            {
                if (o.GetOwnProperty(key) is not { Enumerable: true }) continue;
                map[key.ToString()] = FromJs(o.Get(key), ref budget);
            }

            return map;
        }

        throw new ResultException($"it returned {Shape(value)}, which is not plain data");
    }

    private static string Shape(JsValue value) => value switch
    {
        JsFunction => "a function",
        _ when value.IsUndefined() => "nothing",
        _ when value.IsNull() => "null",
        _ when value.IsString() => "text",
        _ when value.IsBoolean() => "a flag",
        JsArray => "a list",
        _ => value.Type.ToString().ToLowerInvariant()
    };
}
