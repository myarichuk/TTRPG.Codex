using System.Globalization;
using Codex.Core.AI;

namespace Codex.Core.Book;

/// <summary>
/// A guess at how many tokens a text is, for the budget and the pre-flight. Only ever a guess: providers tokenise differently and
/// some report nothing, so everything built on it is labelled an estimate. The factor depends on the script, because "four
/// characters a token" holds for English and under-counts Cyrillic and Hebrew by two to three times, which is exactly the books
/// this app is for.
/// </summary>
public static class BookTokens
{
    /// <summary>Tokens per character, by script. Deliberately on the high side: an estimate that is too low is a budget that is overspent.</summary>
    private const double Latin = 0.28, Cyrillic = 0.55, Hebrew = 0.6, Cjk = 1.1, Other = 0.6, Space = 0.12;

    /// <summary>The range the real count is expected to fall in, as multiples of <see cref="Estimate"/>.</summary>
    public const double LowFactor = 0.7, HighFactor = 1.4;

    public static long Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        double sum = 0;
        foreach (var c in text)
        {
            sum += Weight(c);
        }

        return (long)Math.Ceiling(sum);
    }

    private static double Weight(char c)
    {
        if (c < 0x80)
        {
            return char.IsWhiteSpace(c) ? Space : Latin;
        }

        if (c is >= 'Ѐ' and <= 'ԯ') return Cyrillic;
        if (c is >= '֐' and <= '׿') return Hebrew;
        if (c is >= '぀' and <= '鿿' or >= '가' and <= '힯') return Cjk;
        if (c is >= '̀' and <= 'ͯ' or >= '​' and <= '‏' or >= '‪' and <= '‮') return 0;
        return char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? Space : Other;
    }

    /// <summary>The estimate as a range, never below 1 for a non-empty text.</summary>
    public static (long Low, long High) Range(long estimate) =>
        estimate <= 0 ? (0, 0) : (Math.Max(1, (long)Math.Floor(estimate * LowFactor)), (long)Math.Ceiling(estimate * HighFactor));
}

/// <summary>One section's share of a build, as the pre-flight sees it: how much goes in and how much the reply may be at most.</summary>
public sealed record BookSectionLoad(string Title, long InputTokens, long MaxOutputTokens);

/// <summary>What a build might cost, before it starts: ranges, never one number, and the requests it will make.</summary>
public sealed record BookPreflight(long InputLow, long InputHigh, long OutputLow, long OutputHigh, int RequestsMin, int RequestsMax, decimal? CostLow, decimal? CostHigh)
{
    /// <summary>The most tokens the build can use if every section is retried once: what the cap must cover to finish.</summary>
    public long WorstCaseTokens => InputHigh * 2 + OutputHigh * 2;

    /// <summary>The estimate before a build: the sections, a style sheet resent with each call, and the prices if there are any.</summary>
    public static BookPreflight Estimate(IReadOnlyList<BookSectionLoad> sections, long stylePromptTokens, AiPriceQuote? price)
    {
        long inLow = 0, inHigh = 0, outLow = 0, outHigh = 0;
        foreach (var s in sections)
        {
            var input = Math.Max(0, s.InputTokens) + Math.Max(0, stylePromptTokens);
            var (low, high) = BookTokens.Range(input);
            inLow += low;
            inHigh += high;
            // A reply is rarely the longest it may be, and never longer.
            var max = Math.Max(0, s.MaxOutputTokens);
            outLow += max / 4;
            outHigh += max;
        }

        // At most one retry per section (the checks' second chance); a section that fails twice falls back to the deterministic text.
        var min = sections.Count;
        var maxRequests = sections.Count * 2;
        decimal? costLow = null, costHigh = null;
        if (price != null)
        {
            costLow = AiPrice.Cost(price.InputPerMTok, price.OutputPerMTok, inLow, outLow);
            costHigh = AiPrice.Cost(price.InputPerMTok, price.OutputPerMTok, inHigh * 2, outHigh * 2);
        }

        return new BookPreflight(inLow, inHigh, outLow, outHigh, min, maxRequests, costLow, costHigh);
    }
}
