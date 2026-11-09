using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Tests;

/// <summary>The roll engine (<see cref="SheetRolls"/>, overhaul step 4) on a small system of its own. The reference fixtures
/// are the spec; these pin what the spec leaves implicit: who supplied each input, failures, limits and marks.</summary>
public class SheetRollsTests
{
    private sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static readonly ContentEntry Axe = new() { Id = "axe", Name = "Axe", Type = "item", Properties = new() { ["crit"] = 20L, ["bonus"] = 2L } };

    private static SystemDefinition System(string rolls) => SystemDefinitionLoader.Parse($$"""
        id: roll-test
        name: Roll test
        attributes:
          - { key: str, label: Strength, default: 10, modifier: half-minus-five }
        entityTypes:
          - { type: item, label: Item }
        conditionEffects:
          Blessed:
            modifiers: [{ target: attack, amount: 1, source: Bless }]
        sheetKinds:
          - kind: hero
            label: Hero
            values:
              - { key: skill, formula: "3" }
            lists:
              - { key: gear, label: Gear, type: item }
            tracks:
              - { key: stress, max: 9 }
        rolls:
        {{rolls}}
        """);

    private static SheetRollResult Roll(string rolls, string definition, int[] faces, Dictionary<string, object?>? inputs = null,
        Dictionary<string, object>? sheet = null, IReadOnlyDictionary<string, string>? suppliedBy = null) =>
        SheetRolls.Roll(System(rolls), new Entries(Axe), sheet ?? new Dictionary<string, object> { ["str"] = 14L },
            new SheetRollRequest { Definition = definition, Inputs = inputs ?? new(), SuppliedBy = suppliedBy ?? new Dictionary<string, string>() },
            new SequenceDiceSource(faces));

    private const string Attack = """
          - key: attack
            dice: 1d20
            add: ["mod(str)", "modifiers('attack')", "input.edge"]
            inputs:
              - { key: target, from: gm }
              - { key: edge, default: 0 }
              - { key: crit, from: weapon, default: 20 }
            outcomes:
              - { name: fumble, when: "natural == 1" }
              - { name: triumph, when: "natural >= crit", mark: critical }
              - { name: hit, when: "total >= target" }
              - { name: miss }
        """;

    [Fact]
    public void EachInput_RecordsWhoSuppliedIt()
    {
        var result = Roll(Attack, "attack", new[] { 12 }, new() { ["target"] = 10L, ["weapon"] = "axe" },
            new Dictionary<string, object> { ["str"] = 14L, ["gear"] = new List<object> { "axe" } }, new Dictionary<string, string> { ["target"] = "gm:dana" });

        Assert.Equal(new RollInput(10d, "gm:dana"), result.Inputs["target"]);
        Assert.Equal(new RollInput(0d, "default"), result.Inputs["edge"]);
        Assert.Equal(new RollInput(20d, "weapon"), result.Inputs["crit"]);
        Assert.Equal("roller", result.Inputs["weapon"].From);
        Assert.Equal(14d, result.Total);
        Assert.Equal("hit", result.Outcome);
    }

    [Fact]
    public void AnInputNobodySupplied_FailsTheRoll_NamingIt()
    {
        var ex = Assert.Throws<RollException>(() => Roll(Attack, "attack", new[] { 12 }));

        Assert.Contains("needs target", ex.Message);
    }

    [Fact]
    public void AnInputNotAmongItsOptions_FailsTheRoll()
    {
        const string rolls = """
              - key: act
                dice: 1d6
                inputs: [{ key: position, from: gm, options: [risky, desperate] }]
            """;

        Assert.Throws<RollException>(() => Roll(rolls, "act", new[] { 3 }, new() { ["position"] = "comfy" }));
        Assert.Equal("desperate", Roll(rolls, "act", new[] { 3 }, new() { ["position"] = "desperate" }).Inputs["position"].Value);
    }

    [Fact]
    public void MarksComeFromTheOutcome_NeverFromTheDice()
    {
        var fumble = Roll(Attack, "attack", new[] { 1 }, new() { ["target"] = 5L });
        var triumph = Roll(Attack, "attack", new[] { 20 }, new() { ["target"] = 5L });
        var hit = Roll(Attack, "attack", new[] { 20 }, new() { ["target"] = 5L, ["crit"] = 21L });

        Assert.Equal(("fumble", "fumble"), (fumble.Outcome, fumble.Mark));
        Assert.Equal(("triumph", "critical"), (triumph.Outcome, triumph.Mark));
        // A natural 20 the system does not call a critical is no critical.
        Assert.Equal(("hit", (string?)null), (hit.Outcome, hit.Mark));
    }

    [Fact]
    public void RollInputsReachModifiers_AndConditionsOnTheSheetToo()
    {
        var blessed = Roll(Attack, "attack", new[] { 10 }, new() { ["target"] = 5L, ["edge"] = 2L },
            new Dictionary<string, object> { ["str"] = 14L, ["conditions"] = new List<object> { "Blessed" } });

        Assert.Equal(10 + 2 + 1 + 2, blessed.Total);
    }

    [Fact]
    public void AFormulaThatFails_FailsTheRoll_SayingWhichFormula()
    {
        const string rolls = """
              - key: odd
                dice: 1d6
                add: ["nothingHere + 1"]
            """;

        var ex = Assert.Throws<RollException>(() => Roll(rolls, "odd", new[] { 3 }));

        Assert.Contains("add (nothingHere + 1)", ex.Message);
    }

    [Fact]
    public void RollFormulas_AreSandboxed_LikeEveryOther()
    {
        const string rolls = """
              - key: sneaky
                dice: 1d6
                inputs: [{ key: word, default: "a" }]
                outcomes: [{ name: long, when: "input.word.Replace('a', 'aa') == 'aa'" }]
            """;

        var ex = Assert.Throws<RollException>(() => Roll(rolls, "sneaky", new[] { 3 }));

        Assert.Contains("may not", ex.Message);
    }

    [Fact]
    public void FollowUpsThatGoRoundForever_Stop()
    {
        const string rolls = """
              - key: again
                dice: 1d6
                then: [{ roll: again }]
            """;

        var ex = Assert.Throws<RollException>(() => Roll(rolls, "again", Enumerable.Repeat(3, 50).ToArray()));

        Assert.Contains($"more than {SheetRolls.MaxDepth} deep", ex.Message);
    }

    [Fact]
    public void APool_OfTooManyDice_OrOfNoneWithoutAZeroDiceRule_Fails()
    {
        const string rolls = """
              - key: big
                pool: "input.n"
                die: d6
                inputs: [{ key: n }]
            """;

        Assert.Contains("more than", Assert.Throws<RollException>(() => Roll(rolls, "big", new[] { 1 }, new() { ["n"] = 101L })).Message);
        Assert.Contains("no dice", Assert.Throws<RollException>(() => Roll(rolls, "big", new[] { 1 }, new() { ["n"] = 0L })).Message);
    }

    [Fact]
    public void ExplodingWithoutALimit_StopsAtTheEnginesLimit()
    {
        const string rolls = """
              - key: wild
                dice: 1d2
                explode: { on: 2 }
            """;

        var result = Roll(rolls, "wild", Enumerable.Repeat(2, 500).ToArray());

        Assert.Equal(1 + StandardDiceRoller.MaxExplosions, result.Dice.Count);
    }

    [Fact]
    public void IntentAmounts_AreWorkedOutWhenTheRollIsMade_AndApplyWithinTheTrack()
    {
        const string rolls = """
              - key: push
                dice: 1d6
                then: [{ intents: [{ adjust: { track: stress, by: "kept + 5" } }] }]
            """;
        var sheet = new Dictionary<string, object> { ["stress"] = 2L };

        var result = Roll(rolls, "push", new[] { 4 }, sheet: sheet);
        var left = SheetIntents.Apply(System(rolls), new Entries(), sheet, result.AllIntents());

        Assert.Equal(9d, result.Intents.Single().Fields["by"]);
        Assert.Empty(left.Left);
        Assert.Equal(9L, sheet["stress"]); // 2 + 9, kept within the maximum of 9
    }

    [Fact]
    public void TheLoader_NamesWhatIsWrongWithARollDefinition()
    {
        var system = System("""
              - key: a
                dice: 1d20 + 1d6
              - key: b
                pool: "2"
              - key: c
                dice: 1d6
                pool: "1"
                then: [{ roll: nowhere }]
                inputs: [{ key: x, from: moon }]
              - key: a
                dice: 1d6
            """);

        var problems = SheetRolls.Problems(system);

        Assert.Contains(problems, p => p.Contains("\"a\"") && p.Contains("one group of dice"));
        Assert.Contains(problems, p => p.Contains("\"b\"") && p.Contains("needs a die"));
        Assert.Contains(problems, p => p.Contains("\"c\"") && p.Contains("exactly one of"));
        Assert.Contains(problems, p => p.Contains("\"nowhere\""));
        Assert.Contains(problems, p => p.Contains("\"moon\""));
        Assert.Contains(problems, p => p.Contains("defined twice"));
    }
}
