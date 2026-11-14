using Bunit;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using static Codex.Tests.Components.FitdContent;

namespace Codex.Tests.Components;

/// <summary>The roll, link and members widgets of the real Forged in the Dark sheets (docs/ROLL_BONUSES_DESIGN.md, docs/CREW_LINKS_DESIGN.md),
/// drawn by <see cref="SheetWidget"/> with the real system and pack. The component needs no services beyond the localizer.</summary>
public class SheetWidgetTests(FitdContent content) : ComponentTestBase, IClassFixture<FitdContent>
{
    private static readonly ActorDocument NightCrew = Actor("crew-1", "Night Crew", Crew("forged-in-the-fire"));
    private static readonly ActorDocument OtherCrew = Actor("crew-2", "Day Crew", Crew());

    private IRenderedComponent<SheetWidget> Draw(LayoutWidget widget, Dictionary<string, object> sheet, Action<ComponentParameterCollectionBuilder<SheetWidget>>? more = null, bool canEdit = true, bool canRoll = true)
    {
        var linked = new Linked(NightCrew, OtherCrew);
        return Render<SheetWidget>(p =>
        {
            p.Add(x => x.Widget, widget)
                .Add(x => x.Definition, content.System)
                .Add(x => x.View, new SheetView(content.System, content.Resolver, sheet, linked))
                .Add(x => x.Sheet, sheet)
                .Add(x => x.Resolver, content.Resolver)
                .Add(x => x.Linked, linked)
                .Add(x => x.CanEdit, canEdit)
                .Add(x => x.CanRoll, canRoll)
                .Add(x => x.CampaignId, "camp-1");
            more?.Invoke(p);
        });
    }

    private static LayoutWidget Roll(string roll) => new() { Widget = "roll", Roll = roll };

    // ---- The roll widget ----

    [Fact]
    public void AnActionRoll_OffersAmbushAsATickBox_WithItsLabelAssociated()
    {
        var cut = Draw(Roll("action"), Scoundrel("ambush"));
        cut.Find(Id("roll-action-action")).Change("skirmish");

        var box = cut.Find(Id("roll-action-bonuses")).QuerySelector("input[type=checkbox]")!;
        var label = cut.Find($"label[for='{box.Id}']");
        Assert.Contains("From hiding or a trap", label.TextContent);
        Assert.Contains("1d", label.TextContent); // a pool roll: the amount is dice
        Assert.False(box.HasAttribute("disabled"));
        Assert.Equal("fieldset", cut.Find(Id("roll-action-bonuses")).TagName.ToLowerInvariant());
        Assert.Equal("Bonuses", cut.Find("legend").TextContent);
    }

    [Fact]
    public void ASheetWithNoAbility_OffersNoTickBoxes()
    {
        var cut = Draw(Roll("action"), Scoundrel());
        cut.Find(Id("roll-action-action")).Change("skirmish");

        Assert.Empty(cut.FindAll(Id("roll-action-bonuses")));
    }

    [Fact]
    public void TheTickBox_IsDisabled_WhenTheReaderMayNotRoll()
    {
        var cut = Draw(Roll("action"), Scoundrel("ambush"), canRoll: false);
        cut.Find(Id("roll-action-action")).Change("skirmish");

        Assert.True(cut.Find(Id("roll-action-bonuses")).QuerySelector("input")!.HasAttribute("disabled"));
        Assert.True(cut.Find(Id("roll-action-go")).HasAttribute("disabled"));
    }

    [Fact]
    public void TickingAmbush_IsCarriedToTheRollRequest_AndClearedAfterIt()
    {
        SheetRollRequest? rolled = null;
        var cut = Draw(Roll("action"), Scoundrel("ambush"), p => p.Add(x => x.OnRoll, r => rolled = r));
        cut.Find(Id("roll-action-action")).Change("skirmish");

        cut.Find(Id("roll-action-bonuses")).QuerySelector("input")!.Change(true);
        cut.Find(Id("roll-action-go")).Click();

        Assert.NotNull(rolled);
        Assert.Equal("action", rolled!.Definition);
        Assert.Single(rolled.OptIns);

        cut.Find(Id("roll-action-go")).Click();
        Assert.Empty(rolled!.OptIns); // the second roll starts unticked
        Assert.False(cut.Find(Id("roll-action-bonuses")).QuerySelector("input")!.HasAttribute("checked"));
    }

    [Fact]
    public void ACrewAbility_IsListedUnderAlwaysCounted_AsDice()
    {
        var cut = Draw(Roll("resistance"), Scoundrel());
        cut.Find(Id("roll-resistance-attribute")).Change("resolve");

        var always = cut.Find(Id("roll-resistance-always"));
        Assert.Contains("Always counted", always.TextContent);
        var line = Assert.Single(always.QuerySelectorAll("li"));
        Assert.Contains("+1d", line.TextContent);
        Assert.Equal(always.QuerySelector("span")!.Id, always.QuerySelector("ul")!.GetAttribute("aria-labelledby"));
    }

    [Fact]
    public void WithoutTheLinkedCrew_NothingIsAlwaysCounted()
    {
        var sheet = Scoundrel();
        sheet.Remove("link__crew");
        var cut = Draw(Roll("resistance"), sheet);
        cut.Find(Id("roll-resistance-attribute")).Change("resolve");

        Assert.Empty(cut.FindAll(Id("roll-resistance-always")));
    }

    // ---- The link widget ----

    private static readonly LayoutWidget CrewLink = new() { Widget = "link", Key = "crew" };

    [Fact]
    public void TheLinkWidget_OffersTheCampaignsCrews_AndShowsTheStoredOne()
    {
        var actors = new[] { NightCrew, OtherCrew, Actor("pal", "A friend", Scoundrel()) };
        var cut = Draw(CrewLink, Scoundrel(), p => p.Add(x => x.Actors, actors).Add(x => x.ActorId, "me"));

        var select = cut.Find(Id("link-crew-select"));
        Assert.Equal("Crew", cut.Find("label").TextContent);
        Assert.Equal(select.Id, cut.Find("label").GetAttribute("for"));

        var options = select.QuerySelectorAll("option");
        Assert.Equal(new[] { "No Crew", "Day Crew", "Night Crew" }, options.Select(o => o.TextContent).ToArray());
        Assert.Equal("Night Crew", options.Single(o => o.HasAttribute("selected")).TextContent);

        var open = cut.Find(Id("link-crew-open")).QuerySelector("a")!;
        Assert.Equal("Night Crew", open.TextContent);
        Assert.False(string.IsNullOrEmpty(open.GetAttribute("href")));
    }

    [Fact]
    public void ChoosingACrew_WritesItOnTheSheet_AndTellsTheHost()
    {
        var sheet = Scoundrel();
        var changed = 0;
        var cut = Draw(CrewLink, sheet, p => p.Add(x => x.Actors, new[] { NightCrew, OtherCrew }).Add(x => x.LinkChanged, () => changed++));

        cut.Find(Id("link-crew-select")).Change("crew-2");

        Assert.Equal("crew-2", sheet["link__crew"]);
        Assert.Equal(1, changed);

        cut.Find(Id("link-crew-select")).Change("");
        Assert.False(sheet.ContainsKey("link__crew"));
    }

    [Fact]
    public void AReaderWhoCannotEdit_SeesTheLinkedCrewButNoSelect()
    {
        var cut = Draw(CrewLink, Scoundrel(), p => p.Add(x => x.Actors, new[] { NightCrew }), canEdit: false);

        Assert.Empty(cut.FindAll("select"));
        Assert.Equal("Night Crew", cut.Find(Id("link-crew-open")).TextContent.Trim());
    }

    [Fact]
    public void ACrewTheViewerCannotSee_IsNotNamed()
    {
        var cut = Draw(CrewLink, Scoundrel(), p => p.Add(x => x.Actors, new[] { OtherCrew }));

        Assert.DoesNotContain("Night Crew", cut.Markup);
        Assert.Equal("Not visible", cut.Find(Id("link-crew-select")).QuerySelector("option[selected]")!.TextContent);
    }

    [Fact]
    public void AVaultCharacter_HasNoLinkWidget()
    {
        var cut = Draw(CrewLink, Scoundrel(), p => p.Add(x => x.Vault, true));

        Assert.Empty(cut.FindAll(Id("link-crew")));
    }

    // ---- The members widget, on the crew's own sheet ----

    private IRenderedComponent<SheetWidget> Members(IEnumerable<ActorDocument> actors)
    {
        var crew = Crew();
        return Render<SheetWidget>(p => p
            .Add(x => x.Widget, new LayoutWidget { Widget = "members" })
            .Add(x => x.Definition, content.System)
            .Add(x => x.View, new SheetView(content.System, content.Resolver, crew))
            .Add(x => x.Sheet, crew)
            .Add(x => x.Resolver, content.Resolver)
            .Add(x => x.Actors, actors)
            .Add(x => x.ActorId, "crew-1")
            .Add(x => x.CampaignId, "camp-1"));
    }

    [Fact]
    public void TheMembersWidget_ListsTheScoundrelsLinkedToThisCrew_ByName()
    {
        var linked = Scoundrel();
        var elsewhere = Scoundrel();
        elsewhere["link__crew"] = "crew-2";
        var cut = Members(new[]
        {
            Actor("s2", "Zed", linked), Actor("s1", "Anya", Scoundrel()), Actor("s3", "Not ours", elsewhere), NightCrew
        });

        var list = cut.Find(Id("widget-members")).QuerySelector("ul")!;
        Assert.Equal(new[] { "Anya", "Zed" }, list.QuerySelectorAll("li").Select(li => li.TextContent.Trim()).ToArray());
        Assert.Single(cut.FindAll(Id("member-s1")));
        Assert.Empty(cut.FindAll(Id("member-s3")));
        Assert.Equal("Members", cut.Find(".field-label").TextContent);
        Assert.Equal(cut.Find(".field-label").Id, list.GetAttribute("aria-labelledby"));
    }

    [Fact]
    public void ACrewWithNoMembers_SaysSo()
    {
        var cut = Members(new[] { NightCrew });

        Assert.Equal("No members yet", cut.Find(Id("members-empty")).TextContent);
        Assert.Empty(cut.FindAll("ul"));
    }
}
