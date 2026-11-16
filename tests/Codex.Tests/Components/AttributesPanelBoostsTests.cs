using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>The attribute panel under the boosts method: read-only scores, a fieldset per source with a labelled select for each choice, the
/// trade as check boxes that keep their ticks, the half step in words, and each problem said once, in a polite live region.</summary>
public class AttributesPanelBoostsTests : ComponentTestBase
{
    private readonly SystemDefinition _system = SystemDefinitionLoader.Parse(AttributeBoostTests.System);
    private readonly SheetRuleEngine _engine = AttributeBoostTests.Engine();

    public AttributesPanelBoostsTests() => Services.AddSingleton<IContentRegistry>(new ContentRegistry());

    private IRenderedComponent<AttributesPanel> Draw(Dictionary<string, object> sheet, bool canEdit = true, string culture = "en", Action? onChanged = null)
    {
        if (culture != "en") Services.AddSingleton(Localizer.For(culture));
        var evaluation = _engine.Recompute(sheet);
        IRenderedComponent<AttributesPanel>? cut = null;
        cut = Render<AttributesPanel>(p => p
            .Add(x => x.Definition, _system)
            .Add(x => x.Evaluation, evaluation)
            .Add(x => x.Sheet, sheet)
            .Add(x => x.CanEdit, canEdit)
            .Add(x => x.Problems, evaluation.Violations.ToList())
            .Add(x => x.Describe, (RuleViolation v) => v.Message)
            .Add(x => x.Changed, () =>
            {
                // What the sheet page does: evaluate again and hand the panel the new evaluation.
                var again = _engine.Recompute(sheet);
                cut!.Render(q => q.Add(x => x.Evaluation, again).Add(x => x.Problems, again.Violations.ToList()));
                onChanged?.Invoke();
            }));
        return cut;
    }

    [Fact]
    public void ScoresAreReadOnly_AndEachSourceIsAFieldsetWithLabelledChoices()
    {
        var cut = Draw(AttributeBoostTests.Sheet("hill", 5));
        Assert.Empty(cut.FindAll("input[id^='attr-input-']"));
        Assert.Empty(cut.FindAll("[data-testid$='-total']"));
        Assert.Equal(2, cut.FindAll("fieldset legend").Count);
        foreach (var select in cut.FindAll("[data-testid=attr-boosts] select"))
        {
            Assert.NotNull(cut.Find($"label[for='{select.Id}']"));
        }

        var oneOf = cut.Find("#boost-ancestry-b1");
        Assert.Equal(new[] { "", "str", "con" }, oneOf.QuerySelectorAll("option").Select(o => o.GetAttribute("value")));
        Assert.Contains("Strength or Constitution", cut.Find("label[for='boost-ancestry-b1']").TextContent);
        Assert.Equal(4, cut.Find("#boost-ancestry-b2").QuerySelectorAll("option").Length);
    }

    [Fact]
    public void ChoosingAnOption_WritesTheKey_AndChoosingNothingRemovesIt()
    {
        var sheet = AttributeBoostTests.Sheet("vale");
        var changed = 0;
        var cut = Draw(sheet, onChanged: () => changed++);
        cut.Find("#boost-ancestry-b1").Change("str");
        Assert.Equal(new object[] { "str" }, (IEnumerable<object>)sheet["boost__ancestry__vale.b1"]);
        Assert.Equal(1, changed);
        cut.Find("#boost-ancestry-b1").Change("");
        Assert.False(sheet.ContainsKey("boost__ancestry__vale.b1"));
    }

    [Fact]
    public void TickingTwoTradeBoxesInTurn_StoresBoth_AndKeepsBothTicked()
    {
        var sheet = AttributeBoostTests.Sheet("hill");
        var cut = Draw(sheet);
        cut.Find("#boost-trade-ancestry-con").Change(true);
        cut.Find("#boost-trade-ancestry-dex").Change(true);
        Assert.Equal(new[] { "con", "dex" }, ((IEnumerable<object>)sheet["boost__ancestry__hill.trade"]).Select(o => o.ToString()).ToArray());
        Assert.NotNull(cut.Find("#boost-ancestry-t1"));
        Assert.True(cut.Find("#boost-trade-ancestry-con").HasAttribute("checked"));
        Assert.True(cut.Find("#boost-trade-ancestry-dex").HasAttribute("checked"));
        Assert.Contains("Give up 2 fixed boosts for 1 free", cut.Find("[data-testid=boost-trade-ancestry]").TextContent);
    }

    [Fact]
    public void AHalfStep_IsShownAsOneHalf_AndSpokenInWordsWithoutIt()
    {
        string[] str = { "str" };
        var sheet = AttributeBoostTests.Sheet("vale", 20, ("boost__ancestry__vale.b1", str), ("boost__lvl5__m1", str), ("boost__lvl10__m1", str), ("boost__lvl15__m1", str), ("boost__lvl20__m1", str));
        var cut = Draw(sheet);
        Assert.Contains("½", cut.Find("[data-testid=attr-mod-str]").TextContent);
        Assert.Equal("true", cut.Find("[data-testid=attr-mod-str] span").GetAttribute("aria-hidden"));
        var spoken = cut.Find("[data-testid=attr-mod-spoken-str]").TextContent;
        Assert.DoesNotContain("½", spoken);
        Assert.Contains("half a step", spoken);
        Assert.Empty(cut.FindAll("[data-testid=attr-mod-spoken-dex]"));
    }

    [Fact]
    public void AProblem_IsSaidOnce_InsideThePoliteLiveRegion()
    {
        var sheet = AttributeBoostTests.Sheet("twin");
        var cut = Draw(sheet);
        Assert.Single(cut.FindAll("li.field-error"));
        Assert.Single(cut.Find("[data-testid=boost-status]").QuerySelectorAll("li.field-error"));
        Assert.Equal("polite", cut.Find("[data-testid=boost-status]").GetAttribute("aria-live"));
    }

    [Fact]
    public void AnOpenBoost_ShowsAsAnOpenNotice_Once()
    {
        var cut = Draw(AttributeBoostTests.Sheet("vale", 5));
        var notices = cut.Find("[data-testid=boost-status]").QuerySelectorAll("li.field-error");
        Assert.Equal(2, notices.Length); // the ancestry's one-of choice and level 5's two free ones
        Assert.Empty(cut.FindAll("[data-testid=attr-problems] li"));
    }

    [Fact]
    public void WithoutEditRights_TheFieldsetsAreDisabled_AndWithNoSourceTheNoteShows()
    {
        var cut = Draw(AttributeBoostTests.Sheet("vale"), canEdit: false);
        Assert.All(cut.FindAll("fieldset"), f => Assert.True(f.HasAttribute("disabled")));
        var none = Draw(new Dictionary<string, object> { ["level"] = 1L, ["attrMethod"] = "boosts" });
        Assert.Contains("No boosts yet", none.Markup);
    }

    [Theory]
    [InlineData("ru", "Повышения")]
    [InlineData("he", "שיפורים")]
    public void TheMethodNameAndTheTradeLabelAreTranslated_WithTheSystemsNumbers(string culture, string method)
    {
        var cut = Draw(AttributeBoostTests.Sheet("hill"), culture: culture);
        Assert.Contains(method, cut.Find("#attr-method").TextContent);
        var trade = cut.Find("[data-testid=boost-trade-ancestry]").GetAttribute("aria-label")!;
        Assert.Contains("2", trade);
        Assert.Contains("1", trade);
        Assert.DoesNotContain("{", trade);
    }
}
