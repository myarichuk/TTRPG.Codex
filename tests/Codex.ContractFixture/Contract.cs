using Codex.Plugin.Abstractions;

namespace Codex.ContractFixture;

// Each mirrors a formula in ../Codex.Tests/Fixtures/contract/system.yaml and an export of its rules.js; the contract fixture's
// spec expects the same numbers and intents from all three.

/// <summary><c>10 + 5 * ceil((body + will) / 2)</c>, with its working.</summary>
public sealed class HitPoints : ICodexFunction
{
    public int Arity => 2;

    public CodexValue Call(IReadOnlyList<object?> arguments)
    {
        var body = (double)arguments[0]!;
        var will = (double)arguments[1]!;
        return new CodexValue(10 + 5 * CodexMath.Ceil((body + will) / 2),
            new[] { new KeyValuePair<string, double>("base", 10), new KeyValuePair<string, double>("BODY+WILL", body + will) });
    }
}

/// <summary><c>div(n, 2)</c>: rounded down, so -7 halves to -4, which C#'s <c>(int)-7 / 2</c> would make -3.</summary>
public sealed class Half : ICodexFunction
{
    public int Arity => 1;

    public CodexValue Call(IReadOnlyList<object?> arguments) => CodexMath.Div((double)arguments[0]!, 2);
}

/// <summary><c>flag ? n : -n</c>, and the length of a text when one is given: arguments are numbers, text and flags.</summary>
public sealed class Signed : ICodexFunction
{
    public int Arity => 2;

    public CodexValue Call(IReadOnlyList<object?> arguments) => arguments[0] switch
    {
        string text => arguments[1] is true ? text.Length : -text.Length,
        var number => arguments[1] is true ? (double)number! : -(double)number!
    };
}

/// <summary>Fails, as the script's <c>boom</c> does: the sheet leaves the value out and says why.</summary>
public sealed class Boom : ICodexFunction
{
    public int Arity => 0;

    public CodexValue Call(IReadOnlyList<object?> arguments) => throw new InvalidOperationException("boom");
}

/// <summary>The <c>hit</c> handler: armor stops its value of the damage; what is left comes off HP, and a hit through armor
/// ablates it by one; 5 or more through adds Wounded.</summary>
public sealed class OnHit : ICodexEventHandler
{
    public IReadOnlyList<CodexIntent> Handle(IReadOnlyDictionary<string, object?> ev, IReadOnlyDictionary<string, object?> sheet, ICodexContext context)
    {
        var tracks = (IReadOnlyDictionary<string, object?>)sheet["tracks"]!;
        var through = Math.Max(0, (double)ev["amount"]! - (double)tracks["SP"]!);
        var intents = new List<CodexIntent> { CodexIntent.Of("adjust", ("track", "HP"), ("by", -through)) };
        if (through > 0) intents.Add(CodexIntent.Of("adjust", ("track", "SP"), ("by", -1)));
        if (through >= 5) intents.Add(CodexIntent.Of("condition", ("add", "Wounded")));
        return intents;
    }
}

/// <summary>The <c>locate</c> handler: a d6 for where a hit lands, and half its damage through the named function, logged.</summary>
public sealed class Locate : ICodexEventHandler
{
    public IReadOnlyList<CodexIntent> Handle(IReadOnlyDictionary<string, object?> ev, IReadOnlyDictionary<string, object?> sheet, ICodexContext context) =>
        new[] { CodexIntent.Of("log", ("key", "Contract.Located"), ("args", new object?[] { context.D(6), context.Call("half", ev["amount"]) })) };
}

/// <summary>A summary of the sheet, with a button that heals one.</summary>
public sealed class Summary : ICodexWidget
{
    public WidgetContent Render(WidgetRequest request)
    {
        var tracks = (IReadOnlyDictionary<string, object?>)request.Sheet["tracks"]!;
        var conditions = ((IEnumerable<object?>)request.Sheet["conditions"]!).Select(c => c?.ToString() ?? "").ToList();
        return WidgetContent.Of(
            new WidgetHeading(request.Label ?? "Summary"),
            new WidgetRow("HP", tracks["HP"]?.ToString() ?? "", "of its maximum"),
            new WidgetPills("Conditions", conditions),
            new WidgetAction("Heal 1", new[] { CodexIntent.Of("adjust", ("track", "HP"), ("by", 1)) }));
    }
}

// ---- What the loader refuses ----

/// <summary>Not an ICodexFunction.</summary>
public sealed class NotAFunction
{
}

public sealed class TooMany : ICodexFunction
{
    public int Arity => 5;

    public CodexValue Call(IReadOnlyList<object?> arguments) => 0;
}

public sealed class NoConstructor(int seed) : ICodexFunction
{
    public int Arity => 0;

    public CodexValue Call(IReadOnlyList<object?> arguments) => seed;
}

/// <summary>Returns what is not plain data.</summary>
public sealed class Leaky : ICodexEventHandler
{
    public IReadOnlyList<CodexIntent> Handle(IReadOnlyDictionary<string, object?> ev, IReadOnlyDictionary<string, object?> sheet, ICodexContext context) =>
        new[] { CodexIntent.Of("adjust", ("track", "HP"), ("by", new Uri("https://example.invalid"))) };
}

/// <summary>A compiled plugin (<see cref="ICodexSystemPlugin"/>), for the loader's tests: copied directly into a content root, it
/// is a system of its own, and a reload loads it again when its DLL changes.</summary>
public sealed class ContractPlugin : ICodexSystemPlugin
{
    public string SystemId => "contract-plugin";

    public string DisplayName => "Contract plugin";

    public void RegisterComponents(ComponentRegistry registry)
    {
    }

    public void RegisterSystems(dynamic world)
    {
    }

    public IEnumerable<UISchema> GetUISchemas() => Array.Empty<UISchema>();
}
