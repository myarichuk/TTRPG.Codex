namespace Codex.Plugin.Abstractions.Dice;

/// <summary>
/// The default <see cref="IDiceRoller"/> for any system that doesn't need something stranger
/// than "sum some dice and add a modifier" (3.6) - which is every system except SWFFG's
/// narrative dice pool.
/// </summary>
public sealed class StandardDiceRoller(Random? random = null) : IDiceRoller
{
    public static readonly StandardDiceRoller Instance = new();

    private readonly Random _random = random ?? Random.Shared;

    public DiceRollResult Roll(string expression)
    {
        var parsed = DiceExpressionParser.Parse(expression);

        var rolls = new int[parsed.Count];
        for (var i = 0; i < parsed.Count; i++)
        {
            rolls[i] = _random.Next(1, parsed.Sides + 1);
        }

        if (parsed.KeepMode is null)
        {
            return new DiceRollResult(expression, rolls, Array.Empty<int>(), parsed.Modifier, rolls.Sum() + parsed.Modifier);
        }

        var keepHighest = parsed.KeepMode == 'h';
        var indices = Enumerable.Range(0, rolls.Length).ToList();
        var keptIndices = (keepHighest
                ? indices.OrderByDescending(i => rolls[i])
                : indices.OrderBy(i => rolls[i]))
            .Take(parsed.EffectiveKeepCount)
            .ToHashSet();

        // Report kept/dropped dice in the order they were rolled, not sorted by value - a player
        // reading the log wants to see "rolled 5, 2, 6, dropped the 2," not a re-sorted list.
        var kept = indices.Where(keptIndices.Contains).Select(i => rolls[i]).ToList();
        var dropped = indices.Where(i => !keptIndices.Contains(i)).Select(i => rolls[i]).ToList();

        return new DiceRollResult(expression, kept, dropped, parsed.Modifier, kept.Sum() + parsed.Modifier);
    }
}
