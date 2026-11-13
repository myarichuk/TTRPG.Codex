using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Free castings (docs/FREE_CASTINGS_DESIGN.md): <c>free</c> on an <c>adds</c> item to the granted spells makes the spell castable without a slot, once
/// per rest, a number of times, or at will, as one more way to pay for the spell's action.</summary>
public class SrdFreeCastingTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public SrdFreeCastingTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    /// <summary>A species whose one trait adds the spells given, each as an <c>adds</c> item the test writes.</summary>
    private static ContentEntry Species(params Dictionary<string, object>[] adds)
    {
        var species = new ContentEntry { Type = "species", Id = "testfolk", Name = "Testfolk", SystemId = "srd5e", PackId = "homebrew" };
        species.Properties["traits"] = Items(Map(("name", "Fey Step"), ("description", "."), ("adds", adds.Cast<object>().ToList())));
        return species;
    }

    private static Dictionary<string, object> Grant(string spell, object free) =>
        Map(("to", "grantedSpells"), ("entries", Items(spell)), ("free", free));

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Character(string cls, int level, ContentEntry? species = null, string speciesId = "testfolk")
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, species == null ? Array.Empty<ContentEntry>() : new[] { species }));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = cls;
        sheet["level"] = (long)level;
        sheet["species"] = speciesId;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        sheet[SheetRuleEngine.BasePrefix + "wis"] = 14L;
        sheet[SheetRuleEngine.BasePrefix + "int"] = 14L;
        return (engine, sheet);
    }

    private static SheetAction Cast(SheetEvaluation evaluation, string spell) => evaluation.Actions.Single(a => a.Entry.Id == spell);

    private static int? Value(Dictionary<string, object> sheet, string key) => YamlValues.GetInt(sheet, key);

    [Fact]
    public void EachSpellOfAFeature_GetsItsOwnCounter_KeyedFreeAndTheSpellId()
    {
        var species = Species(
            Map(("to", "grantedSpells"), ("entries", Items("misty-step", "detect-magic")), ("free", Map(("max", 1L), ("recoversOn", Items("Long Rest"))))));
        var (engine, sheet) = Character("fighter", 3, species);

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "uses_free_misty_step", "uses_free_detect_magic" }.Order(), evaluation.Counters.Select(c => c.Key).Where(k => k.Contains("free")).Order());
        var misty = evaluation.Counters.Single(c => c.Key == "uses_free_misty_step");
        Assert.Equal("Fey Step: Misty Step", misty.Label);
        Assert.Equal(1L, sheet["uses_free_misty_step"]);
        Assert.Equal(new[] { "Long Rest" }, SheetRuleEngine.UsesRests(_loader.GetDefinition("srd5e"), sheet, "uses_free_misty_step"));
        Assert.Contains("misty-step", SheetRuleEngine.RawList(sheet, "grantedSpells")); // the spell is still granted
    }

    [Fact]
    public void ACounterNamingAKey_IsOneCounterForAllItsSpells()
    {
        var species = Species(Map(("to", "grantedSpells"), ("entries", Items("misty-step", "detect-magic")), ("free", Map(("max", 2L), ("key", "fey-casts"), ("recoversOn", Items("Long Rest"))))));
        var (engine, sheet) = Character("fighter", 3, species);

        var evaluation = engine.Recompute(sheet);

        var shared = evaluation.Counters.Where(c => c.Key.Contains("fey")).ToList();
        Assert.Single(shared);
        Assert.Equal(2, evaluation.FreeCastings.Count(f => f.Counter == shared[0]));
    }

    [Fact]
    public void AGrantedSpellsAction_HasItsSlotsAndTheFreeCastingAfterThem_AndACharacterWithNoSlotsHasOnlyTheFreeOne()
    {
        var species = Species(Grant("longstrider", Map(("max", 1L), ("recoversOn", Items("Long Rest")))));

        var (engine, sheet) = Character("wizard", 3, species);
        var wizard = Cast(engine.Recompute(sheet), "longstrider");
        Assert.True(wizard.Costs.Count > 1);
        Assert.All(wizard.Costs.Take(wizard.Costs.Count - 1), c => Assert.NotNull(c.Slot));
        Assert.Equal("uses_free_longstrider", wizard.Costs[^1].Key);

        var (fighterEngine, fighterSheet) = Character("fighter", 1, species);
        var fighter = Cast(fighterEngine.Recompute(fighterSheet), "longstrider");
        var only = Assert.Single(fighter.Costs);
        Assert.Equal("uses_free_longstrider", only.Key);
        Assert.True(fighter.Spends);
    }

    [Fact]
    public void SpendingTheFreeCost_LowersTheCounterAndNoSlot_AndALongRestFillsItAgain()
    {
        var species = Species(Grant("longstrider", Map(("max", 1L), ("recoversOn", Items("Long Rest")))));
        var (engine, sheet) = Character("wizard", 3, species);
        var action = Cast(engine.Recompute(sheet), "longstrider");
        var free = action.Costs[^1];
        var slotsBefore = sheet.Where(p => p.Key.StartsWith("slots_") && !p.Key.EndsWith("_Max")).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal(1, free.Left(k => Value(sheet, k)));
        sheet[free.Key!] = (long)(free.Left(k => Value(sheet, k))! - free.Amount); // what the Spend does
        Assert.False(free.CanPay(k => Value(sheet, k)));
        Assert.True(action.Costs[0].CanPay(k => Value(sheet, k))); // the slots are untouched
        Assert.Equal(slotsBefore, sheet.Where(p => p.Key.StartsWith("slots_") && !p.Key.EndsWith("_Max")).ToDictionary(p => p.Key, p => p.Value));

        engine.Recompute(sheet);
        var back = FeatureRecovery.Apply(FeatureRecovery.For(sheet, free.Key!, "Long Rest"), 0, 1, _ => 0);
        Assert.Equal(1, back.Value);
    }

    [Fact]
    public void AtWill_IsAFreeCostThatNeverRunsOut_AndHasNoCounter()
    {
        var species = Species(Grant("detect-magic", "atWill"));
        var (engine, sheet) = Character("fighter", 1, species);

        var evaluation = engine.Recompute(sheet);

        var cost = Assert.Single(Cast(evaluation, "detect-magic").Costs);
        Assert.True(cost.Free);
        Assert.Equal(ActionCost.AtWillLabel, cost.Label);
        Assert.True(cost.CanPay(_ => 0));
        Assert.DoesNotContain(evaluation.Counters, c => c.Key.Contains("detect"));
        Assert.Contains(evaluation.FreeCastings, f => f.SpellId == "detect-magic" && f.Counter == null);
    }

    [Fact]
    public void AFreeCastingBelowItsLevel_IsNotThereYet()
    {
        var species = Species(Map(("to", "grantedSpells"), ("entries", Items("misty-step")), ("level", 5L), ("free", Map(("max", 1L), ("recoversOn", Items("Long Rest"))))));
        var (engine, sheet) = Character("fighter", 3, species);
        Assert.DoesNotContain(engine.Recompute(sheet).Counters, c => c.Key.Contains("free"));

        sheet["level"] = 5L;
        Assert.Contains(engine.Recompute(sheet).Counters, c => c.Key == "uses_free_misty_step");
    }

    [Fact]
    public void ASheetThatHoldsAFreeCounter_KeepsWhatWasSpent_WhenTheRowMovesToFree()
    {
        // Step 10 wrote uses_free_hunters_mark as a plain counter of the Ranger's Favored Enemy row; now it is the spell's free casting under the same key.
        var (engine, sheet) = Character("ranger", 1, speciesId: "human");
        sheet["uses_free_hunters_mark"] = 0L;
        sheet["uses_free_hunters_mark_Max"] = 2L;

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(0L, sheet["uses_free_hunters_mark"]);
        Assert.Equal(2, evaluation.Counters.Single(c => c.Key == "uses_free_hunters_mark").Max);
        Assert.Equal(0, ActionCost.OfCounter(evaluation.Counters.Single(c => c.Key == "uses_free_hunters_mark"), 1, null).Left(k => Value(sheet, k)));
    }

    [Fact]
    public void WhenTheFeatureGoes_ItsFreeCounterLeavesTheSheet()
    {
        var species = Species(Grant("misty-step", Map(("max", 1L), ("recoversOn", Items("Long Rest")))));
        var (engine, sheet) = Character("fighter", 3, species);
        engine.Recompute(sheet);
        Assert.True(sheet.ContainsKey("uses_free_misty_step"));

        sheet["species"] = "human";
        engine.Recompute(sheet);

        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith("uses_free_misty_step"));
    }

    [Fact]
    public void TheGrantCheck_RefusesFreeOutsideASpellList_AndAFreeThatIsNeitherAtWillNorACounter()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var entry = new ContentEntry { Type = "feat", Id = "odd", Name = "Odd", SystemId = "srd5e" };
        entry.Properties["adds"] = Items(
            Map(("to", "resistances"), ("entries", Items("fire")), ("free", Map(("max", 1L)))),
            Grant("misty-step", "sometimes"));

        var problems = FeatureGrantCheck.Problems(system, entry);

        Assert.Contains(problems, p => p.Contains("\"free\" goes on entries added to a list of Library entries"));
        Assert.Contains(problems, p => p.Contains("atWill, or a counter"));
        Assert.Empty(FeatureGrantCheck.Problems(system, SpeciesCarrier(Grant("misty-step", Map(("max", 1L), ("recoversOn", Items("Long Rest")))))));
    }

    private static ContentEntry SpeciesCarrier(Dictionary<string, object> add)
    {
        var feat = new ContentEntry { Type = "feat", Id = "fine", Name = "Fine", SystemId = "srd5e" };
        feat.Properties["adds"] = Items(add);
        return feat;
    }

    [Fact]
    public void FreeIsKeptByTheFeatureEditor_WhenAnEntryRoundTrips()
    {
        var feature = new Dictionary<string, object> { ["name"] = "Fey Step", ["adds"] = Items(Grant("misty-step", Map(("max", 1L), ("recoversOn", Items("Long Rest"))))) };

        var data = FeatureGrantData.Read(feature);
        var written = new Dictionary<string, object>();
        data.Apply(written);

        var add = ((List<object>)written["adds"]).Cast<Dictionary<string, object>>().Single();
        Assert.Equal("misty-step", ((List<object>)add["entries"]).Single());
        Assert.IsAssignableFrom<IDictionary<string, object>>(add["free"]);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
