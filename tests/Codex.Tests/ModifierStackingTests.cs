using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The stacking rules every typed modifier goes through (<see cref="ModifierStacking"/>): a sheet kind's values and 5e's
/// armor class alike.</summary>
public class ModifierStackingTests
{
    private static readonly ModifierTypesDefinition Types = new()
    {
        Types = new Dictionary<string, string> { ["armor"] = "highest", ["base"] = "best", ["luck"] = "lowest" }
    };

    private static IReadOnlyList<(string Item, bool Counted)> Stack(params (string Type, double Amount, string Item)[] modifiers) =>
        ModifierStacking.Stack(modifiers, Types).Select(m => (m.Item, m.Counted)).ToList();

    [Fact]
    public void Sum_CountsEvery_Highest_TheLargestBonusAndPenaltyApart_Lowest_TheSmallest()
    {
        Assert.Equal(new[] { ("a", true), ("b", true) }, Stack(("untyped", 1, "a"), ("untyped", 2, "b")));
        Assert.Equal(new[] { ("shield", false), ("mail", true), ("rust", true) }, Stack(("armor", 2, "shield"), ("armor", 5, "mail"), ("armor", -1, "rust")));
        Assert.Equal(new[] { ("x", false), ("y", true) }, Stack(("luck", 3, "x"), ("luck", 1, "y")));
    }

    [Fact]
    public void Best_CountsExactlyOne_TheGreatest_PenaltiesIncluded_AndATieGoesToTheFirst()
    {
        Assert.Equal(new[] { ("unarmored", false), ("monk", true), ("dex", false) }, Stack(("base", 12, "unarmored"), ("base", 15, "monk"), ("base", 15, "dex")));
        Assert.Equal(new[] { ("cursed", true), ("worse", false) }, Stack(("base", -1, "cursed"), ("base", -3, "worse")));
    }

    [Fact]
    public void TwoEqualBonusesOfAHighestType_AreOneThatCountsAndOneThatDoesNot()
    {
        Assert.Equal(new[] { ("ring", true), ("ring", false) }, Stack(("armor", 2, "ring"), ("armor", 2, "ring")));
    }

    [Fact]
    public void ASheetKind_MayChooseItsBaseAsTheBestCandidate()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: best-test
            name: Best test
            modifierTypes: { types: { base: best } }
            sheetKinds:
              - kind: hero
                values:
                  - { key: dex, input: true, default: 3 }
                  - { key: AC, formula: "0", modifiers: AC }
                modifiers:
                  - { target: AC, amount: "10 + dex", type: base, source: Unarmored }
                  - { target: AC, amount: 14, type: base, source: Scale mail }
                  - { target: AC, amount: 2, source: Shield }
            """);
        var result = new SheetModel(system, NullEntryResolver.Instance, new Dictionary<string, object>()).Evaluate();

        Assert.Equal(16d, result.Values["AC"]);
        Assert.Equal(new[] { ("Unarmored", false), ("Scale mail", true), ("Shield", true) }, result.Modifiers["AC"].Select(m => (m.Source!, m.Counted)));
    }
}
