using System.Text.RegularExpressions;

namespace Codex.Plugin.Abstractions.Dice;

/// <summary>The parsed, unrolled shape of a dice expression - separated from <see cref="StandardDiceRoller"/>
/// so the grammar can be unit-tested without needing to seed randomness.</summary>
public sealed record ParsedDiceExpression(int Count, int Sides, char? KeepMode, int KeepCount, int Modifier)
{
    /// <summary>How many of <see cref="Count"/> rolled dice are kept: all of them unless
    /// <see cref="KeepMode"/> narrows it (e.g. <c>4d6kh3</c> keeps 3 of 4).</summary>
    public int EffectiveKeepCount => KeepMode is null ? Count : Math.Min(KeepCount, Count);
}

/// <summary>
/// Parses the dice expression grammar the plan calls for (3.6): <c>2d6+3</c>, <c>4d6kh3</c>
/// (keep-highest-3), <c>klN</c> (keep-lowest-N), and the <c>adv</c>/<c>dis</c> shorthand for
/// "roll 2d20, keep the highest/lowest" that a d20 game's advantage/disadvantage mechanic needs.
/// </summary>
public static class DiceExpressionParser
{
    private static readonly Regex Pattern = new(
        @"^\s*(?:(?<prefix>adv|dis)|(?<count>\d+)?d(?<sides>\d+)(?:k(?<keepMode>h|l)(?<keepCount>\d+))?)\s*(?<modifier>[+-]\s*\d+)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxDice = 100;
    private const int MaxSides = 1000;

    public static ParsedDiceExpression Parse(string expression)
    {
        var match = Pattern.Match(expression);
        if (!match.Success)
        {
            throw new FormatException($"Unrecognized dice expression '{expression}'.");
        }

        int count, sides;
        char? keepMode;
        var keepCount = 0;

        if (match.Groups["prefix"].Success)
        {
            count = 2;
            sides = 20;
            keepMode = match.Groups["prefix"].Value.Equals("adv", StringComparison.OrdinalIgnoreCase) ? 'h' : 'l';
            keepCount = 1;
        }
        else
        {
            count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1;
            sides = int.Parse(match.Groups["sides"].Value);
            if (match.Groups["keepMode"].Success)
            {
                keepMode = char.ToLowerInvariant(match.Groups["keepMode"].Value[0]);
                keepCount = int.Parse(match.Groups["keepCount"].Value);
            }
            else
            {
                keepMode = null;
            }
        }

        if (count is <= 0 or > MaxDice)
        {
            throw new FormatException($"Dice count must be between 1 and {MaxDice} in '{expression}'.");
        }

        if (sides is <= 1 or > MaxSides)
        {
            throw new FormatException($"Die sides must be between 2 and {MaxSides} in '{expression}'.");
        }

        if (keepMode != null && keepCount is <= 0)
        {
            throw new FormatException($"Keep count must be positive in '{expression}'.");
        }

        var modifier = 0;
        if (match.Groups["modifier"].Success)
        {
            modifier = int.Parse(match.Groups["modifier"].Value.Replace(" ", string.Empty));
        }

        return new ParsedDiceExpression(count, sides, keepMode, keepCount, modifier);
    }
}
