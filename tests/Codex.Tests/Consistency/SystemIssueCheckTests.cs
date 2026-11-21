using Codex.Core.AI;
using Codex.Core.Consistency;
using Codex.Core.Systems;
using Codex.Core.WorldBuilder;
using Codex.Plugin.Abstractions;

namespace Codex.Tests.Consistency;

/// <summary>
/// Layers 2 and 3 of the consistency checker (docs/WORLD_BUILDER.md, 9): a system's <c>issueChecks</c> in system.yaml, as data rules over its sheet keys
/// and as compiled checks beside it. Loaded loudly (a bad section refuses the system), judged by code, never able to break the run.
/// </summary>
public class SystemIssueCheckTests : IDisposable
{
    private const string Header = """
        id: demo
        name: Demo
        attributes:
          - { key: str, label: Strength, default: 10, min: 1, max: 20, modifier: half-minus-five }
        actorFields:
          - { key: level, label: Level, type: Number, min: 1, max: 20 }
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: background, label: Background, type: EntityReference, targetEntityType: background }
        entityTypes:
          - { type: class, label: Class }
          - { type: background, label: Background }
          - { type: equipment, label: Equipment }
          - { type: spell, label: Spell }
        sheetRules:
          enforcement: strict
          inventory: { key: inventory, types: [equipment] }
          lists:
            - { key: spells, label: Spells, targetEntityType: spell }
        """;

    private const string Rules = """
        issueChecks:
          - id: wizard-needs-spells
            rule: required
            field: spells
            when: { field: class, is: wizard }
            severity: warning
            message: "{name} is a wizard with no {field}."
          - id: fighter-no-spells
            rule: empty
            field: spells
            when: { field: class, in: [fighter, barbarian] }
            severity: info
            message: "{name} is a {value}-casting fighter? {field}: {value}."
          - id: level-cap
            rule: range
            field: level
            max: 10
            severity: error
            message: "{name} is level {value}; the cap here is {max}."
          - id: gear-count
            rule: count
            field: inventory
            max: 3
            message: "{name} carries {value} items; at most {max}."
          - id: class-exists
            rule: reference
            field: class
            severity: error
            message: "{name}'s class {value} is not in the Library."
        """;

    private static SystemDefinition System(string checks = Rules) => SystemDefinitionLoader.Parse(Header + "\n" + checks);

    private static readonly Fx.Entries Library = new(
        Fx.Entry("class", "wizard", "Wizard"), Fx.Entry("class", "fighter", "Fighter"), Fx.Entry("spell", "light", "Light"), Fx.Entry("equipment", "rope", "Rope"));

    private static ActorView Pc(string id, string name, params (string Key, object Value)[] sheet) =>
        Fx.Actor(id, name, sheet: sheet.ToDictionary(p => p.Key, p => p.Value));

    private static List<Issue> Run(SystemDefinition system, params ActorView[] actors) =>
        ConsistencyChecker.Run(Fx.Snap(actors: actors, system: system, library: Library), new IConsistencyCheck[] { new SystemRuleCheck() }).ToList();

    // ---- loading ----

    [Fact]
    public void AGoodSection_LoadsWithItsChecksInOrder_AndSurvivesALocalizedCopy()
    {
        var system = System();
        Assert.Equal(new[] { "wizard-needs-spells", "fighter-no-spells", "level-cap", "gear-count", "class-exists" }, system.IssueChecks.Select(c => c.Id));
        Assert.Equal(("required", "spells", "wizard"), (system.IssueChecks[0].Rule, system.IssueChecks[0].Field, system.IssueChecks[0].When!.Is));
        Assert.Equal(new[] { "fighter", "barbarian" }, system.IssueChecks[1].When!.In);
        Assert.Equal(10d, system.IssueChecks[2].Max);
        Assert.Equal("warning", system.IssueChecks[3].Severity); // the default
        Assert.Equal(system.IssueChecks.Count, system.Localize("ru").IssueChecks.Count);
    }

    [Theory]
    [InlineData("- { id: a, rule: required, field: class, sevrity: error, message: x }", "issueChecks[0].sevrity is not a key an issue check reads")]
    [InlineData("- { id: a, rule: required, field: class, when: { field: class, equal: x }, message: x }", "issueChecks[0].when.equal is not a key")]
    [InlineData("- { id: a, rule: nonsense, field: class, message: x }", "rule 'nonsense' is not one of")]
    [InlineData("- { id: a, rule: required, field: wibble, message: x }", "field 'wibble' is not a sheet key")]
    [InlineData("- { id: a, rule: required, field: class, severity: loud, message: x }", "severity 'loud' is not one of")]
    [InlineData("- { id: a, rule: required, field: class }", "message is required")]
    [InlineData("- { id: a, rule: required, field: class, message: '{nmae} is bad' }", "the message has {nmae}")]
    [InlineData("- { id: A B, rule: required, field: class, message: x }", "id must be lower-case")]
    [InlineData("- { rule: required, field: class, message: x }", "id is required")]
    [InlineData("- { id: a, rule: required, type: 'X.Y, Z', field: class, message: x }", "either a rule")]
    [InlineData("- { id: a, field: class, message: x }", "either a rule")]
    [InlineData("- { id: a, rule: range, field: level, message: x }", "a range needs min, max or both")]
    [InlineData("- { id: a, rule: range, field: class, max: 3, message: x }", "a range is over a number")]
    [InlineData("- { id: a, rule: count, field: level, max: 3, message: x }", "a count is over a list")]
    [InlineData("- { id: a, rule: range, field: level, min: 5, max: 3, message: x }", "min is above max")]
    [InlineData("- { id: a, rule: required, field: class, when: { field: class, is: x, notEmpty: true }, message: x }", "exactly one of is, in, notEmpty and empty")]
    [InlineData("- { id: a, rule: required, field: class, when: { field: nowhere, is: x }, message: x }", "when.field 'nowhere' is not a sheet key")]
    [InlineData("- { id: a, rule: required, field: class, roles: [wizard], message: x }", "role 'wizard' is not one of")]
    [InlineData("- { id: a, rule: reference, field: level, message: x }", "is not a reference to an entry")]
    [InlineData("- { id: a, rule: required, field: class, code: lower, message: x }", "must be a dotted key")]
    [InlineData("- { id: a, type: 'X.Y, Z', message: x }", "a compiled check says its own message")]
    public void ABadSection_RefusesTheSystem_Loudly_NamingTheCheck(string entry, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => System("issueChecks:\n  " + entry));
        Assert.Contains(expected, string.Join("\n", SystemDefinitionLoader.ErrorsOf(error)));
    }

    [Fact]
    public void TwoChecksWithOneId_AreRefused()
    {
        var error = Assert.Throws<InvalidDataException>(() => System("""
            issueChecks:
              - { id: a, rule: required, field: class, message: x }
              - { id: a, rule: required, field: level, message: x }
            """));
        Assert.Contains("issue check 'a' is declared more than once", string.Join("\n", SystemDefinitionLoader.ErrorsOf(error)));
    }

    [Fact]
    public void ASystemWithNoSection_IsUntouched_AndItsOtherUnknownKeysStillOnlyWarn()
    {
        var unknown = new List<string>();
        var system = SystemDefinitionLoader.Parse(Header + "\nsomethingNew: 1\n", "demo", unknown);
        Assert.Empty(system.IssueChecks);
        Assert.Contains("somethingNew", unknown);
    }

    [Fact]
    public void EveryBundledSystemStillLoads_AndCommittedSchemaKnowsTheSection()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "content", "systems"), "system.yaml", SearchOption.AllDirectories))
        {
            Assert.NotNull(SystemDefinitionLoader.Load(path));
        }

        Assert.Contains("\"issueChecks\"", File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "schema", SystemSchema.SchemaFile)));
    }

    // ---- layer 2: what the rules find ----

    [Fact]
    public void TheRules_FindWhatTheyAreWrittenFor_WithStableIdsAndTheMessageFilledIn()
    {
        var issues = Run(System(),
            Pc("actors/1-A", "Merlin", ("class", "wizard")),
            Pc("actors/2-A", "Conan", ("class", "fighter"), ("spells", new List<object> { "light" })),
            Pc("actors/3-A", "Elder", ("class", "fighter"), ("level", 12L), ("inventory", new List<object> { "rope", "rope", "rope", "rope" })),
            Pc("actors/4-A", "Ghosty", ("class", "ghost")),
            Pc("actors/5-A", "Fine", ("class", "wizard"), ("spells", new List<object> { "light" }), ("level", 3L), ("inventory", new List<object> { "rope" })));

        Assert.Equal(new[]
        {
            "system-rule:actor:actors/3-A:level:level-cap",
            "system-rule:actor:actors/4-A:class:class-exists",
            "system-rule:actor:actors/1-A:spells:wizard-needs-spells",
            "system-rule:actor:actors/3-A:inventory:gear-count",
            "system-rule:actor:actors/2-A:spells:fighter-no-spells"
        }, issues.Select(i => i.Id));
        Assert.Equal(new[] { IssueSeverity.Error, IssueSeverity.Error, IssueSeverity.Warning, IssueSeverity.Warning, IssueSeverity.Info }, issues.Select(i => i.Severity));
        Assert.Equal("Elder is level 12; the cap here is 10.", issues[0].Message);
        Assert.Equal("Merlin is a wizard with no Spells.", issues[2].Message);
        Assert.Equal("Elder carries 4 items; at most 3.", issues[3].Message);
        Assert.Equal("Consistency.System.LevelCap", issues[0].Code);
        Assert.Equal(new[] { "Elder", "Level", "12" }, issues[0].Args);
        // A rule about a key an edit may set is fixable by a sheet edit; the model is told to ask, never to invent.
        Assert.All(issues, i => Assert.Equal(FixVia.Sheet, i.FixVia));
        Assert.Contains("Do not", issues[0].FixHint);
    }

    [Fact]
    public void TheRules_LookOnlyAtTheRolesAsked_NeverARetiredCharacter_AndNothingWhereEnforcementIsOff()
    {
        var system = System("""
            issueChecks:
              - { id: monsters-need-level, rule: required, field: level, roles: [monster], message: "{name} has no level." }
            """);
        var actors = new[]
        {
            Fx.Actor("actors/1-A", "Pc", ActorRole.PlayerCharacter), Fx.Actor("actors/2-A", "Orc", ActorRole.Monster),
            Fx.Actor("actors/3-A", "Dead", ActorRole.Monster, retired: true)
        };
        Assert.Equal(new[] { "system-rule:actor:actors/2-A:level:monsters-need-level" }, Run(system, actors).Select(i => i.Id));
        Assert.Empty(ConsistencyChecker.Run(Fx.Snap(actors: actors, system: system, library: Library, enforcement: "off"), new IConsistencyCheck[] { new SystemRuleCheck() }));
    }

    [Fact]
    public void ARuleAboutCreatures_IsReportOnly_ForNoSheetEditReachesThem_AndOneAboutCharactersIsStillSheet()
    {
        var system = System("""
            issueChecks:
              - { id: monsters-need-level, rule: required, field: level, roles: [monster], message: "{name} has no level." }
              - { id: pcs-need-level, rule: required, field: level, roles: [pc], message: "{name} has no level." }
            """);
        var issues = Run(system, Fx.Actor("actors/1-A", "Pc", ActorRole.PlayerCharacter), Fx.Actor("actors/2-A", "Orc", ActorRole.Monster));

        Assert.Equal(FixVia.ReportOnly, issues.Single(i => i.EntityId == "actors/2-A").FixVia);
        Assert.Equal(FixVia.Sheet, issues.Single(i => i.EntityId == "actors/1-A").FixVia);
        Assert.True(SheetProposal.IsEditable(system, "level")); // the key itself is editable; it is the creature that is out of reach
    }

    [Fact]
    public void ARuleThatIsOnlyTrueWhenItsConditionHolds_AndARangeWithNothingToJudge_SayNothing()
    {
        var issues = Run(System(), Pc("actors/1-A", "Plain", ("class", "fighter")), Pc("actors/2-A", "NoLevel", ("class", "wizard"), ("spells", new List<object> { "light" })));
        Assert.Empty(issues);
    }

    [Fact]
    public void TheRules_AreTheSameOnEveryRun_InAnyOrderOfTheCast()
    {
        var actors = new[]
        {
            Pc("actors/1-A", "Merlin", ("class", "wizard")), Pc("actors/3-A", "Elder", ("level", 12L)), Pc("actors/4-A", "Ghosty", ("class", "ghost"))
        };
        var first = Run(System(), actors);
        Assert.Equal(first.Select(Fx.Flat), Run(System(), actors).Select(Fx.Flat));
        Assert.Equal(first.Select(Fx.Flat), Run(System(), actors.Reverse().ToArray()).Select(Fx.Flat));
        Assert.Equal(first.Count, first.Select(i => i.Id).Distinct().Count());
        Assert.All(first, i => Assert.Equal(CheckNames.SystemRule, i.Check));
    }

    [Fact]
    public void ASystemRuleIssue_CanBeResolvedByASheetEditOfThatCharacter()
    {
        var open = Run(System(), Pc("actors/1-A", "Merlin", ("class", "wizard")));
        var issue = Assert.Single(open);
        var rules = new ProposalRules(new Dictionary<string, string>(), new Dictionary<string, string>(), Array.Empty<string>(), _ => Array.Empty<FieldSpec>(),
            System: System(), ExistingSheets: new[] { "actors/1-A" });

        var check = WorldBundleParser.ParseProposal($$"""{"entries": [{"ref": "s", "kind": "edit", "target": "existing:sheet:actors/1-A", "sheet": {"spells": ["light"]}, "resolves": ["{{issue.Id}}"]}]}""", rules, open);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        // The edit really fixes it: laid over the sheet, the rule finds nothing.
        var fixedSheet = SheetProposal.Apply(new Dictionary<string, object> { ["class"] = "wizard" }, check.Bundle.Drafts[0].SheetSet, Array.Empty<string>());
        Assert.Empty(Run(System(), Pc("actors/1-A", "Merlin", fixedSheet.Select(p => (p.Key, p.Value)).ToArray())));
    }

    // ---- layer 3: compiled checks ----

    private readonly List<string> folders = new();

    public void Dispose()
    {
        foreach (var folder in folders)
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private SystemDefinition Installed(string checks, bool withDll = true)
    {
        var folder = Path.Combine(Path.GetTempPath(), "codex-issue-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        folders.Add(folder);
        File.WriteAllText(Path.Combine(folder, "system.yaml"), Header + "\n" + checks);
        if (withDll) File.Copy(Path.Combine(AppContext.BaseDirectory, "contract-fixture", "Codex.ContractFixture.dll"), Path.Combine(folder, "Codex.ContractFixture.dll"));
        return SystemDefinitionLoader.Load(Path.Combine(folder, "system.yaml"));
    }

    private const string Compiled = """
        issueChecks:
          - { id: long-name, type: "Codex.ContractFixture.LongNameCheck, Codex.ContractFixture" }
          - { id: boom, type: "Codex.ContractFixture.ThrowingCheck, Codex.ContractFixture" }
          - { id: bad-row, type: "Codex.ContractFixture.BadRowCheck, Codex.ContractFixture" }
          - { id: level-cap, rule: range, field: level, max: 10, severity: error, message: "{name} is level {value}." }
        """;

    private static readonly ActorView[] Cast =
    {
        Fx.Actor("actors/1-A", "Bartholomew Featherstonehaugh", sheet: new Dictionary<string, object> { ["level"] = 12L }), Fx.Actor("actors/2-A", "Ann")
    };

    [Fact]
    public void ACompiledCheck_ContributesIssues_AThrowingOne_IsOneInfoIssue_AndNothingElseIsLost()
    {
        var system = Installed(Compiled);
        Assert.Empty(CompiledProblems(system).Where(p => !p.Contains("bad")));

        var issues = ConsistencyChecker.Run(Fx.Snap(actors: Cast, system: system, library: Library, campaign: Fx.Campaign()), new IConsistencyCheck[] { new SystemRuleCheck() });

        Assert.Equal(new[]
        {
            "system-rule:actor:actors/1-A:level:level-cap", // a data rule beside the compiled ones still runs
            "check-failed:campaign:c1:issueChecks:bad-row",
            "check-failed:campaign:c1:issueChecks:boom",
            "system-rule:actor:actors/1-A:-:long-name"
        }, issues.Select(i => i.Id));
        var failed = issues.Single(i => i.Id.EndsWith(":boom"));
        Assert.Equal((IssueSeverity.Info, FixVia.ReportOnly, "Consistency.CheckFailed"), (failed.Severity, failed.FixVia, failed.Code));
        Assert.Equal(new[] { "boom", "InvalidOperationException" }, failed.Args);
        Assert.Contains("could not run", failed.Message);
        Assert.Contains("unknown kind", issues.Single(i => i.Id.EndsWith(":bad-row")).Message + issues.Single(i => i.Id.EndsWith(":bad-row")).Args[1]);
        var long_ = issues.Single(i => i.Id.EndsWith(":long-name"));
        Assert.Equal(("actor", "Consistency.System.LongName", IssueSeverity.Info), (long_.EntityKind, long_.Code, long_.Severity));
    }

    [Fact]
    public void ACompiledCheck_GivesTheSameIssuesOnEveryRun()
    {
        var system = Installed(Compiled);
        var snapshot = Fx.Snap(actors: Cast, system: system, library: Library, campaign: Fx.Campaign());
        var first = ConsistencyChecker.Run(snapshot, new IConsistencyCheck[] { new SystemRuleCheck() }).Select(Fx.Flat).ToList();
        Assert.Equal(first, ConsistencyChecker.Run(snapshot, new IConsistencyCheck[] { new SystemRuleCheck() }).Select(Fx.Flat));
        Assert.Equal(first, ConsistencyChecker.Run(Fx.Snap(actors: Cast.Reverse(), system: system, library: Library, campaign: Fx.Campaign()), new IConsistencyCheck[] { new SystemRuleCheck() }).Select(Fx.Flat));
    }

    [Fact]
    public void ACompiledCheckWhoseDllIsMissing_IsOneInfoIssue_NotAFailureOfTheRun()
    {
        var system = Installed(Compiled, withDll: false);
        var issues = ConsistencyChecker.Run(Fx.Snap(actors: Cast, system: system, library: Library), new IConsistencyCheck[] { new SystemRuleCheck() });

        Assert.Equal(3, issues.Count(i => i.Check == CheckNames.CheckFailed));
        Assert.Contains(issues, i => i.Id == "system-rule:actor:actors/1-A:level:level-cap");
        Assert.Contains("no Codex.ContractFixture.dll", issues.First(i => i.Check == CheckNames.CheckFailed).Args[1]);
    }

    [Fact]
    public void TheCompiledChecksOfASystem_AreAmongTheProblemsTheLoaderReports_WhenTheirTypeIsWrong()
    {
        var system = Installed("""
            issueChecks:
              - { id: nothing, type: "Codex.ContractFixture.Half, Codex.ContractFixture" }
            """);
        Assert.Contains(CompiledProblems(system), p => p.Contains("not an ICodexIssueCheck"));
    }

    private static IReadOnlyList<string> CompiledProblems(SystemDefinition system) => Codex.Core.Rules.CompiledSuppliers.Problems(system);

    [Fact]
    public void TheDefaultRun_IncludesTheSystemsChecks_AndTheNamesAreKnownScopes()
    {
        Assert.Contains(ConsistencyChecker.Default, c => c is SystemRuleCheck);
        Assert.True(IssueFilter.IsKnownScope(CheckNames.SystemRule));
        Assert.True(IssueFilter.IsKnownScope(CheckNames.CheckFailed));
        Assert.Contains(CheckNames.SystemRule, CheckNames.V2);
    }
}
