using Codex.Plugin.Abstractions.Dice;

namespace Codex.Tests;

/// <summary>
/// The dice expression grammar (3.6) is pure - no randomness needed to test the parser itself,
/// and <see cref="StandardDiceRoller"/> is exercised here with a seeded <see cref="Random"/> so
/// the keep-highest/lowest split is deterministic rather than "run it a few times and hope."
/// </summary>
public class DiceExpressionTests
{
    [Theory]
    [InlineData("2d6+3", 2, 6, null, 0, 3)]
    [InlineData("d20", 1, 20, null, 0, 0)]
    [InlineData("4d6kh3", 4, 6, 'h', 3, 0)]
    [InlineData("4d6kl1", 4, 6, 'l', 1, 0)]
    [InlineData("1d8-2", 1, 8, null, 0, -2)]
    public void Parse_ExtractsExpectedShape(string expression, int count, int sides, char? keepMode, int keepCount, int modifier)
    {
        var parsed = DiceExpressionParser.Parse(expression);

        Assert.Equal(count, parsed.Count);
        Assert.Equal(sides, parsed.Sides);
        Assert.Equal(keepMode, parsed.KeepMode);
        Assert.Equal(keepCount, parsed.KeepCount);
        Assert.Equal(modifier, parsed.Modifier);
    }

    [Theory]
    [InlineData("adv", 2, 20, 'h', 1)]
    [InlineData("dis", 2, 20, 'l', 1)]
    [InlineData("adv+5", 2, 20, 'h', 1)]
    public void Parse_AdvantageDisadvantageShorthand_ExpandsToD20Pair(string expression, int count, int sides, char keepMode, int keepCount)
    {
        var parsed = DiceExpressionParser.Parse(expression);

        Assert.Equal(count, parsed.Count);
        Assert.Equal(sides, parsed.Sides);
        Assert.Equal(keepMode, parsed.KeepMode);
        Assert.Equal(keepCount, parsed.KeepCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not dice")]
    [InlineData("0d6")]
    [InlineData("d1")]
    [InlineData("101d6")]
    public void Parse_RejectsInvalidOrOutOfRangeExpressions(string expression)
    {
        Assert.Throws<FormatException>(() => DiceExpressionParser.Parse(expression));
    }

    [Fact]
    public void Roll_WithoutKeepModifier_SumsEveryDie()
    {
        // Random.Next(1, 7) with this seed is deterministic across runs on the same runtime.
        var roller = new StandardDiceRoller(new Random(42));
        var result = roller.Roll("3d6+1");

        Assert.Equal(3, result.Kept.Count);
        Assert.Empty(result.Dropped);
        Assert.Equal(result.Kept.Sum() + 1, result.Total);
    }

    [Fact]
    public void Roll_KeepHighest_DropsTheLowestRolls()
    {
        var roller = new StandardDiceRoller(new Random(1));
        var result = roller.Roll("4d6kh3");

        Assert.Equal(3, result.Kept.Count);
        Assert.Single(result.Dropped);
        Assert.True(result.Dropped[0] <= result.Kept.Min(),
            "the dropped die must not be higher than any kept die when keeping the highest 3 of 4");
        Assert.Equal(result.Kept.Sum(), result.Total);
    }

    [Fact]
    public void Roll_Advantage_KeepsTheHigherOfTwoD20s()
    {
        var roller = new StandardDiceRoller(new Random(7));
        var result = roller.Roll("adv+2");

        Assert.Single(result.Kept);
        Assert.Single(result.Dropped);
        Assert.True(result.Kept[0] >= result.Dropped[0]);
        Assert.Equal(result.Kept[0] + 2, result.Total);
    }
}
