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
        Assert.Contains("flaw it has goes too", note.TextContent);
        Assert.Equal(note.Id, cut.Find("[data-testid=boost-trade-ancestry]").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void TheTradeNote_IsAbsent_WhenTheTradeKeepsFlaws()
    {
        var plain = AttributeBoostTests.Engine();
        var sheet = AttributeBoostTests.Sheet("hill");
        var evaluation = plain.Recompute(sheet);
        var cut = Render<AttributesPanel>(p => p
            .Add(x => x.Definition, SystemDefinitionLoader.Parse(AttributeBoostTests.System))
            .Add(x => x.Evaluation, evaluation)
            .Add(x => x.Sheet, sheet)
            .Add(x => x.CanEdit, true)
            .Add(x => x.Describe, (RuleViolation v) => v.Message));
        Assert.Empty(cut.FindAll("#boost-trade-note-ancestry"));
        Assert.False(cut.Find("[data-testid=boost-trade-ancestry]").HasAttribute("aria-describedby"));
        Assert.Empty(cut.FindAll("[data-testid=boost-flaws-ancestry]"));
    }

    [Fact]
    public void AFlawIsOneToggleButtonPerAttribute_NamedByItsText_AndKeepsItsPlace()
    {
        var sheet = AttributeBoostTests.Sheet("vale");
        var cut = Draw(sheet);
        var button = cut.Find("[data-testid=boost-flaw-ancestry-dex]");
        Assert.Equal("Flaw: Dexterity", button.TextContent.Trim());
        Assert.False(button.HasAttribute("aria-label"));
        Assert.Equal("false", button.GetAttribute("aria-pressed"));
        button.Click();
        Assert.Equal(new[] { "dex" }, ((IEnumerable<object>)sheet["boost__ancestry__vale.flaws"]).Select(o => o.ToString()).ToArray());
        Assert.Equal(8L, sheet["dex"]);
        Assert.Equal("true", cut.Find("[data-testid=boost-flaw-ancestry-dex]").GetAttribute("aria-pressed"));
        cut.Find("[data-testid=boost-flaw-ancestry-dex]").Click();
        Assert.False(sheet.ContainsKey("boost__ancestry__vale.flaws"));
        Assert.Equal(10L, sheet["dex"]);
        Assert.Equal("boost-flaws-label-ancestry", cut.Find("[data-testid=boost-flaws-ancestry]").GetAttribute("aria-labelledby"));
    }

    [Fact]
    public void AnAttributeThatAlreadyHasTheEntrysFlaw_OffersNoSecondOne()
    {
        var cut = Draw(AttributeBoostTests.Sheet("hill"));
        Assert.Empty(cut.FindAll("[data-testid=boost-flaw-ancestry-str]"));
        Assert.NotNull(cut.Find("[data-testid=boost-flaw-ancestry-dex]"));
    }
}
