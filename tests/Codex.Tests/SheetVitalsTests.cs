using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Scripting;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Hit points, armor class and inventory, against a small in-memory system (the rules are data).</summary>
public class SheetVitalsTests
{
    private const string Yaml = """
        id: demo
        name: Demo
        attributes:
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: half-minus-five }
          - { key: con, label: Constitution, abbreviation: CON, default: 10, modifier: half-minus-five }
        resources:
          - { key: HP, label: Hit Points, maxKey: HP_Max, default: 10, isPrimary: true }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: armorClass, label: Armor Class, type: Number, defaultValue: 10 }
          - { key: armorClassMisc, label: Misc, type: Number, defaultValue: 0 }
        entityTypes:
          - { type: class, label: Class }
          - { type: feat, label: Feat }
          - { type: equipment, label: Equipment }
        sheetRules:
          lists:
            - { key: feats, label: Feats, targetEntityType: feat }
          multiclass: { key: multiclass, classField: class, levelField: level, classLevelKey: classLevel }
          hitPoints: { attribute: con, dieProperty: hitDie, default: average }
          armorClass: { unarmored: "10 + mod(dex)", bonusLists: [feats] }
          inventory: { types: [equipment] }
        """;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse(Yaml);

    private sealed class Resolver : IEntryResolver
    {
        private readonly List<ContentEntry> _entries = new();

        public Resolver()
        {
            Add("class", "knight", "Knight", p => p["hitDie"] = "d10");
            Add("class", "mage", "Mage", p => p["hitDie"] = "d6");
            Add("class", "brawler", "Brawler", p => { p["hitDie"] = "d12"; p["unarmoredDefense"] = "10 + mod(dex) + mod(con)"; p["unarmoredDefenseShield"] = true; });
            Add("class", "mystic", "Mystic", p => { p["hitDie"] = "d8"; p["unarmoredDefense"] = "10 + mod(dex) + 3"; p["unarmoredDefenseShield"] = false; });
            Add("feat", "guard", "Guard", p => { p["armorClassBonus"] = 1L; p["armorClassRequiresArmor"] = true; });
            Add("equipment", "mail", "Mail", p => { p["armorType"] = "heavy"; p["armorBase"] = 16L; p["armorDexCap"] = 0L; });
            Add("equipment", "scale", "Scale", p => { p["armorType"] = "medium"; p["armorBase"] = 14L; p["armorDexCap"] = 2L; });
            Add("equipment", "leather", "Leather", p => { p["armorType"] = "light"; p["armorBase"] = 11L; });
            Add("equipment", "buckler", "Buckler", p => { p["armorType"] = "shield"; p["armorClassBonus"] = 2L; });
            Add("equipment", "rope", "Rope", _ => { });
        }

        private void Add(string type, string id, string name, Action<Dictionary<string, object>> fill)
        {
            var entry = new ContentEntry { Type = type, Id = id, Name = name, SystemId = "demo", PackId = "p" };
            fill(entry.Properties);
            _entries.Add(entry);
        }

        public ContentEntry? Resolve(string type, string id) => _entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => _entries.Where(e => e.Type == type);
    }

    private static SheetRuleEngine Engine() => new(System, new Resolver());

    private static Dictionary<string, object> Sheet(string cls, int level, int dex = 10, int con = 10, params string[] inventory)
    {
        var sheet = new Dictionary<string, object> { ["class"] = cls, ["level"] = (long)level, ["dex"] = (long)dex, ["con"] = (long)con };
        Engine().InitializeSheet(sheet);
        if (inventory.Length > 0) sheet["inventory"] = inventory.Cast<object>().ToList();
        return sheet;
    }

    private static SheetEvaluation Run(Dictionary<string, object> sheet)
    {
        var engine = Engine();
        var evaluation = engine.Evaluate(sheet);
        engine.ApplyHitPoints(sheet, evaluation);
        engine.ApplyArmorClass(sheet, evaluation);
        return evaluation;
    }

    private static int Int(Dictionary<string, object> sheet, string key) => (int)(long)sheet[key];

    [Fact]
    public void Validation_AcceptsTheDemoRules() => Assert.Empty(SystemDefinitionLoader.Validate(System));

    [Theory]
    [InlineData("d10", 10)]
    [InlineData("D8", 8)]
    [InlineData("12", 12)]
    public void ParseHitDie_ReadsTheSides(string text, int sides) => Assert.Equal(sides, SheetRuleEngine.ParseHitDie(text));

    [Theory]
    [InlineData("")]
    [InlineData("big")]
    [InlineData("d0")]
    public void ParseHitDie_RejectsNonsense(string text) => Assert.Null(SheetRuleEngine.ParseHitDie(text));

    [Fact]
    public void HitPoints_FirstLevelTakesTheFullDie_LaterLevelsTheAverage_PlusConstitution()
    {
        var sheet = Sheet("knight", 5, con: 14);
        var evaluation = Run(sheet);

        // 10 + 2 at level 1, then four levels of (5 + 1) + 2 = 8.
        Assert.Equal(44, evaluation.HitPoints!.Max);
        Assert.Equal(44, Int(sheet, "HP_Max"));
        Assert.Equal(44, Int(sheet, "HP"));
    }

    [Fact]
    public void HitPoints_RaisingConstitution_RaisesEveryLevelAtOnce()
    {
        var sheet = Sheet("knight", 5, con: 14);
        Run(sheet);
        sheet["con"] = 16L;
        Run(sheet);

        Assert.Equal(49, Int(sheet, "HP_Max")); // +1 for each of the five levels
        Assert.Equal(49, Int(sheet, "HP")); // a larger maximum arrives as health
    }

    [Fact]
    public void HitPoints_ANegativeModifier_NeverTakesALevelBelowOne()
    {
        var sheet = Sheet("mage", 3, con: 3);
        var evaluation = Run(sheet);

        // d6 with -4: 6-4 = 2, then (4-4) = 0 -> 1, 1.
        Assert.Equal(4, evaluation.HitPoints!.Max);
    }

    [Fact]
    public void HitPoints_Multiclass_UsesEachClassOwnDie()
    {
        var sheet = Sheet("knight", 5, con: 10);
        sheet["multiclass"] = new List<object> { "mage:2" };
        var evaluation = Run(sheet);

        // Knight 3 (10 + 6 + 6) + mage 2 (4 + 4).
        Assert.Equal(30, evaluation.HitPoints!.Max);
        Assert.Equal(new[] { "Knight", "Knight", "Knight", "Mage", "Mage" }, evaluation.HitPoints.Levels.Select(l => l.ClassName));
    }

    [Fact]
    public void HitPoints_RolledMode_UsesTheRollsClampedToTheDie_AndAveragesTheRest()
    {
        var sheet = Sheet("knight", 4);
        sheet["hpMode"] = "rolled";
        sheet["hpRolls"] = new List<object> { "9", "99" }; // level 2: 9, level 3: clamped to 10, level 4: average 6
        var evaluation = Run(sheet);

        Assert.Equal(10 + 9 + 10 + 6, evaluation.HitPoints!.Max);
        Assert.Equal(new[] { false, true, true, false }, evaluation.HitPoints.Levels.Select(l => l.IsRolled));
    }

    [Fact]
    public void HitPoints_ManualMode_AndSheetsWithoutAMode_LeaveTheNumbersAlone()
    {
        var manual = Sheet("knight", 5, con: 14);
        manual["hpMode"] = "manual";
        manual["HP_Max"] = 99L;
        Assert.False(Run(manual).HitPoints!.Active);
        Assert.Equal(99, Int(manual, "HP_Max"));

        var legacy = new Dictionary<string, object> { ["class"] = "knight", ["level"] = 5L, ["HP_Max"] = 31L, ["HP"] = 12L };
        Run(legacy);
        Assert.Equal(31, Int(legacy, "HP_Max"));
        Assert.Equal(12, Int(legacy, "HP"));
    }

    [Fact]
    public void HitPoints_ALevelUp_HealsByTheGain_AndALowerMaximumTakesTheSameAmountOff()
    {
        var sheet = Sheet("knight", 2);
        Run(sheet); // 10 + 6 = 16
        sheet["HP"] = 5L;
        sheet["level"] = 3L;
        Run(sheet); // + 6

        Assert.Equal(22, Int(sheet, "HP_Max"));
        Assert.Equal(11, Int(sheet, "HP"));

        sheet["HP"] = 15L;
        sheet["level"] = 2L;
        Run(sheet); // maximum 22 -> 16: the same 6 comes off
        Assert.Equal(16, Int(sheet, "HP_Max"));
        Assert.Equal(9, Int(sheet, "HP"));

        sheet["HP"] = 16L;
        sheet["level"] = 1L;
        Run(sheet); // a character at full health stays at full health
        Assert.Equal(10, Int(sheet, "HP_Max"));
        Assert.Equal(10, Int(sheet, "HP"));

        sheet["level"] = 3L;
        Run(sheet);
        sheet["HP"] = 5L;
        sheet["level"] = 1L;
        Run(sheet); // never below zero
        Assert.Equal(0, Int(sheet, "HP"));
    }

    [Fact]
    public void ArmorClass_Unarmored_IsTenPlusDexterity()
    {
        var sheet = Sheet("knight", 1, dex: 14);
        var evaluation = Run(sheet);

        Assert.Equal(12, evaluation.ArmorClass!.Total);
        Assert.Equal(12, Int(sheet, "armorClass"));
    }

    [Fact]
    public void ArmorClass_BodyArmor_AppliesItsDexterityCap()
    {
        var heavy = Sheet("knight", 1, dex: 16, inventory: "mail|equipped:1");
        Assert.Equal(16, Run(heavy).ArmorClass!.Total);

        var medium = Sheet("knight", 1, dex: 16, inventory: "scale|equipped:1");
        Assert.Equal(16, Run(medium).ArmorClass!.Total); // 14 + capped 2

        var light = Sheet("knight", 1, dex: 16, inventory: "leather|equipped:1");
        Assert.Equal(14, Run(light).ArmorClass!.Total); // 11 + 3

        var clumsyHeavy = Sheet("knight", 1, dex: 6, inventory: "mail|equipped:1");
        Assert.Equal(16, Run(clumsyHeavy).ArmorClass!.Total); // heavy armor ignores Dexterity both ways
    }

    [Fact]
    public void ArmorClass_OnlyEquippedItemsCount_AndShieldsAddTheirBonus()
    {
        var sheet = Sheet("knight", 1, dex: 10, inventory: new[] { "mail", "buckler|equipped:1", "rope|qty:3" });
        var evaluation = Run(sheet);

        Assert.Equal(12, evaluation.ArmorClass!.Total); // unarmored 10 + shield 2, the mail stays in the pack
        Assert.Equal(3, evaluation.Inventory.Single(i => i.Entry.Id == "rope").Quantity);
        Assert.Contains(evaluation.ArmorClass.Lines, l => l.StartsWith("Buckler"));
    }

    [Fact]
    public void ArmorClass_ClassFormulas_CompeteWithTheBaseFormula_AndRespectShields()
    {
        var brawler = Sheet("brawler", 1, dex: 12, con: 16);
        Assert.Equal(10 + 1 + 3, Run(brawler).ArmorClass!.Total);

        var brawlerWithShield = Sheet("brawler", 1, dex: 12, con: 16, inventory: "buckler|equipped:1");
        Assert.Equal(10 + 1 + 3 + 2, Run(brawlerWithShield).ArmorClass!.Total);

        var mystic = Sheet("mystic", 1, dex: 12);
        Assert.Equal(14, Run(mystic).ArmorClass!.Total);

        // A shield switches the mystic's formula off, leaving 10 + Dex + the shield.
        var mysticWithShield = Sheet("mystic", 1, dex: 12, inventory: "buckler|equipped:1");
        Assert.Equal(10 + 1 + 2, Run(mysticWithShield).ArmorClass!.Total);

        // Armor beats class formulas: they apply only while wearing none.
        var armoredBrawler = Sheet("brawler", 1, dex: 12, con: 16, inventory: "leather|equipped:1");
        Assert.Equal(12, Run(armoredBrawler).ArmorClass!.Total);
    }

    [Fact]
    public void ArmorClass_PickBonuses_RespectTheArmorRequirement()
    {
        var bare = Sheet("knight", 1, dex: 10);
        bare["feats"] = new List<object> { "guard" };
        Assert.Equal(10, Run(bare).ArmorClass!.Total);

        var armored = Sheet("knight", 1, dex: 10, inventory: "mail|equipped:1");
        armored["feats"] = new List<object> { "guard" };
        Assert.Equal(17, Run(armored).ArmorClass!.Total);
    }

    [Fact]
    public void ArmorClass_MiscBonus_IsAdded_AndManualSheetsKeepTheirNumber()
    {
        var sheet = Sheet("knight", 1);
        sheet["armorClassMisc"] = 2L;
        Assert.Equal(12, Run(sheet).ArmorClass!.Total);

        var manual = Sheet("knight", 1);
        manual["acMode"] = "manual";
        manual["armorClass"] = 17L;
        Assert.False(Run(manual).ArmorClass!.Active);
        Assert.Equal(17, Int(manual, "armorClass"));
    }

    [Fact]
    public void Inventory_TwoSuitsOrTwoShields_AreViolations_AndUnknownItemsAreReported()
    {
        var suits = Run(Sheet("knight", 1, inventory: new[] { "mail|equipped:1", "scale|equipped:1" }));
        Assert.Contains(suits.Violations, v => v.FieldKey == "inventory" && v.Message.Contains("one suit of armor"));

        var shields = Run(Sheet("knight", 1, inventory: new[] { "buckler|equipped:1", "buckler|equipped:1" }));
        Assert.Contains(shields.Violations, v => v.Message.Contains("one shield"));

        var missing = Run(Sheet("knight", 1, inventory: "no-such-thing"));
        Assert.Contains(missing.Violations, v => v.Message.Contains("not in the Library"));
    }

    [Fact]
    public void FormatItem_RoundTripsQuantityAndEquippedState()
    {
        var rules = new InventoryRules();
        var raw = SheetRuleEngine.FormatItem(rules, "mail", 2, true);
        var (id, choices) = SheetRuleEngine.ParsePick(raw);

        Assert.Equal("mail", id);
        Assert.Equal(2, choices["qty"]);
        Assert.Equal(1, choices["equipped"]);
        Assert.Equal("rope", SheetRuleEngine.FormatItem(rules, "rope", 1, false));
    }

    [Fact]
    public void Validation_ReportsBrokenVitalsRules()
    {
        var problems = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(
            Yaml.Replace("hitPoints: { attribute: con, dieProperty: hitDie, default: average }", "hitPoints: { attribute: luck, default: sometimes }")
                .Replace("inventory: { types: [equipment] }", "inventory: { types: [gadgets] }"))).Message;

        Assert.Contains("luck", problems);
        Assert.Contains("sometimes", problems);
        Assert.Contains("gadgets", problems);
    }
}

/// <summary>The same rules against the shipped SRD data, so a change to the importer or the pack that
/// breaks hit points or armor class is caught.</summary>
public class SrdVitalsScenarioTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
    private readonly PluginLoader _loader;

    public SrdVitalsScenarioTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class RegistryResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type);
    }

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) NewSheet(string cls, int level, int dex, int con, params string[] inventory)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new RegistryResolver(_registry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = cls;
        sheet["level"] = (long)level;
        sheet["base_dex"] = (long)dex;
        sheet["base_con"] = (long)con;
        if (inventory.Length > 0) sheet["inventory"] = inventory.Cast<object>().ToList();
        engine.ApplyAttributeTotals(sheet);
        return (engine, sheet);
    }

    private static int Ac(SheetRuleEngine engine, Dictionary<string, object> sheet)
    {
        var evaluation = engine.Evaluate(sheet);
        engine.ApplyArmorClass(sheet, evaluation);
        return evaluation.ArmorClass!.Total;
    }

    [Fact]
    public void EveryShippedClass_HasAHitDieTheRulesCanRead()
    {
        foreach (var cls in _registry.GetEntries("srd5e", "class"))
        {
            Assert.True(SheetRuleEngine.ParseHitDie(cls.Properties["hitDie"]).HasValue, cls.Id);
        }
    }

    [Fact]
    public void AFighterFive_WithConstitution14_HasFortyFourHitPoints()
    {
        var (engine, sheet) = NewSheet("fighter", 5, dex: 10, con: 14);
        var evaluation = engine.Evaluate(sheet);
        engine.ApplyHitPoints(sheet, evaluation);

        Assert.Equal(44, (int)(long)sheet["HP_Max"]); // 10+2, then 4 x (6+2)
    }

    [Fact]
    public void ArmorFromThePack_UsesItsBaseAndDexterityCap()
    {
        var (engine, chain) = NewSheet("fighter", 1, dex: 14, con: 10, "chain-mail|equipped:1", "shield|equipped:1");
        Assert.Equal(16 + 2, Ac(engine, chain)); // heavy: no Dexterity, shield +2

        var (_, halfPlate) = NewSheet("fighter", 1, dex: 18, con: 10, "half-plate-armor|equipped:1");
        Assert.Equal(15 + 2, Ac(engine, halfPlate)); // medium: Dexterity capped at +2

        var (_, studded) = NewSheet("rogue", 1, dex: 16, con: 10, "studded-leather-armor|equipped:1");
        Assert.Equal(12 + 3, Ac(engine, studded)); // light: all of Dexterity
    }

    [Fact]
    public void UnarmoredDefense_BarbarianKeepsAShield_MonkDoesNot()
    {
        var (engine, barbarian) = NewSheet("barbarian", 1, dex: 14, con: 16);
        Assert.Equal(10 + 2 + 3, Ac(engine, barbarian));

        var (_, barbarianShield) = NewSheet("barbarian", 1, dex: 14, con: 16, "shield|equipped:1");
        Assert.Equal(10 + 2 + 3 + 2, Ac(engine, barbarianShield));

        var (_, monk) = NewSheet("monk", 1, dex: 14, con: 10);
        monk["base_wis"] = 16L;
        engine.ApplyAttributeTotals(monk);
        Assert.Equal(10 + 2 + 3, Ac(engine, monk));

        var (_, monkShield) = NewSheet("monk", 1, dex: 14, con: 10, "shield|equipped:1");
        monkShield["base_wis"] = 16L;
        engine.ApplyAttributeTotals(monkShield);
        Assert.Equal(10 + 2 + 2, Ac(engine, monkShield)); // 10 + Dex + shield: the monk formula is off
    }

    [Fact]
    public void TheDefenseFightingStyle_AddsOneOnlyWhileWearingArmor()
    {
        var (engine, bare) = NewSheet("fighter", 1, dex: 10, con: 10);
        bare["feats"] = new List<object> { "defense" };
        Assert.Equal(10, Ac(engine, bare));

        var (_, armored) = NewSheet("fighter", 1, dex: 10, con: 10, "chain-mail|equipped:1");
        armored["feats"] = new List<object> { "defense" };
        Assert.Equal(17, Ac(engine, armored));
    }

    [Fact]
    public void EveryPieceOfShippedArmor_CarriesTypedData()
    {
        var armor = _registry.GetEntries("srd5e", "equipment").Where(e => e.Properties.ContainsKey("armorClass")).ToList();
        Assert.Equal(13, armor.Count);
        foreach (var piece in armor)
        {
            Assert.True(piece.Properties.ContainsKey("armorType"), piece.Id);
            Assert.True(piece.Properties.ContainsKey(piece.Properties["armorType"]?.ToString() == "shield" ? "armorClassBonus" : "armorBase"), piece.Id);
        }
    }
}
