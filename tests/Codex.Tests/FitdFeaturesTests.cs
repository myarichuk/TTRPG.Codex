using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Tests;

/// <summary>The Forged in the Dark system's own use of the sheet kinds' feature engine (docs/SHEETKINDS_FEATURES_DESIGN.md, section 4): the real
/// <c>system.yaml</c>, with abilities written here as the importer writes them, so this holds whatever the pack carries.</summary>
public class FitdFeaturesTests
{
    private sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static ContentEntry Entry(string type, string id, params (string Key, object Value)[] properties)
    {
        var entry = new ContentEntry { Type = type, Id = id, Name = id, SystemId = "fitd-srd", PackId = "test", Description = id };
        foreach (var (key, value) in properties) entry.Properties[key] = value;
        return entry;
    }

    private static readonly ContentEntry[] All =
    {
        Entry("special-ability", "mule", ("modifiers", Items(Map(("target", "LoadMax"), ("amount", 2L))))),
        Entry("special-ability", "battleborn", ("modifiers", Items(Map(("target", "specialArmorBoxes"), ("amount", 1L), ("type", "boxes"))))),
        Entry("special-ability", "shadow", ("modifiers", Items(Map(("target", "specialArmorBoxes"), ("amount", 1L), ("type", "boxes"))))),
        Entry("special-ability", "iron-will", ("modifiers", Items(Map(("target", "@resistance"), ("amount", 1L), ("when", "input.attribute == 'resolve'"))))),
        Entry("special-ability", "foresight", ("uses", Map(("key", "foresight"), ("max", 2L), ("recovers", Items(Map(("on", "loadChosen"))))))),
        Entry("special-ability", "analyst", ("uses", Map(("key", "analyst"), ("max", 2L), ("recovers", Items(Map(("on", "downtime"))))))),
        Entry("special-ability", "expertise", ("adds", Items(Map(("to", "actions"), ("choice", "expertise"), ("choose", 1L))))),
        Entry("crew-ability", "fiends", ("modifiers", Items(Map(("target", "turfCount"), ("amount", "wanted"))))),
    };

    private static readonly SystemDefinition Fitd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "fitd-srd", "system.yaml"));

    private static SheetRuleEngine Engine() => new(Fitd, new Entries(All));

    private static Dictionary<string, object> Scoundrel(params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "scoundrel" };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static Dictionary<string, object> Crew(params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "crew" };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    [Fact]
    public void TheSystem_LoadsAndValidates_WithItsLayoutsAndMoments()
    {
        Assert.Empty(SystemDefinitionLoader.Validate(Fitd));
        Assert.Empty(SheetLayout.Problems(Fitd));
        Assert.Equal(new[] { "downtime", "loadChosen" }, Fitd.Moments.Select(m => m.Key));
        Assert.Equal("load", Fitd.Moments.Single(m => m.Key == "loadChosen").OnSet);
        Assert.Equal("highest", Fitd.ModifierTypes.Types["boxes"]);
    }

    [Theory]
    [InlineData("light", 3d, 5d)]
    [InlineData("normal", 5d, 7d)]
    [InlineData("heavy", 6d, 8d)]
    public void LoadMax_IsTheSrdsLimit_AndMuleAddsTwo(string load, double plain, double withMule)
    {
        Assert.Equal(plain, new SheetModel(Fitd, new Entries(All), Scoundrel(("load", load))).Evaluate().Values["LoadMax"]);
        Assert.Equal(withMule, new SheetModel(Fitd, new Entries(All), Scoundrel(("load", load), ("abilities", Items("mule")))).Evaluate().Values["LoadMax"]);
    }

    [Fact]
    public void TheSpecialArmorBox_IsGivenByAnAbility_AndSeveralGiveOne()
    {
        double Boxes(params object[] picks)
        {
            var sheet = Scoundrel(("abilities", picks.ToList()));
            Engine().Recompute(sheet);
            return Convert.ToDouble(sheet["special_armor_Max"], global::System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(0d, Boxes());
        Assert.Equal(1d, Boxes("battleborn"));
        Assert.Equal(1d, Boxes("battleborn", "shadow"));
        var view = new SheetView(Fitd, new Entries(All), Scoundrel(("abilities", Items("battleborn", "shadow"))));
        Assert.Equal(new[] { "battleborn", "shadow" }, view.TrackSources("special_armor"));
        Assert.Empty(new SheetView(Fitd, new Entries(All), Scoundrel()).TrackSources("special_armor")!);
        Assert.Null(view.TrackSources("armor"));
    }

    [Fact]
    public void IronWill_AddsADieToResolveResistance_AndOnlyThat()
    {
        int Dice(string attribute, params object[] picks)
        {
            var actions = Map(("attune", Map(("rating", 1L))), ("command", Map(("rating", 1L))), ("hunt", Map(("rating", 1L))));
            var request = new SheetRollRequest { Definition = "resistance", Inputs = new Dictionary<string, object?> { ["attribute"] = attribute }, SuppliedBy = new Dictionary<string, string>() };
            var result = SheetRolls.Roll(Fitd, new Entries(All), Scoundrel(("actions", actions), ("abilities", picks.ToList())), request, new SequenceDiceSource(new[] { 3, 3, 3, 3, 3 }));
            return result.Dice.Count;
        }

        Assert.Equal(2, Dice("resolve"));
        Assert.Equal(3, Dice("resolve", "iron-will"));
        Assert.Equal(1, Dice("insight", "iron-will"));
    }

    [Fact]
    public void ForesightAndAnalyst_AreCounters_ThatRefillAtTheirOwnMoments()
    {
        var engine = Engine();
        var sheet = Scoundrel(("abilities", Items("foresight", "analyst")));
        engine.Recompute(sheet);

        Assert.Equal(2L, sheet["uses_foresight"]);
        Assert.Equal("loadChosen", sheet["uses_foresight_Rest"]);
        Assert.Equal("downtime", sheet["uses_analyst_Rest"]);
        Assert.Empty(SheetRuleEngine.UsesRests(Fitd, sheet, "uses_foresight"));
    }

    [Fact]
    public void Expertise_LeavesOneOfTheTwelveActionsToChoose()
    {
        var pending = Assert.Single(Engine().Recompute(Scoundrel(("abilities", Items("expertise")))).PendingChoices);

        Assert.Equal("actions", pending.Target);
        Assert.Equal(12, pending.Options.Count);
    }

    [Fact]
    public void LoadChosen_ClearsAllThreeArmorBoxes_AndDowntimeOnlyTheSpecialOne()
    {
        var entries = new Entries(All);
        var sheet = Scoundrel(("armor", 1L), ("heavy_armor", 1L), ("special_armor", 1L), ("stress", 3L));

        SheetEvents.Raise(Fitd, entries, sheet, new SheetEvent("downtime", new Dictionary<string, object?>()));
        Assert.Equal(1L, sheet["armor"]);
        Assert.Equal(0L, Convert.ToInt64(sheet["special_armor"]));

        sheet["special_armor"] = 1L;
        SheetEvents.Raise(Fitd, entries, sheet, new SheetEvent("loadChosen", new Dictionary<string, object?>()));
        Assert.Equal(0L, Convert.ToInt64(sheet["armor"]));
        Assert.Equal(0L, Convert.ToInt64(sheet["heavy_armor"]));
        Assert.Equal(0L, Convert.ToInt64(sheet["special_armor"]));
        Assert.Equal(3L, sheet["stress"]); // nothing else moves
    }

    [Fact]
    public void AMomentThatNamesTracksTheCrewLacks_WritesNothingOnACrewSheet()
    {
        var sheet = Crew(("heat", 2L));

        SheetEvents.Raise(Fitd, new Entries(All), sheet, new SheetEvent("loadChosen", new Dictionary<string, object?>()));

        Assert.DoesNotContain("armor", sheet.Keys);
        Assert.DoesNotContain("special_armor", sheet.Keys);
        Assert.False(SystemMoments.Matters(Fitd, sheet, "loadChosen"));
        Assert.True(SystemMoments.Matters(Fitd, Scoundrel(), "loadChosen"));
        Assert.True(SystemMoments.Matters(Fitd, Scoundrel(), "downtime"));
    }

    [Fact]
    public void TheReputationTrack_IsTwelveMinusTurf_NeverBelowSix_AndFiendsCountsTheWantedLevel()
    {
        long Rep(params (string Key, object Value)[] more)
        {
            var sheet = Crew(more);
            Engine().Recompute(sheet);
            return Convert.ToInt64(sheet["rep_Max"], global::System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(12, Rep());
        Assert.Equal(8, Rep(("turf", 4L)));
        Assert.Equal(6, Rep(("turf", 6L)));
        Assert.Equal(6, Rep(("turf", 9L)));
        Assert.Equal(12 - 3, Rep(("turf", 1L), ("wanted", 2L), ("abilities", Items("fiends"))) );
    }

    [Fact]
    public void WhatAMomentDoes_IsSaidInTheSheetsWords()
    {
        var sheet = Scoundrel(("abilities", Items("foresight", "analyst")));
        Engine().Recompute(sheet);

        var (tracks, counters) = SystemMoments.Effects(Fitd, sheet, "loadChosen");
        Assert.Equal(new[] { "Armor", "Heavy armor", "Special armor" }, tracks);
        Assert.Equal(new[] { "foresight" }, counters.Select(c => c.ToLowerInvariant()));
        Assert.Equal(new[] { "Special armor" }, SystemMoments.Effects(Fitd, sheet, "downtime").Tracks);

        var before = new Dictionary<string, object>(sheet) { ["armor"] = 1L };
        var after = new Dictionary<string, object>(sheet) { ["armor"] = 0L, ["uses_foresight"] = 1L };
        Assert.Equal(new[] { "Armor", "foresight" }, SystemMoments.WhatChanged(Fitd, before, after).Select(n => n == "Armor" ? n : n.ToLowerInvariant()));
    }
}
