using Bunit;
using Codex.Core.Rules;
using Codex.Web.Components.Shared;

namespace Codex.Tests.Components;

/// <summary>The list of bonuses a roll counted, or always counts (docs/ROLL_BONUSES_DESIGN.md).</summary>
public class RollBonusListTests : ComponentTestBase
{
    private IRenderedComponent<RollBonusList> Render(IReadOnlyList<RollBonus> bonuses, bool pool = false, string? testId = "bonuses")
        => Render<RollBonusList>(p => p.Add(x => x.Bonuses, bonuses).Add(x => x.Caption, "Always counted").Add(x => x.Pool, pool).Add(x => x.TestId, testId));

    [Fact]
    public void EachBonus_ShowsItsAmountAndLabel_UnderTheTestId()
    {
        var cut = Render(new[]
        {
            new RollBonus(null, "Crew", "Composed", 1, false),
            new RollBonus(null, "Deadly", "Deadly", -2, false)
        });

        var lines = cut.Find(Id("bonuses")).QuerySelectorAll("li");
        Assert.Equal(2, lines.Length);
        Assert.Contains("+1", lines[0].TextContent);
        Assert.Contains("Composed", lines[0].TextContent);
        Assert.Contains("-2", lines[1].TextContent);
        Assert.Contains("Deadly", lines[1].TextContent);
    }

    [Fact]
    public void TheSource_ShowsOnlyWhenItDiffersFromTheLabel()
    {
        var cut = Render(new[]
        {
            new RollBonus(null, "Ambush", "Ambush", 1, false),
            new RollBonus(null, "Crew upgrade", "Lucky charm", 1, false)
        });

        var lines = cut.FindAll("li");
        Assert.DoesNotContain("(", lines[0].TextContent);
        Assert.Contains("(Crew upgrade)", lines[1].TextContent);
    }

    [Fact]
    public void ATickedBonus_SaysItWasChosen_AnAlwaysOnOneDoesNot()
    {
        var cut = Render(new[]
        {
            new RollBonus("ambush#0", "Ambush", "Ambush", 1, true),
            new RollBonus(null, "Crew", "Composed", 1, false)
        });

        var lines = cut.FindAll("li");
        Assert.Contains("chosen", lines[0].TextContent);
        Assert.DoesNotContain("chosen", lines[1].TextContent);
    }

    [Fact]
    public void ANumber_IsSigned_AndAPoolAmountIsANumberOfDice()
    {
        var bonuses = new[] { new RollBonus(null, "Crew", "Composed", 2, false) };

        Assert.Equal("+2", Render(bonuses).Find("bdi").TextContent);
        Assert.Equal("+2d", Render(bonuses, pool: true).Find("bdi").TextContent);
    }

    [Fact]
    public void ThereAreNoBonuses_NothingIsRendered()
    {
        var cut = Render(Array.Empty<RollBonus>());

        Assert.Empty(cut.FindAll(Id("bonuses")));
        Assert.Empty(cut.FindAll("ul"));
    }

    [Fact]
    public void TheList_IsNamedByItsCaption()
    {
        var cut = Render(new[] { new RollBonus(null, "Crew", "Composed", 1, false) });

        var caption = cut.Find("span.small-muted");
        Assert.Equal("Always counted", caption.TextContent);
        Assert.Equal(caption.Id, cut.Find("ul").GetAttribute("aria-labelledby"));
    }
}
