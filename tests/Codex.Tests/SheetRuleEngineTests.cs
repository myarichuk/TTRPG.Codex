using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The data-driven sheet rules, exercised against a small in-memory system that mirrors
/// the shape of the SRD rules (class → subclass, class spell list, level tables, point buy, bonuses).</summary>
public class SheetRuleEngineTests
{
    protected const string Yaml = """
        id: demo
        name: Demo
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 10, modifier: half-minus-five }
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: half-minus-five }
          - { key: int, label: Intelligence, abbreviation: INT, default: 10, modifier: half-minus-five }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: subclass, label: Subclass, type: EntityReference, targetEntityType: subclass }
          - { key: background, label: Background, type: EntityReference, targetEntityType: background }
          - { key: originFeat, label: Origin Feat, type: EntityReference, targetEntityType: feat }
        entityTypes:
          - { type: class, label: Class }
          - { type: subclass, label: Subclass }
          - { type: background, label: Background }
          - { type: feat, label: Feat }
          - { type: spell, label: Spell }
        sheetRules:
          enforcement: strict
          attributes:
            methods: [point-buy, standard-array, manual]
            default: point-buy
            max: 20
            pointBuy: { budget: 20, min: 8, max: 15, costs: { 8: 0, 9: 1, 10: 2, 11: 3, 12: 4, 13: 5, 14: 7, 15: 9 } }
            standardArray: [15, 14, 13]
            bonuses: { label: Background bonuses, fromField: background, property: abilityScores, allocations: [[2, 1], [1, 1, 1]] }
          derived:
            - { key: proficiencyBonus, label: Proficiency Bonus, formula: "2 + (level - 1) / 4" }
            - { key: initiative, label: Initiative, formula: "mod(dex)" }
          constraints:
            - field: subclass
              where: [{ property: class, equalsField: class, message: "that subclass belongs to another class." }]
            - field: originFeat
              where:
                - { property: category, equals: origin, message: "only an origin feat can go here." }
                - { property: id, equalsPropertyOfField: { field: background, property: feat }, message: "your background grants a different feat." }
          lists:
            - key: spells
              label: Spells
              targetEntityType: spell
              where:
                - { property: level, gte: 1, message: "no cantrips." }
                - { property: classes, containsNameOfField: class, message: "not on your list." }
                - { property: level, lteLookup: { field: class, table: spellcasting, byField: level, column: maxSpellLevel }, message: "too high." }
              maxCount: { lookup: { field: class, table: spellcasting, byField: level, column: preparedSpells } }
            - key: feats
              label: Feats
              targetEntityType: feat
              where:
                - { property: category, in: [general], message: "general feats only." }
                - { property: minLevel, lteField: level, message: "too low level." }
              maxCount: { lookup: { field: class, table: featSlots, byField: level, column: slots } }
        """;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse(Yaml);

    private sealed class Resolver : IEntryResolver
    {
        private readonly List<ContentEntry> _entries = new();

        public Resolver()
        {
            Add("class", "wizard", "Wizard", p =>
            {
                p["spellcasting"] = new List<object>
                {
                    Row(("level", 1L), ("preparedSpells", 4L), ("maxSpellLevel", 1L)),
                    Row(("level", 3L), ("preparedSpells", 6L), ("maxSpellLevel", 2L)),
                    Row(("level", 5L), ("preparedSpells", 9L), ("maxSpellLevel", 3L))
                };
                p["featSlots"] = new List<object> { Row(("level", 1L), ("slots", 0L)), Row(("level", 4L), ("slots", 1L)) };
            });
            Add("class", "fighter", "Fighter", p => p["featSlots"] = new List<object> { Row(("level", 1L), ("slots", 0L)), Row(("level", 4L), ("slots", 1L)), Row(("level", 6L), ("slots", 2L)) });
            Add("subclass", "evoker", "Evoker", p => p["class"] = "wizard");
            Add("subclass", "champion", "Champion", p => p["class"] = "fighter");
            Add("background", "scholar", "Scholar", p => { p["abilityScores"] = new List<object> { "Intelligence", "Dexterity" }; });
            Add("background", "sage", "Sage", p => { p["abilityScores"] = new List<object> { "CON", "INT", "WIS" }; p["feat"] = "magic-initiate"; });
            Add("feat", "magic-initiate", "Magic Initiate", p => { p["category"] = "origin"; p["minLevel"] = 1L; });
            Add("feat", "alert", "Alert", p => { p["category"] = "origin"; p["minLevel"] = 1L; });
            Add("feat", "grappler", "Grappler", p => { p["category"] = "general"; p["minLevel"] = 4L; });
            Add("spell", "fire-bolt", "Fire Bolt", p => { p["level"] = 0L; p["classes"] = new List<object> { "Wizard" }; });
            Add("spell", "magic-missile", "Magic Missile", p => { p["level"] = 1L; p["classes"] = new List<object> { "Wizard" }; });
            Add("spell", "shield", "Shield", p => { p["level"] = 1L; p["classes"] = new List<object> { "Wizard" }; });
            Add("spell", "fireball", "Fireball", p => { p["level"] = 3L; p["classes"] = new List<object> { "Wizard" }; });
            Add("spell", "cure-wounds", "Cure Wounds", p => { p["level"] = 1L; p["classes"] = new List<object> { "Cleric" }; });
        }

        private static Dictionary<string, object> Row(params (string Key, object Value)[] cells) => cells.ToDictionary(c => c.Key, c => c.Value);

        private void Add(string type, string id, string name, Action<Dictionary<string, object>> fill)
        {
            var entry = new ContentEntry { Type = type, Id = id, Name = name, SystemId = "demo", PackId = "p" };
            fill(entry.Properties);
            _entries.Add(entry);
        }

        public ContentEntry? Resolve(string type, string id) => _entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => _entries.Where(e => e.Type == type);
    }

    /// <summary>The system the tests run against; <see cref="LocalizedSheetRuleEngineTests"/> swaps in a translated copy.</summary>
    protected virtual SystemDefinition Sys => System;

    private SheetRuleEngine Engine() => new(Sys, new Resolver());

    private static Dictionary<string, object> Sheet(params (string Key, object Value)[] values)
    {
        var sheet = new Dictionary<string, object>();
        foreach (var (key, value) in values) sheet[key] = value;
        return sheet;
    }

    [Fact]
    public void Subclass_MustBelongToTheChosenClass()
    {
        var engine = Engine();
        var sheet = Sheet(("level", 3L), ("class", "wizard"), ("subclass", "champion"), ("attrMethod", "manual"));

        var result = engine.Evaluate(sheet);

        Assert.Contains(result.Violations, v => v.FieldKey == "subclass" && v.Message.Contains("another class"));
        Assert.Equal(new[] { "evoker" }, result.AllowedOptions["subclass"].Select(e => e.Id));
    }

    [Fact]
    public void OriginFeat_MustBeTheOneTheBackgroundGrants()
    {
        var engine = Engine();
        var ok = engine.Evaluate(Sheet(("background", "sage"), ("originFeat", "magic-initiate"), ("attrMethod", "manual")));
        Assert.DoesNotContain(ok.Violations, v => v.FieldKey == "originFeat");

        var wrong = engine.Evaluate(Sheet(("background", "sage"), ("originFeat", "alert"), ("attrMethod", "manual")));
        Assert.Contains(wrong.Violations, v => v.FieldKey == "originFeat" && v.Message.Contains("different feat"));
        Assert.Equal(new[] { "magic-initiate" }, wrong.AllowedOptions["originFeat"].Select(e => e.Id));

        var general = engine.Evaluate(Sheet(("originFeat", "grappler"), ("attrMethod", "manual")));
        Assert.Contains(general.Violations, v => v.FieldKey == "originFeat" && v.Message.Contains("origin feat"));
    }

    [Fact]
    public void Spells_FilteredByClassListAndSlotLevel_AndCounted()
    {
        var engine = Engine();
        var sheet = Sheet(("level", 3L), ("class", "wizard"), ("attrMethod", "manual"),
            ("spells", new List<object> { "magic-missile", "fireball", "cure-wounds", "fire-bolt" }));

        var result = engine.Evaluate(sheet);

        Assert.Contains(result.Violations, v => v.FieldKey == "spells" && v.Message.Contains("Fireball") && v.Message.Contains("too high"));
        Assert.Contains(result.Violations, v => v.FieldKey == "spells" && v.Message.Contains("Cure Wounds") && v.Message.Contains("not on your list"));
        Assert.Contains(result.Violations, v => v.FieldKey == "spells" && v.Message.Contains("Fire Bolt") && v.Message.Contains("no cantrips"));
        Assert.Equal(6, result.ListLimits["spells"]);                       // level 3 row (the highest row not above 3)
        Assert.Equal(new[] { "shield" }, result.ListOptions["spells"].Select(e => e.Id)); // the only unpicked legal spell
        Assert.Equal(4, result.ListPicks["spells"].Count);
    }

    [Fact]
    public void Spells_TooMany_IsAViolation_AndLevelFiveRowRaisesTheCap()
    {
        var engine = Engine();
        var level1 = engine.Evaluate(Sheet(("level", 1L), ("class", "wizard"), ("attrMethod", "manual"), ("spells", new List<object> { "magic-missile", "shield" })));
        Assert.Equal(4, level1.ListLimits["spells"]);
        Assert.DoesNotContain(level1.Violations, v => v.FieldKey == "spells");

        var level5 = engine.Evaluate(Sheet(("level", 5L), ("class", "wizard"), ("attrMethod", "manual"), ("spells", new List<object> { "fireball" })));
        Assert.DoesNotContain(level5.Violations, v => v.FieldKey == "spells");
        Assert.Equal(9, level5.ListLimits["spells"]);
    }

    [Fact]
    public void Lists_ClassWithoutTheTable_GetsZero_NoClassGetsNoLimit()
    {
        var engine = Engine();
        var fighter = engine.Evaluate(Sheet(("level", 5L), ("class", "fighter"), ("attrMethod", "manual"), ("spells", new List<object> { "magic-missile" })));
        Assert.Equal(0, fighter.ListLimits["spells"]);
        Assert.Contains(fighter.Violations, v => v.FieldKey == "spells" && v.Message.Contains("only 0 allowed"));
        Assert.Contains(fighter.Violations, v => v.FieldKey == "spells" && v.Message.Contains("too high")); // no table: no slot level either
        Assert.Empty(fighter.ListOptions["spells"]);

        var noClass = engine.Evaluate(Sheet(("level", 5L), ("attrMethod", "manual")));
        Assert.Null(noClass.ListLimits["spells"]);
    }

    [Fact]
    public void Feats_RespectMinimumLevelAndClassSlots()
    {
        var engine = Engine();
        var early = engine.Evaluate(Sheet(("level", 2L), ("class", "fighter"), ("attrMethod", "manual"), ("feats", new List<object> { "grappler" })));
        Assert.Contains(early.Violations, v => v.FieldKey == "feats" && v.Message.Contains("too low level"));
        Assert.Contains(early.Violations, v => v.FieldKey == "feats" && v.Message.Contains("only 0 allowed"));

        var later = engine.Evaluate(Sheet(("level", 6L), ("class", "fighter"), ("attrMethod", "manual"), ("feats", new List<object> { "grappler" })));
        Assert.DoesNotContain(later.Violations, v => v.FieldKey == "feats");
        Assert.Equal(2, later.ListLimits["feats"]);
        Assert.Empty(later.ListOptions["feats"]); // grappler is the only general feat and it's taken
    }

    [Fact]
    public void PointBuy_CountsCostsAndRejectsOverspendOrOutOfRange()
    {
        var engine = Engine();
        var fine = engine.Evaluate(Sheet(("attrMethod", "point-buy"), ("base_str", 15L), ("base_dex", 13L), ("base_int", 12L)));
        Assert.Equal(18, fine.Attributes!.PointsSpent);
        Assert.Equal(20, fine.Attributes.PointsBudget);
        Assert.DoesNotContain(fine.Violations, v => v.FieldKey == "attrMethod");

        var over = engine.Evaluate(Sheet(("attrMethod", "point-buy"), ("base_str", 15L), ("base_dex", 15L), ("base_int", 15L)));
        Assert.Contains(over.Violations, v => v.FieldKey == "attrMethod" && v.Message.Contains("27 of 20"));

        var outOfRange = engine.Evaluate(Sheet(("attrMethod", "point-buy"), ("base_str", 18L), ("base_dex", 8L), ("base_int", 8L)));
        Assert.Contains(outOfRange.Violations, v => v.FieldKey == "str" && v.Message.Contains("8 to 15"));
    }

    [Fact]
    public void StandardArray_MustBeUsedExactly()
    {
        var engine = Engine();
        var ok = engine.Evaluate(Sheet(("attrMethod", "standard-array"), ("base_str", 13L), ("base_dex", 15L), ("base_int", 14L)));
        Assert.DoesNotContain(ok.Violations, v => v.FieldKey == "attrMethod");

        var bad = engine.Evaluate(Sheet(("attrMethod", "standard-array"), ("base_str", 15L), ("base_dex", 15L), ("base_int", 14L)));
        Assert.Contains(bad.Violations, v => v.FieldKey == "attrMethod" && v.Message.Contains("Standard array"));
    }

    [Fact]
    public void Bonuses_OnlyOnEligibleAttributes_InAnAllowedSpread_UnderTheCap()
    {
        var engine = Engine();
        var good = engine.Evaluate(Sheet(("attrMethod", "manual"), ("background", "sage"), ("base_int", 15L), ("bonus_int", 2L), ("bonus_dex", 1L), ("base_dex", 10L), ("base_str", 10L)));
        Assert.Contains(good.Violations, v => v.FieldKey == "bonus_dex" && v.Message.Contains("not one of the eligible"));
        Assert.Equal(new[] { "int" }, good.Attributes!.EligibleBonusAttributes); // the Sage lists CON, INT, WIS; only INT exists in this demo system

        var spread = engine.Evaluate(Sheet(("attrMethod", "manual"), ("background", "sage"), ("bonus_int", 2L), ("bonus_dex", 2L)));
        Assert.Contains(spread.Violations, v => v.FieldKey == "attrMethod" && v.Message.Contains("+2/+1"));

        var capped = engine.Evaluate(Sheet(("attrMethod", "manual"), ("background", "sage"), ("base_int", 19L), ("bonus_int", 2L), ("bonus_dex", 1L)));
        Assert.Contains(capped.Violations, v => v.FieldKey == "int" && v.Message.Contains("exceed 20"));
        Assert.Equal(21, capped.Attributes!.Totals["int"]);
    }

    [Fact]
    public void Bonuses_AcceptAttributeNamesWrittenInFull()
    {
        // Packs may name an attribute by its label ("Intelligence") rather than its key or abbreviation; that must keep
        // working when the system is shown in another language.
        var result = Engine().Evaluate(Sheet(("attrMethod", "manual"), ("background", "scholar")));

        Assert.Equal(new[] { "dex", "int" }, result.Attributes!.EligibleBonusAttributes.Order());
    }

    [Fact]
    public void Derived_ComputesFromLevelAndAttributes()
    {
        var engine = Engine();
        var result = engine.Evaluate(Sheet(("attrMethod", "manual"), ("level", 9L), ("dex", 17L)));

        Assert.Equal(4L, result.Derived["proficiencyBonus"]);
        Assert.Equal(3L, result.Derived["initiative"]);
    }

    [Fact]
    public void InitializeSheet_AndApplyTotals_RoundTrip()
    {
        var engine = Engine();
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        Assert.Equal("point-buy", sheet["attrMethod"]);
        Assert.Equal(8L, sheet["base_str"]);
        Assert.Equal(8L, sheet["str"]);

        sheet["base_str"] = 15L;
        sheet["bonus_str"] = 2L;
        engine.ApplyAttributeTotals(sheet);
        Assert.Equal(17L, sheet["str"]);
    }

    [Fact]
    public void NoRules_MeansNoViolationsAndNoOptionsFiltering()
    {
        var loose = SystemDefinitionLoader.Parse("id: loose\nname: Loose\nactorFields:\n  - { key: class, label: Class, type: EntityReference, targetEntityType: class }\nentityTypes:\n  - { type: class, label: Class }\n");
        var engine = new SheetRuleEngine(loose, new Resolver());
        var result = engine.Evaluate(Sheet(("class", "anything")));
        Assert.Empty(result.Violations);
        Assert.Empty(result.AllowedOptions);
    }

    [Theory]
    [InlineData("strict", null, "strict")]
    [InlineData("strict", "inherit", "strict")]
    [InlineData("strict", "warn", "warn")]
    [InlineData("warn", "off", "off")]
    [InlineData("strict", "nonsense", "off")]
    public void EffectiveEnforcement_CampaignOverridesSystem(string systemLevel, string? campaign, string expected)
    {
        var definition = SystemDefinitionLoader.Parse($"id: x\nname: X\nsheetRules:\n  enforcement: {systemLevel}\n");
        Assert.Equal(expected, SheetRuleEngine.EffectiveEnforcement(definition, campaign));
        Assert.Equal("off", SheetRuleEngine.EffectiveEnforcement(null, null));
    }

    [Fact]
    public void Validation_RejectsRulesThatPointAtMissingFields()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse("""
            id: bad
            name: Bad
            actorFields:
              - { key: level, label: Level, type: Number }
            entityTypes:
              - { type: spell, label: Spell }
            sheetRules:
              enforcement: maybe
              constraints:
                - { field: level, where: [] }
              lists:
                - { key: level, label: L, targetEntityType: spell, where: [{ property: level, lteField: nope }] }
                - { key: knacks, label: K, targetEntityType: knack }
            """));
        Assert.Contains("must be strict, warn or off", ex.Message);
        Assert.Contains("'level' is not an EntityReference", ex.Message);
        Assert.Contains("collides with an actor field", ex.Message);
        Assert.Contains("does not declare", ex.Message);
        Assert.Contains("'nope', which does not exist", ex.Message);
    }
}
