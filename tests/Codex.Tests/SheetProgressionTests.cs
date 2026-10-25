using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>Score increases from picks, multiclassing and slot pools, against a small in-memory system
/// whose rules are all data: nothing here is specific to the SRD.</summary>
public class SheetProgressionTests
{
    protected const string Yaml = """
        id: demo
        name: Demo
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 10 }
          - { key: int, label: Intelligence, abbreviation: INT, default: 10 }
          - { key: wis, label: Wisdom, abbreviation: WIS, default: 10 }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
        entityTypes:
          - { type: class, label: Class }
          - { type: feat, label: Feat }
          - { type: spell, label: Spell }
        sheetRules:
          attributes:
            methods: [manual]
            default: manual
            max: 20
            increases: { label: Increases, fromList: feats, property: points, optionsProperty: abilities, capProperty: cap }
          lists:
            - key: spells
              label: Spells
              targetEntityType: spell
              where:
                - { property: classes, containsNameOfField: class, message: "not on your list." }
                - { property: level, lteLookup: { field: class, table: casting, byField: classLevel, column: maxLevel }, message: "too high." }
              maxCount: { lookup: { field: class, table: casting, byField: classLevel, column: prepared } }
            - key: feats
              label: Feats
              targetEntityType: feat
              repeatableProperty: repeatable
          multiclass: { key: multiclass, classField: class, levelField: level, classLevelKey: classLevel, prerequisitesProperty: requires }
          slotPools:
            - key: slots
              label: Spell slots
              byField: classLevel
              table: casting
              column: slots
              excludeCasterTypes: [pact]
              recoversOn: [Long Rest]
              combine: { weights: { full: 1, half: 0.5 }, rounding: up, table: ["2", "3", "4/2", "4/3", "4/3/2"] }
            - key: pact
              label: Pact slots
              byField: classLevel
              table: casting
              column: slots
              slotLevelColumn: maxLevel
              includeCasterTypes: [pact]
              recoversOn: [Short Rest, Long Rest]
        """;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse(Yaml);

    private sealed class Resolver : IEntryResolver
    {
        private readonly List<ContentEntry> _entries = new();

        public Resolver()
        {
            Add("class", "mage", "Mage", p =>
            {
                p["casterType"] = "full";
                p["requires"] = new List<object> { "INT 13+" };
                p["casting"] = new List<object>
                {
                    Row(("level", 1L), ("prepared", 2L), ("maxLevel", 1L), ("slots", "2")),
                    Row(("level", 2L), ("prepared", 3L), ("maxLevel", 1L), ("slots", "3")),
                    Row(("level", 3L), ("prepared", 4L), ("maxLevel", 2L), ("slots", "4/2"))
                };
            });
            Add("class", "priest", "Priest", p =>
            {
                p["casterType"] = "full";
                p["requires"] = new List<object> { "WIS 13+" };
                p["casting"] = new List<object> { Row(("level", 1L), ("prepared", 2L), ("maxLevel", 1L), ("slots", "2")) };
            });
            Add("class", "knight", "Knight", p =>
            {
                p["casterType"] = "half";
                p["requires"] = new List<object> { "STR 13+, WIS 13+" };
                p["casting"] = new List<object> { Row(("level", 1L), ("prepared", 1L), ("maxLevel", 1L), ("slots", "2")) };
            });
            Add("class", "hexer", "Hexer", p =>
            {
                p["casterType"] = "pact";
                p["casting"] = new List<object> { Row(("level", 1L), ("prepared", 2L), ("maxLevel", 1L), ("slots", "1")), Row(("level", 3L), ("prepared", 4L), ("maxLevel", 2L), ("slots", "2")) };
            });
            Add("class", "brute", "Brute", p => p["casterType"] = "none");
            Add("feat", "asi", "Improvement", p => { p["points"] = 2L; p["cap"] = 20L; p["repeatable"] = true; });
            Add("feat", "wrestler", "Wrestler", p => { p["points"] = 1L; p["abilities"] = new List<object> { "STR" }; p["cap"] = 20L; });
            Add("feat", "boon", "Boon", p => { p["points"] = 1L; p["cap"] = 30L; });
            Add("feat", "lucky", "Lucky", _ => { });
            Add("spell", "bolt", "Bolt", p => { p["level"] = 1L; p["classes"] = new List<object> { "Mage" }; });
            Add("spell", "storm", "Storm", p => { p["level"] = 2L; p["classes"] = new List<object> { "Mage" }; });
            Add("spell", "mend", "Mend", p => { p["level"] = 1L; p["classes"] = new List<object> { "Priest" }; });
            Add("spell", "smite", "Smite", p => { p["level"] = 2L; p["classes"] = new List<object> { "Priest" }; });
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

    /// <summary>The system under test; <see cref="LocalizedSheetProgressionTests"/> swaps in a pseudo-translated copy.</summary>
    protected virtual SystemDefinition Sys => System;

    private SheetRuleEngine Engine() => new(Sys, new Resolver());

    private static Dictionary<string, object> Sheet(params (string Key, object Value)[] values)
    {
        var sheet = new Dictionary<string, object> { ["base_str"] = 10L, ["base_int"] = 14L, ["base_wis"] = 14L };
        foreach (var (key, value) in values) sheet[key] = value;
        return sheet;
    }

    private static List<object> List(params string[] items) => items.Cast<object>().ToList();

    private static string Messages(SheetEvaluation evaluation) => string.Join(" | ", evaluation.Violations.Select(v => v.Message));

    [Fact]
    public void Validation_AcceptsTheDemoRules() => Assert.Empty(SystemDefinitionLoader.Validate(System));

    [Fact]
    public void PickChoices_RoundTrip()
    {
        var raw = SheetRuleEngine.FormatPick("asi", new Dictionary<string, int> { ["str"] = 1, ["int"] = 1 });
        Assert.Equal("asi|str:1,int:1", raw);
        var (id, choices) = SheetRuleEngine.ParsePick(raw);
        Assert.Equal("asi", id);
        Assert.Equal(1, choices["STR"]);
        Assert.Equal("lucky", SheetRuleEngine.FormatPick("lucky", new Dictionary<string, int>()));
    }

    [Fact]
    public void Increases_RaiseTotals_AndAreChecked()
    {
        var engine = Engine();
        var sheet = Sheet(("feats", List("asi|str:2", "asi|int:1,wis:1", "wrestler|str:1")));
        engine.ApplyAttributeTotals(sheet);
        var result = engine.Evaluate(sheet);

        Assert.False(result.HasViolations, Messages(result));
        Assert.Equal(13L, sheet["str"]);
        Assert.Equal(15L, sheet["int"]);
        Assert.Equal(3, result.Increases.Count);
        Assert.Equal(13, result.Attributes!.Totals["str"]);
    }

    [Fact]
    public void Increases_UnspentWrongAttributeOrNoGrant_AreViolations()
    {
        var result = Engine().Evaluate(Sheet(("feats", List("asi|str:1", "wrestler|int:1", "lucky|str:1"))));
        var messages = Messages(result);
        Assert.Contains("1 remaining point goes", messages);
        Assert.Matches("Wrestler can't raise §?Intelligence", messages); // the label shown may be a translation (§ marks the pseudo one)
        Assert.Contains("Lucky doesn't raise any score", messages);
    }

    [Fact]
    public void Increases_RespectEachCap()
    {
        var engine = Engine();
        var over = Sheet(("base_str", 19L), ("feats", List("asi|str:2")));
        engine.ApplyAttributeTotals(over);
        Assert.Contains("Strength: scores can't exceed 20", Messages(engine.Evaluate(over)));

        // A boon may go past the usual maximum.
        var boon = Sheet(("base_str", 20L), ("feats", List("boon|str:1")));
        engine.ApplyAttributeTotals(boon);
        Assert.False(engine.Evaluate(boon).HasViolations);
    }

    [Fact]
    public void RepeatableEntries_StayOnOffer_OthersOnlyOnce()
    {
        var result = Engine().Evaluate(Sheet(("feats", List("asi|str:2", "wrestler|str:1", "wrestler|str:1"))));
        Assert.Contains(result.ListOptions["feats"], e => e.Id == "asi");
        Assert.DoesNotContain(result.ListOptions["feats"], e => e.Id == "wrestler");
        Assert.Contains("Wrestler can only be taken once", Messages(result));
        Assert.Equal(new[] { 0, 1, 2 }, result.ListPicks["feats"].Select(p => p.Index));
    }

    [Fact]
    public void Multiclass_ListsUseEachClassAtItsOwnLevel_AndAddLimits()
    {
        // Mage 3 / Priest 1: mage spells up to level 2, priest spells up to level 1.
        var result = Engine().Evaluate(Sheet(("class", "mage"), ("level", 4L), ("multiclass", List("priest:1"))));

        Assert.False(result.HasViolations, Messages(result));
        Assert.Equal(new[] { ("mage", 3, true), ("priest", 1, false) }, result.Classes.Select(c => (c.Id, c.Level, c.IsFirst)));
        Assert.Equal(new[] { "bolt", "mend", "storm" }, result.ListOptions["spells"].Select(e => e.Id).OrderBy(i => i));
        Assert.Equal(6, result.ListLimits["spells"]); // 4 + 2

        var tooHigh = Engine().Evaluate(Sheet(("class", "mage"), ("level", 4L), ("multiclass", List("priest:1")), ("spells", List("smite"))));
        Assert.Contains("Smite - too high", Messages(tooHigh));
    }

    [Fact]
    public void Multiclass_ChecksPrerequisitesAndLevels()
    {
        var engine = Engine();
        var lowStrength = engine.Evaluate(Sheet(("class", "mage"), ("level", 3L), ("multiclass", List("knight:1"))));
        Assert.Contains("Knight needs STR 13+, WIS 13+", Messages(lowStrength));
        Assert.DoesNotContain(lowStrength.MulticlassOptions, c => c.Id == "knight");
        Assert.Contains(lowStrength.MulticlassOptions, c => c.Id == "priest");

        var lowIntelligence = engine.Evaluate(Sheet(("class", "mage"), ("base_int", 10L), ("level", 2L), ("multiclass", List("priest:1"))));
        Assert.Contains("to multiclass out of Mage you need INT 13+", Messages(lowIntelligence));

        var noLevelsLeft = engine.Evaluate(Sheet(("class", "mage"), ("level", 2L), ("multiclass", List("priest:2"))));
        Assert.Contains("leaving none for Mage", Messages(noLevelsLeft));
    }

    [Fact]
    public void Slots_OneCasterUsesItsOwnTable()
    {
        var result = Engine().Evaluate(Sheet(("class", "mage"), ("level", 3L)));
        var pool = Assert.Single(result.SlotPools);
        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 2 }, pool.Max);
    }

    [Fact]
    public void Slots_SeveralCastersCombine_WithWeightedLevels()
    {
        // Mage 3 + Knight 1 (half, rounded up to 1) = caster level 4 → "4/3".
        var sheet = Sheet(("class", "mage"), ("level", 4L), ("base_str", 13L), ("multiclass", List("knight:1")));
        var result = Engine().Evaluate(sheet);
        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 3 }, Assert.Single(result.SlotPools).Max);
    }

    [Fact]
    public void Slots_PactPoolStaysSeparate_AndNonCastersGetNone()
    {
        var result = Engine().Evaluate(Sheet(("class", "mage"), ("level", 4L), ("multiclass", List("hexer:3"))));
        Assert.Equal(new Dictionary<int, int> { [1] = 2 }, result.SlotPools.Single(p => p.Rule.Key == "slots").Max);
        Assert.Equal(new Dictionary<int, int> { [2] = 2 }, result.SlotPools.Single(p => p.Rule.Key == "pact").Max);

        Assert.Empty(Engine().Evaluate(Sheet(("class", "brute"), ("level", 5L))).SlotPools);
    }

    [Fact]
    public void ApplySlotPools_SetsMaxima_KeepsSpentSlots_AndDropsLostLevels()
    {
        var engine = Engine();
        var sheet = Sheet(("class", "mage"), ("level", 2L));
        engine.ApplySlotPools(sheet, engine.Evaluate(sheet));
        Assert.Equal(3L, sheet["slots_1"]);
        Assert.Equal(3L, sheet["slots_1_Max"]);

        sheet["slots_1"] = 1L; // two spent
        sheet["level"] = 3L;   // level 3: four level-1 slots, two level-2 slots
        engine.ApplySlotPools(sheet, engine.Evaluate(sheet));
        Assert.Equal(2L, sheet["slots_1"]); // the new slot arrives available, the spent ones stay spent
        Assert.Equal(2L, sheet["slots_2"]);

        sheet["level"] = 1L;
        engine.ApplySlotPools(sheet, engine.Evaluate(sheet));
        Assert.Equal(2L, sheet["slots_1"]);
        Assert.False(sheet.ContainsKey("slots_2"));
        Assert.False(sheet.ContainsKey("slots_2_Max"));
        Assert.Equal(new[] { "Long Rest", "Short Rest" }, engine.RestNames().OrderBy(n => n));
    }
}
