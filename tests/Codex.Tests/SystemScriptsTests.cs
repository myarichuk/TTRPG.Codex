using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Tests;

/// <summary>The script supplier (<see cref="SystemScripts"/>, overhaul step 8) on a small system of its own. The street fixture
/// is the spec for what scripts do; these pin the contract's edges: plain data both ways, the limits and what a failure says, no
/// randomness but the host's, no .NET, and what the loader reads.</summary>
public class SystemScriptsTests
{
    private const string Yaml = """
        id: script-test
        name: Script test
        attributes:
          - { key: BODY, label: Body, default: 5 }
          - { key: WILL, label: Will, default: 5 }
        functions:
          hitPoints: { script: rules.js#hitPoints }
          bare: { script: rules.js#bare }
          spin: { script: rules.js#spin }
          gamble: { script: rules.js#gamble }
          clr: { script: rules.js#clr }
          oops: { script: rules.js#oops }
          words: { script: rules.js#words }
          double: { formula: "(x) => 2 * x" }
        sheetKinds:
          - kind: hero
            label: Hero
            keyedLists:
              - key: skills
                label: Skills
                itemFields: [{ key: level, type: Number, min: 0, max: 5 }]
                keys: [{ key: climb, label: Climb, attribute: BODY }]
                derive: { value: "s => score(s.attribute) + s.level" }
            lists:
              - { key: gear, label: Gear, type: gear, itemFields: [{ key: charges, type: Number, min: 0, max: 3 }] }
            values:
              - { key: HP_Max, formula: "hitPoints(BODY, WILL)" }
              - { key: Bare, formula: "bare() + 1" }
              - { key: Spin, formula: "spin(1)" }
              - { key: Gamble, formula: "gamble()" }
              - { key: Clr, formula: "clr()" }
              - { key: Oops, formula: "oops()" }
              - { key: Words, formula: "words(2)" }
            tracks:
              - { key: HP, max: HP_Max, min: -10, start: max }
              - { key: IP, min: 0 }
        events:
          poke: { script: rules.js#poke }
          roll3: { script: rules.js#roll3 }
          mutate: { script: rules.js#mutate }
          bad: { script: rules.js#bad }
          unknown: { script: rules.js#unknownKind }
          train: { script: rules.js#train }
          missing: { script: rules.js#nothingHere }
        """;

    private const string Rules = """
        export function hitPoints(body, will) {
          const half = Math.ceil((body + will) / 2);
          return { value: 10 + 5 * half, parts: [["base", 10], ["5 x half", 5 * half]] };
        }
        export function bare() { return 4; }
        export function spin(x) { while (x > 0) { x++; } return x; }
        export function gamble() { return Math.random(); }
        export function clr() { return typeof System === "undefined" && typeof importNamespace === "undefined" ? 1 : 0; }
        export function oops() {
          const a = 1;
          throw new Error("no luck");
        }
        export function words(n) { return "two"; }
        export function poke(ev, sheet, ctx) {
          return [{ adjust: { track: "HP", by: -(ev.amount + ctx.call("double", sheet.attributes.BODY)) } }];
        }
        export function roll3(ev, sheet, ctx) { return [{ adjust: { track: "IP", by: ctx.d(6) + ctx.d(6) + ctx.d(6) } }]; }
        export function mutate(ev, sheet) { sheet.tracks.HP = 1; ev.amount = 99; return []; }
        export function bad() { return { adjust: { track: "HP", by: 1 } }; }
        export function unknownKind() { return [{ explode: { track: "HP" } }]; }
        export function train(ev, sheet) {
          const level = sheet.lists.skills.climb.level;
          return [
            { set: { list: "skills", key: "climb", field: "level", to: level + 9 } },
            { adjust: { list: "gear", key: "rope", field: "charges", by: -1 } },
            { log: { key: "trained", args: [sheet.lists.skills.climb.value, sheet.values.HP_Max] } },
          ];
        }
        """;

    private static SystemDefinition System(string rules = Rules)
    {
        var system = SystemDefinitionLoader.Parse(Yaml);
        system.AddScript("rules.js", rules);
        return system;
    }

    private static Dictionary<string, object> Sheet() => new()
    {
        ["kind"] = "hero",
        ["BODY"] = 6L,
        ["WILL"] = 5L,
        ["skills"] = new Dictionary<string, object> { ["climb"] = new Dictionary<string, object> { ["level"] = 2L } },
        ["gear"] = new List<object> { new Dictionary<string, object> { ["id"] = "rope", ["charges"] = 3L }, "lamp" }
    };

    [Fact]
    public void AFunction_IsCalledFromAFormula_AndAFailureIsAWarningNamingTheScriptAndLine()
    {
        var result = new SheetModel(System(), NullEntryResolver.Instance, Sheet()).Evaluate();

        Assert.Equal(40d, result.Values["HP_Max"]);
        Assert.Equal(5d, result.Values["Bare"]);
        string Warning(string key) => Assert.Single(result.Warnings, w => w.FieldKey == key).Message;
        Assert.Contains("ran more than 100000 statements", Warning("Spin"));
        Assert.Contains("Math.random is not for rules", Warning("Gamble"));
        Assert.Equal(1d, result.Values["Clr"]);
        Assert.Contains("script-test: the function \"oops\" (script rules.js#oops), line 11: no luck", Warning("Oops"));
        Assert.Contains("returned text, not a number", Warning("Words"));
        Assert.All(result.Warnings, w => Assert.Equal("Actor.Rule.ScriptError", w.Key));
    }

    [Fact]
    public void AFunction_GivesItsParts_AndIsCachedByItsArguments()
    {
        var scripts = SystemScripts.For(System());

        var value = scripts.Function("hitPoints", "rules.js#hitPoints", new object?[] { 4d, 5d });

        Assert.Equal(35d, value.Value);
        Assert.Equal(new[] { ("base", 10d), ("5 x half", 25d) }, value.Parts);
        Assert.Same(value, scripts.Function("hitPoints", "rules.js#hitPoints", new object?[] { 4d, 5d }));
        Assert.NotSame(value, scripts.Function("hitPoints", "rules.js#hitPoints", new object?[] { 5d, 5d }));
    }

    [Fact]
    public void AHandler_ReadsThePlainSheet_CallsFunctions_AndRollsOnlyThroughTheHost()
    {
        var system = System();
        var sheet = Sheet();

        var poked = SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("poke", ("amount", 3L)));
        var dice = new SequenceDiceSource(new[] { 6, 5, 4 });
        SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("roll3"), dice: dice);

        Assert.Equal(-15d, YamlValues.ToNumber(Assert.Single(poked.Intents).Fields["by"]));
        Assert.Equal(25L, sheet["HP"]);
        Assert.Equal(15L, sheet["IP"]);
        Assert.Equal(3, dice.Used);
    }

    [Fact]
    public void AHandler_SetsAndAdjustsFieldsOfListEntries_WithinTheirBounds()
    {
        var sheet = Sheet();

        var result = SheetEvents.Raise(System(), NullEntryResolver.Instance, sheet, SheetEvent.Of("train"));

        Assert.Equal(5L, ((Dictionary<string, object>)((Dictionary<string, object>)sheet["skills"])["climb"])["level"]);
        var gear = (List<object>)sheet["gear"];
        Assert.Equal(2L, ((Dictionary<string, object>)gear[0])["charges"]);
        Assert.Equal("lamp", gear[1]);
        var log = Assert.Single(result.Applied.Left);
        Assert.Equal(new object?[] { 8d, 40d }, (IEnumerable<object?>)log.Fields["args"]!);
        Assert.Equal(new[] { "skills", "gear" }, result.Applied.Changed);
    }

    [Fact]
    public void AHandler_ChangesNothingByChangingWhatItWasGiven_AndWhatItReturnsMustBeIntents()
    {
        var system = System();
        var sheet = Sheet();
        SheetEventException Raise(string type) =>
            Assert.Throws<SheetEventException>(() => SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of(type)));

        var fields = new Dictionary<string, object?> { ["amount"] = 3L };
        SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, new SheetEvent("mutate", fields));

        Assert.False(sheet.ContainsKey("HP"));
        Assert.Equal(3L, fields["amount"]);
        Assert.Contains("not a list of intents", Raise("bad").Message);
        Assert.Contains("\"explode\", which is not an intent", Raise("unknown").Message);
        Assert.Contains("has no exported function \"nothingHere\"", Raise("missing").Message);
    }

    [Fact]
    public void ManyCallsAtOnce_EachGetTheirOwnEngine()
    {
        var scripts = SystemScripts.For(System());

        var results = Enumerable.Range(0, 64).AsParallel().Select(i => scripts.Function("hitPoints", "rules.js#hitPoints", new object?[] { (double)(i % 7), 3d }).Value).ToList();

        Assert.Equal(Enumerable.Range(0, 64).Select(i => 10d + 5 * Math.Ceiling((i % 7 + 3) / 2.0)), results);
    }

    [Fact]
    public void TheLoader_ReadsOnlyScriptsInTheSystemsFolder_AndSaysWhatIsWrong()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-scripts-" + Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateDirectory(Path.Combine(root, "sys")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "outside.js"), "export function f() { return 1; }");
            File.WriteAllText(Path.Combine(folder, "rules.js"), "export function f() { return 1; }\nexport const notAFunction = 3;");
            File.WriteAllText(Path.Combine(folder, "broken.js"), "export function (");
            File.WriteAllText(Path.Combine(folder, "imports.js"), "import { x } from 'fs';\nexport function f() { return x; }");
            File.WriteAllText(Path.Combine(folder, "system.yaml"), """
                id: loader-test
                name: Loader test
                functions:
                  ok: { script: rules.js#f }
                  escape: { script: ../outside.js#f }
                  constant: { script: rules.js#notAFunction }
                  broken: { script: broken.js#f }
                  imports: { script: imports.js#f }
                  shapeless: { script: rules.js }
                  wide: { script: wide.js#f }
                """);
            File.WriteAllText(Path.Combine(folder, "wide.js"), "export function f(a, b, c, d, e) { return 1; }");

            var system = SystemDefinitionLoader.Load(Path.Combine(folder, "system.yaml"));
            var problems = SystemScripts.Problems(system);

            Assert.Equal(new[] { "broken.js", "imports.js", "rules.js", "wide.js" }, system.Scripts.Keys.Order());
            Assert.Contains(problems, p => p.Contains("names the script ../outside.js, which is not in the system's folder"));
            Assert.Contains(problems, p => p.Contains("rules.js has no exported function \"notAFunction\""));
            Assert.Contains(problems, p => p.StartsWith("The script broken.js does not parse (line 1"));
            Assert.Contains(problems, p => p.Contains("imports.js fails as it loads"));
            Assert.Contains(problems, p => p.Contains("\"rules.js\", which is not file#export"));
            Assert.Contains(problems, p => p.Contains("The function \"wide\" (wide.js#f) takes more than 4 arguments"));
            Assert.DoesNotContain(problems, p => p.Contains("\"ok\""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
