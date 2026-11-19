using Codex.Core.Consistency;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests.Consistency;

/// <summary>An issue id names a problem, not its position or its numbers: a dismissal or a <c>resolves</c> claim stays on the same problem when something
/// else in the world changes or the problem is partly fixed.</summary>
public class IssueIdStabilityTests
{
    private static List<Issue> Secrets(string body)
    {
        var snapshot = Fx.Snap(pages: new[] { Fx.Page("wikipages/1-A", "Road", body) });
        return new IConsistencyCheck[] { new SecretUnclosedCheck(), new SecretUnknownAudienceCheck() }.SelectMany(c => c.Run(snapshot)).OrderBy(i => i, Issue.Order).ToList();
    }

    [Fact]
    public void InsertingASecretBlockEarlier_LeavesTheIdsOfTheLaterOnesAsTheyWere()
    {
        const string later = ":::oops\nx\n:::\n:::dm\nnever closed";
        var before = Secrets(later).Select(i => i.Id).ToList();
        Assert.Equal(2, before.Count);

        var after = Secrets(":::partyy\nnew\n:::\nSome words.\n" + later).Select(i => i.Id).ToList();

        Assert.Equal(3, after.Count);
        foreach (var id in before) Assert.Contains(id, after);
    }

    [Fact]
    public void TwoBlocksThatOpenWithTheSameLine_AreToldApartByOrder_AndOnlyThen()
    {
        var ids = Secrets(":::oops\na\n:::\n:::oops\nb\n:::").Select(i => i.Id).ToList();
        Assert.Equal(new[] { "secret-unknown-audience:page:wikipages/1-A:body:oops", "secret-unknown-audience:page:wikipages/1-A:body:oops.2" }, ids);
    }

    [Fact]
    public void ALongOrOddOpeningLine_GivesABoundedSlug_NotItsText()
    {
        var id = Assert.Single(Secrets(":::weird " + new string('x', 300) + "\nbody\n:::")).Id;
        Assert.True(id.Length < 120, id);
        Assert.DoesNotContain(" ", id);
    }

    private static readonly SystemDefinition Limited = SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        entityTypes:
          - { type: feat, label: Feat }
        sheetRules:
          enforcement: strict
          lists:
            - { key: feats, label: Feats, targetEntityType: feat, maxCount: { value: 2 } }
        """);

    private static readonly Fx.Entries Library = new(Fx.Entry("feat", "alert", "Alert"), Fx.Entry("feat", "lucky", "Lucky"), Fx.Entry("feat", "tough", "Tough"), Fx.Entry("feat", "sharp", "Sharp"));

    private static List<Issue> Rules(params string[] feats) => Fx.Run(new SheetRuleCheck(), Fx.Snap(
        actors: new[] { Fx.Actor("actors/1-A", sheet: new Dictionary<string, object> { ["feats"] = feats.Cast<object>().ToList() }) }, system: Limited, library: Library));

    [Fact]
    public void APartialFix_OfARuleWithNumbersInIt_KeepsTheIssuesId()
    {
        // Five picked where two are allowed; dropping to four is still the same problem, with other numbers in its sentence.
        var five = Assert.Single(Rules("alert", "lucky", "tough", "sharp", "alert|x"), i => i.Code == "Actor.Rule.Limit");
        var four = Assert.Single(Rules("alert", "lucky", "tough", "sharp"), i => i.Code == "Actor.Rule.Limit");

        Assert.NotEqual(five.Message, four.Message);
        Assert.Equal(five.Id, four.Id);
        Assert.Equal("sheet-rule:actor:actors/1-A:feats:Actor.Rule.Limit", four.Id);
    }

    [Fact]
    public void FixingOneOfTwoMissingEntries_KeepsTheOthersId_NoIdIsRenumbered()
    {
        var both = Rules("ghost", "wraith").Where(i => i.Code == "Actor.Rule.NotInLibrary").Select(i => i.Id).ToList();
        Assert.Equal(new[] { "sheet-rule:actor:actors/1-A:feats:Actor.Rule.NotInLibrary:ghost", "sheet-rule:actor:actors/1-A:feats:Actor.Rule.NotInLibrary:wraith" }, both);

        // Whichever of the two is fixed, the other is still the same id (no "~2" that would move to the other).
        Assert.Equal(new[] { both[1] }, Rules("wraith").Where(i => i.Code == "Actor.Rule.NotInLibrary").Select(i => i.Id));
        Assert.Equal(new[] { both[0] }, Rules("ghost").Where(i => i.Code == "Actor.Rule.NotInLibrary").Select(i => i.Id));
    }

    [Fact]
    public void TwoIssuesThatStillShareAnId_AreNumberedByWhatTheySay_NotByTheOrderTheCheckMadeThem()
    {
        IReadOnlyList<Issue> Run(params string[] messages) => ConsistencyChecker.Run(Fx.Snap(), new IConsistencyCheck[] { new TwoOf(messages) });
        var forward = Run("alpha", "beta");
        var backward = Run("beta", "alpha");

        Assert.Equal(new[] { "x:y:z:f", "x:y:z:f~2" }, forward.Select(i => i.Id));
        Assert.Equal(forward.Select(i => (i.Id, i.Message)), backward.Select(i => (i.Id, i.Message)));
    }

    private sealed class TwoOf(string[] messages) : IConsistencyCheck
    {
        public string Name => "x";

        public IEnumerable<Issue> Run(WorldSnapshot snapshot) => messages.Select(m => new Issue("x:y:z:f", "x", IssueSeverity.Info, "y", "z", "f", "Consistency.Test", Array.Empty<string>(), m, "", "y:z", FixVia.ReportOnly));
    }
}
