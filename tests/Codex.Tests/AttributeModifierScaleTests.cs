using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>Attributes that are modifiers (<c>modifier: value</c>, docs/DEGREES_IMPORT_DESIGN.md section 19): the value is the modifier, a boost adds 1,
/// a flaw takes 1, a boost at +4 or above is a pending partial boost and two of them make one step. The score mode stays as it was.</summary>
public class AttributeModifierScaleTests
{
    internal const string System = """
        id: boosty
        name: Boosty
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 0, min: -5, max: 10, modifier: value }
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 0, min: -5, max: 10, modifier: value }
          - { key: con, label: Constitution, abbreviation: CON, default: 0, min: -5, max: 10, modifier: value }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
          - { key: ancestry, label: Ancestry, type: EntityReference, targetEntityType: ancestry }
        entityTypes:
          - { type: ancestry, label: Ancestry }
        sheetRules:
          enforcement: warn
          attributes:
            methods: [manual, boosts]
            default: boosts
            boosts:
              sources:
                - { key: ancestry, label: Ancestry, field: ancestry }
                - { key: l1, label: One, level: 1, free: 1 }
                - { key: l2, label: Two, level: 2, free: 1 }
                - { key: l3, label: Three, level: 3, free: 1 }
                - { key: l4, label: Four, level: 4, free: 1 }
                - { key: l5, label: Five, level: 5, free: 1 }
                - { key: l6, label: Six, level: 6, free: 1 }
                - { key: l7, label: Seven, level: 7, free: 1 }
          derived:
            - { key: strMod, label: Str mod, formula: "mod(str)" }
        """;

    private static SheetRuleEngine Engine() => new(SystemDefinitionLoader.Parse(System), new AttributeBoostTests.Resolver());

    /// <summary>A character of this level who puts every level boost on Strength.</summary>
    private static Dictionary<string, object> StrengthTo(long level, string ancestry = "vale")
    {
        var picks = Enumerable.Range(1, (int)level).Select(n => ($"boost__l{n}__m1", new[] { "str" })).ToArray();
        return AttributeBoostTests.Sheet(ancestry, level, picks);
    }

    [Fact]
    public void TheLoaderAcceptsBoostsOnTheModifierScale_AndRefusesTheWrongStartOrAMix()
    {
        var definition = SystemDefinitionLoader.Parse(System);
        Assert.All(definition.Attributes, a => Assert.True(a.IsModifier));
        Assert.Equal(8, definition.SheetRules!.Attributes!.Boosts!.Sources.Count);

        var tenInstead = System.Replace("key: str, label: Strength, abbreviation: STR, default: 0", "key: str, label: Strength, abbreviation: STR, default: 10");
        Assert.Contains("default 0", Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(tenInstead)).Message);

        var mixed = System.Replace("default: 0, min: -5, max: 10, modifier: value }\n  - { key: dex", "default: 10, modifier: half-minus-five }\n  - { key: dex");
        Assert.Contains("every attribute or by none", Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(mixed)).Message);
    }

    [Fact]
    public void ABoostBelowTheThreshold_AddsOne_AndDefaultIsPlusZero()
    {
        var sheet = StrengthTo(3);
        var evaluation = Engine().Recompute(sheet);
        Assert.Equal(3L, sheet["str"]);
        Assert.Equal(0L, sheet["dex"]);
        Assert.Empty(evaluation.Attributes!.Boosts!.Halves);
    }

    [Fact]
    public void ABoostAtTheThreshold_IsAPendingPartialBoost_AndTheModifierStaysPutUntilTheNextOne()
    {
        var sheet = StrengthTo(4);
        var evaluation = Engine().Recompute(sheet);
        Assert.Equal(4L, sheet["str"]);
        Assert.Empty(evaluation.Attributes!.Boosts!.Halves);

        sheet = StrengthTo(5);
        evaluation = Engine().Recompute(sheet);
        Assert.Equal(4L, sheet["str"]);
        Assert.Contains("str", evaluation.Attributes!.Boosts!.Halves);
    }

    [Fact]
    public void TwoPartialBoosts_MakeOneStep_AndAThirdStartsAnother()
    {
        var sheet = StrengthTo(6);
        var evaluation = Engine().Recompute(sheet);
        Assert.Equal(5L, sheet["str"]);
        Assert.Empty(evaluation.Attributes!.Boosts!.Halves);

        sheet = StrengthTo(7);
        evaluation = Engine().Recompute(sheet);
        Assert.Equal(5L, sheet["str"]);
        Assert.Contains("str", evaluation.Attributes!.Boosts!.Halves);
        Assert.Equal(5L, Convert.ToInt64(evaluation.Derived["strMod"]));
    }

    [Fact]
    public void AFlaw_TakesOne_AndModReadsTheValueItself()
    {
        // "hill" gives Constitution and Dexterity as fixed boosts and a flaw in Strength.
        var sheet = AttributeBoostTests.Sheet("hill", 1);
        var evaluation = Engine().Recompute(sheet);
        Assert.Equal(-1L, sheet["str"]);
        Assert.Equal(1L, sheet["con"]);
        Assert.Equal(-1L, Convert.ToInt64(evaluation.Derived["strMod"]));
    }

    [Fact]
    public void RemovingAPickInTheMiddleOfTheChain_ReworksThePartialBoost()
    {
        var sheet = StrengthTo(5);
        Assert.Contains("str", Engine().Recompute(sheet).Attributes!.Boosts!.Halves);

        sheet.Remove("boost__l2__m1");
        var evaluation = Engine().Recompute(sheet);
        Assert.Equal(4L, sheet["str"]);
        Assert.Empty(evaluation.Attributes!.Boosts!.Halves);
    }

    [Fact]
    public void AFlaw_LeavesAPendingPartialBoostStanding()
    {
        // Same ancestry ("hill", flaw in Strength) but its source comes after the level boosts, so the mark is pending when the flaw is applied.
        var ancestry = "        - { key: ancestry, label: Ancestry, field: ancestry }\n";
        var l7 = "        - { key: l7, label: Seven, level: 7, free: 1 }\n";
        Assert.Contains(ancestry, System);
        var late = System.Replace(ancestry, "").Replace(l7, l7 + ancestry);
        var sheet = StrengthTo(5, "hill");
        var evaluation = new SheetRuleEngine(SystemDefinitionLoader.Parse(late), new AttributeBoostTests.Resolver()).Recompute(sheet);
        Assert.Equal(3L, sheet["str"]);
        Assert.Contains("str", evaluation.Attributes!.Boosts!.Halves);
    }

    [Fact]
    public void TheScoreModePartialBoost_StillRunsFromEighteenToNineteenToTwenty()
    {
        string[] str = { "str" };
        var at18 = AttributeBoostTests.Sheet("vale", 20, ("boost__ancestry__vale.b1", str), ("boost__lvl5__m1", str), ("boost__lvl10__m1", str), ("boost__lvl15__m1", str));
        AttributeBoostTests.Engine().Recompute(at18);
        Assert.Equal(18L, at18["str"]);

        var at19 = AttributeBoostTests.Sheet("vale", 20, ("boost__ancestry__vale.b1", str), ("boost__lvl5__m1", str), ("boost__lvl10__m1", str), ("boost__lvl15__m1", str), ("boost__lvl20__m1", str));
        Assert.Contains("str", AttributeBoostTests.Engine().Recompute(at19).Attributes!.Boosts!.Halves);
        Assert.Equal(19L, at19["str"]);
    }

    [Fact]
    public void TheScoreModeIsUnchanged_ADefaultOfTenAndAStepOfTwoPoints()
    {
        var sheet = AttributeBoostTests.Sheet("vale", 1, ("boost__ancestry__vale.b1", new[] { "str" }));
        var evaluation = AttributeBoostTests.Engine().Recompute(sheet);
        Assert.Equal(12L, sheet["str"]);
        Assert.Equal(1L, Convert.ToInt64(evaluation.Derived["strMod"]));

        var half = new AttributeDefinition { Modifier = AttributeDefinition.HalfMinusFiveRule };
        var value = new AttributeDefinition { Modifier = AttributeDefinition.ValueRule };
        Assert.Equal(2, half.ModifierFor(14));
        Assert.Equal(3, value.ModifierFor(3));
        Assert.Null(new AttributeDefinition().ModifierFor(14));
    }
}
