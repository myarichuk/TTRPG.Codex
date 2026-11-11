using System.Globalization;

namespace Codex.Core.AI;

/// <summary>The prices in force for a call, as the user entered them (either may be missing).</summary>
/// <param name="CachedInputPerMTok">What input read back from the provider's prompt cache costs per million tokens, when the user entered it;
/// without it such tokens are counted at the input price.</param>
public sealed record AiPriceQuote(decimal? InputPerMTok, decimal? OutputPerMTok, string Label, decimal? CachedInputPerMTok = null);

/// <summary>What one picture costs, as the user entered it.</summary>
public sealed record AiImagePrice(decimal PerImage, string Label);

/// <summary>
/// What a call cost, from prices the user typed in. The app ships no price table: prices change, and the endpoint can be
/// anything that speaks the OpenAI protocol, so the only honest source is the person paying. With no price the answer is
/// "unknown", never zero.
/// </summary>
public static class AiPrice
{
    /// <summary>The most a price field accepts (per million tokens); far above any real model, small enough that the sums cannot overflow.</summary>
    public const decimal MaxPerMTok = 100_000m;

    /// <summary>The most a price per picture accepts; far above any real model.</summary>
    public const decimal MaxPerImage = 10_000m;

    public const int MaxLabelLength = 12;

    /// <summary>Null unless a price is a usable number: not negative, not above <see cref="MaxPerMTok"/>.</summary>
    public static decimal? Clean(decimal? price) =>
        price is { } p && p >= 0 && p <= MaxPerMTok ? p : null;

    /// <summary>Null unless a price per picture is a usable number: not negative, not above <see cref="MaxPerImage"/>.</summary>
    public static decimal? CleanImage(decimal? price) =>
        price is { } p && p >= 0 && p <= MaxPerImage ? p : null;

    /// <summary>The cost of the tokens, or null when it cannot be known: a price is missing, or a count is.
    /// A model that is free (both prices 0) costs 0, which is a fact the user stated.</summary>
    public static decimal? Cost(decimal? inputPerMTok, decimal? outputPerMTok, long? inputTokens, long? outputTokens) =>
        Cost(inputPerMTok, outputPerMTok, null, inputTokens, outputTokens, null, null);

    /// <summary>What writing a prompt into Anthropic's 5-minute cache costs, as a multiple of the input price. A fact of that
    /// provider's price list rather than something a user would type, and it only comes into play when the provider reports cache
    /// writes, which no other one does.</summary>
    public const decimal CacheWriteMultiplier = 1.25m;

    /// <summary>The cost when some of the input came from the provider's prompt cache. <paramref name="inputTokens"/> is the whole
    /// input, as providers (and the client libraries) report it: the cache reads and writes are part of it, not extra. Reads are
    /// counted at <paramref name="cachedInputPerMTok"/>, or at the input price when the user gave none (the same answer as before
    /// caching, so never less than the truth); writes at <see cref="CacheWriteMultiplier"/> times the input price.</summary>
    public static decimal? Cost(decimal? inputPerMTok, decimal? outputPerMTok, decimal? cachedInputPerMTok, long? inputTokens, long? outputTokens, long? cacheReadTokens, long? cacheWriteTokens)
    {
        if (Clean(inputPerMTok) is not { } input || Clean(outputPerMTok) is not { } output || inputTokens is not { } tin || outputTokens is not { } tout)
        {
            return null;
        }

        var total = Math.Max(0, tin);
        var read = Math.Clamp(cacheReadTokens ?? 0, 0, total);
        var written = Math.Clamp(cacheWriteTokens ?? 0, 0, total - read);
        var readPrice = Clean(cachedInputPerMTok) ?? input;
        return ((total - read - written) * input + read * readPrice + written * input * CacheWriteMultiplier + Math.Max(0, tout) * output) / 1_000_000m;
    }

    /// <summary>The label with control and format characters removed, cut to <see cref="MaxLabelLength"/> text elements.</summary>
    public static string CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var kept = new System.Text.StringBuilder();
        foreach (var c in label.Trim())
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse)
            {
                continue;
            }

            kept.Append(c);
        }

        var text = kept.ToString().Trim();
        var elements = StringInfo.ParseCombiningCharacters(text);
        return elements.Length <= MaxLabelLength ? text : text[..elements[MaxLabelLength]];
    }

    /// <summary>"0.0123 USD" in the reader's culture: more digits for small sums, so a cent's fraction is not shown as 0.</summary>
    public static string Format(decimal amount, string? label, IFormatProvider culture)
    {
        var digits = amount == 0m ? 2 : amount < 0.01m ? 4 : amount < 1m ? 3 : 2;
        var number = amount.ToString("N" + digits, culture);
        return string.IsNullOrWhiteSpace(label) ? number : number + " " + label.Trim();
    }
}
