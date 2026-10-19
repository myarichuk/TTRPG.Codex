using Codex.Core;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Loads the real <c>content/</c> folder shipped in the repository and checks the SRD 5.2.1 pack is
/// complete and carries its licence. This is the regression gate for the import tool: counts come
/// from the SRD 5.2.1 table of contents, not from the importer's own output.
/// </summary>
public class SrdPackSmokeTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
    private readonly PluginLoader _loader;

    public SrdPackSmokeTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void LoadsWithoutWarnings()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
        Assert.Contains("srd5e", _loader.LoadedSystemIds);
        Assert.Contains("simple-d6", _loader.LoadedSystemIds);
    }

    [Fact]
    public void SystemIsNamedAfterTheDocument_NotTheTrademark()
    {
        var system = _loader.Systems.Single(s => s.Id == "srd5e");
        Assert.Equal("Fifth Edition SRD (5.2.1)", system.Name);
        Assert.Equal("5E compatible", system.Compatibility);
        Assert.DoesNotContain("D&D", system.Name);
        Assert.DoesNotContain("Dungeons", system.Name);
    }

    [Theory]
    [InlineData("class", 12)]
    [InlineData("subclass", 12)]
    [InlineData("species", 9)]
    [InlineData("background", 4)]
    [InlineData("feat", 17)]
    [InlineData("spell", 339)]
    [InlineData("monster", 340)] // the document lists 341; the source data's Mule is the Octopus stat block, so it is omitted
    [InlineData("condition", 15)]
    public void SrdPack_HasTheDocumentsCounts(string type, int expected)
    {
        Assert.Equal(expected, _registry.GetEntries("srd5e", type, new HashSet<string> { "srd-5.2.1" }).Count());
    }

    [Fact]
    public void SrdPack_CarriesTheVerbatimAttribution()
    {
        var pack = _registry.GetPack("srd-5.2.1");
        Assert.NotNull(pack);
        Assert.Equal("CC-BY-4.0", pack!.License!.Name);
        Assert.True(pack.License.Modified);
        Assert.StartsWith("This work includes material from the System Reference Document 5.2.1", pack.License.Attribution);
        Assert.Contains("https://www.dndbeyond.com/srd", pack.License.Attribution);
        Assert.Contains("https://creativecommons.org/licenses/by/4.0/legalcode", pack.License.Attribution);
        Assert.Contains(pack.License.Notices, n => n.Contains("5e-bits") && n.Contains("MIT"));
    }

    [Fact]
    public void Fireball_IsStructuredAndReadable()
    {
        var fireball = _registry.GetEntry("srd5e", "spell", "fireball");
        Assert.NotNull(fireball);
        Assert.Equal(3L, fireball!.Properties["level"]);
        Assert.Equal("Evocation", fireball.Properties["school"]);
        Assert.Contains("8d6", fireball.Description);
        Assert.Contains("level-3", fireball.Tags);
    }

    [Fact]
    public void Monsters_DoubleAsActorBlueprints_WithHitPoints()
    {
        var goblin = _registry.GetActor("srd5e:goblin-warrior");
        Assert.NotNull(goblin);
        Assert.Equal(10, goblin!.Resources["HP"]);
        Assert.Equal(15L, goblin.Properties["dex"]);
        Assert.Equal(340, _registry.GetActorsBySystem("srd5e").Count(a => a.PackId == "srd-5.2.1"));
    }

    [Fact]
    public void Classes_HaveFeaturesByLevel_AndOneSubclassEach()
    {
        var fighter = _registry.GetEntry("srd5e", "class", "fighter")!;
        var features = Assert.IsType<List<object>>(fighter.Properties["features"]);
        Assert.Contains(features.Cast<Dictionary<string, object>>(), f => (string)f["name"] == "Second Wind" && (long)f["level"] == 1);
        Assert.Equal(new List<object> { "Champion" }, fighter.Properties["subclasses"]);

        var champion = _registry.GetEntry("srd5e", "subclass", "champion")!;
        Assert.Equal("fighter", champion.Properties["class"]);
    }

    [Fact]
    public void Classes_CarryTheSrdTables_ForTheSheetRules()
    {
        var bard = _registry.GetEntry("srd5e", "class", "bard")!;
        Assert.Equal("full", bard.Properties["casterType"]);
        var rows = Assert.IsType<List<object>>(bard.Properties["spellcasting"]).Cast<Dictionary<string, object>>().ToList();
        var level5 = rows.Single(r => (long)r["level"] == 5);
        Assert.Equal(3L, level5["cantrips"]);
        Assert.Equal(9L, level5["preparedSpells"]);
        Assert.Equal(3L, level5["maxSpellLevel"]);
        Assert.Equal("4/3/2", level5["slots"]);

        var fighter = _registry.GetEntry("srd5e", "class", "fighter")!;
        Assert.Equal("none", fighter.Properties["casterType"]);
        Assert.False(fighter.Properties.ContainsKey("spellcasting"));
        var feats = Assert.IsType<List<object>>(fighter.Properties["featSlots"]).Cast<Dictionary<string, object>>().ToList();
        Assert.Equal(2L, feats.Single(r => (long)r["level"] == 6)["slots"]);   // ASI at 4 and 6
        Assert.Equal(7L, feats.Single(r => (long)r["level"] == 20)["slots"]);  // 4, 6, 8, 12, 14, 16 and the level 19 boon

        var warlock = _registry.GetEntry("srd5e", "class", "warlock")!;
        Assert.Equal("pact", warlock.Properties["casterType"]);

        Assert.Equal(4L, _registry.GetEntry("srd5e", "feat", "grappler")!.Properties["minLevel"]);
        Assert.Equal(19L, _registry.GetEntry("srd5e", "feat", "boon-of-fate")!.Properties["minLevel"]);
        Assert.Equal(1L, _registry.GetEntry("srd5e", "feat", "alert")!.Properties["minLevel"]);
    }

    [Fact]
    public void SheetRules_AreDeclaredAndResolveAgainstThePack()
    {
        var definition = _loader.GetDefinition("srd5e")!;
        Assert.NotNull(definition.SheetRules);
        Assert.Equal("strict", definition.SheetRules!.Enforcement);
        Assert.Equal(27, definition.SheetRules.Attributes!.PointBuy!.Budget);
        Assert.Equal(new[] { "cantrips", "spells", "feats" }, definition.SheetRules.Lists.Select(l => l.Key));
    }

    [Fact]
    public void Search_FindsAcrossTypes_AndTheActorSchemaReferencesContentTypes()
    {
        var hits = _registry.Search("srd5e", "dwarf").ToList();
        Assert.Contains(hits, h => h.Type == "species" && h.Id == "dwarf");

        var definition = _loader.GetDefinition("srd5e")!;
        foreach (var reference in definition.ActorFields.Where(f => f.Type == FieldType.EntityReference))
        {
            Assert.NotEmpty(_registry.GetEntries("srd5e", reference.TargetEntityType!));
        }
    }

    [Fact]
    public void ExampleWorldPack_LoadsLoreLocationsAndNpcs()
    {
        Assert.NotNull(_registry.GetPack("example-world-greyharbour"));
        Assert.Equal("world", _registry.GetPack("example-world-greyharbour")!.Kind);
        Assert.Equal(2, _registry.GetEntries("srd5e", "lore").Count());
        Assert.NotNull(_registry.GetLocation("srd5e:the-salt-lantern"));
        Assert.NotNull(_registry.GetActor("srd5e:maud-ketter"));
    }
}

public class SrdSheetScenarioTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
    private readonly PluginLoader _loader;

    public SrdSheetScenarioTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class RegistryResolver(ContentRegistry registry) : Codex.Core.Rules.IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type);
    }

    [Fact]
    public void ALegalPointBuyFighterWithSageBonuses_HasNoViolations()
    {
        var engine = new Codex.Core.Rules.SheetRuleEngine(_loader.GetDefinition("srd5e")!, new RegistryResolver(_registry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "fighter";
        sheet["species"] = "dwarf";
        sheet["background"] = "sage";
        foreach (var (key, score) in new[] { ("str", 15), ("dex", 14), ("con", 13), ("int", 12), ("wis", 10), ("cha", 8) })
            sheet["base_" + key] = (long)score;
        sheet["bonus_int"] = 2L;
        sheet["bonus_wis"] = 1L;
        engine.ApplyAttributeTotals(sheet);
        var evaluation = engine.Evaluate(sheet);
        Assert.True(!evaluation.HasViolations, string.Join(" | ", evaluation.Violations.Select(v => v.FieldKey + ": " + v.Message)));
        Assert.Equal(14, (int)(long)sheet["int"]);
    }

    [Fact]
    public void AWizardClericWithAnAsi_RaisesTheScore_AndCombinesSpellSlots()
    {
        var engine = new Codex.Core.Rules.SheetRuleEngine(_loader.GetDefinition("srd5e")!, new RegistryResolver(_registry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "wizard";
        sheet["level"] = 6L;
        sheet["multiclass"] = new List<object> { "cleric:2" };
        foreach (var (key, score) in new[] { ("str", 8), ("dex", 14), ("con", 13), ("int", 15), ("wis", 13), ("cha", 8) })
            sheet["base_" + key] = (long)score;
        sheet["feats"] = new List<object> { "ability-score-improvement|int:1,con:1" };
        engine.ApplyAttributeTotals(sheet);

        var evaluation = engine.Evaluate(sheet);
        Assert.True(!evaluation.HasViolations, string.Join(" | ", evaluation.Violations.Select(v => v.FieldKey + ": " + v.Message)));
        Assert.Equal(16, (int)(long)sheet["int"]);
        Assert.Equal(new[] { ("wizard", 4), ("cleric", 2) }, evaluation.Classes.Select(c => (c.Id, c.Level)));

        // Wizard 4 + cleric 2 = caster level 6 on the multiclass spellcaster table: 4/3/3.
        var slots = Assert.Single(evaluation.SlotPools);
        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 3, [3] = 3 }, slots.Max);

        // Feat slots: wizard 4 grants one; cleric 2 none. A second ASI is over the limit.
        Assert.Equal(1, evaluation.ListLimits["feats"]);
        // Cleric cantrips and spells are allowed next to wizard ones.
        Assert.Contains(evaluation.ListOptions["spells"], s => s.Id == "cure-wounds");
    }

    [Fact]
    public void AWarlock_GetsPactSlots_AtTheirSlotLevel()
    {
        var engine = new Codex.Core.Rules.SheetRuleEngine(_loader.GetDefinition("srd5e")!, new RegistryResolver(_registry));
        var sheet = new Dictionary<string, object> { ["class"] = "warlock", ["level"] = 5L };
        var evaluation = engine.Evaluate(sheet);
        var pact = Assert.Single(evaluation.SlotPools);
        Assert.Equal("pact", pact.Rule.Key);
        Assert.Equal(new Dictionary<int, int> { [3] = 2 }, pact.Max);
    }
}
