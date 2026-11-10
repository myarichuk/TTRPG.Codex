using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

// The compiled supplier (overhaul section 4, step 9): a DLL beside system.yaml implements a named function, an event handler or
// a sheet widget, and system.yaml names its type:
//
//   functions: { hitPoints: { type: "Acme.Street.HitPoints, Acme.Street" } }
//   events:    { damage:    { type: "Acme.Street.OnDamage, Acme.Street" } }
//   widgets:   { cyberware: { type: "Acme.Street.Cyberware, Acme.Street" } }
//
// The contract is the script supplier's (rule 1): plain data in, plain data out. A compiled supplier is handed numbers, text,
// flags, and lists and maps of them, made afresh for each call; it never sees the world, a repository or a live object, and
// what it changes on its arguments is not kept. It rolls only through the context's D (rule 3), and does its rounding with
// CodexMath, the host's (rule 4). Each type needs a public parameterless constructor; one instance serves every call, from
// any thread, so it keeps no state.
//
// A DLL runs with the server's rights: nothing sandboxes it the way a script is. It is installed only by copying it onto the
// server machine, never uploaded, and the Library says a system with one "contains compiled code".

/// <summary>A named function, callable from formulas like a built-in: <c>hitPoints(BODY, WILL)</c>.</summary>
public interface ICodexFunction
{
    /// <summary>How many arguments it takes, 0 to 4. Each is a number (<see cref="double"/>), text or a flag.</summary>
    int Arity { get; }

    /// <summary>The value, with its working when it has some. Given the same arguments it returns the same value: results are
    /// cached by them.</summary>
    CodexValue Call(IReadOnlyList<object?> arguments);
}

/// <summary>A function's result: a number, and optionally the working that led to it, as labelled amounts.</summary>
public readonly record struct CodexValue(double Value, IReadOnlyList<KeyValuePair<string, double>>? Parts = null)
{
    public static implicit operator CodexValue(double value) => new(value);
}

/// <summary>
/// An event handler: what it returns is applied in order, as a YAML or script handler's intents are. <c>sheet</c> is the view a
/// script handler gets: <c>kind</c>, <c>attributes</c>, <c>values</c>, <c>tracks</c>, <c>lists</c> and <c>conditions</c>.
/// </summary>
public interface ICodexEventHandler
{
    IReadOnlyList<CodexIntent> Handle(IReadOnlyDictionary<string, object?> ev, IReadOnlyDictionary<string, object?> sheet, ICodexContext context);
}

/// <summary>What a handler may call on: the system's named functions and the host's dice.</summary>
public interface ICodexContext
{
    /// <summary>Calls one of the system's named functions, whoever supplies it.</summary>
    double Call(string function, params object?[] arguments);

    /// <summary>Rolls one die of <paramref name="sides"/> through the table, which logs the face.</summary>
    int D(int sides);
}

/// <summary>One intent, as system.yaml writes it: <c>{ adjust: { track: HP, by: -3 } }</c> is
/// <c>new CodexIntent("adjust", new() { ["track"] = "HP", ["by"] = -3 })</c>. <c>actor</c> among the fields names another
/// actor's sheet.</summary>
public sealed record CodexIntent(string Kind, IReadOnlyDictionary<string, object?> Fields)
{
    public static CodexIntent Of(string kind, params (string Key, object? Value)[] fields)
    {
        var map = new Dictionary<string, object?>(System.StringComparer.Ordinal);
        foreach (var (key, value) in fields) map[key] = value;
        return new CodexIntent(kind, map);
    }
}

/// <summary>
/// A sheet widget a layout places by name (<c>{ widget: cyberware }</c>). It says what to show as plain data, and the host
/// draws it in the sheet's own style; it is never handed the page. An action's intents apply to the sheet as a track widget's
/// do, so they are the ones a sheet takes on its own: <c>adjust</c>, <c>set</c>, <c>mark</c>, <c>clear</c> and <c>condition</c>.
/// </summary>
public interface ICodexWidget
{
    WidgetContent Render(WidgetRequest request);
}

/// <summary>What a widget is given: the sheet's view (as an event handler's), and the layout entry that placed it.</summary>
/// <param name="Language">The reader's language tag ("en", "ru"), for the widget's own text.</param>
public sealed record WidgetRequest(IReadOnlyDictionary<string, object?> Sheet, string? Key, IReadOnlyList<string> Keys, string? Label, string Language);

/// <summary>What a widget shows, top to bottom.</summary>
public sealed record WidgetContent(IReadOnlyList<WidgetBlock> Blocks)
{
    public static WidgetContent Of(params WidgetBlock[] blocks) => new(blocks);
}

/// <summary>One thing a widget shows. The host draws each kind; text is shown as text, never as markup.</summary>
public abstract record WidgetBlock;

public sealed record WidgetHeading(string Text) : WidgetBlock;

public sealed record WidgetText(string Text) : WidgetBlock;

/// <summary>A labelled value, with an optional note beside it (its working, a range).</summary>
public sealed record WidgetRow(string Label, string Value, string? Note = null) : WidgetBlock;

/// <summary>Short items side by side: conditions, tags, what slots hold.</summary>
public sealed record WidgetPills(string? Label, IReadOnlyList<string> Items) : WidgetBlock;

/// <summary>A button: pressing it applies <see cref="Intents"/> to the sheet, for someone who may edit it.</summary>
public sealed record WidgetAction(string Label, IReadOnlyList<CodexIntent> Intents) : WidgetBlock;

/// <summary>
/// The host's arithmetic (overhaul section 4, rule 4), the same code formulas run: <c>div</c> is whole division rounded down,
/// so <c>div(7, 2)</c> is 3 and <c>div(-7, 2)</c> is -4, whatever C# makes of <c>7 / 2</c> or <c>-7 / 2</c>.
/// </summary>
public static class CodexMath
{
    public static double Floor(double x) => System.Math.Floor(x);

    public static double Ceil(double x) => System.Math.Ceiling(x);

    public static double Div(double a, double b) => System.Math.Floor(a / b);
}

/// <summary>What a compiled type name in system.yaml came to at load: an instance of it, or why there is none (no such DLL,
/// no such type, not the interface its place needs).</summary>
public sealed record CompiledSupplier(object? Instance, string? Problem)
{
    public static CompiledSupplier Failed(string problem) => new(null, problem);
}
