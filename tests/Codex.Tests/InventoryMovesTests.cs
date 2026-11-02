using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

public class InventoryMovesTests
{
    private static readonly InventoryRules Rules = new();

    [Fact]
    public void Take_LooseOnesFirst_KeepsTheEquippedOneInHand()
    {
        var raw = new List<string> { "dagger|equipped:1", "dagger|qty:2", "rope" };

        Assert.True(InventoryMoves.Take(raw, Rules, "dagger", 2));

        Assert.Equal(new[] { "dagger|equipped:1", "rope" }, raw);
    }

    [Fact]
    public void Take_PartOfALine_LeavesTheRestWithItsState()
    {
        var raw = new List<string> { "arrow|qty:20,equipped:1" };

        Assert.True(InventoryMoves.Take(raw, Rules, "ARROW", 5));

        Assert.Equal(new[] { SheetRuleEngine.FormatItem(Rules, "arrow", 15, true) }, raw);
    }

    [Fact]
    public void Take_MoreThanCarried_ChangesNothing()
    {
        var raw = new List<string> { "potion|qty:2" };

        Assert.False(InventoryMoves.Take(raw, Rules, "potion", 3));
        Assert.False(InventoryMoves.Take(raw, Rules, "potion", 0));
        Assert.False(InventoryMoves.Take(raw, Rules, "sword", 1));

        Assert.Equal(new[] { "potion|qty:2" }, raw);
    }

    [Fact]
    public void Add_JoinsALooseLine_NeverAWornOne()
    {
        var raw = new List<string> { "dagger|equipped:1" };

        InventoryMoves.Add(raw, Rules, "dagger", 1);
        InventoryMoves.Add(raw, Rules, "dagger", 2);

        Assert.Equal(new[] { "dagger|equipped:1", SheetRuleEngine.FormatItem(Rules, "dagger", 3, false) }, raw);
        Assert.Equal(4, InventoryMoves.Count(raw, Rules, "dagger"));
    }

    [Fact]
    public void Count_UsesTheSystemsOwnQuantityKey()
    {
        var rules = new InventoryRules { QuantityChoice = "n" };

        Assert.Equal(5, InventoryMoves.Count(new[] { "gem|n:3", "gem|n:2", "coin|n:9" }, rules, "gem"));
    }
}
