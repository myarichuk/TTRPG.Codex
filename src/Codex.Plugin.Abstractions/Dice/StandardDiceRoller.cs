namespace Codex.Plugin.Abstractions.Dice;

/// <summary>
/// The default <see cref="IDiceRoller"/> for any system that doesn't need something stranger
/// than "sum some dice and add a modifier" (3.6) - which is every system except SWFFG's
/// narrative dice pool. Its faces come from an <see cref="IDiceSource"/>.
/// </summary>
public sealed class StandardDiceRoller : IDiceRoller
{
    public static readonly StandardDiceRoller Instance = new();

    /// <summary>How many dice one exploding term may add: a d2 that keeps exploding must stop somewhere.</summary>
    public const int MaxExplosions = 100;

    private readonly IDiceSource _source;

    public StandardDiceRoller(IDiceSource source) => _source = source;

    public StandardDiceRoller(Random? random = null) : this(random == null ? RandomDiceSource.Shared : new RandomDiceSource(random))
    {
    }

    public DiceRollResult Roll(string expression)
    {
        var parsed = DiceExpressionParser.Parse(expression);
        var checkDie = parsed.CheckDie;
        var kept = new List<int>();
        var dropped = new List<int>();
        int? checkIndex = null;
        int? successes = null;

        foreach (var term in parsed.Dice)
        {
            var rolls = new List<int>(term.Count);
            for (var i = 0; i < term.Count; i++)
            {
                rolls.Add(_source.Roll(term.Sides));
            }

            // Each die showing its highest face adds another, which may explode in turn; the added dice join the term.
            if (term.Explodes)
            {
                var added = 0;
                for (var i = 0; i < rolls.Count && added < MaxExplosions; i++)
                {
                    if (rolls[i] != term.Sides) continue;
                    rolls.Add(_source.Roll(term.Sides));
                    added++;
                }
            }

            if (ReferenceEquals(term, checkDie))
            {
                checkIndex = kept.Count;
            }

            // A subtracted term's dice are recorded negative, so the kept dice and the modifier still add up to the total.
            var (keptHere, droppedHere) = Keep(term, rolls);
            if (term.SuccessAt is { } at)
            {
                successes = (successes ?? 0) + term.Sign * keptHere.Count(d => d >= at);
            }

            kept.AddRange(keptHere.Select(d => d * term.Sign));
            dropped.AddRange(droppedHere.Select(d => d * term.Sign));
        }

        var total = (successes ?? kept.Sum()) + parsed.Modifier;
        return new DiceRollResult(expression, kept, dropped, parsed.Modifier, total) { CheckDieIndex = checkIndex, Successes = successes };
    }

    private static (List<int> Kept, List<int> Dropped) Keep(DiceTerm term, List<int> rolls)
    {
        if (term.KeepMode is null)
        {
            return (rolls.ToList(), new List<int>());
        }

        var keepHighest = term.KeepMode == 'h';
        var indices = Enumerable.Range(0, rolls.Count).ToList();
        var keptIndices = (keepHighest
                ? indices.OrderByDescending(i => rolls[i])
                : indices.OrderBy(i => rolls[i]))
            .Take(Math.Min(term.KeepCount, rolls.Count))
            .ToHashSet();

        // Report kept/dropped dice in the order they were rolled, not sorted by value - a player
        // reading the log wants to see "rolled 5, 2, 6, dropped the 2," not a re-sorted list.
        return (indices.Where(keptIndices.Contains).Select(i => rolls[i]).ToList(),
            indices.Where(i => !keptIndices.Contains(i)).Select(i => rolls[i]).ToList());
    }
}
