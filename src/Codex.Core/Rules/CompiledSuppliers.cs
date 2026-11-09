using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>Compiled code that failed: it threw, or returned what the host cannot read. The message names the system, what was
/// being worked out and the type, for the GM (rule 5).</summary>
public sealed class CompiledException(string message) : FormulaException(message);

/// <summary>
/// The compiled supplier (overhaul section 4, step 9): types in the DLLs beside system.yaml that implement a named function
/// (<see cref="ICodexFunction"/>), an event handler (<see cref="ICodexEventHandler"/>) or a widget (<see cref="ICodexWidget"/>),
/// named in system.yaml as <c>{ type: "Namespace.Type, Assembly" }</c>.
/// <list type="bullet">
/// <item><b>Loading.</b> The loader resolves every name once (<see cref="Resolve"/>): the assembly must be a DLL directly in
/// the system's folder, loaded into that folder's own <see cref="AssemblyLoadContext"/>, so two systems may ship different
/// versions of one dependency. What the host has (Codex.Plugin.Abstractions above all, whose interfaces must be the host's
/// types) comes from the host. A type the host has itself is not one a system may name.</item>
/// <item><b>Checks.</b> The type must be public, concrete, with a public parameterless constructor, and implement the interface
/// its place needs; a function takes 0 to 4 arguments. A mismatch is a load problem (<see cref="Problems"/>), and the function
/// has no value, the event does nothing, the widget says it cannot be shown.</item>
/// <item><b>Calls.</b> Plain data both ways, copied on the way in and out, so nothing a type keeps reaches the sheet. A function
/// is cached by its arguments, as a script's is. A failure is a <see cref="CompiledException"/> naming system, function and
/// type, which the sheet and the table turn into a warning; it never throws through a table command.</item>
/// </list>
/// Nothing limits what compiled code does: it runs in the server's process with its rights, with no time or memory limit. That
/// is why it is installed only by copying it onto the server, and why the Library says a system "contains compiled code".
/// </summary>
public static class CompiledSuppliers
{
    /// <summary>The most plain values a compiled supplier may return, as for a script.</summary>
    public const int MaxResultSize = SystemScripts.MaxResultSize;

    private const int CacheSize = 2048;

    // Keyed by the resolved types, which a system's localized copies share, so they share the cache too.
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, CompiledSupplier>, ConcurrentDictionary<string, ScriptValue>> Caches = new();
    private static readonly ConcurrentDictionary<string, SystemLoadContext> Contexts = new(StringComparer.Ordinal);

    /// <summary>Every <c>{ type }</c> the system names, with what names it and the interface its place needs.</summary>
    private static IEnumerable<(string What, string TypeName, Type Needs)> References(SystemDefinition system)
    {
        foreach (var (name, function) in system.Functions)
        {
            if (function.Type != null) yield return ($"The function \"{name}\"", function.Type, typeof(ICodexFunction));
        }

        foreach (var (type, raw) in system.Events)
        {
            if (YamlValues.Normalize(raw) is IDictionary<string, object> map && map.TryGetValue("type", out var named) && named != null)
            {
                yield return ($"The {type} handler", named.ToString()!, typeof(ICodexEventHandler));
            }
        }

        foreach (var (name, widget) in system.Widgets)
        {
            if (widget.Type != null) yield return ($"The widget \"{name}\"", widget.Type, typeof(ICodexWidget));
        }
    }

    /// <summary>Whether the system names compiled code anywhere.</summary>
    public static bool Names(SystemDefinition system) => References(system).Any();

    // ---- Loading ----

    /// <summary>Finds each type the system names in the DLLs of <paramref name="folder"/> and makes its one instance, into
    /// <see cref="SystemDefinition.Compiled"/>; a name that cannot be had is recorded with why.</summary>
    public static void Resolve(SystemDefinition system, string folder)
    {
        folder = Path.GetFullPath(folder);
        SystemLoadContext? context = null;
        foreach (var typeName in References(system).Select(r => r.TypeName).Distinct(StringComparer.Ordinal))
        {
            context ??= ContextFor(folder);
            system.AddCompiled(typeName, Find(typeName, folder, context));
        }
    }

    /// <summary>
    /// The folder's load context, for the DLLs it holds now. A reload (overhaul step 10) that finds the same DLLs gets the same
    /// context, and so the same types; one that finds a DLL added, removed or changed gets a new context, and a table that started
    /// under the old system keeps the old one's types until it ends. The old context is never unloaded: a type JIT-compiles its
    /// references to the host's assemblies lazily, through the context, and an unloading context cannot resolve them, so a live
    /// table's next call could fail. A replaced DLL's old copy therefore stays in memory until the server restarts. Assemblies are
    /// read from their bytes, so the files are never locked and can be replaced while the server runs.
    /// </summary>
    private static SystemLoadContext ContextFor(string folder)
    {
        var stamp = string.Join(";", Directory.GetFiles(folder, "*.dll").Order(StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + "=" + SystemFingerprint.Hash(f)));
        return Contexts.AddOrUpdate(folder, f => new SystemLoadContext(f, stamp),
            (f, existing) => existing.Stamp == stamp ? existing : new SystemLoadContext(f, stamp));
    }

    private static CompiledSupplier Find(string typeName, string folder, SystemLoadContext context)
    {
        var comma = typeName.IndexOf(',');
        if (comma <= 0 || comma == typeName.Length - 1)
        {
            return CompiledSupplier.Failed($"\"{typeName}\" is not \"Namespace.Type, Assembly\"");
        }

        var name = typeName[..comma].Trim();
        var assemblyName = typeName[(comma + 1)..].Split(',')[0].Trim();
        if (assemblyName.Length == 0 || assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || assemblyName.Contains(".."))
        {
            return CompiledSupplier.Failed($"\"{assemblyName}\" is not an assembly name");
        }

        var file = Path.Combine(folder, assemblyName + ".dll");
        if (!File.Exists(file)) return CompiledSupplier.Failed($"there is no {assemblyName}.dll in the system's folder");
        try
        {
            var assembly = context.Main(assemblyName, file);
            if (assembly == null) return CompiledSupplier.Failed($"{assemblyName}.dll is the host's own assembly, which a system cannot name");
            var type = assembly.GetType(name, throwOnError: false);
            if (type == null) return CompiledSupplier.Failed($"{assemblyName}.dll has no type {name}");
            if (!type.IsPublic || type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            {
                return CompiledSupplier.Failed($"{name} is not a public, concrete type");
            }

            if (type.GetConstructor(Type.EmptyTypes) == null) return CompiledSupplier.Failed($"{name} has no public constructor without parameters");
            return new CompiledSupplier(Activator.CreateInstance(type), null);
        }
        catch (TargetInvocationException ex)
        {
            return CompiledSupplier.Failed($"{name}'s constructor failed: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException or TypeLoadException)
        {
            return CompiledSupplier.Failed($"{assemblyName}.dll does not load: {ex.Message}");
        }
    }

    /// <summary>One system folder's assemblies. The host's come from the host, by name; the rest from the folder, by file name.</summary>
    private sealed class SystemLoadContext(string folder, string stamp) : AssemblyLoadContext("codex-system " + folder)
    {
        private readonly object _gate = new();

        /// <summary>The DLLs' names and hashes when this context was made.</summary>
        public string Stamp { get; } = stamp;

        /// <summary>The system's own assembly, or null when the name is the host's (a system may not name a host type).</summary>
        public Assembly? Main(string name, string file)
        {
            if (Host(new AssemblyName(name)) != null) return null;
            lock (_gate)
            {
                return Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) ?? FromBytes(file);
            }
        }

        private Assembly FromBytes(string file)
        {
            using var bytes = new MemoryStream(File.ReadAllBytes(file));
            return LoadFromStream(bytes);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (Host(name) is { } shared) return shared;
            var file = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(file) ? FromBytes(file) : null;
        }

        private static Assembly? Host(AssemblyName name)
        {
            try
            {
                return Default.LoadFromAssemblyName(new AssemblyName(name.Name!));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                return null;
            }
        }
    }

    // ---- Load problems ----

    /// <summary>What is wrong with the compiled code the system names: a type that could not be found or made, one that does not
    /// implement what its place needs, a function of more than four arguments, a function or handler with a second supplier.</summary>
    public static IReadOnlyList<string> Problems(SystemDefinition system)
    {
        var problems = new List<string>();
        foreach (var (what, typeName, needs) in References(system))
        {
            if (!system.Compiled.TryGetValue(typeName, out var supplier))
            {
                problems.Add($"{what} names the type {typeName}, which was not looked for: compiled code is read only from a system's folder.");
            }
            else if (supplier.Problem != null)
            {
                problems.Add($"{what} names the type {typeName}: {supplier.Problem}.");
            }
            else if (!needs.IsInstanceOfType(supplier.Instance))
            {
                problems.Add($"{what} names the type {typeName}, which is not an {needs.Name}.");
            }
            else if (supplier.Instance is ICodexFunction function && needs == typeof(ICodexFunction) && Arity(function) is not (>= 0 and <= SheetFormulas.MaxFunctionArguments))
            {
                problems.Add($"{what} ({typeName}) takes {Arity(function)?.ToString(CultureInfo.InvariantCulture) ?? "an unknown number of"} arguments; a function takes 0 to {SheetFormulas.MaxFunctionArguments}.");
            }
        }

        foreach (var (name, function) in system.Functions.Where(f => f.Value.Type != null))
        {
            if (function.Formula != null || function.Script != null) problems.Add($"The function \"{name}\" has a type and a {(function.Script != null ? "script" : "formula")}; the type is used.");
        }

        foreach (var (name, widget) in system.Widgets)
        {
            if (string.IsNullOrWhiteSpace(widget.Type)) problems.Add($"The widget \"{name}\" names no type; a widget is a compiled ICodexWidget.");
            if (SheetLayout.GenericWidgets.Contains(name) || SheetLayout.BuiltinWidgets.ContainsKey(name)) problems.Add($"The widget \"{name}\" has the name of the host's own widget, which a layout means instead.");
        }

        return problems;
    }

    private static int? Arity(ICodexFunction function)
    {
        try
        {
            return function.Arity;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The instance a name resolved to, when it is a <typeparamref name="T"/>.</summary>
    private static T? Instance<T>(SystemDefinition system, string typeName) where T : class =>
        system.Compiled.TryGetValue(typeName, out var supplier) ? supplier.Instance as T : null;

    /// <summary>How many arguments the named compiled function takes, or null when it cannot be had.</summary>
    public static int? Arity(SystemDefinition system, string typeName) =>
        Instance<ICodexFunction>(system, typeName) is { } function && Arity(function) is { } arity and >= 0 and <= SheetFormulas.MaxFunctionArguments ? arity : null;

    // ---- Calls ----

    /// <summary>Calls a compiled function with plain arguments and reads its number back.</summary>
    /// <exception cref="CompiledException">It cannot be had, failed, or returned what a sheet cannot hold.</exception>
    public static ScriptValue Function(SystemDefinition system, string name, string typeName, IReadOnlyList<object?> arguments)
    {
        var cache = Caches.GetValue(system.Compiled, _ => new ConcurrentDictionary<string, ScriptValue>(StringComparer.Ordinal));
        var key = SystemScripts.CacheKey(typeName, arguments);
        if (key != null && cache.TryGetValue(key, out var known)) return known;
        var value = Call(system, $"the function \"{name}\"", typeName, (ICodexFunction function) =>
        {
            var result = function.Call(arguments.ToList());
            var parts = (result.Parts ?? Array.Empty<KeyValuePair<string, double>>()).Take(64).Select(p => (p.Key ?? "", Finite(p.Value))).ToList();
            return new ScriptValue(Finite(result.Value), parts);
        });
        if (key != null)
        {
            if (cache.Count >= CacheSize) cache.Clear();
            cache[key] = value;
        }

        return value;
    }

    /// <summary>Calls a compiled handler as <c>Handle(ev, sheet, ctx)</c> and reads back its intents, their fields copied to
    /// plain data.</summary>
    /// <exception cref="CompiledException">It cannot be had, failed, or returned what cannot be read.</exception>
    public static IReadOnlyList<(string Kind, IReadOnlyDictionary<string, object?> Fields)> Handler(SystemDefinition system, string eventType, string typeName,
        IReadOnlyDictionary<string, object?> ev, IReadOnlyDictionary<string, object?> sheet, SystemScripts.HandlerContext context) =>
        Call(system, $"the {eventType} handler", typeName, (ICodexEventHandler handler) =>
        {
            var budget = MaxResultSize;
            var returned = handler.Handle(PlainMap(ev, ref budget), PlainMap(sheet, ref budget), new Context(context)) ?? Array.Empty<CodexIntent>();
            budget = MaxResultSize;
            var intents = new List<(string, IReadOnlyDictionary<string, object?>)>();
            foreach (var intent in returned)
            {
                if (intent == null || string.IsNullOrEmpty(intent.Kind)) throw new ResultException("it returned an intent with no kind");
                intents.Add((intent.Kind, PlainMap(intent.Fields ?? new Dictionary<string, object?>(), ref budget)));
            }

            return (IReadOnlyList<(string, IReadOnlyDictionary<string, object?>)>)intents;
        });

    /// <summary>What a compiled widget shows, its actions' intents copied to plain data.</summary>
    /// <exception cref="CompiledException">It cannot be had, failed, or returned nothing.</exception>
    public static WidgetContent Widget(SystemDefinition system, string name, WidgetRequest request)
    {
        var typeName = system.Widgets.TryGetValue(name, out var definition) ? definition.Type : null;
        if (typeName == null) throw new CompiledException($"{system.Id}: there is no widget \"{name}\"");
        return Call(system, $"the widget \"{name}\"", typeName, (ICodexWidget widget) =>
        {
            var budget = MaxResultSize;
            var content = widget.Render(request with { Sheet = PlainMap(request.Sheet, ref budget), Keys = request.Keys.ToList() })
                ?? throw new ResultException("it returned nothing to show");
            budget = MaxResultSize;
            var blocks = new List<WidgetBlock>();
            foreach (var block in content.Blocks ?? Array.Empty<WidgetBlock>())
            {
                if (--budget < 0) throw new ResultException($"it returned more than {MaxResultSize} values");
                blocks.Add(block switch
                {
                    WidgetAction action => action with
                    {
                        Intents = (action.Intents ?? Array.Empty<CodexIntent>()).Select(i => new CodexIntent(i.Kind, PlainMap(i.Fields ?? new Dictionary<string, object?>(), ref budget))).ToList()
                    },
                    WidgetPills pills => pills with { Items = (pills.Items ?? Array.Empty<string>()).ToList() },
                    null => throw new ResultException("it returned an empty block"),
                    var other => other
                });
            }

            return new WidgetContent(blocks);
        });
    }

    private static T Call<TSupplier, T>(SystemDefinition system, string what, string typeName, Func<TSupplier, T> call) where TSupplier : class
    {
        var named = $"{system.Id}: {what} (compiled {typeName})";
        if (!system.Compiled.TryGetValue(typeName, out var supplier)) throw new CompiledException($"{named}: the type was not loaded");
        if (supplier.Problem != null) throw new CompiledException($"{named}: {supplier.Problem}");
        if (supplier.Instance is not TSupplier instance) throw new CompiledException($"{named}: the type is not an {typeof(TSupplier).Name}");
        try
        {
            return call(instance);
        }
        catch (FormulaException ex) when (ex is not CompiledException)
        {
            // A function it called through the context failed; that one's message names it.
            throw new CompiledException($"{named}: {ex.Message}");
        }
        catch (CompiledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
        {
            var message = $"{named}: {(ex is ResultException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}")}";
            System.Diagnostics.Trace.TraceWarning(message);
            throw new CompiledException(message);
        }
    }

    private sealed class ResultException(string message) : Exception(message);

    private sealed class Context(SystemScripts.HandlerContext context) : ICodexContext
    {
        public double Call(string function, params object?[] arguments)
        {
            var budget = MaxResultSize;
            return context.CallFunction(function, (arguments ?? Array.Empty<object?>()).Select(a => Plain(a, ref budget)).ToArray());
        }

        public int D(int sides) =>
            sides is >= 2 and <= 1000 ? context.Roll(sides) : throw new ResultException("D takes a whole number of sides, from 2 to 1000");
    }

    // ---- Plain data ----

    private static double Finite(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= 1e15 ? value : throw new ResultException($"it returned {value.ToString(CultureInfo.InvariantCulture)}, which is not a number a sheet can hold");

    private static Dictionary<string, object?> PlainMap(IReadOnlyDictionary<string, object?> map, ref int budget)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in map) copy[key] = Plain(value, ref budget);
        return copy;
    }

    /// <summary>A copy of <paramref name="value"/> as the plain data a script gets and returns: null, a flag, text, a number (as a
    /// double, whatever its .NET type), a list, or a map by text. Anything else is not plain data.</summary>
    internal static object? Plain(object? value, ref int budget)
    {
        if (--budget < 0) throw new ResultException($"it returned more than {MaxResultSize} values");
        switch (value)
        {
            case null: return null;
            case bool or string: return value;
            case double or float or decimal or int or long or short or byte or sbyte or uint or ulong or ushort:
                return Finite(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case IDictionary<string, object?> map:
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, item) in map) copy[key] = Plain(item, ref budget);
                return copy;
            case IReadOnlyDictionary<string, object?> map:
                var readOnly = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, item) in map) readOnly[key] = Plain(item, ref budget);
                return readOnly;
            case IDictionary map:
                var untyped = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in map) untyped[entry.Key?.ToString() ?? ""] = Plain(entry.Value, ref budget);
                return untyped;
            case IEnumerable list:
                var items = new List<object?>();
                foreach (var item in list) items.Add(Plain(item, ref budget));
                return items;
            default:
                throw new ResultException($"it gave a {value.GetType().Name}, which is not plain data (numbers, text, flags, lists and maps of them)");
        }
    }
}
