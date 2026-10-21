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
        Assert.Equal(new[] { "cantrips", "spells", "grantedSpells", "feats", "fightingStyles", "extraOriginFeats" }, definition.SheetRules.Lists.Select(l => l.Key));
        Assert.Empty(Codex.Core.Systems.SystemDefinitionLoader.Validate(definition));
    }

    [Fact]
    public void Pack_CarriesTheValuesTheSheetRulesNeed()
    {
        // Fighting styles are their own column: a Fighter has one from level 1, a Paladin and Ranger from 2.
        long Styles(string cls, int level) => (long)Assert.IsType<List<object>>(_registry.GetEntry("srd5e", "class", cls)!.Properties["featSlots"])
            .Cast<Dictionary<string, object>>().Single(r => (long)r["level"] == level)["styles"];
        Assert.Equal(1, Styles("fighter", 1));
        Assert.Equal(0, Styles("paladin", 1));
        Assert.Equal(1, Styles("paladin", 2));
        Assert.Equal(1, Styles("ranger", 2));
        Assert.Equal(0, Styles("wizard", 20));

        var champion = _registry.GetEntry("srd5e", "subclass", "champion")!;
        var championRows = Assert.IsType<List<object>>(champion.Properties["featSlots"]).Cast<Dictionary<string, object>>().ToList();
        Assert.Equal(0L, championRows.Single(r => (long)r["level"] == 6)["styles"]);
        Assert.Equal(1L, championRows.Single(r => (long)r["level"] == 7)["styles"]);

        Assert.Equal("int", _registry.GetEntry("srd5e", "class", "wizard")!.Properties["spellcastingAbility"]);
        Assert.Equal("cha", _registry.GetEntry("srd5e", "class", "paladin")!.Properties["spellcastingAbility"]);
        Assert.False(_registry.GetEntry("srd5e", "class", "fighter")!.Properties.ContainsKey("spellcastingAbility"));

        Assert.Equal("medium", _registry.GetEntry("srd5e", "equipment", "hide-armor")!.Properties["armorType"]);
        Assert.Equal(120L, _registry.GetEntry("srd5e", "species", "dwarf")!.Properties["darkvision"]);
        Assert.Equal(1L, _registry.GetEntry("srd5e", "species", "dwarf")!.Properties["hitPointsPerLevel"]);
        Assert.Equal(1L, _registry.GetEntry("srd5e", "species", "human")!.Properties["extraOriginFeats"]);
        Assert.Equal(35L, _registry.GetEntry("srd5e", "species", "goliath")!.Properties["speed"]);
        Assert.All(_registry.GetEntries("srd5e", "species"), species => Assert.False(string.IsNullOrEmpty(species.Properties["size"]?.ToString()), species.Id));
    }

    [Fact]
    public void ClassFeatureRows_NameTheSubclassChoiceOnce_AndTheTextIsClean()
    {
        foreach (var cls in _registry.GetEntries("srd5e", "class"))
        {
            var rows = Assert.IsType<List<object>>(cls.Properties["features"]).Cast<Dictionary<string, object>>().ToList();
            var choices = rows.Where(r => r["name"].ToString() == $"{cls.Name} Subclass").ToList();
            Assert.Single(choices);
            Assert.Equal(3L, choices[0]["level"]);
            Assert.All(rows, r => Assert.DoesNotContain("â€", r["description"]?.ToString() ?? ""));
        }

        Assert.DoesNotContain("â€", _registry.GetEntry("srd5e", "class", "monk")!.Properties["skillChoices"].ToString());
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

    // ---- Sheet gaps strict mode exposed (audit R1, R3, R9, R14-R17, R21, R22) ----

    private Codex.Core.Rules.SheetRuleEngine Engine() => new(_loader.GetDefinition("srd5e")!, new RegistryResolver(_registry));

    private Dictionary<string, object> NewSheet(string cls, int level, params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object>();
        Engine().InitializeSheet(sheet);
        sheet["class"] = cls;
        sheet["level"] = (long)level;
        sheet["attrMethod"] = "manual";
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static string Problems(Codex.Core.Rules.SheetEvaluation evaluation) => string.Join(" | ", evaluation.Violations.Select(v => v.FieldKey + ": " + v.Message));

    private static List<object> Items(params string[] items) => items.Cast<object>().ToList();

    [Fact]
    public void ALevelOneFighter_CanSaveAFightingStyle()
    {
        var evaluation = Engine().Evaluate(NewSheet("fighter", 1, ("fightingStyles", Items("defense"))));
        Assert.True(!evaluation.HasViolations, Problems(evaluation));
        Assert.Equal(1, evaluation.ListLimits["fightingStyles"]);
        Assert.Equal(0, evaluation.ListLimits["feats"]);

        var two = Engine().Evaluate(NewSheet("fighter", 1, ("fightingStyles", Items("defense", "archery"))));
        Assert.Contains(two.Violations, v => v.FieldKey == "fightingStyles" && v.Message.Contains("only 1 allowed"));

        // A fighting style is not a general feat, and a general feat is not a fighting style.
        var swapped = Engine().Evaluate(NewSheet("fighter", 4, ("feats", Items("defense")), ("fightingStyles", Items("grappler"))));
        Assert.Contains(swapped.Violations, v => v.FieldKey == "feats");
        Assert.Contains(swapped.Violations, v => v.FieldKey == "fightingStyles");
    }

    [Fact]
    public void FightingStyles_ComeFromPaladinsAndRangersAtTwo_AndAChampionsSecondAtSeven()
    {
        Assert.Equal(0, Engine().Evaluate(NewSheet("paladin", 1)).ListLimits["fightingStyles"]);
        Assert.Equal(1, Engine().Evaluate(NewSheet("paladin", 2)).ListLimits["fightingStyles"]);
        Assert.Equal(1, Engine().Evaluate(NewSheet("ranger", 2)).ListLimits["fightingStyles"]);
        Assert.Equal(1, Engine().Evaluate(NewSheet("fighter", 7, ("subclass", "berserker"))).ListLimits["fightingStyles"]);
        Assert.Equal(1, Engine().Evaluate(NewSheet("fighter", 7)).ListLimits["fightingStyles"]);
        Assert.Equal(2, Engine().Evaluate(NewSheet("fighter", 7, ("subclass", "champion"))).ListLimits["fightingStyles"]);
        Assert.Equal(1, Engine().Evaluate(NewSheet("fighter", 6, ("subclass", "champion"))).ListLimits["fightingStyles"]);

        var evaluation = Engine().Evaluate(NewSheet("fighter", 7, ("subclass", "champion"), ("fightingStyles", Items("defense", "archery"))));
        Assert.True(!evaluation.HasViolations, Problems(evaluation));
    }

    [Fact]
    public void DefenseFromTheFightingStylesList_StillAddsToArmorClass()
    {
        var engine = Engine();
        var sheet = NewSheet("fighter", 1, ("fightingStyles", Items("defense")), ("inventory", Items("chain-mail|equipped:1")));
        sheet["acMode"] = "auto";
        var evaluation = engine.Evaluate(sheet);
        Assert.Contains(evaluation.ArmorClass!.Lines, l => l.StartsWith("Defense"));
    }

    [Fact]
    public void ABoonCannotCarryAnEarlierAsiPastItsOwnCap()
    {
        // Base 18 + Boon +1 (cap 30) + ASI +2 (cap 20) used to pass at 21 because the higher cap won.
        var overCap = NewSheet("fighter", 19, ("base_str", 18L), ("feats", Items("boon-of-combat-prowess|str:1", "ability-score-improvement|str:2")));
        var evaluation = Engine().Evaluate(overCap);
        Assert.Contains(evaluation.Violations, v => v.FieldKey == "str" && v.Message.Contains("exceed 20"));

        // The other order is legal: the ASI reaches 20, then the Boon's own cap allows 21.
        var inOrder = NewSheet("fighter", 19, ("base_str", 18L), ("feats", Items("ability-score-improvement|str:2", "boon-of-combat-prowess|str:1")));
        Assert.DoesNotContain(Engine().Evaluate(inOrder).Violations, v => v.FieldKey == "str");
    }

    [Fact]
    public void ASubclass_NeedsLevelThree_AndEachClassKeepsItsOwn()
    {
        var early = Engine().Evaluate(NewSheet("fighter", 2, ("subclass", "champion")));
        Assert.Contains(early.Violations, v => v.FieldKey == "subclass" && v.Message.Contains("level 3"));
        Assert.Empty(early.AllowedOptions["subclass"]);

        var ready = Engine().Evaluate(NewSheet("fighter", 3));
        Assert.Contains(ready.AllowedOptions["subclass"], s => s.Id == "champion");

        // Fighter 3 / Wizard 3: both subclasses recorded, neither inherited from the other.
        var multi = NewSheet("fighter", 6, ("subclass", "champion"), ("multiclass", Items("wizard:3:evoker")),
            ("base_int", 13L), ("base_str", 13L));
        var evaluation = Engine().Evaluate(multi);
        Assert.True(!evaluation.HasViolations, Problems(evaluation));
        Assert.Equal(new[] { "champion", "evoker" }, evaluation.Classes.Select(c => c.SubclassId));
        Assert.Equal(new[] { "Champion", "Evoker" }, evaluation.Classes.Select(c => c.Subclass?.Name));

        // A subclass belonging to another class, or chosen too early, is reported on the multiclass row.
        var wrong = Engine().Evaluate(NewSheet("fighter", 5, ("multiclass", Items("wizard:2:champion")), ("base_int", 13L), ("base_str", 13L)));
        Assert.Contains(wrong.Violations, v => v.Message.Contains("level 3") && v.Message.Contains("Wizard"));
        Assert.Contains(wrong.Violations, v => v.FieldKey == "multiclass" && v.Message.Contains("another class"));
    }

    [Fact]
    public void AnOtherClass_DoesNotInheritTheFirstClassSubclass_ForItsLimits()
    {
        // Fighter 3 (a Champion, no extra style yet) / Wizard 7: the wizard row must not read the Champion's table at wizard level 7.
        var evaluation = Engine().Evaluate(NewSheet("fighter", 10, ("subclass", "champion"), ("multiclass", Items("wizard:7")), ("base_int", 13L), ("base_str", 13L)));
        Assert.Equal(1, evaluation.ListLimits["fightingStyles"]);
    }

    [Fact]
    public void GrantedSpells_AreOutsideTheClassListAndThePreparedCount()
    {
        // A fighter has no spellcasting; Magic Initiate-style spells go in the granted list instead.
        var sheet = NewSheet("fighter", 1, ("grantedSpells", Items("fireball", "shield")));
        var evaluation = Engine().Evaluate(sheet);
        Assert.True(!evaluation.HasViolations, Problems(evaluation));
        Assert.Null(evaluation.ListLimits["grantedSpells"]);

        // The same spells in the prepared list are still checked against the class.
        var prepared = Engine().Evaluate(NewSheet("fighter", 1, ("spells", Items("fireball"))));
        Assert.Contains(prepared.Violations, v => v.FieldKey == "spells");
    }

    [Fact]
    public void AHuman_CanTakeASecondOriginFeat_AndNobodyElseCan()
    {
        var human = Engine().Evaluate(NewSheet("fighter", 1, ("species", "human"), ("extraOriginFeats", Items("alert"))));
        Assert.True(!human.HasViolations, Problems(human));
        Assert.Equal(1, human.ListLimits["extraOriginFeats"]);

        var dwarf = Engine().Evaluate(NewSheet("fighter", 1, ("species", "dwarf"), ("extraOriginFeats", Items("alert"))));
        Assert.Equal(0, dwarf.ListLimits["extraOriginFeats"]);
        Assert.Contains(dwarf.Violations, v => v.FieldKey == "extraOriginFeats");

        var general = Engine().Evaluate(NewSheet("fighter", 1, ("species", "human"), ("extraOriginFeats", Items("grappler"))));
        Assert.Contains(general.Violations, v => v.FieldKey == "extraOriginFeats" && v.Message.Contains("origin"));
    }

    [Fact]
    public void SkillsAndSaves_FollowTheClassBackgroundAndPicks()
    {
        // Wizard 5 (proficiency +3), Int 16, Wis 14; Sage grants Arcana and History.
        var sheet = NewSheet("wizard", 5, ("background", "sage"), ("base_int", 16L), ("base_wis", 14L),
            ("skillProficiencies", Items("perception")), ("skillExpertise", Items("arcana")));
        var engine = Engine();
        engine.ApplyAttributeTotals(sheet);
        var evaluation = engine.Evaluate(sheet);

        var intSave = evaluation.SavingThrows.Single(s => s.Key == "int");
        Assert.True(intSave.Proficient);
        Assert.Equal(3 + 3, intSave.Bonus);
        Assert.False(evaluation.SavingThrows.Single(s => s.Key == "str").Proficient);

        var arcana = evaluation.Skills.Single(s => s.Key == "arcana");
        Assert.True(arcana.Expert);
        Assert.Equal(3 + 3 * 2, arcana.Bonus);
        Assert.True(evaluation.Skills.Single(s => s.Key == "history").Proficient); // from the background
        var perception = evaluation.Skills.Single(s => s.Key == "perception");
        Assert.Equal(2 + 3, perception.Bonus);
        Assert.Equal(10 + 2 + 3, evaluation.Passives.Single(p => p.Key == "passivePerception").Value);
        Assert.Equal(10 + 2, evaluation.Passives.Single(p => p.Key == "passiveInsight").Value); // Wisdom +2, no proficiency

        var noProficiency = Engine().Evaluate(NewSheet("wizard", 5, ("skillExpertise", Items("stealth"))));
        Assert.Contains(noProficiency.Warnings, w => w.Message.Contains("expertise but no proficiency"));
    }

    [Fact]
    public void SpellSaveDc_AndAttack_UseEachClassesAbility()
    {
        var sheet = NewSheet("wizard", 5, ("base_int", 16L), ("multiclass", Items("cleric:2")), ("base_wis", 14L));
        var engine = Engine();
        engine.ApplyAttributeTotals(sheet);
        var evaluation = engine.Evaluate(sheet);

        var wizard = evaluation.Spellcasting.Single(s => s.ClassName == "Wizard");
        Assert.Equal("int", wizard.Attribute);
        Assert.Equal(8 + 3 + 3, wizard.SaveDc);
        Assert.Equal(3 + 3, wizard.AttackBonus);
        var cleric = evaluation.Spellcasting.Single(s => s.ClassName == "Cleric");
        Assert.Equal(8 + 3 + 2, cleric.SaveDc);

        Assert.Empty(Engine().Evaluate(NewSheet("fighter", 5)).Spellcasting);
    }

    [Fact]
    public void ChoosingASpecies_CopiesSpeedSizeAndDarkvisionOnce()
    {
        var engine = Engine();
        var sheet = NewSheet("fighter", 1, ("species", "goliath"));
        engine.ApplyInherited(sheet);
        Assert.Equal(35L, sheet["speed"]);
        Assert.Equal("Medium", sheet["size"]);

        sheet["speed"] = 40L; // a hand edit survives until the species changes
        engine.ApplyInherited(sheet);
        Assert.Equal(40L, sheet["speed"]);

        sheet["species"] = "dwarf";
        engine.ApplyInherited(sheet);
        Assert.Equal(30L, sheet["speed"]);
        Assert.Equal(120L, sheet["darkvision"]);
    }

    [Fact]
    public void DwarvenToughness_AddsOneHitPointPerLevel()
    {
        var dwarf = NewSheet("fighter", 3, ("species", "dwarf"), ("base_con", 10L));
        var human = NewSheet("fighter", 3, ("species", "human"), ("base_con", 10L));
        var engine = Engine();
        var withToughness = engine.Evaluate(dwarf).HitPoints!;
        var without = engine.Evaluate(human).HitPoints!;
        Assert.Equal(without.Max + 3, withToughness.Max);
        Assert.Equal("Dwarf", withToughness.ExtraSource);
    }

    [Fact]
    public void AWizardInPlate_GetsWarnings_NotViolations()
    {
        var sheet = NewSheet("wizard", 1, ("base_str", 10L), ("inventory", Items("plate-armor|equipped:1", "longsword|equipped:1", "dagger|equipped:1")));
        var engine = Engine();
        engine.ApplyAttributeTotals(sheet);
        var evaluation = engine.Evaluate(sheet);

        Assert.True(!evaluation.HasViolations, Problems(evaluation));
        Assert.Contains(evaluation.Warnings, w => w.Message.Contains("Plate Armor") && w.Message.Contains("isn't trained with heavy armor"));
        Assert.Contains(evaluation.Warnings, w => w.Message.Contains("Plate Armor") && w.Message.Contains("Strength 15"));
        Assert.Contains(evaluation.Warnings, w => w.Message.Contains("Plate Armor") && w.Message.Contains("Stealth"));
        Assert.Contains(evaluation.Warnings, w => w.Message.Contains("Longsword") && w.Message.Contains("isn't trained"));
        Assert.DoesNotContain(evaluation.Warnings, w => w.Message.Contains("Dagger"));

        var fighter = NewSheet("fighter", 1, ("base_str", 15L), ("inventory", Items("plate-armor|equipped:1", "longsword|equipped:1", "shield|equipped:1")));
        engine.ApplyAttributeTotals(fighter);
        Assert.DoesNotContain(engine.Evaluate(fighter).Warnings, w => w.Message.Contains("trained") || w.Message.Contains("Strength"));
    }

    [Fact]
    public void HideArmor_IsMediumArmor_SoItTakesDexterityToTwo()
    {
        var sheet = NewSheet("barbarian", 1, ("base_dex", 16L), ("inventory", Items("hide-armor|equipped:1")));
        sheet["acMode"] = "auto";
        var engine = Engine();
        engine.ApplyAttributeTotals(sheet);
        Assert.Equal(12 + 2, engine.Evaluate(sheet).ArmorClass!.Total);
        Assert.DoesNotContain(engine.Evaluate(sheet).Warnings, w => w.Message.Contains("Hide Armor")); // barbarians wear medium armor
    }

    [Fact]
    public void MulticlassHalfCasters_AreRoundedOnTheSum()
    {
        // Paladin 3 / Ranger 3: half of 6 is 3 caster levels (4/2), not 2 + 2 = 4 (4/3).
        var sheet = NewSheet("paladin", 6, ("multiclass", Items("ranger:3")), ("base_str", 13L), ("base_dex", 13L), ("base_wis", 13L));
        var evaluation = Engine().Evaluate(sheet);
        var slots = Assert.Single(evaluation.SlotPools);
        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 2 }, slots.Max);
    }
}
