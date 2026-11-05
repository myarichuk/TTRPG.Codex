using System.Globalization;
using Codex.Core.AI;

namespace Codex.Core.Book;

/// <summary>
/// A guess at how many tokens a text is, for the budget and the pre-flight. Only ever a guess: providers tokenise differently and
/// some report nothing, so everything built on it is labelled an estimate.
/// <para>The weights were measured, not assumed, against the usage Anthropic's API reported (its tokenizer) on single-script
/// prose and on Markdown-heavy text (2,000-2,700 characters each; the numbers are pinned in <c>BookTokensTests</c>). They corrected two
/// guesses of mine: Cyrillic costs only about a fifth more per letter than Latin there, not two to three times; and punctuation, digits and
/// markup cost about one token or more <i>each</i>, which a letters-only count missed by 2x on a stat-block-like text. Hebrew costs about
/// twice a Latin letter. Another provider's tokenizer will differ (OpenAI's count Cyrillic and Hebrew higher): not measured, so a budget
/// on such a provider is a rougher estimate than the range says.</para>
/// </summary>
public static class BookTokens
{
    /// <summary>Tokens per character, by class. A space costs little because it merges into the word after it.</summary>
    private const double Latin = 0.39, Cyrillic = 0.47, Hebrew = 0.78, Cjk = 1.1, OtherLetter = 0.7, Mark = 1.2, Space = 0.05;

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
        if (char.IsWhiteSpace(c)) return Space;
        if (c is >= '\u0300' and <= '\u036F' or >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E') return 0;
        if (c is >= '\u0590' and <= '\u05FF') return c is >= '\u05B0' and <= '\u05C7' ? 0.3 : Hebrew; // niqqud marks are cheap
        if (!char.IsLetter(c)) return Mark;
        if (c < 0x250) return Latin;
        if (c is >= '\u0400' and <= '\u052F') return Cyrillic;
        if (c is >= '\u3040' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF') return Cjk;
        return OtherLetter;
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
    public long WorstCaseTokens => InputHigh * 2 + OutputHigh * 3;

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
            // The upper figure: every chapter tried twice, the second try carrying the first reply back as well.
            costHigh = AiPrice.Cost(price.InputPerMTok, price.OutputPerMTok, inHigh * 2 + outHigh, outHigh * 2);
        }

        return new BookPreflight(inLow, inHigh, outLow, outHigh, min, maxRequests, costLow, costHigh);
    }
}
