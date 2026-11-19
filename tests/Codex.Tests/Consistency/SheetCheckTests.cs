using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>The empty-inventory check and the bridge to the rules engine.</summary>
public class SheetCheckTests
{
    private static readonly Fx.Entries Library = new(
        Fx.Entry("class", "fighter", "Fighter", ("startingEquipment", "(a) Chain Mail, Greatsword or (b) 155 GP")),
        Fx.Entry("class", "wizard", "Wizard"),
        Fx.Entry("background", "sailor", "Sailor", ("equipment", "Choose A or B: (A) Dagger, 20 GP; or (B) 50 GP")),
        Fx.Entry("background", "hermit", "Hermit"));

    private static ActorView Pc(string id, params (string Key, object Value)[] sheet) =>
        Fx.Actor(id, sheet: sheet.ToDictionary(p => p.Key, p => p.Value));

    // ---- empty inventory ----

    [Fact]
    public void ACharacterWithNoGear_WhoseClassBringsAStartingKit_IsAnInfo_ThatNamesTheSource()
    {
        var pc = Pc("actors/1-A", ("class", "fighter"), ("background", "sailor"));
        var issue = Assert.Single(Fx.Run(new EmptyInventoryCheck(), Fx.Snap(actors: new[] { pc }, system: Fx.System(), library: Library)));

        Assert.Equal("empty-inventory:actor:actors/1-A:inventory", issue.Id);
        Assert.Equal(IssueSeverity.Info, issue.Severity);
        Assert.Equal(FixVia.ReportOnly, issue.FixVia);
        Assert.Equal("Fighter, Sailor", issue.Args[1]);
    }

    [Fact]
    public void NoIssue_WhenTheyCarrySomething_NeitherEntryHasAKit_OrTheyAreNotACharacterOfTheTable()
    {
        var actors = new[]
        {
            Pc("actors/1-A", ("class", "fighter"), ("inventory", new List<string> { "chain-mail" })),
            Pc("actors/2-A", ("class", "wizard"), ("background", "hermit")),
            Pc("actors/3-A"),
            Fx.Actor("actors/4-A", role: ActorRole.Monster, sheet: new Dictionary<string, object> { ["class"] = "fighter" }),
            Fx.Actor("actors/5-A", retired: true, sheet: new Dictionary<string, object> { ["class"] = "fighter" }),
            Pc("actors/6-A", ("class", "no-such-class"))
        };
        Assert.Empty(Fx.Run(new EmptyInventoryCheck(), Fx.Snap(actors: actors, system: Fx.System(), library: Library)));
    }

    [Fact]
    public void ASystemWithNoInventory_OrNoSystemAtAll_HasNothingToCheck()
    {
        var pc = Pc("actors/1-A", ("class", "fighter"));
        Assert.Empty(Fx.Run(new EmptyInventoryCheck(), Fx.Snap(actors: new[] { pc }, library: Library)));
    }

    // ---- sheet rules ----

    [Fact]
    public void WhatTheRulesEngineSaysAboutASheet_BecomesIssues_AnErrorForAViolation()
    {
        var pc = Pc("actors/1-A", ("class", "ghost"));
        var issues = Fx.Run(new SheetRuleCheck(), Fx.Snap(actors: new[] { pc }, system: Fx.System(), library: Library));

        var issue = Assert.Single(issues, i => i.Code == "Actor.Rule.NotInLibrary");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal("sheet-rule:actor:actors/1-A:class:Actor.Rule.NotInLibrary:class-ghost", issue.Id);
        Assert.Equal(FixVia.ReportOnly, issue.FixVia);
        Assert.Equal(new[] { "Class", "ghost" }, issue.Args);
    }

    [Fact]
    public void ARuleBrokenByAnEntryThatExists_IsReportedToo_UnderTheSystemsOwnMessage()
    {
        var library = new Fx.Entries(Fx.Entry("class", "rogue", "Rogue"));
        var issue = Assert.Single(Fx.Run(new SheetRuleCheck(), Fx.Snap(actors: new[] { Pc("actors/1-A", ("class", "rogue")) }, system: Fx.System(), library: library)));
        Assert.Contains("no such class", issue.Message);
        Assert.Equal("Actor.Rule.Choice", issue.Code);
    }

    [Fact]
    public void NothingIsReported_WhenEnforcementIsOff_ForARetiredCharacter_OrForABestiaryCreature()
    {
        var sheet = new Dictionary<string, object> { ["class"] = "ghost" };
        var pc = Pc("actors/1-A", ("class", "ghost"));
        Assert.Empty(Fx.Run(new SheetRuleCheck(), Fx.Snap(actors: new[] { pc }, system: Fx.System(), library: Library, enforcement: "off")));

        var skipped = new[] { Fx.Actor("actors/2-A", retired: true, sheet: sheet), Fx.Actor("actors/3-A", role: ActorRole.Monster, sheet: sheet) };
        Assert.Empty(Fx.Run(new SheetRuleCheck(), Fx.Snap(actors: skipped, system: Fx.System(), library: Library)));
    }

    [Fact]
    public void TheSheetInTheSnapshot_IsNeverWrittenTo()
    {
        var pc = Pc("actors/1-A", ("class", "fighter"));
        var before = pc.Sheet.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}").ToList();
        Fx.Run(new SheetRuleCheck(), Fx.Snap(actors: new[] { pc }, system: Fx.System(), library: Library));
        Assert.Equal(before, pc.Sheet.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}").ToList());
    }

    [Fact]
    public void HowAbilityScoresWereBought_IsNotCheckedHere()
    {
        var system = Codex.Core.Systems.SystemDefinitionLoader.Parse("""
            id: demo
            name: Demo
            attributes:
              - { key: str, label: Strength, abbreviation: STR, default: 10, modifier: half-minus-five }
            sheetRules:
              enforcement: strict
              attributes:
                methods: [point-buy, manual]
                default: point-buy
                max: 20
                pointBuy: { budget: 5, min: 8, max: 15, costs: { 8: 0, 9: 1, 10: 2, 11: 3, 12: 4, 13: 5, 14: 7, 15: 9 } }
            """);
        var pc = Pc("actors/1-A", ("attrMethod", "point-buy"), ("base_str", 15L));
        Assert.DoesNotContain(Fx.Run(new SheetRuleCheck(), Fx.Snap(actors: new[] { pc }, system: system)), i => i.Code.StartsWith("Actor.Rule.PointBuy"));
    }
}
