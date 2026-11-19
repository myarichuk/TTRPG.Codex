using System.Text.RegularExpressions;
using Codex.Core.Consistency;
using Codex.Localization;
using Codex.Web.Services;

namespace Codex.Tests.Consistency;

/// <summary>Every issue the checks make can be shown in the reader's language from its code and args; the model still gets the English message.</summary>
public class ConsistencyCodeTests
{
    private static readonly string[] Languages = { "en", "he", "ru" };

    // Codes the fixtures cannot reach cheaply; each is still rendered by the rule below when it is made.
    private static readonly string[] NotInTheFixtures = { "Consistency.SheetRule", "Consistency.SheetEvaluationFailed", "Consistency.InventoryItemGone", "Consistency.CheckFailed" };

    private static IEnumerable<Issue> Everything()
    {
        var both = ConsistencyChecker.Default.Concat(ConsistencyChecker.Noisy).ToList();
        foreach (var snapshot in new[] { ConsistencyCheckerTests.Snapshot(), WorldStructureCheckTests.V2World(), WorldStructureCheckTests.Noisy(),
                     Fx.Snap(campaign: Fx.Campaign("ghost", installed: false)),
                     Fx.Snap(pages: new[] { Fx.Page("wikipages/1-A", "Hall", ":::for\nx\n:::") },
                         places: new[] { new PlaceView("p-1", "regions/1", "Loop", null, null, "p-1"), new PlaceView("p-2", "regions/1", "Far", null, null, "p-x"),
                             new PlaceView("p-3", "regions/1", "Nowhere", null, null, "p-gone"), new PlaceView("p-x", "regions/2", "X", null, null) }) })
        {
            foreach (var issue in ConsistencyChecker.Run(snapshot, both)) yield return issue;
        }
    }

    [Fact]
    public void EveryKnownCode_HasATextInEveryLanguage_AndTheSourceMakesNoOther()
    {
        foreach (var language in Languages)
        {
            var l = Localizer.For(language);
            Assert.All(ConsistencyCodes.Known, code => Assert.True(l.Has(code), $"{language}: {code} has no text"));
        }

        var literals = Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Core", "Consistency"), "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"""(Consistency\.[A-Za-z.]+)""").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        // "Consistency.System." (a system's own check) and "Consistency.PlaceTree" + a word are built, the rest are written out.
        Assert.Empty(literals.Except(ConsistencyCodes.Known).Except(new[] { "Consistency.System.", "Consistency.PlaceTree" }));
        Assert.Empty(ConsistencyCodes.Known.Where(c => !c.StartsWith("Consistency.PlaceTree", StringComparison.Ordinal)).Except(literals));
    }

    [Fact]
    public void TheEnglishText_OfEveryIssue_IsTheMessageTheModelReads()
    {
        var l = Localizer.For("en");
        var issues = Everything().Where(i => ConsistencyCodes.Known.Contains(i.Code)).ToList();
        Assert.NotEmpty(issues);
        Assert.All(issues, i => Assert.Equal(i.Message, IssueText.Message(l, i)));
        Assert.Empty(ConsistencyCodes.Known.Except(issues.Select(i => i.Code)).Except(NotInTheFixtures));
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void OtherLanguages_FillEveryBlank_AndAreNotTheEnglish(string language)
    {
        var l = Localizer.For(language);
        foreach (var issue in Everything().Where(i => ConsistencyCodes.Known.Contains(i.Code) && i.Code != "Consistency.SheetRule" && i.Code != "Consistency.HazardShape" && i.Code != "Consistency.PackProblem"))
        {
            var text = IssueText.Message(l, issue);
            Assert.DoesNotMatch(@"\{\d", text);
            Assert.NotEqual(issue.Message, text);
        }
    }

    [Fact]
    public void ACodeWithNoText_FallsBackToTheEnglishMessage_AndSoDoesATextThatCannotBeFilled()
    {
        var l = Localizer.For("ru");
        var system = new Issue("x", "system-rule", IssueSeverity.Info, "actor", "a", "-", "Consistency.System.Whatever", new[] { "A" }, "Aria is odd.", "", "actor:a", FixVia.ReportOnly);
        Assert.Equal("Aria is odd.", IssueText.Message(l, system));
        var thin = system with { Code = "Consistency.EmptyBio", Args = Array.Empty<string>() };
        Assert.Equal("Aria is odd.", IssueText.Message(l, thin));
    }

    [Fact]
    public void ThePanelAndItsRecheck_HaveTheirWordsInEveryLanguage()
    {
        var keys = Localizer.Keys("en").Where(k => k.StartsWith("Campaign.Issues.", StringComparison.Ordinal)).ToList();
        Assert.True(keys.Count > 30);
        foreach (var language in new[] { "he", "ru" })
        {
            var other = Localizer.Keys(language).ToHashSet();
            Assert.All(keys, k => Assert.Contains(k, other));
        }
    }
}
