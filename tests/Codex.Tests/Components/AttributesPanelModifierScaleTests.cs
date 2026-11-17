using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>The attribute panel for attributes that are modifiers: a signed modifier and no score, the pending partial boost drawn as a half and
/// said in words, and a typed-in value labelled as a modifier.</summary>
public class AttributesPanelModifierScaleTests : ComponentTestBase
{
    private readonly SystemDefinition _system = SystemDefinitionLoader.Parse(AttributeModifierScaleTests.System);
    private readonly SheetRuleEngine _engine = new(SystemDefinitionLoader.Parse(AttributeModifierScaleTests.System), new AttributeBoostTests.Resolver());

    public AttributesPanelModifierScaleTests() => Services.AddSingleton<IContentRegistry>(new ContentRegistry());

    private IRenderedComponent<AttributesPanel> Draw(Dictionary<string, object> sheet, bool canEdit = true)
    {
        var evaluation = _engine.Recompute(sheet);
        return Render<AttributesPanel>(p => p
            .Add(x => x.Definition, _system)
            .Add(x => x.Evaluation, evaluation)
            .Add(x => x.Sheet, sheet)
            .Add(x => x.CanEdit, canEdit)
            .Add(x => x.Problems, evaluation.Violations.ToList())
            .Add(x => x.Describe, (RuleViolation v) => v.Message));
    }

    private static Dictionary<string, object> StrengthTo(long level)
    {
        var picks = Enumerable.Range(1, (int)level).Select(n => ($"boost__l{n}__m1", new[] { "str" })).ToArray();
        return AttributeBoostTests.Sheet("vale", level, picks);
    }

    [Fact]
    public void UnderBoosts_EachAttributeShowsASignedModifier_AndNoScore()
    {
        var cut = Draw(StrengthTo(3));
        Assert.Equal("+3", cut.Find("[data-testid=attr-mod-str]").TextContent.Trim());
        Assert.Equal("+0", cut.Find("[data-testid=attr-mod-dex]").TextContent.Trim());
        Assert.Empty(cut.FindAll(".attr-score"));
        Assert.Empty(cut.FindAll("input[id^='attr-input-']"));
    }

    [Fact]
    public void APendingPartialBoost_IsAHalfOnTheModifier_AndSaidInWords()
    {
        var cut = Draw(StrengthTo(5));
        Assert.Contains("+4½", cut.Find("[data-testid=attr-mod-str]").TextContent);
        Assert.Contains("partial boost", cut.Find("[data-testid=attr-mod-spoken-str]").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial boost", cut.Find("[data-testid=attr-boosts] p").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-testid=attr-mod-spoken-dex]"));
        Assert.Equal("Partial boost", cut.Find("[data-testid=attr-partial-str]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=attr-partial-dex]"));
    }

    [Fact]
    public void ATypedInValue_IsLabelledAsAModifier()
    {
        var sheet = StrengthTo(1);
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        var cut = Draw(sheet);
        var input = cut.Find("#attr-input-str");
        Assert.Equal("-5", input.GetAttribute("min"));
        Assert.Equal("10", input.GetAttribute("max"));
        Assert.Contains("modifier", cut.Find("label[for='attr-input-str']").TextContent);
        // The value is not repeated in a total line.
        Assert.Empty(cut.FindAll("[data-testid=attr-str-total]"));
    }
}
