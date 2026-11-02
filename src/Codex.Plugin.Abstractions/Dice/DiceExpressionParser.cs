using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.Plugin.Abstractions.Dice;

/// <summary>One group of dice in an expression: <c>4d6kh3</c>, or <c>-1d4</c> (a penalty die, <see cref="Sign"/> -1).</summary>
public sealed record DiceTerm(int Sign, int Count, int Sides, char? KeepMode, int KeepCount)
{
    /// <summary>How many of <see cref="Count"/> rolled dice are kept: all of them unless
    /// <see cref="KeepMode"/> narrows it (e.g. <c>4d6kh3</c> keeps 3 of 4).</summary>
    public int EffectiveKeepCount => KeepMode is null ? Count : Math.Min(KeepCount, Count);

    /// <summary>A single d20 that counts (<c>1d20</c>, <c>adv</c>, <c>dis</c>, <c>2d20kh1</c>): the die a natural 20 or 1 is read from.</summary>
    public bool IsCheckDie => Sides == 20 && EffectiveKeepCount == 1 && Sign > 0;
}

/// <summary>The parsed, unrolled shape of a dice expression - separated from <see cref="StandardDiceRoller"/>
/// so the grammar can be unit-tested without needing to seed randomness. <see cref="Dice"/> holds every dice term in order,
/// <see cref="Modifier"/> the plain numbers summed; the single-term properties read the first dice term.</summary>
public sealed record ParsedDiceExpression(IReadOnlyList<DiceTerm> Dice, int Modifier)
{
    public int Count => Dice[0].Count;
    public int Sides => Dice[0].Sides;
    public char? KeepMode => Dice[0].KeepMode;
    public int KeepCount => Dice[0].KeepCount;
    public int EffectiveKeepCount => Dice[0].EffectiveKeepCount;

    /// <summary>The one term a natural 20 or 1 is read from, or null when there is none or more than one (<c>1d20+1d20</c>
    /// says nothing about which die is the check).</summary>
    public DiceTerm? CheckDie => Dice.Count(t => t.IsCheckDie) == 1 ? Dice.First(t => t.IsCheckDie) : null;
}

/// <summary>
/// Parses the dice expression grammar the plan calls for (3.6): terms joined by <c>+</c> and <c>-</c>, each dice
/// (<c>2d6</c>, <c>4d6kh3</c> keep-highest-3, <c>klN</c> keep-lowest-N, and <c>adv</c>/<c>dis</c> for "roll 2d20, keep the
/// highest/lowest") or a number. <c>1d8+1d6</c> (sneak attack), <c>2d6+3+2</c> and <c>1d20+5-1d4</c> all parse (R8).
/// </summary>
public static class DiceExpressionParser
{
    private static readonly Regex Term = new(
        @"\G\s*(?<sign>[+-])?\s*(?:(?<prefix>adv|dis)(?![a-z0-9])|(?<count>\d+)?d(?<sides>\d+)(?:k(?<keepMode>h|l)(?<keepCount>\d+))?|(?<number>\d+)(?![d\d]))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxDice = 100;
    private const int MaxSides = 1000;
    private const int MaxModifier = 10_000;
    private const int MaxTerms = 20;

    // Too many digits for an int is the same kind of mistake as too many dice: callers catch FormatException only (D8).
    private static int Number(string digits, string expression) =>
        int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"The number '{digits}' is too large in '{expression}'.");

    public static ParsedDiceExpression Parse(string expression)
    {
        expression ??= string.Empty;
        var dice = new List<DiceTerm>();
        var modifier = 0L;
        var terms = 0;
        var at = 0;
        while (at < expression.Length && expression[at..].Trim().Length > 0)
        {
            var match = Term.Match(expression, at);
            if (!match.Success || match.Length == 0 || (terms > 0 && !match.Groups["sign"].Success))
            {
                throw new FormatException($"Unrecognized dice expression '{expression}'.");
            }

            if (++terms > MaxTerms)
            {
                throw new FormatException($"Too many terms in '{expression}'.");
            }

            var sign = match.Groups["sign"].Value == "-" ? -1 : 1;
            if (match.Groups["number"].Success)
            {
                modifier += sign * (long)Number(match.Groups["number"].Value, expression);
            }
            else
            {
                dice.Add(DiceOf(match, sign, expression));
            }

            at += match.Length;
        }

        if (dice.Count == 0)
        {
            throw new FormatException($"Unrecognized dice expression '{expression}'.");
        }

        if (dice.Sum(t => t.Count) > MaxDice)
        {
            throw new FormatException($"Dice count must be between 1 and {MaxDice} in '{expression}'.");
        }

        if (Math.Abs(modifier) > MaxModifier)
        {
            throw new FormatException($"The modifier must be between -{MaxModifier} and {MaxModifier} in '{expression}'.");
        }

        return new ParsedDiceExpression(dice, (int)modifier);
    }

    private static DiceTerm DiceOf(Match match, int sign, string expression)
    {
        if (match.Groups["prefix"].Success)
        {
            return new DiceTerm(sign, 2, 20, match.Groups["prefix"].Value.Equals("adv", StringComparison.OrdinalIgnoreCase) ? 'h' : 'l', 1);
        }

        var count = match.Groups["count"].Success ? Number(match.Groups["count"].Value, expression) : 1;
        var sides = Number(match.Groups["sides"].Value, expression);
        char? keepMode = match.Groups["keepMode"].Success ? char.ToLowerInvariant(match.Groups["keepMode"].Value[0]) : null;
        var keepCount = keepMode != null ? Number(match.Groups["keepCount"].Value, expression) : 0;

        if (count is <= 0 or > MaxDice)
        {
            throw new FormatException($"Dice count must be between 1 and {MaxDice} in '{expression}'.");
        }

        if (sides is <= 1 or > MaxSides)
        {
            throw new FormatException($"Die sides must be between 2 and {MaxSides} in '{expression}'.");
        }

        if (keepMode != null && keepCount <= 0)
        {
            throw new FormatException($"Keep count must be positive in '{expression}'.");
        }

        return new DiceTerm(sign, count, sides, keepMode, keepCount);
    }
}
