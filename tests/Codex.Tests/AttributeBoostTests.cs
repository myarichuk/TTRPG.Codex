using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The boosts attribute method (docs/ATTRIBUTE_BOOSTS_DESIGN.md): loader checks, and a few cases the degrees fixture's spec does not cover.</summary>
public class AttributeBoostTests
{
    private static string Yaml(string boosts = "sources: [{ key: ancestry, label: Ancestry, field: ancestry }]", string modifier = "half-minus-five") => $$"""
        id: boosty
        name: Boosty
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 10, modifier: {{modifier}} }
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: {{modifier}} }
        actorFields:
          - { key: ancestry, label: Ancestry, type: EntityReference, targetEntityType: ancestry }
        entityTypes:
          - { type: ancestry, label: Ancestry }
        sheetRules:
          enforcement: warn
          attributes:
            methods: [manual, boosts]
            default: boosts
            boosts: { {{boosts}} }
        """;

    private static void Rejects(string yaml, string fragment) =>
        Assert.Contains(fragment, Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml)).Message);

    [Fact]
    public void TheLoaderRefusesBoostsWithoutRulesSourcesOrANumericModifier()
    {
        Rejects(Yaml().Replace("boosts: { sources: [{ key: ancestry, label: Ancestry, field: ancestry }] }", ""), "boosts is not defined");
        Rejects(Yaml("sources: []"), "list at least one source");
        Rejects(Yaml(modifier: "none"), "half-minus-five modifier");
    }

    [Fact]
    public void TheLoaderChecksEachSource()
    {
        Rejects(Yaml("sources: [{ key: a, label: A }]"), "a field or a level");
        Rejects(Yaml("sources: [{ key: a, label: A, field: nowhere }]"), "not an EntityReference actor field");
        Rejects(Yaml("sources: [{ key: a, label: A, level: 5, free: 3 }]"), "different attributes");
        Rejects(Yaml("sources: [{ key: a__b, label: A, level: 5, free: 1 }]"), "without '__'");
        Rejects(Yaml("sources: [{ key: a, label: A, level: 5, free: 1 }, { key: a, label: B, level: 6, free: 1 }]"), "duplicate key");
        Rejects(Yaml("sources: [{ key: a, label: A, level: 5, free: 1, trade: { fixed: 2, free: 1 } }]"), "needs a field source");
    }

    [Fact]
    public void AValidBoostSystemLoads()
    {
        var definition = SystemDefinitionLoader.Parse(Yaml("partialFrom: 4, sources: [{ key: ancestry, label: Ancestry, field: ancestry, trade: { fixed: 2, free: 1 } }, { key: m5, label: Five, level: 5, free: 2 }]"));
        Assert.Equal(2, definition.SheetRules!.Attributes!.Boosts!.Sources.Count);
    }

    // ---- The engine, over a small system with invented ancestries ----

    internal const string System = """
        id: boosty
        name: Boosty
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 10, modifier: half-minus-five }
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: half-minus-five }
          - { key: con, label: Constitution, abbreviation: CON, default: 10, modifier: half-minus-five }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
          - { key: ancestry, label: Ancestry, type: EntityReference, targetEntityType: ancestry }
        entityTypes:
          - { type: ancestry, label: Ancestry }
        sheetRules:
          enforcement: strict
          attributes:
            methods: [manual, boosts]
            default: boosts
            boosts:
              sources:
                - { key: ancestry, label: Ancestry, field: ancestry, trade: { fixed: 2, free: 1 } }
                - { key: lvl5, label: Level 5, level: 5, free: 2 }
                - { key: lvl10, label: Level 10, level: 10, free: 1 }
                - { key: lvl15, label: Level 15, level: 15, free: 1 }
                - { key: lvl20, label: Level 20, level: 20, free: 1 }
          derived:
            - { key: strMod, label: Str mod, formula: "mod(str)" }
        """;

    internal sealed class Resolver : IEntryResolver
    {
        private readonly List<ContentEntry> _entries = new();

        public Resolver()
        {
            Add("hill", "con", "dex", "str|con", "free", "-str");
            Add("vale", "con", "str|dex");
            Add("twin", "con", "con");
        }

        private void Add(string id, params string[] boosts)
        {
            var entry = new ContentEntry { Type = "ancestry", Id = id, Name = id, SystemId = "boosty", PackId = "p" };
            entry.Properties["boosts"] = boosts.Cast<object>().ToList();
            _entries.Add(entry);
        }

        public ContentEntry? Resolve(string type, string id) => _entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => _entries.Where(e => e.Type == type);
    }

    internal static SheetRuleEngine Engine() => new(SystemDefinitionLoader.Parse(System), new Resolver());

    internal static Dictionary<string, object> Sheet(string ancestry, long level = 1, params (string Key, string[] Picks)[] picks)
    {
        var sheet = new Dictionary<string, object> { ["level"] = level, ["attrMethod"] = "boosts", ["ancestry"] = ancestry };
        foreach (var (key, list) in picks) sheet[key] = list.Cast<object>().ToList();
        return sheet;
    }

    private static string[] Keys(IEnumerable<RuleViolation> violations) => violations.Select(v => v.Key ?? "").OrderBy(k => k).ToArray();

    [Fact]
    public void APickOutsideAOneOfSlot_IsRefused_AndChangesNothing()
    {
        var sheet = Sheet("hill", 1, ("boost__ancestry__hill.b1", new[] { "dex" }));
        var evaluation = Engine().Recompute(sheet);
        Assert.Contains(SheetRuleEngine.BoostCantKey, Keys(evaluation.Violations));
        Assert.Equal(12L, sheet["con"]);
        Assert.Equal(8L, sheet["str"]);
    }

    [Fact]
    public void AWrongTrade_IsRefused_NoFreeSlotAppears_AndTheFixedBoostsStillApply()
    {
        var one = Sheet("hill", 1, ("boost__ancestry__hill.trade", new[] { "con" }));
        var evaluation = Engine().Recompute(one);
        Assert.Contains(SheetRuleEngine.BoostTradeKey, Keys(evaluation.Violations));
        var ancestry = evaluation.Attributes!.Boosts!.Sources.Single(s => s.Key == "ancestry");
        Assert.DoesNotContain(ancestry.Slots, s => s.Id == "t1");
        Assert.Equal(new[] { "con" }, ancestry.Given);
        Assert.Empty(ancestry.Traded);
        Assert.Equal(12L, one["con"]);

        var notFixed = Sheet("hill", 1, ("boost__ancestry__hill.trade", new[] { "con", "str" }));
        Assert.Contains(SheetRuleEngine.BoostTradeKey, Keys(Engine().Recompute(notFixed).Violations));
    }

    [Fact]
    public void ATradeNeedsEnoughFixedBoosts_ToBeOffered()
    {
        var evaluation = Engine().Recompute(Sheet("vale"));
        Assert.Null(evaluation.Attributes!.Boosts!.Sources.Single(s => s.Key == "ancestry").TradeKey);
    }

    [Fact]
    public void AFlawAndABoostOnTheSameAttribute_CancelOut()
    {
        var sheet = Sheet("hill", 1, ("boost__ancestry__hill.b1", new[] { "str" }));
        Engine().Recompute(sheet);
        Assert.Equal(10L, sheet["str"]);
    }

    [Fact]
    public void AMilestoneCountsFromItsLevel()
    {
        var picks = new[] { ("boost__lvl5__m1", new[] { "str" }) };
        var low = Sheet("vale", 4, picks);
        Engine().Recompute(low);
        Assert.Equal(10L, low["str"]);
        var high = Sheet("vale", 5, picks);
        Engine().Recompute(high);
        Assert.Equal(12L, high["str"]);
    }

    [Fact]
    public void TheSummary_ShowsHalvesAndTotals_AndStoredBasesAreIgnored()
    {
        var sheet = Sheet("vale", 20, ("boost__ancestry__vale.b1", new[] { "str" }), ("boost__lvl5__m1", new[] { "str" }));
        sheet["base_str"] = 18L;
        sheet["bonus_str"] = 4L;
        var evaluation = Engine().Recompute(sheet);
        var summary = evaluation.Attributes!;
        Assert.Equal(14, summary.Totals["str"]);
        Assert.Equal(4, summary.Bonuses["str"]);
        Assert.Empty(summary.Boosts!.Halves);
        Assert.Equal(14L, sheet["str"]);
    }

    [Fact]
    public void ABoostAtPlusFour_IsHalfAStep()
    {
        string[] str = { "str" };
        var sheet = Sheet("vale", 20, ("boost__ancestry__vale.b1", str), ("boost__lvl5__m1", str), ("boost__lvl10__m1", str), ("boost__lvl15__m1", str), ("boost__lvl20__m1", str));
        var evaluation = Engine().Recompute(sheet);
        Assert.Equal(19L, sheet["str"]);
        Assert.Contains("str", evaluation.Attributes!.Boosts!.Halves);
    }

    [Fact]
    public void ABoostAbovePlusFour_ShowsAsAHalf_AndApplyAttributeTotalsAgreesWithTheEvaluation()
    {
        var engine = Engine();
        var sheet = Sheet("vale", 5, ("boost__ancestry__vale.b1", new[] { "str" }), ("boost__lvl5__m1", new[] { "str" }));
        sheet["str"] = 99L;
        engine.ApplyAttributeTotals(sheet);
        Assert.Equal(14L, sheet["str"]);
        Assert.Equal(sheet["str"], (long)engine.Evaluate(sheet).Attributes!.Totals["str"]);
    }

    [Fact]
    public void DerivedValues_ReadTheBoostedModifier()
    {
        var sheet = Sheet("vale", 1, ("boost__ancestry__vale.b1", new[] { "str" }));
        var evaluation = Engine().Recompute(sheet);
        Assert.Equal(1L, Convert.ToInt64(evaluation.Derived["strMod"]));
    }

    [Fact]
    public void ChangingTheAncestry_DropsThePicksMadeForTheOldOne()
    {
        var engine = Engine();
        var sheet = Sheet("hill", 5, ("boost__ancestry__hill.b2", new[] { "dex" }), ("boost__ancestry__hill.trade", new[] { "con", "dex" }), ("boost__lvl5__m1", new[] { "str" }));
        engine.Recompute(sheet);
        Assert.True(sheet.ContainsKey("boost__ancestry__hill.b2"));
        sheet["ancestry"] = "vale";
        engine.Recompute(sheet);
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith("boost__ancestry__hill."));
        Assert.True(sheet.ContainsKey("boost__lvl5__m1"));
        Assert.Equal(10L, sheet["dex"]);
    }

    [Fact]
    public void AnUnplacedBoost_IsAWarning_NotAViolation_SoASheetInProgressCanBeSaved()
    {
        var evaluation = Engine().Recompute(Sheet("hill", 5));
        Assert.Empty(evaluation.Violations);
        Assert.Contains(evaluation.Warnings, w => w.Key == SheetRuleEngine.BoostOpenKey);
    }

    [Fact]
    public void TheSameAttributeTwice_InOneSource_IsRefused_EvenForTwoFixedBoosts()
    {
        var evaluation = Engine().Recompute(Sheet("twin"));
        Assert.Contains(SheetRuleEngine.BoostSameKey, Keys(evaluation.Violations));
    }

    [Fact]
    public void TheMethodNameIsReadInAnyCase()
    {
        var sheet = Sheet("vale");
        sheet["attrMethod"] = "Boosts";
        Engine().Recompute(sheet);
        Assert.Equal(12L, sheet["con"]);
    }

    [Fact]
    public void ALevelThatOpensBoosts_OffersThemAsChoices_NotAsBrokenRules()
    {
        var engine = Engine();
        var definition = SystemDefinitionLoader.Parse(System);
        var before = engine.Recompute(Sheet("vale", 4));
        var after = engine.Recompute(Sheet("vale", 5));
        var plan = LevelUpPlan.Build(definition.SheetRules!, before, after, 5, null, 5, null, null);
        Assert.Contains(plan.Choices, c => c.Key == "boosts:lvl5" && c.Added == 2);
        Assert.Empty(plan.NewViolations);
    }

    [Fact]
    public void MoreLoaderRefusals()
    {
        Rejects(Yaml("partialFrom: 0, sources: [{ key: a, label: A, field: ancestry }]"), "partialFrom");
        Rejects(Yaml("sources: [{ key: a#1, label: A, field: ancestry }]"), "without '__'");
        Rejects(Yaml("sources: [{ key: a, label: A, field: ancestry, free: 1 }]"), "free applies to a level milestone");
        Rejects(Yaml("sources: [{ key: a, label: A, field: ancestry, trade: { fixed: 0, free: 1 } }]"), "trade");
        Rejects(Yaml("sources: [{ key: a, label: A, field: ancestry }]").Replace("default: boosts", "default: boosts\n    bonuses: { fromField: ancestry, property: x, allocations: [[1]] }"), "do not mix");
        Rejects(Yaml("sources: [{ key: a, label: A, field: ancestry }]").Replace("default: boosts", "default: boosts\n    increases: { fromList: feats }"), "do not mix");
        Rejects(Yaml("sources: [{ key: a, label: A, field: ancestry }]").Replace("default: 10, modifier: half-minus-five }\n  - { key: dex", "default: 12, modifier: half-minus-five }\n  - { key: dex"), "default 10");
    }
}
