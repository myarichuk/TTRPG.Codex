using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>The panel's flaw controls: the trade line that says the flaw goes too, and the add and remove buttons for a source's voluntary flaws.</summary>
public class AttributesPanelFlawsTests : ComponentTestBase
{
    private readonly SystemDefinition _system = SystemDefinitionLoader.Parse(AttributeBoostTests.FlawSystem);
    private readonly SheetRuleEngine _engine = AttributeBoostTests.FlawEngine();

    public AttributesPanelFlawsTests() => Services.AddSingleton<IContentRegistry>(new ContentRegistry());

    private IRenderedComponent<AttributesPanel> Draw(Dictionary<string, object> sheet)
    {
        var evaluation = _engine.Recompute(sheet);
        IRenderedComponent<AttributesPanel>? cut = null;
        cut = Render<AttributesPanel>(p => p
            .Add(x => x.Definition, _system)
            .Add(x => x.Evaluation, evaluation)
            .Add(x => x.Sheet, sheet)
            .Add(x => x.CanEdit, true)
            .Add(x => x.Problems, evaluation.Violations.ToList())
            .Add(x => x.Describe, (RuleViolation v) => v.Message)
            .Add(x => x.Changed, () =>
            {
                var again = _engine.Recompute(sheet);
                cut!.Render(q => q.Add(x => x.Evaluation, again).Add(x => x.Problems, again.Violations.ToList()));
            }));
        return cut;
    }

    [Fact]
    public void TheTradeLine_SaysTheFlawGoesToo()
    {
        var cut = Draw(AttributeBoostTests.Sheet("hill"));
        var note = cut.Find("#boost-trade-note-ancestry");
        Assert.Contains("flaw goes too", note.TextContent);
        Assert.Equal(note.Id, cut.Find("[data-testid=boost-trade-ancestry]").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void AddingAndRemovingAFlaw_WritesTheStoredList_AndTheButtonsNameTheAttribute()
    {
        var sheet = AttributeBoostTests.Sheet("vale");
        var cut = Draw(sheet);
        var add = cut.Find("[data-testid=boost-flaw-add-ancestry-dex]");
        Assert.Equal("Add a flaw to Dexterity", add.GetAttribute("aria-label"));
        add.Click();
        Assert.Equal(new[] { "dex" }, ((IEnumerable<object>)sheet["boost__ancestry__vale.flaws"]).Select(o => o.ToString()).ToArray());
        Assert.Equal(8L, sheet["dex"]);
        Assert.Empty(cut.FindAll("[data-testid=boost-flaw-add-ancestry-dex]"));
        var remove = cut.Find("[data-testid=boost-flaw-remove-ancestry-dex]");
        Assert.Equal("Remove the flaw from Dexterity", remove.GetAttribute("aria-label"));
        remove.Click();
        Assert.False(sheet.ContainsKey("boost__ancestry__vale.flaws"));
        Assert.Equal(10L, sheet["dex"]);
    }

    [Fact]
    public void AnAttributeThatAlreadyHasTheEntrysFlaw_OffersNoSecondOne()
    {
        var cut = Draw(AttributeBoostTests.Sheet("hill"));
        Assert.Empty(cut.FindAll("[data-testid=boost-flaw-add-ancestry-str]"));
        Assert.NotNull(cut.Find("[data-testid=boost-flaw-add-ancestry-dex]"));
    }
}
