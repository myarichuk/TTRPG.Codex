namespace Codex.Tests.Reference;

/// <summary>
/// The runner itself, against a small system written in today's syntax, so the reference fixtures' "0 of N pass" means the
/// engine is behind its spec and not that the runner can never say Pass.
/// </summary>
public class ReferenceRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), TestTempData.OwnedName("codex-refrunner-"));

    public ReferenceRunnerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "system.yaml"), """
            id: today
            name: Today
            attributes:
              - { key: str, label: Strength, default: 10, modifier: half-minus-five }
            actorFields:
              - { key: level, label: Level, type: Number, defaultValue: 1 }
            sheetRules:
              derived:
                - { key: bonus, label: Bonus, formula: "2 + (level - 1) / 4" }
              attributes: { methods: [manual], default: manual, max: 20 }
            events:
              advance: { script: rules.js#onAdvance }
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ReferenceSpec Spec(params SpecExpectation[] expectations) => new()
    {
        System = "today",
        Fixture = "today",
        Sheets = { ["hero"] = new SpecSheet { Values = { ["level"] = "5", ["base_str"] = "18", ["attrMethod"] = "manual" } } },
        Expectations = expectations.ToList()
    };

    private CheckResult Run(SpecExpectation expectation)
    {
        var system = new CurrentEngineHost().Load(_root, out var problem);
        Assert.True(system != null, problem);
        return ReferenceRunner.Check(system!, Spec(expectation), expectation);
    }

    [Fact]
    public void ValuesTheEngineDerives_Pass_AndWrongOnesFailSayingWhatTheyWere()
    {
        var right = Run(new SpecExpectation { Id = "t.right", Sheet = "hero", Expect = { Values = new() { ["bonus"] = 3, ["str"] = 18 } } });
        var wrong = Run(new SpecExpectation { Id = "t.wrong", Sheet = "hero", Expect = { Values = new() { ["bonus"] = 4, ["nothing"] = 1 } } });

        Assert.Equal(CheckStatus.Pass, right.Status);
        Assert.Equal(CheckStatus.Fail, wrong.Status);
        Assert.Contains("bonus: 3, expected 4", wrong.Detail);
        Assert.Contains("nothing: no value", wrong.Detail);
    }

    [Fact]
    public void ViolationsMatchByFieldAndKey_AndWithIsLaidOverTheSheet()
    {
        var over = Run(new SpecExpectation
        {
            Id = "t.over",
            Sheet = "hero",
            With = new SpecSheet { Values = { ["base_str"] = "21" } },
            Expect = { Violations = new() { new SpecViolation { Field = "str", Key = "Actor.Rule.ScoreMax" } } }
        });
        var none = Run(new SpecExpectation { Id = "t.none", Sheet = "hero", Expect = { Violations = new() } });
        var missed = Run(new SpecExpectation { Id = "t.missed", Sheet = "hero", Expect = { Violations = new() { new SpecViolation { Field = "str", Key = "Actor.Rule.ScoreMax" } } } });

        Assert.Equal(CheckStatus.Pass, over.Status);
        Assert.Equal(CheckStatus.Pass, none.Status);
        Assert.Equal(CheckStatus.Fail, missed.Status);
    }

    [Fact]
    public void WhatTheEngineCannotDo_IsUnsupported_NamingTheStep_AndAnUndefinedRollFails()
    {
        var roll = Run(new SpecExpectation { Id = "t.roll", Sheet = "hero", Roll = new SpecRoll { Definition = "check", Dice = { 6 } }, Expect = { Outcome = "full" } });
        var unhandled = Run(new SpecExpectation { Id = "t.event", Sheet = "hero", Event = new() { ["type"] = "turnStart" }, Expect = { Intents = new() } });
        var scripted = Run(new SpecExpectation { Id = "t.script", Sheet = "hero", Event = new() { ["type"] = "advance" }, Expect = { Intents = new() } });
        var conditions = Run(new SpecExpectation { Id = "t.cond", Sheet = "hero", Expect = { Conditions = new() { "Down" } } });

        // A roll the system does not define is the system's problem, and fails saying so.
        Assert.Equal(CheckStatus.Fail, roll.Status);
        Assert.Contains("no roll called \"check\"", roll.Detail);
        // An event nothing handles does nothing; one a script handles waits for scripts.
        Assert.Equal(CheckStatus.Pass, unhandled.Status);
        Assert.Equal(CheckStatus.Unsupported, scripted.Status);
        Assert.Contains("step 8", scripted.Detail);
        Assert.Equal(CheckStatus.Unsupported, conditions.Status);
        Assert.Contains("step 3", conditions.Detail);
    }

    [Fact]
    public void MergingWith_MergesMapsAndReplacesLists()
    {
        var merged = (Dictionary<string, object>)SpecSheets.Merge(
            new Dictionary<string, object> { ["a"] = new Dictionary<string, object> { ["x"] = 1L, ["y"] = 2L }, ["l"] = new List<object> { 1L, 2L } },
            new Dictionary<string, object> { ["a"] = new Dictionary<string, object> { ["y"] = 3L }, ["l"] = new List<object>() });

        Assert.Equal(new Dictionary<string, object> { ["x"] = 1L, ["y"] = 3L }, merged["a"]);
        Assert.Empty((List<object>)merged["l"]);
    }
}
