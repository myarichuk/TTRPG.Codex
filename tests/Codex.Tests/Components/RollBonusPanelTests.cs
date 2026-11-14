using Bunit;
using Codex.Core.Rules;
using Codex.Web.Components.Shared;

namespace Codex.Tests.Components;

/// <summary>"Bonuses for the next roll": one tick box per opt-in, kept in the owner's tray.</summary>
public class RollBonusPanelTests : ComponentTestBase, IClassFixture<FitdContent>
{
    private readonly FitdContent content;

    public RollBonusPanelTests(FitdContent content) => this.content = content;

    private static readonly RollOptIn Ambush = new("ambush#0", "Ambush", "+1d", "Ambush", null, true, null, "check");
    private static readonly RollOptIn Spent = new("charm#0", "Lucky charm", "+1", "Charm", new RollOptInSpend("uses_charm", 1), false, null, "attack.weapon.ranged");

    private IRenderedComponent<RollBonusPanel> Render(IReadOnlyList<RollOptIn> offers, RollBonusTray tray, bool compact = false, Action? changed = null)
        => Render<RollBonusPanel>(p =>
        {
            p.Add(x => x.Offers, offers).Add(x => x.Tray, tray).Add(x => x.Compact, compact);
            if (changed != null) p.Add(x => x.Changed, changed);
        });

    [Fact]
    public void EachOffer_IsACheckbox_WhoseLabelIsAssociatedWithIt()
    {
        var cut = Render(new[] { Ambush, Spent }, new RollBonusTray());

        var boxes = cut.FindAll("input[type=checkbox]");
        Assert.Equal(2, boxes.Count);
        foreach (var box in boxes)
        {
            var label = cut.Find($"label[for='{box.Id}']");
            Assert.False(string.IsNullOrWhiteSpace(label.TextContent));
        }

        Assert.Contains("Ambush", cut.Find("label[for='" + boxes[0].Id + "']").TextContent);
        Assert.Contains("+1d", cut.Find("label[for='" + boxes[0].Id + "']").TextContent);
    }

    [Fact]
    public void TheBoxes_AreGroupedUnderALegend()
    {
        var cut = Render(new[] { Ambush }, new RollBonusTray());

        var group = cut.Find(Id("roll-bonuses"));
        Assert.Equal("fieldset", group.TagName.ToLowerInvariant());
        Assert.Equal("Bonuses for the next roll", group.QuerySelector("legend")!.TextContent);
        Assert.Contains("Tick the ones that apply", group.QuerySelector("p")!.TextContent);
    }

    [Fact]
    public void ATightPanel_HasNoHint_AndAnotherTestId()
    {
        var cut = Render(new[] { Ambush }, new RollBonusTray(), compact: true);

        Assert.Single(cut.FindAll(Id("card-roll-bonuses")));
        Assert.Empty(cut.FindAll("p"));
    }

    [Fact]
    public void Ticking_TogglesTheTray_AndTellsTheHost()
    {
        var tray = new RollBonusTray();
        var changed = 0;
        var cut = Render(new[] { Ambush }, tray, changed: () => changed++);

        cut.Find(Id("roll-bonus-ambush-0")).Change(true);
        Assert.True(tray.IsTicked("ambush#0"));
        Assert.Equal(1, changed);

        cut.Find(Id("roll-bonus-ambush-0")).Change(false);
        Assert.False(tray.IsTicked("ambush#0"));
        Assert.Equal(2, changed);
    }

    [Fact]
    public void ATickedOffer_IsShownChecked()
    {
        var tray = new RollBonusTray();
        tray.Set("ambush#0", true);

        var cut = Render(new[] { Ambush }, tray);

        Assert.True(cut.Find(Id("roll-bonus-ambush-0")).HasAttribute("checked"));
    }

    [Fact]
    public void AnOfferThatCannotBePaid_IsDisabled_AndSaysWhy()
    {
        var cut = Render(new[] { Ambush, Spent }, new RollBonusTray());

        Assert.False(cut.Find(Id("roll-bonus-ambush-0")).HasAttribute("disabled"));
        var box = cut.Find(Id("roll-bonus-charm-0"));
        Assert.True(box.HasAttribute("disabled"));

        var why = cut.Find(Id("roll-bonus-why-charm-0"));
        Assert.Equal("No uses left", why.TextContent);
        Assert.Equal(why.Id, box.GetAttribute("aria-describedby"));
        Assert.Null(cut.Find(Id("roll-bonus-ambush-0")).GetAttribute("aria-describedby"));
        Assert.Contains("costs 1", cut.Find("li:nth-child(2)").TextContent);
    }

    [Theory]
    [InlineData("ru", "Из укрытия или из ловушки")]
    [InlineData("he", "ממסתור או ממלכודת")]
    public void WithNames_TheLabelIsInTheReadersLanguage_ButTheBoxKeepsItsIds(string culture, string label)
    {
        var offer = Ambush with { Label = "From hiding or a trap" };
        var names = RollBonusNames.For(content.Registry, content.System, culture);
        var tray = new RollBonusTray();
        var cut = Render<RollBonusPanel>(p => p.Add(x => x.Offers, new[] { offer }).Add(x => x.Tray, tray).Add(x => x.Names, names));

        var box = cut.Find(Id("roll-bonus-ambush-0"));
        Assert.Contains(label, cut.Find($"label[for='{box.Id}']").TextContent);
        Assert.DoesNotContain("From hiding or a trap", cut.Markup);

        box.Change(true);
        Assert.True(tray.IsTicked("ambush#0"));
    }

    [Fact]
    public void TheTarget_SaysWhichRollsAnOfferFits()
    {
        var cut = Render(new[] { Spent }, new RollBonusTray());

        Assert.Contains("on ranged weapon attack rolls", cut.Find("label").TextContent);
    }

    [Fact]
    public void ANamedTarget_ReadsTheSystemsOwnName()
    {
        var cut = Render<RollBonusPanel>(p => p.Add(x => x.Offers, new[] { Ambush with { Target = "attack.entry.rapier" } })
            .Add(x => x.Tray, new RollBonusTray()).Add(x => x.EntryName, id => id == "rapier" ? "Rapier" : null));

        Assert.Contains("on attacks with Rapier", cut.Find("label").TextContent);
    }

    [Fact]
    public void NoOffers_RenderNothing()
    {
        var cut = Render(Array.Empty<RollOptIn>(), new RollBonusTray());

        Assert.Empty(cut.FindAll("fieldset"));
    }
}
