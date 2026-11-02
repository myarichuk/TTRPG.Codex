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
        var checkDie = parsed.CheckDie;
        var kept = new List<int>();
        var dropped = new List<int>();
        int? checkIndex = null;

        foreach (var term in parsed.Dice)
        {
            var rolls = new int[term.Count];
            for (var i = 0; i < term.Count; i++)
            {
                rolls[i] = _random.Next(1, term.Sides + 1);
            }

            if (ReferenceEquals(term, checkDie))
            {
                checkIndex = kept.Count;
            }

            // A subtracted term's dice are recorded negative, so the kept dice and the modifier still add up to the total.
            var (keptHere, droppedHere) = Keep(term, rolls);
            kept.AddRange(keptHere.Select(d => d * term.Sign));
            dropped.AddRange(droppedHere.Select(d => d * term.Sign));
        }

        return new DiceRollResult(expression, kept, dropped, parsed.Modifier, kept.Sum() + parsed.Modifier) { CheckDieIndex = checkIndex };
    }

    private static (List<int> Kept, List<int> Dropped) Keep(DiceTerm term, int[] rolls)
    {
        if (term.KeepMode is null)
        {
            return (rolls.ToList(), new List<int>());
        }

        var keepHighest = term.KeepMode == 'h';
        var indices = Enumerable.Range(0, rolls.Length).ToList();
        var keptIndices = (keepHighest
                ? indices.OrderByDescending(i => rolls[i])
                : indices.OrderBy(i => rolls[i]))
            .Take(term.EffectiveKeepCount)
            .ToHashSet();

        // Report kept/dropped dice in the order they were rolled, not sorted by value - a player
        // reading the log wants to see "rolled 5, 2, 6, dropped the 2," not a re-sorted list.
        return (indices.Where(keptIndices.Contains).Select(i => rolls[i]).ToList(),
            indices.Where(i => !keptIndices.Contains(i)).Select(i => rolls[i]).ToList());
    }
}
