using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using static Codex.Tests.Components.FitdContent;

namespace Codex.Tests.Components;

/// <summary>P11c of docs/COMPANIONS_DESIGN.md: what the sheet shows under a pick list that has a <c>changesOn</c>, and the members widget the generic sheet of a
/// kind that others link to ends with (an invented system).</summary>
public class SheetWidgetP11cTests : ComponentTestBase
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: shapes
        name: Shapes
        moments:
          - { key: prep, label: Preparations }
        entityTypes:
          - { type: gadget, label: Gadget }
        sheetKinds:
          - kind: hero
            label: Hero
            lists:
              - { key: kit, label: Kit, type: gadget, description: "Pick them at the preparations.", changesOn: [prep] }
              - { key: spare, label: Spare, type: gadget }
          - kind: pet
            label: Pet
            links:
              - { key: owner, kind: hero, label: Owner }
        """);

    public SheetWidgetP11cTests() => Services.AddSingleton<IContentRegistry>(new ContentRegistry());

    private IRenderedComponent<SheetWidget> Draw(LayoutWidget widget, string kind, IEnumerable<ActorDocument>? actors = null)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = kind };
        var resolver = new AttributeBoostTests.Resolver();
        return Render<SheetWidget>(p => p
            .Add(x => x.Widget, widget)
            .Add(x => x.Definition, System)
            .Add(x => x.View, new SheetView(System, resolver, sheet))
            .Add(x => x.Sheet, sheet)
            .Add(x => x.Resolver, resolver)
            .Add(x => x.Actors, actors ?? Array.Empty<ActorDocument>())
            .Add(x => x.ActorId, "hero-1")
            .Add(x => x.CampaignId, "camp-1")
            .Add(x => x.CanEdit, true));
    }

    private static LayoutWidget List(string key) => new() { Widget = "list", Key = key };

    [Fact]
    public void AListWithAChangesOn_SaysWhenItIsUsuallyChanged_AndHasAnAnchorForTheNoticeAfterTheMoment()
    {
        var cut = Draw(List("kit"), "hero");

        Assert.Equal("Usually changed on Preparations", cut.Find(Id("list-kit-changes-on")).TextContent.Trim());
        Assert.Equal("choice-kit", cut.Find(Id("list-kit-changes-on")).Id);
        Assert.Equal("Pick them at the preparations.", cut.Find(Id("list-kit-note")).TextContent.Trim());
        // The sheet scrolls to the list by this key when the notice's link is followed.
        Assert.Equal("kit", cut.Find(Id("widget-kit")).GetAttribute("data-rule-key"));
    }

    [Fact]
    public void TheGenericMembersWidget_HasItsOwnHeading_AndDrawsNothingWhileNothingLinks()
    {
        var generic = Assert.Single(SheetLayout.For(System, new Dictionary<string, object> { ["kind"] = "hero" })).Widgets.Single(w => w.Widget == "members");

        var empty = Draw(generic, "hero", new[] { Actor("p3", "Elsewhere", new() { ["kind"] = "pet", ["link__owner"] = "hero-2" }) });
        Assert.Empty(empty.FindAll(Id("widget-members")));
        Assert.Empty(empty.FindAll(Id("members-empty")));
        Assert.Equal(string.Empty, empty.Markup.Trim());

        // A members widget a system declares itself keeps the old heading and its empty line.
        var declared = Draw(new LayoutWidget { Widget = "members" }, "hero");
        Assert.Equal("Members", declared.Find(".field-label").TextContent);
        Assert.Equal("No members yet", declared.Find(Id("members-empty")).TextContent.Trim());
    }

    [Fact]
    public void AListWithNeither_ShowsNoNote()
    {
        var cut = Draw(List("spare"), "hero");

        Assert.Empty(cut.FindAll(Id("list-spare-changes-on")));
        Assert.Empty(cut.FindAll(Id("list-spare-note")));
    }

    [Fact]
    public void TheGenericSheetsMembersWidget_ListsThePetsLinkedToTheHero()
    {
        var generic = Assert.Single(SheetLayout.For(System, new Dictionary<string, object> { ["kind"] = "hero" })).Widgets.Single(w => w.Widget == "members");
        var cut = Draw(generic, "hero", new[]
        {
            Actor("p2", "Zed", new() { ["kind"] = "pet", ["link__owner"] = "hero-1" }),
            Actor("p1", "Anya", new() { ["kind"] = "pet", ["link__owner"] = "hero-1" }),
            Actor("p3", "Elsewhere", new() { ["kind"] = "pet", ["link__owner"] = "hero-2" })
        });

        var names = cut.Find(Id("widget-members")).QuerySelectorAll("li").Select(li => li.TextContent.Trim()).ToArray();
        Assert.Equal(new[] { "Anya", "Zed" }, names);
        Assert.Equal("Linked to this sheet", cut.Find(".field-label").TextContent);
    }
}

/// <summary>A derived number marked <c>hidden</c> is left out of the attributes panel's list; the others are shown as before.</summary>
public class AttributesPanelHiddenDerivedTests : ComponentTestBase
{
    private static readonly string Text = AttributeModifierScaleTests.System.Replace(
        "- { key: strMod, label: Str mod, formula: \"mod(str)\" }",
        "- { key: strMod, label: Str mod, formula: \"mod(str)\" }\n    - { key: hush, label: Hush, formula: \"mod(str) + 1\", hidden: true }");

    public AttributesPanelHiddenDerivedTests() => Services.AddSingleton<IContentRegistry>(new ContentRegistry());

    [Fact]
    public void WhenEveryDerivedNumberIsHidden_NoEmptyListIsDrawn()
    {
        var system = SystemDefinitionLoader.Parse(Text.Replace("{ key: strMod, label: Str mod, formula: \"mod(str)\" }", "{ key: strMod, label: Str mod, formula: \"mod(str)\", hidden: true }"));
        var sheet = AttributeBoostTests.Sheet("vale", 1);
        var evaluation = new SheetRuleEngine(system, new AttributeBoostTests.Resolver()).Recompute(sheet);

        var cut = Render<AttributesPanel>(p => p
            .Add(x => x.Definition, system)
            .Add(x => x.Evaluation, evaluation)
            .Add(x => x.Sheet, sheet)
            .Add(x => x.CanEdit, true)
            .Add(x => x.Problems, evaluation.Violations.ToList())
            .Add(x => x.Describe, (RuleViolation v) => v.Message));

        Assert.Empty(cut.FindAll(Id("derived-fields")));
    }

    [Fact]
    public void TheHiddenNumber_IsNotDrawn_ButItIsWorkedOut()
    {
        Assert.Contains("hush", Text);
        var system = SystemDefinitionLoader.Parse(Text);
        var sheet = AttributeBoostTests.Sheet("vale", 1);
        var evaluation = new SheetRuleEngine(system, new AttributeBoostTests.Resolver()).Recompute(sheet);

        var cut = Render<AttributesPanel>(p => p
            .Add(x => x.Definition, system)
            .Add(x => x.Evaluation, evaluation)
            .Add(x => x.Sheet, sheet)
            .Add(x => x.CanEdit, true)
            .Add(x => x.Problems, evaluation.Violations.ToList())
            .Add(x => x.Describe, (RuleViolation v) => v.Message));

        Assert.True(evaluation.Derived.ContainsKey("hush"));
        Assert.Single(cut.FindAll(Id("derived-strMod")));
        Assert.Empty(cut.FindAll(Id("derived-hush")));
    }
}
