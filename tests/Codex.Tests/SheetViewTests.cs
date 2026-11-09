using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>What the layout's generic widgets read (<see cref="SheetView"/>) and write (<see cref="SheetEdits"/>), overhaul step 6.
/// The page only renders these, so a widget showing the wrong number is a failure here.</summary>
public class SheetViewTests
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: view-test
        name: View test
        attributes: [{ key: str, label: Strength, default: 10 }]
        modifierTypes: { default: sum, types: { armor: highest } }
        sheetKinds:
          - kind: hero
            label: Hero
            values:
              - { key: grit, label: Grit, formula: "count(actions, a => a.rating >= 1)" }
              - { key: style, input: true, options: [bold, sly], default: bold }
              - { key: guard, formula: "10", modifiers: guard }
              - { key: turf, input: true, default: 2 }
            modifiers:
              - { target: guard, amount: 2, type: armor, source: Shield }
              - { target: guard, amount: 1, type: armor, source: Buckler }
              - { target: guard, amount: 1, source: Luck }
            tracks:
              - { key: stress, label: Stress, max: 6, display: boxes }
              - { key: HP, max: 10, start: max }
              - { key: rep, max: "12 - turf", display: boxes }
              - { key: notes, display: slots, holds: { type: text }, max: 2 }
              - key: harm
                display: slots
                holds: { type: text }
                levels: [{ level: 1, slots: 2, condition: Hurt }, { level: 2, slots: 1 }]
            lists:
              - { key: gear, label: Gear, type: item, itemFields: [{ key: used, type: Boolean }] }
            keyedLists:
              - key: actions
                keys: [{ key: hunt, label: Hunt, group: insight }, { key: sway, label: Sway, group: resolve }]
                itemFields: [{ key: rating, type: Number, default: 0 }]
                derive: { value: "a => a.rating * 2" }
        """);

    private sealed class Items : IEntryResolver
    {
        private static readonly ContentEntry Rope = new() { Id = "rope", Name = "Rope", Type = "item" };
        public ContentEntry? Resolve(string type, string id) => id == "rope" ? Rope : null;
        public IEnumerable<ContentEntry> Candidates(string type) => new[] { Rope };
    }

    private static SheetView View(Dictionary<string, object> sheet) => new(System, new Items(), sheet);

    [Fact]
    public void Values_ReadWorkedOutValues_InputsWithTheirDefault_AndAttributes()
    {
        var sheet = new Dictionary<string, object> { ["actions"] = new Dictionary<string, object> { ["hunt"] = new Dictionary<string, object> { ["rating"] = 1L } } };
        var view = View(sheet);
        Assert.Empty(view.Result.Warnings);
        var values = view.Values(new LayoutWidget { Widget = "values", Keys = { "grit", "style", "str", "stress_Max", "actions.hunt" } });

        Assert.Equal(new object?[] { 1d, "bold", 10d, 6d, 2d }, values.Select(v => v.Value is long l ? (double)l : v.Value));
        Assert.Equal(new[] { false, true, true, false, false }, values.Select(v => v.Input));
        Assert.Equal(new[] { "Grit", "style", "Strength", "Stress", "actions.hunt" }, values.Select(v => v.Label));
        Assert.Equal(new[] { "bold", "sly" }, values[1].Options);
    }

    [Fact]
    public void Tracks_StartWhereTheyDeclare_AndSlotsReadWhatTheyHold_ByLevelWhenTheyHaveLevels()
    {
        var sheet = new Dictionary<string, object>
        {
            ["stress"] = 3L,
            ["notes"] = new List<object> { "owes Lyssa" },
            ["harm"] = new Dictionary<string, object> { ["1"] = new List<object> { "Bruised" } }
        };
        var view = View(sheet);

        var stress = Assert.Single(view.Tracks(new LayoutWidget { Widget = "track", Key = "stress" }));
        Assert.Equal((3d, 6d, true), (stress.Current, stress.Track.Max!.Value, stress.IsBoxes));
        Assert.Equal(10, Assert.Single(view.Tracks(new LayoutWidget { Widget = "track", Key = "HP" })).Current);
        // An entered value's YAML default is text ("2"); a formula reading it still gets a number.
        Assert.Equal(10, Assert.Single(view.Tracks(new LayoutWidget { Widget = "track", Key = "rep" })).Track.Max);
        Assert.Empty(view.Result.Warnings);
        Assert.Equal(new[] { "owes Lyssa" }, Assert.Single(view.Tracks(new LayoutWidget { Widget = "track", Key = "notes" })).Held);
        var harm = Assert.Single(view.Tracks(new LayoutWidget { Widget = "track", Key = "harm" }));
        Assert.Equal(new[] { "Bruised" }, harm.HeldByLevel[1]);
        Assert.Equal(new[] { 1, 2 }, harm.Track.Levels.Select(l => l.Level));
    }

    [Fact]
    public void Rows_OfAKeyedList_CarryTheirGroupAndDerivedColumns_AndRowsOfPicks_TheirPlace()
    {
        var sheet = new Dictionary<string, object>
        {
            ["actions"] = new Dictionary<string, object> { ["sway"] = new Dictionary<string, object> { ["rating"] = 3L } },
            ["gear"] = new List<object> { "rope", new Dictionary<string, object> { ["id"] = "rope", ["used"] = true } }
        };
        var view = View(sheet);

        var actions = view.Rows(new LayoutWidget { Widget = "table", Key = "actions", GroupBy = "group", Columns = { "rating", "value" } });
        Assert.Equal(new[] { ("hunt", "Hunt", "insight"), ("sway", "Sway", "resolve") }, actions.Select(r => (r.Id, r.Label, r.Group!)));
        Assert.Equal(new object?[] { 3d, 6d }, actions[1].Cells.Select(c => c.Value));
        Assert.NotNull(actions[1].Cells[0].Field);
        Assert.Null(actions[1].Cells[1].Field);

        var gear = view.Rows(new LayoutWidget { Widget = "list", Key = "gear", Columns = { "used" } });
        Assert.Equal(new[] { ("Rope", 0, false), ("Rope", 1, true) }, gear.Select(r => (r.Label, r.Index!.Value, r.Cells[0].Value is true)));
    }

    [Fact]
    public void Modifiers_ShowEveryOneThatReached_AndWhichCounted()
    {
        var lines = View(new Dictionary<string, object>()).Modifiers("guard");

        Assert.Equal(new[] { ("Shield", true), ("Buckler", false), ("Luck", true) }, lines.Select(l => (l.Source!, l.Counted)));
    }

    [Fact]
    public void Edits_WriteNewListsAndMaps_SoTheSheetAsLoadedStaysAsItWas()
    {
        var hunt = new Dictionary<string, object> { ["rating"] = 1L };
        var loaded = new Dictionary<string, object>
        {
            ["actions"] = new Dictionary<string, object> { ["hunt"] = hunt },
            ["gear"] = new List<object> { "rope" }
        };
        var sheet = new Dictionary<string, object>(loaded);
        var rating = System.SheetKinds[0].KeyedLists[0].ItemFields[0];
        var used = System.SheetKinds[0].Lists[0].ItemFields[0];

        SheetEdits.SetField(sheet, "actions", keyed: true, "hunt", null, rating, "3");
        SheetEdits.SetField(sheet, "gear", keyed: false, "rope", 0, used, true);
        SheetEdits.AddPick(sheet, "gear", "rope");
        SheetEdits.SetValue(sheet, "style", "sly", isChoice: true);
        SheetEdits.SetValue(sheet, "str", "14", isChoice: false);

        Assert.Equal(1L, hunt["rating"]);
        Assert.Equal(new object[] { "rope" }, (List<object>)loaded["gear"]);
        Assert.Equal(3L, ((Dictionary<string, object>)((Dictionary<string, object>)sheet["actions"])["hunt"])["rating"]);
        var gear = (List<object>)sheet["gear"];
        Assert.Equal(2, gear.Count);
        Assert.Equal(true, ((Dictionary<string, object>)gear[0])["used"]);
        Assert.Equal(("sly", 14L), ((string)sheet["style"], (long)sheet["str"]));
        Assert.Equal(4, YamlValues.ChangedKeys(sheet, loaded));

        SheetEdits.RemovePick(sheet, "gear", 0);
        SheetEdits.RemovePick(sheet, "gear", 0);
        Assert.False(sheet.ContainsKey("gear"));
    }

    [Fact]
    public void AWidgetShows_OnlyWhenItHasSomethingToShow()
    {
        var view = View(new Dictionary<string, object>());

        Assert.True(view.Shows(new LayoutWidget { Widget = "track", Key = "stress" }));
        Assert.False(view.Shows(new LayoutWidget { Widget = "track", Key = "luck" }));
        Assert.False(view.Shows(new LayoutWidget { Widget = "roll", Roll = "none" }));
        Assert.False(view.Shows(new LayoutWidget { Widget = "hit-points" }));
        Assert.Equal("3", SheetView.Format(3d));
        Assert.Equal("2.5", SheetView.Format(2.5, global::System.Globalization.CultureInfo.InvariantCulture));
    }
}
