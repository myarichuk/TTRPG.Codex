using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The engine extensions the SRD grants import needs (docs/SRD_GRANTS_IMPORT_DESIGN.md, E1, E1b, E2, E3), on the real
/// SRD system with hand-made entries: species rows gated by level, the origin feat as a feature source, adopting ticks, counter labels.</summary>
public class SrdEngineExtensionTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public SrdEngineExtensionTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Character(int level, params ContentEntry[] extra)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, extra));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "fighter";
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        sheet[SheetRuleEngine.BasePrefix + "dex"] = 12L;
        return (engine, sheet);
    }

    [Fact]
    public void ASpeciesTraitRow_WithALevel_AppliesOnlyFromThatLevel()
    {
        var species = new ContentEntry { Type = "species", Id = "testfolk", Name = "Testfolk", SystemId = "srd5e", PackId = "homebrew" };
        species.Properties["traits"] = Items(
            Map(("name", "Early Gift"), ("description", "."), ("uses", Map(("max", 1L), ("key", "early-gift"), ("recoversOn", Items("Long Rest"))))),
            Map(("name", "Late Gift"), ("description", "."), ("level", 5L), ("uses", Map(("max", 1L), ("key", "late-gift"), ("recoversOn", Items("Long Rest"))))));

        var (engine, sheet) = Character(4, species);
        sheet["species"] = "testfolk";
        var four = engine.Recompute(sheet);
        Assert.Equal(new[] { "uses_early_gift" }, four.Counters.Select(c => c.Key).Where(k => k.Contains("gift")).ToArray());

        sheet["level"] = 5L;
        var five = engine.Recompute(sheet);
        Assert.Equal(2, five.Counters.Count(c => c.Key.Contains("gift")));
    }

    [Fact]
    public void ALineageRows_GrantWithAnAddLevel_AppliesFromThatLevel()
    {
        var species = new ContentEntry { Type = "species", Id = "testfolk", Name = "Testfolk", SystemId = "srd5e", PackId = "homebrew" };
        species.Properties["lineages"] = Items(
            Map(("name", "Ember"), ("description", "."), ("adds", Items(
                Map(("to", "grantedSpells"), ("entries", Items("faerie-fire")), ("level", 3L))))));

        var (engine, sheet) = Character(2, species);
        sheet["species"] = "testfolk";
        sheet["choice__testfolk__lineage"] = Items("Ember");
        engine.Recompute(sheet);
        Assert.DoesNotContain("faerie-fire", SheetRuleEngine.RawList(sheet, "grantedSpells"));

        sheet["level"] = 3L;
        engine.Recompute(sheet);
        Assert.Contains("faerie-fire", SheetRuleEngine.RawList(sheet, "grantedSpells"));
    }

    [Fact]
    public void TheOriginFeat_IsAFeatureSource_ItsModifiersApply()
    {
        var feat = new ContentEntry { Type = "feat", Id = "quick-wit", Name = "Quick Wit", SystemId = "srd5e", PackId = "homebrew" };
        feat.Properties["category"] = "origin";
        feat.Properties["modifiers"] = Items(Map(("target", "initiative"), ("amount", 3L)));

        var (engine, sheet) = Character(1, feat);
        var without = engine.Recompute(new Dictionary<string, object>(sheet));
        sheet["originFeat"] = "quick-wit";
        var with = engine.Recompute(sheet);

        Assert.Equal(Convert.ToInt64(without.Derived["initiative"]) + 3, Convert.ToInt64(with.Derived["initiative"]));
    }

    [Fact]
    public void ACountersLabel_NamesItOnTheSheetAndInTheAction_NotTheRowThatCarriesIt()
    {
        var species = new ContentEntry { Type = "species", Id = "testfolk", Name = "Testfolk", SystemId = "srd5e", PackId = "homebrew" };
        species.Properties["lineages"] = Items(
            Map(("name", "Ancestor: Red"), ("description", "."), ("uses", Map(("max", 2L), ("key", "breath"), ("label", "Breath Weapon"), ("recoversOn", Items("Long Rest"))))),
            Map(("name", "Ancestor: Blue"), ("description", "."), ("uses", Map(("max", 2L), ("key", "breath"), ("label", "Breath Weapon"), ("recoversOn", Items("Long Rest"))))));

        var (engine, sheet) = Character(1, species);
        sheet["species"] = "testfolk";
        sheet["choice__testfolk__lineage"] = Items("Ancestor: Red");
        var evaluation = engine.Recompute(sheet);

        var counter = Assert.Single(evaluation.Counters, c => c.Key == "uses_breath");
        Assert.Equal("Breath Weapon", counter.Label);
        Assert.Equal("Ancestor: Red", counter.Feature.Name);
        Assert.Equal("Breath Weapon", sheet["uses_breath_Label"]);
        Assert.Equal("Breath Weapon", ActionCost.OfCounter(counter, 1, null).Label);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
