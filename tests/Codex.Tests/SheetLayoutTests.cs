using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>Layouts (<see cref="SheetLayout"/>, overhaul step 6): which one a sheet gets, the generic one, srd5e's built-in one, and
/// what the loader says about a malformed one.</summary>
public class SheetLayoutTests
{
    private static SystemDefinition Kinds(string layout = "[]", string topLevel = "") => SystemDefinitionLoader.Parse($$"""
        id: layout-test
        name: Layout test
        attributes: [{ key: str, label: Strength }]
        {{topLevel}}
        sheetKinds:
          - kind: hero
            label: Hero
            values:
              - { key: grit, formula: "2" }
              - { key: style, input: true, options: [bold, sly], default: bold }
            tracks:
              - { key: stress, max: 6, display: boxes }
              - { key: "slots_{item.id}_{level}", each: "classes", levels: "1..2", max: "1" }
            lists:
              - { key: gear, label: Gear, type: item, itemFields: [{ key: used, type: Boolean }] }
            keyedLists:
              - key: actions
                keys: [{ key: hunt, group: insight }, { key: sway, group: resolve }]
                itemFields: [{ key: rating, type: Number, default: 0 }]
            layout: {{layout}}
          - kind: gang
            label: Gang
            role: shared
            tracks: [{ key: heat, max: 9 }]
        rolls:
          - { key: action, pool: "1", die: 6 }
        """);

    [Fact]
    public void A_KindsDeclaredLayout_IsItsOwn_AndAKindWithoutOne_GetsTheGenericOne()
    {
        var system = Kinds("[{ section: Play, widgets: [{ widget: track, key: stress }] }]");
        var hero = new Dictionary<string, object> { ["kind"] = "hero" };
        var gang = new Dictionary<string, object> { ["kind"] = "gang" };

        Assert.Equal(new[] { "Play" }, SheetLayout.For(system, hero).Select(s => s.Section));
        Assert.Empty(SheetLayout.Declared(system, gang));
        var generic = Assert.Single(SheetLayout.For(system, gang));
        Assert.Equal(SheetLayout.GenericSection, generic.Section);
        Assert.Equal(new[] { ("track", "heat") }, generic.Widgets.Select(w => (w.Widget, w.Key)));
    }

    [Fact]
    public void TheGenericLayout_ShowsEveryValueTrackListAndKeyedList_AndGeneratedTracksByTheirPrefix()
    {
        var widgets = Assert.Single(SheetLayout.Generic(Kinds().SheetKinds[0])).Widgets;

        Assert.Equal(new[] { "values", "track", "track", "list", "table" }, widgets.Select(w => w.Widget));
        Assert.Equal(new[] { "grit", "style" }, widgets[0].Keys);
        Assert.Equal("slots_*", widgets[2].Key);
        Assert.True(SheetLayout.Matches(widgets[2].Key!, "slots_wizard_2"));
        Assert.False(SheetLayout.Matches("stress", "stressed"));
        Assert.Equal(new[] { "used" }, widgets[3].Columns);
        Assert.Equal(new[] { "rating" }, widgets[4].Columns);
    }

    [Fact]
    public void KindsForARole_AreThoseOfThatRole_ThenThoseWithNone()
    {
        var system = Kinds();
        Assert.Equal(new[] { "gang", "hero" }, SheetLayout.KindsFor(system, "shared").Select(k => k.Kind));
        Assert.Equal(new[] { "hero" }, SheetLayout.KindsFor(system, "player").Select(k => k.Kind));
    }

    [Fact]
    public void Srd5e_DeclaresTheBuiltInLayout_AndASystemWithoutOne_GetsTheSame()
    {
        var srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.RepoRoot, "content", "systems", "srd5e", SystemDefinitionLoader.FileName));
        string Shape(IEnumerable<LayoutSection> layout) =>
            string.Join(" | ", layout.Select(s => $"{s.Section}{(s.Aside ? " (aside)" : "")}: {string.Join(",", s.Widgets.Select(w => w.Widget))}"));

        Assert.Equal(Shape(SheetLayout.Builtin), Shape(SheetLayout.For(srd, new Dictionary<string, object>())));
        Assert.Empty(SheetLayout.Problems(srd));
        Assert.Equal(Shape(SheetLayout.Builtin), Shape(SheetLayout.For(SystemDefinitionLoader.Parse("id: bare\nname: Bare"), new Dictionary<string, object>())));
    }

    [Fact]
    public void AWellFormedLayout_HasNoProblems()
    {
        var system = Kinds("""
            [{ section: Play, widgets: [
                 { widget: values, keys: [grit, str, stress_Max, actions.hunt] },
                 { widget: track, key: stress }, { widget: track, key: "slots_*" },
                 { widget: list, key: gear, columns: [used, name] },
                 { widget: table, key: actions, groupBy: group, columns: [rating] },
                 { widget: roll, roll: action }, { widget: text, key: notes }, { widget: modifiers, target: grit }] }]
            """);
        Assert.Empty(SheetLayout.Problems(system));
    }

    [Fact]
    public void TheLoaderNames_EachThingAMalformedLayoutGetsWrong()
    {
        var system = Kinds(
            "[{ section: Play, widgets: [{ widget: gauge }, { widget: values, keys: [nerve] }, { widget: track, key: stres }, "
            + "{ widget: list, key: gear, columns: [weight] }, { widget: table, key: skills }, { widget: roll, roll: fortune }, "
            + "{ widget: armor-class }, { widget: modifiers }] }, { section: Play, widgets: [] }]",
            topLevel: "layout: [{ section: Old, widgets: [{ widget: fields }] }]");

        var problems = SheetLayout.Problems(system).ToList();

        Assert.Equal(new[]
        {
            "layout: the system has sheet kinds, so each kind's own layout is read and this one is not.",
            "sheetKinds.hero.layout.Play: there is no widget called gauge.",
            "sheetKinds.hero.layout.Play: values: nerve is not a value of hero.",
            "sheetKinds.hero.layout.Play: track: stres is not a track of hero.",
            "sheetKinds.hero.layout.Play: list: gear has no field weight.",
            "sheetKinds.hero.layout.Play: table: skills is not a list of hero.",
            "sheetKinds.hero.layout.Play: roll: there is no roll called fortune.",
            "sheetKinds.hero.layout.Play: armor-class is a built-in widget over sheetRules.armorClass, which a sheet kind does not have; it shows nothing.",
            "sheetKinds.hero.layout.Play: modifiers needs a target.",
            "sheetKinds.hero.layout.Play: there are two sections of that name.",
            "sheetKinds.hero.layout.Play: the section has no widgets."
        }, problems);
    }
}
