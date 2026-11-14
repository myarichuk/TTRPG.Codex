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
        Assert.Equal(new[] { "Long Rest" }, SheetRuleEngine.UsesRests(null, sheet, counter.Key));
        Assert.Empty(evaluation.Warnings.Where(w => w.Key == "Actor.Rule.FeatureError"));
    }


    [Fact]
    public void AFeaturesCounter_IsAPoolAnActionCostSpends_ReadAsTheTableCardsUseButtonReadsIt()
    {
        var (engine, sheet) = Sorcerer(5);
        var counter = engine.Recompute(sheet).Counters.Single();

        // The table's spend finds the counter's maximum under the key the counter names.
        Assert.Equal(counter.MaxKey, SystemDefinition.PoolMaxKey(_loader.GetDefinition("srd5e"), counter.Key));

        var cost = ActionCost.OfCounter(counter, 2, null);
        Assert.Equal(counter.Feature.Name, cost.Label);
        int? Read(string key) => YamlValues.GetInt(sheet, key);
        Assert.Equal(3, cost.Left(Read));
        Assert.True(cost.CanPay(Read));
        sheet[counter.Key] = 1L;
        Assert.Equal(1, cost.Left(Read));
        Assert.False(cost.CanPay(Read)); // one use left, two needed

        // The card reads max = pool(MaxKey) ?? counter.Max and current = pool(Key) ?? max: so does the cost.
        foreach (var live in new[] { new Dictionary<string, int>(), new() { [counter.Key] = 2 }, new() { [counter.Key] = 7, [counter.MaxKey] = 4 } })
        {
            int? Pool(string key) => live.TryGetValue(key, out var value) ? value : null;
            var max = Pool(counter.MaxKey) ?? counter.Max;
            Assert.Equal(Math.Clamp(Pool(counter.Key) ?? max, 0, max), cost.Left(Pool));
        }
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
    public void ANumberFieldGrant_ThatNamesADerivedValue_IsApplied_AndListedWithTheSameAmount()
    {
        var entry = Shadowmarked();
        entry.Properties["features"] = Items(Row("Fleet Mark", 3, ("modifiers", Items(Map(("target", "speed"), ("amount", "proficiencyBonus * 5"))))));
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, entry));
        var (_, sheet) = Sorcerer(5);
        sheet["speed"] = 30L;

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(3L, evaluation.Derived["proficiencyBonus"]);
        Assert.Equal(45L, sheet["speed"]); // 30 + 3 * 5
        var granted = Assert.Single(evaluation.Granted, g => g.Target == "speed" && g.Feature.Name == "Fleet Mark");
        Assert.Equal(15, granted.Amount);
        Assert.True(granted.Counted);
        Assert.Empty(evaluation.Warnings.Where(w => w.Key == "Actor.Rule.FeatureError"));

        engine.Recompute(sheet);
        Assert.Equal(45L, sheet["speed"]); // not added twice
    }

    [Fact]
    public void ANumberFieldGrant_WithABrokenFormula_IsNotApplied_NotListed_AndIsAWarning()
    {
        var entry = Shadowmarked();
        entry.Properties["features"] = Items(Row("Odd Mark", 3, ("modifiers", Items(Map(("target", "speed"), ("amount", "noSuchValue * 5"))))));
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, entry));
        var (_, sheet) = Sorcerer(5);
        sheet["speed"] = 30L;

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(30L, sheet["speed"]);
        Assert.DoesNotContain(evaluation.Granted, g => g.Target == "speed");
        Assert.Single(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
        Assert.False(evaluation.HasViolations);
    }

    [Fact]
    public void APlainNumberFieldGrant_StillAddsToTheField_AndIsListed()
    {
        var entry = Shadowmarked();
        entry.Properties["features"] = Items(Row("Swift Mark", 3, ("modifiers", Items(Map(("target", "speed"), ("amount", 10L))))));
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, entry));
        var (_, sheet) = Sorcerer(5);
        sheet["speed"] = 30L;

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(40L, sheet["speed"]);
        Assert.Equal(10, Assert.Single(evaluation.Granted, g => g.Target == "speed").Amount);
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

    // ---- Shared pools, spends and refill rules ----

    private static ContentEntry Subclass(params Dictionary<string, object>[] rows)
    {
        var entry = new ContentEntry { Type = "subclass", Id = "shadowmarked", Name = "Shadowmarked", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "sorcerer";
        entry.Properties["features"] = Items(rows.Cast<object>().ToArray());
        return entry;
    }

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) SorcererWith(ContentEntry entry, int level = 5, long charisma = 16)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, entry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "sorcerer";
        sheet["subclass"] = "shadowmarked";
        sheet["species"] = "human";
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        sheet[SheetRuleEngine.BasePrefix + "cha"] = charisma;
        sheet[SheetRuleEngine.BasePrefix + "dex"] = 12L;
        return (engine, sheet);
    }

    private static Dictionary<string, object> Focus(long max, params (string Key, object Value)[] more)
    {
        var uses = Map(("max", max), ("key", "focus"));
        foreach (var (key, value) in more) uses[key] = value;
        return Row("Focus", 3, ("uses", uses));
    }

    [Fact]
    public void TwoFeaturesNamingOnePool_ShareIt_TheHighestMaximumWins_AndTheSheetWarns()
    {
        foreach (var rows in new[]
                 {
                     new[] { Focus(3), Row("Deeper Focus", 3, ("uses", Map(("max", 5L), ("key", "focus")))) },
                     new[] { Row("Deeper Focus", 3, ("uses", Map(("max", 5L), ("key", "focus")))), Focus(3) }
                 })
        {
            var (engine, sheet) = SorcererWith(Subclass(rows));
            var evaluation = engine.Recompute(sheet);

            var counter = Assert.Single(evaluation.Counters);
            Assert.Equal("uses_focus", counter.Key);
            Assert.Equal(5, counter.Max); // the highest, whatever the order
            Assert.Equal("Deeper Focus", counter.Feature.Name);
            Assert.Equal(5L, sheet["uses_focus_Max"]);
            Assert.Contains(evaluation.Warnings, w => w.Key == "Actor.Rule.CounterKeyShared");
        }

        var (alone, aloneSheet) = SorcererWith(Subclass(Focus(3)));
        Assert.DoesNotContain(alone.Recompute(aloneSheet).Warnings, w => w.Key == "Actor.Rule.CounterKeyShared");
    }

    [Fact]
    public void AFeatureThatSpends_GetsAButtonCostingThePoolsPoints_DisabledWhenTooFewAreLeft_AndWarnsWithoutThePool()
    {
        var entry = Subclass(Focus(4),
            Row("Flurry", 3, ("spends", Map(("key", "focus"), ("amount", 2L)))),
            Row("Mending", 3, ("spends", Map(("key", "focus"), ("amount", "ask")))),
            Row("Ghost Step", 3, ("spends", Map(("key", "nowhere"), ("amount", 1L)))));
        var (engine, sheet) = SorcererWith(entry);
        var evaluation = engine.Recompute(sheet);
        int? Read(string key) => YamlValues.GetInt(sheet, key);

        Assert.Equal(new[] { "Flurry", "Mending" }, evaluation.Spenders.Select(s => s.Feature.Name)); // Ghost Step has no pool: no button
        var warning = Assert.Single(evaluation.Warnings, w => w.Key == "Actor.Rule.SpendNoPool");
        Assert.Contains("Ghost Step", warning.Message);

        var flurry = evaluation.Spenders[0];
        Assert.False(flurry.Ask);
        Assert.Equal("uses_focus", flurry.Cost.Key);
        Assert.Equal(2, flurry.Cost.Amount);
        Assert.Equal("Focus", flurry.Cost.Label); // the pool is named by the feature that owns it
        Assert.True(flurry.Cost.CanPay(Read));

        sheet["uses_focus"] = 1L;
        Assert.False(flurry.Cost.CanPay(Read)); // one point left, two needed
        var mending = evaluation.Spenders[1];
        Assert.True(mending.Ask);
        Assert.Equal(1, mending.Cost.Amount); // the least; the player picks up to what is left
        Assert.True(mending.Cost.CanPay(Read));
        sheet["uses_focus"] = 0L;
        Assert.False(mending.Cost.CanPay(Read));
    }

    [Fact]
    public void GivingACounterAKey_CarriesWhatWasSpentAcrossOnce()
    {
        var (before, sheet) = SorcererWith(Subclass(Row("Focus", 3, ("uses", Map(("max", 4L))))));
        var oldKey = before.Recompute(sheet).Counters.Single().Key;
        Assert.NotEqual("uses_focus", oldKey);
        sheet[oldKey] = 1L; // three spent

        var (after, _) = SorcererWith(Subclass(Focus(4)));
        var evaluation = after.Recompute(sheet);

        Assert.Equal("uses_focus", Assert.Single(evaluation.Counters).Key);
        Assert.Equal(1L, sheet["uses_focus"]); // still three spent
        Assert.Equal(4L, sheet["uses_focus_Max"]);
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith(oldKey, StringComparison.Ordinal)); // the old keys are gone
        after.Recompute(sheet);
        Assert.Equal(1L, sheet["uses_focus"]);
    }

    [Fact]
    public void RefillRules_AreWorkedOutOnTheSheet_AndStoredForTheTableToRead()
    {
        var entry = Subclass(Focus(4, ("regain", "gm"), ("recovers", Items(
            Map(("on", "Short Rest"), ("amount", 1L)),
            Map(("on", "Long Rest"), ("amount", "proficiencyBonus")),
            Map(("on", "dawn"), ("amount", "1d6+1")),
            Map(("on", "initiative"), ("amount", 1L), ("when", "empty")),
            Map(("on", "manual"))))));
        var (engine, sheet) = SorcererWith(entry);
        var counter = engine.Recompute(sheet).Counters.Single();

        Assert.Equal("Short Rest=1|Long Rest=3|dawn=1d6+1|initiative=1!empty|manual", (string)sheet[counter.Key + SheetRuleEngine.UsesRestSuffix]); // level 5: proficiency +3
        Assert.Equal(new[] { "Short Rest", "Long Rest" }, counter.RecoversOn); // moments are never rest buttons
        Assert.Equal(new[] { "Short Rest", "Long Rest" }, SheetRuleEngine.UsesRests(null, sheet, counter.Key));
        Assert.Equal("Focus", sheet[counter.Key + SheetRuleEngine.UsesLabelSuffix]);
        Assert.True(SheetRuleEngine.IsUsesKey(counter.Key));
        Assert.False(SheetRuleEngine.IsUsesKey(counter.Key + SheetRuleEngine.UsesLabelSuffix));

        // regain: gm
        Assert.False(counter.CanRegain(false));
        Assert.True(counter.CanRegain(true));
        var (plain, plainSheet) = SorcererWith(Subclass(Focus(4)));
        var open = plain.Recompute(plainSheet).Counters.Single();
        Assert.True(open.CanRegain(false));
    }

    [Fact]
    public void ARecoversOnShorthand_StaysAFullRefill_AndOldSheetsKeepReading()
    {
        var (engine, sheet) = SorcererWith(Subclass(Focus(4, ("recoversOn", Items("Long Rest")))));
        var counter = engine.Recompute(sheet).Counters.Single();
        Assert.Equal("Long Rest", sheet[counter.Key + SheetRuleEngine.UsesRestSuffix]);
        var rule = Assert.Single(FeatureRecovery.Decode(new Dictionary<string, object> { ["uses_x_Rest"] = "Long Rest" }, "uses_x"));
        Assert.Equal(new RecoverRule("Long Rest", "all", false), rule);
    }

    [Fact]
    public void Refills_AddWholeNumbersDiceAndEmptyOnly_NeverPastTheMaximum()
    {
        var sheet = new Dictionary<string, object> { ["uses_w_Rest"] = "Short Rest=1|Long Rest=3|Long Rest=1d6+1|initiative=1!empty|dawn" };
        var rolls = new List<string>();
        int Roll(string dice) { rolls.Add(dice); return 5; }

        var partial = FeatureRecovery.Apply(FeatureRecovery.For(sheet, "uses_w", "Short Rest"), 1, 4, Roll);
        Assert.Equal(2, partial.Value);
        Assert.Equal(1, partial.Regained);
        Assert.Empty(rolls);

        var rolled = FeatureRecovery.Apply(FeatureRecovery.For(sheet, "uses_w", "Long Rest"), 0, 6, Roll); // 3, then 1d6+1 rolled as 5: capped at 6
        Assert.Equal(6, rolled.Value);
        Assert.Equal(("1d6+1", 5), Assert.Single(rolled.Rolls));

        Assert.Equal(1, FeatureRecovery.Apply(FeatureRecovery.For(sheet, "uses_w", "initiative"), 0, 4, Roll).Value);
        Assert.Equal(2, FeatureRecovery.Apply(FeatureRecovery.For(sheet, "uses_w", "initiative"), 2, 4, Roll).Value); // not empty: nothing
        Assert.Equal(4, FeatureRecovery.Apply(FeatureRecovery.For(sheet, "uses_w", "dawn"), 0, 4, Roll).Value);
    }

    [Fact]
    public void WhenTheMaximumDrops_WhatIsLeftIsCapped_AndNoChangeToTheBuildRefillsIt()
    {
        var (engine, sheet) = Sorcerer(5);
        var counter = engine.Recompute(sheet).Counters.Single();
        Assert.Equal(3, counter.Max);

        sheet[counter.Key] = 2L;
        sheet[SheetRuleEngine.BasePrefix + "cha"] = 10L; // the maximum falls to 1
        Assert.Equal(1, engine.Recompute(sheet).Counters.Single().Max);
        Assert.Equal(1L, sheet[counter.Key]); // capped, not refilled

        var (other, spent) = Sorcerer(5);
        other.Recompute(spent);
        spent[counter.Key] = 0L; // all three spent
        spent[SheetRuleEngine.BasePrefix + "cha"] = 10L;
        other.Recompute(spent);
        Assert.Equal(0L, spent[counter.Key]); // spent stays spent: the lower maximum does not refill it
    }

    [Fact]
    public void TheGrantEditorsData_ReadsAndWritesKeyRecoversRegainAndSpends()
    {
        var row = Row("Focus", 3, ("uses", Map(("max", 4L), ("key", "focus"), ("regain", "gm"),
            ("recovers", Items(Map(("on", "Short Rest"), ("amount", 1L)), Map(("on", "initiative"), ("when", "empty")))))),
            ("spends", Map(("key", "focus"), ("amount", "ask"))));

        var data = FeatureGrantData.Read(row);
        Assert.Equal("focus", data.Uses!.Key);
        Assert.Equal("gm", data.Uses.Regain);
        Assert.Equal(2, data.Uses.Recovers.Count);
        Assert.Equal("ask", data.Spends!.Amount);

        var written = new Dictionary<string, object>();
        data.Apply(written);
        var uses = (Dictionary<string, object>)written["uses"];
        Assert.Equal("focus", uses["key"]);
        Assert.Equal("gm", uses["regain"]);
        var recovers = ((List<object>)uses["recovers"]).Cast<Dictionary<string, object>>().ToList();
        Assert.Equal(1L, recovers[0]["amount"]);
        Assert.False(recovers[1].ContainsKey("amount")); // all
        Assert.Equal("empty", recovers[1]["when"]);
        Assert.Equal("ask", ((Dictionary<string, object>)written["spends"])["amount"]);
        Assert.Equal(3, uses.Count(u => u.Key is "max" or "key" or "regain") ); // nothing written twice
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
