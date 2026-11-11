using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>What a feature's data grants reaches the sheet (sheetRules.features), on the real SRD system with a homebrew
/// subclass beside its pack: numbers, entries on lists, counters of uses; and it leaves again with the feature.</summary>
public class FeatureGrantsTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public FeatureGrantsTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Row(string name, int level, params (string Key, object Value)[] grants)
    {
        var row = new Dictionary<string, object> { ["name"] = name, ["level"] = (long)level, ["description"] = name + "." };
        foreach (var (key, value) in grants) row[key] = value;
        return row;
    }

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static ContentEntry Shadowmarked()
    {
        var entry = new ContentEntry { Type = "subclass", Id = "shadowmarked", Name = "Shadowmarked", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "sorcerer";
        entry.Properties["features"] = Items(
            Row("Shadowed Blood", 3,
                ("modifiers", Items(
                    Map(("target", "darkvision"), ("amount", 120L), ("type", "base")),
                    Map(("target", "HP_Max"), ("amount", "classLevel")),
                    Map(("target", "initiative"), ("amount", 2L)))),
                ("adds", Items(Map(("to", "resistances"), ("entries", Items("necrotic")))))),
            Row("Shadowmarked Spells", 3,
                ("adds", Items(
                    Map(("to", "grantedSpells"), ("entries", Items("darkness", "Disguise Self")), ("level", 3L)),
                    Map(("to", "grantedSpells"), ("entries", Items("blindness-deafness", "fear")), ("level", 5L)),
                    Map(("to", "grantedSpells"), ("entries", Items("greater-invisibility")), ("level", 7L))))),
            Row("Dusk Form", 3, ("uses", Map(("max", "max(1, mod(cha))"), ("recoversOn", Items("Long Rest"))))),
            Row("Shadow Skin", 3, ("modifiers", Items(Map(("target", "armorClass"), ("amount", 1L))))),
            Row("Umbral Heart", 14, ("uses", Map(("max", 1L), ("recoversOn", Items("Long Rest"))))));
        return entry;
    }

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Sorcerer(int level)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, Shadowmarked()));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "sorcerer";
        sheet["subclass"] = "shadowmarked";
        sheet["species"] = "human";
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        sheet[SheetRuleEngine.BasePrefix + "cha"] = 16L;
        sheet[SheetRuleEngine.BasePrefix + "dex"] = 12L;
        return (engine, sheet);
    }

    [Fact]
    public void AFeaturesGrants_ReachTheSheet_AtTheLevelsTheyName()
    {
        var (engine, sheet) = Sorcerer(5);
        var plain = new Dictionary<string, object>(sheet) { ["subclass"] = "draconic-sorcery" };
        var without = engine.Recompute(plain);

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(120L, sheet["darkvision"]); // a floor, not +120
        Assert.Equal(without.HitPoints!.Max - DraconicBonus(without) + 5, evaluation.HitPoints!.Max); // + the sorcerer's level
        Assert.Equal((long)evaluation.HitPoints.Max, sheet["HP_Max"]);
        Assert.Equal(3L, evaluation.Derived["initiative"]); // Dexterity +1, the feature +2
        Assert.Equal(new[] { "darkness", "disguise-self", "blindness-deafness", "fear" }, SheetRuleEngine.RawList(sheet, "grantedSpells")); // a name finds its entry; level 7's waits
        Assert.Equal(new[] { "necrotic" }, SheetRuleEngine.RawList(sheet, "resistances"));
        Assert.Equal(11 + 1, evaluation.ArmorClass!.Total); // unarmored 10 + Dexterity, and the feature's +1
        Assert.Contains(evaluation.ArmorClass.Parts, p => p.Label == "Shadow Skin" && p.Value == 1);

        var counter = Assert.Single(evaluation.Counters); // Umbral Heart is level 14
        Assert.Equal("Dusk Form", counter.Feature.Name);
        Assert.Equal(3, counter.Max); // Charisma 16
        Assert.Equal(3L, sheet[counter.Key]);
        Assert.Equal(new[] { "Long Rest" }, SheetRuleEngine.UsesRests(sheet, counter.Key));
        Assert.Empty(evaluation.Warnings.Where(w => w.Key == "Actor.Rule.FeatureError"));
    }

    // The published subclass adds hit points too; whatever it adds is not this test's business.
    private static int DraconicBonus(SheetEvaluation evaluation) => evaluation.HitPoints!.Granted?.Where(g => g.Counted).Sum(g => g.Amount) ?? 0;

    [Fact]
    public void RunTwice_NothingIsAddedTwice_AndAPlayersOwnNumberAndSpentUsesAreKept()
    {
        var (engine, sheet) = Sorcerer(5);
        engine.Recompute(sheet);
        var key = engine.Evaluate(sheet).Counters.Single().Key;
        sheet[key] = 1L; // two uses spent
        var hitPoints = sheet["HP_Max"];

        engine.Recompute(sheet);
        engine.Recompute(sheet);

        Assert.Equal(120L, sheet["darkvision"]);
        Assert.Equal(hitPoints, sheet["HP_Max"]);
        Assert.Equal(1L, sheet[key]);
        Assert.Equal(4, SheetRuleEngine.RawList(sheet, "grantedSpells").Count);

        // The player types a speed of 40 while a feature adds nothing to speed: it stays theirs.
        sheet["speed"] = 40L;
        engine.Recompute(sheet);
        Assert.Equal(40L, sheet["speed"]);
    }

    [Fact]
    public void WhenTheFeatureGoes_WhatItGrantedGoesToo_AndWhatThePlayerPickedStays()
    {
        var (engine, sheet) = Sorcerer(5);
        sheet["grantedSpells"] = new List<object> { "fear", "light" }; // the player had Fear from elsewhere already, and Light
        engine.Recompute(sheet);
        Assert.Equal(new[] { "fear", "light", "darkness", "disguise-self", "blindness-deafness" }, SheetRuleEngine.RawList(sheet, "grantedSpells"));

        sheet["level"] = 4L; // below the level-5 spells
        engine.Recompute(sheet);
        Assert.Equal(new[] { "fear", "light", "darkness", "disguise-self" }, SheetRuleEngine.RawList(sheet, "grantedSpells"));

        sheet.Remove("subclass");
        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "fear", "light" }, SheetRuleEngine.RawList(sheet, "grantedSpells"));
        Assert.Empty(SheetRuleEngine.RawList(sheet, "resistances"));
        Assert.Equal(0L, Convert.ToInt64(sheet["darkvision"]));
        Assert.Empty(evaluation.Counters);
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith(SheetRuleEngine.UsesPrefix) || k.StartsWith(SheetRuleEngine.GrantedPrefix));
    }

    [Fact]
    public void AGrantThatCannotBeWorkedOut_IsAWarning_NotABrokenSheet()
    {
        var broken = Shadowmarked();
        broken.Properties["features"] = Items(Row("Odd One", 3, ("modifiers", Items(
            Map(("target", "noSuchNumber"), ("amount", 1L)),
            Map(("target", "initiative"), ("amount", "level +")))))) ;
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, broken));
        var (_, sheet) = Sorcerer(5);

        var evaluation = engine.Recompute(sheet);

        Assert.Single(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
        Assert.Equal(1L, evaluation.Derived["initiative"]);
        Assert.False(evaluation.HasViolations);
    }

    [Fact]
    public void TheWritersGuide_NamesTheSystemsOwnTargetsListsAndRests_OnlyWhereFeaturesAreRead()
    {
        var system = _loader.GetDefinition("srd5e")!;

        var guide = FeatureGrantGuide.For(system, "subclass")!;

        Assert.Contains("Each row of \"features\"", guide);
        Assert.Contains("darkvision", guide);
        Assert.Contains("HP_Max", guide);
        Assert.Contains("grantedSpells", guide);
        Assert.Contains("\"type\": \"base\"", guide);
        Assert.Contains("Long Rest", guide);
        Assert.DoesNotContain("cantrips (", guide); // a list with a count is the player's to fill
        Assert.Contains("The entry may carry", FeatureGrantGuide.For(system, "feat")!);
        Assert.Null(FeatureGrantGuide.For(system, "spell"));
        Assert.Null(FeatureGrantGuide.For(_loader.GetDefinition("simple-d6")!, "calling"));
    }

    [Fact]
    public void AModelsRow_KeepsWhatTheFeatureGrants()
    {
        var spec = new FieldSpec("features", "Features", "Collection", null, null, null);
        using var doc = System.Text.Json.JsonDocument.Parse("""
            [{"name": "Shadowed Blood", "level": 3, "description": "Dark.",
              "modifiers": [{"target": "darkvision", "amount": 120, "type": "base"}],
              "adds": [{"to": "grantedSpells", "entries": ["darkness"], "level": 3}],
              "uses": {"max": "mod(cha)", "recoversOn": ["Long Rest"]}}]
            """);

        var rows = (List<object>)FieldCoercion.Coerce(doc.RootElement, spec)!;

        var row = (Dictionary<string, object>)rows.Single();
        var modifier = (Dictionary<string, object>)((List<object>)row["modifiers"]).Single();
        Assert.Equal(120L, modifier["amount"]);
        Assert.Equal(new object[] { "darkness" }, (List<object>)((Dictionary<string, object>)((List<object>)row["adds"]).Single())["entries"]);
        Assert.Equal("mod(cha)", ((Dictionary<string, object>)row["uses"])["max"]);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
