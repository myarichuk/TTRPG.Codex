using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>What a handler reads and writes of a creature's valued conditions and resources (<see cref="SheetEvents"/>' script view and
/// <see cref="SheetIntents"/>' <c>condition</c> intent), on a small system of its own.</summary>
public class ValuedConditionHandlerTests
{
    private const string Yaml = """
        id: valued-handler
        name: Valued handler
        conditions: [Spent, Marked]
        resources: [{ key: HP, label: Hit points, maxKey: HP_Max }]
        conditionEffects:
          Fading:
            value: { max: 4 }
          Burning:
            value: { key: burn, max: "cap" }
        attributes:
          - { key: cap, label: Cap, default: 3 }
        sheetKinds:
          - kind: hero
            label: Hero
        events:
          peek: { script: rules.js#peek }
          fade: { script: rules.js#fade }
          wear:
            - { intents: [{ condition: { add: Fading, by: "ev.n" } }] }
          fixed:
            - { intents: [{ condition: { add: Fading, value: "1 + 1" } }] }
        """;

    private const string Rules = """
        export function peek(ev, sheet) {
          return [{ log: { key: "seen", args: [sheet.conditionValues.Fading, sheet.conditionValues.Burning, sheet.resources.HP, sheet.resources.HP_Max, Object.keys(sheet.conditionValues).join("+")] } }];
        }
        export function fade(ev, sheet) {
          return ev.mode === "set" ? [{ condition: { add: "Fading", value: ev.n } }]
            : ev.mode === "by" ? [{ condition: { add: "Fading", by: ev.n } }]
            : [{ condition: { remove: "Fading" } }];
        }
        """;

    private static SystemDefinition System()
    {
        var system = SystemDefinitionLoader.Parse(Yaml);
        system.AddScript("rules.js", Rules);
        return system;
    }

    private static Dictionary<string, object> Sheet(params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "hero" };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    [Fact]
    public void AHandler_ReadsValuedConditionValues_AndResources_AsPlainNumbers()
    {
        var sheet = Sheet(("condition_fading", 2L), ("HP", 7L), ("HP_Max", 18L));

        var result = SheetEvents.Raise(System(), NullEntryResolver.Instance, sheet, SheetEvent.Of("peek"));

        var args = ((IEnumerable<object?>)Assert.Single(result.Intents).Fields["args"]!).ToList();
        Assert.Equal(new object?[] { 2d, 0d, 7d, 18d, "Fading+Burning" }, args);
    }

    [Fact]
    public void AHandler_ReadsZeroForWhatTheSheetDoesNotHold()
    {
        var result = SheetEvents.Raise(System(), NullEntryResolver.Instance, Sheet(), SheetEvent.Of("peek"));

        var args = ((IEnumerable<object?>)Assert.Single(result.Intents).Fields["args"]!).ToList();
        Assert.Equal(new object?[] { 0d, 0d, 0d, 0d, "Fading+Burning" }, args);
    }

    [Theory]
    [InlineData("set", 3, 0L, 3L)]
    [InlineData("set", 9, 0L, 4L)] // within the maximum
    [InlineData("by", 1, 2L, 3L)]
    [InlineData("by", 5, 2L, 4L)]
    [InlineData("by", -5, 2L, 0L)] // and not below 0
    [InlineData("remove", 0, 3L, 0L)]
    public void AConditionIntent_SetsRaisesAndZeroesAValuedCondition_WithinItsMaximum(string mode, int n, long from, long expected)
    {
        var sheet = Sheet(("condition_fading", from));

        var result = SheetEvents.Raise(System(), NullEntryResolver.Instance, sheet, SheetEvent.Of("fade", ("mode", mode), ("n", (long)n)));

        Assert.Equal(expected, YamlValues.ToNumber(sheet["condition_fading"]) is { } v ? (long)v : -1L);
        Assert.Contains("condition_fading", result.Applied.Changed);
        Assert.DoesNotContain(SheetModel.ConditionsKey, result.Applied.Changed);
    }

    [Fact]
    public void AConditionIntent_KeepsAValuedConditionWithinAMaximumThatIsAFormula_AndUnderItsOwnKey()
    {
        var system = System();
        var sheet = Sheet(("cap", 2L));

        SheetIntents.Apply(system, NullEntryResolver.Instance, sheet,
            new[] { new SheetIntent("condition", null, new Dictionary<string, object?> { ["add"] = "Burning", ["value"] = 9d }) });

        Assert.Equal(2L, sheet["burn"]);
        Assert.False(sheet.ContainsKey("condition_burning"));
    }

    [Fact]
    public void AConditionIntent_ThatChangesNothing_IsNotMarkedChanged()
    {
        var sheet = Sheet(("condition_fading", 0L));

        var result = SheetEvents.Raise(System(), NullEntryResolver.Instance, sheet, SheetEvent.Of("fade", ("mode", "remove")));

        Assert.Empty(result.Applied.Changed);
    }

    [Fact]
    public void AConditionWithNoValue_IsStillANameInTheList_AndAValuedOneWithNoNumberToo()
    {
        var system = System();
        var sheet = Sheet();

        SheetIntents.Apply(system, NullEntryResolver.Instance, sheet, new[]
        {
            new SheetIntent("condition", null, new Dictionary<string, object?> { ["add"] = "Spent", ["value"] = 3d }),
            new SheetIntent("condition", null, new Dictionary<string, object?> { ["add"] = "Fading" })
        });

        Assert.Equal(new[] { "Spent", "Fading" }, SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey));
        Assert.False(sheet.ContainsKey("condition_fading"));
    }

    [Fact]
    public void AHandlerLine_ReadsValueAndByAsFormulas()
    {
        var system = System();
        var sheet = Sheet(("condition_fading", 1L));

        SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("wear", ("n", 2L)));
        Assert.Equal(3L, sheet["condition_fading"]);

        SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("fixed"));
        Assert.Equal(2L, sheet["condition_fading"]);
    }

    [Fact]
    public void ScriptTypings_DescribeConditionValuesResourcesAndTheValuedConditionIntent()
    {
        var typings = ScriptTypings.For(System());

        Assert.Contains("readonly conditionValues: { readonly [key in \"Fading\" | \"Burning\"]?: number }", typings);
        Assert.Contains("readonly resources: { readonly [key in \"HP\" | \"HP_Max\"]?: number }", typings);
        Assert.Contains("add: ConditionName; value?: number; by?: number", typings);
    }
}
