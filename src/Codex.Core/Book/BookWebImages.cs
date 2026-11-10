using System.Net;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>
/// The web addresses a draft's chapters name as pictures (docs/BOOK_EXPORT.md 41.25): <c>![alt](https://…)</c>, Homebrewery's form with a
/// trailing <c>{style}</c>, and <c>&lt;img src="https://…"&gt;</c>. A brew written for Homebrewery keeps its art on imgur or a CDN; the page
/// fetches nothing, so the server fetches each of these once and keeps a copy with the draft (<see cref="BookCssWeb"/>), as it does for the
/// addresses the stylesheet names. The renderer asks for a copy by the same canonical address, so what the server fetches and what the page
/// looks for are one thing.
/// <para>This reads the text, not the rendered page, so it is a little wider than the renderer in places (an image inside a code block is
/// fetched and never shown) and narrower in others (a reference-style <c>![alt][id]</c> is not found, and prints as an empty box). A picture
/// the renderer wants and has no copy of prints as that empty box; a copy no picture wants is let go at the next fetch.</para>
/// </summary>
public static partial class BookWebImages
{
    /// <summary>The most addresses read from one draft: every copy a draft keeps is bounded anyway (<see cref="BookCssWeb.MaxFiles"/>).</summary>
    public const int MaxAddresses = 100;

    // The address ends at white space or the closing parenthesis; one level of balanced parentheses is part of it (a wiki address).
    [GeneratedRegex(@"!\[[^\]\r\n]*\]\(\s*(?:<(?<u>[^>\s]+)>|(?<u>(?:[^\s()]|\([^\s()]*\))+))", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"<img\b[^>]*?\ssrc\s*=\s*(?:""(?<u>[^""\r\n]*)""|'(?<u>[^'\r\n]*)')", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex HtmlImage();

    /// <summary>The picture addresses in <paramref name="chapters"/>, canonical (<see cref="BookCssWeb.Address"/>), each once, in the order they
    /// first appear. Only https; an <c>art:</c> slot, a data URL or a relative address is not one.</summary>
    public static IReadOnlyList<string> Addresses(IEnumerable<string?> chapters)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in chapters)
        {
            if (string.IsNullOrEmpty(text)) continue;
            // Position order across both forms, so the order is the text's.
            foreach (var raw in Matches(text).OrderBy(m => m.Index).Select(m => m.Value))
            {
                if (BookCssWeb.Address(WebUtility.HtmlDecode(raw), null) is not { } address || !seen.Add(address)) continue;
                found.Add(address);
                if (found.Count >= MaxAddresses) return found;
            }
        }

        return found;
    }

    public static IReadOnlyList<string> Addresses(string? chapter) => Addresses([chapter]);

    private static IEnumerable<(int Index, string Value)> Matches(string text)
    {
        // A text with no "://" names no address: the common case costs nothing.
        if (!text.Contains("://", StringComparison.Ordinal)) yield break;
        foreach (Match m in MarkdownImage().Matches(text)) yield return (m.Index, m.Groups["u"].Value);
        foreach (Match m in HtmlImage().Matches(text)) yield return (m.Index, m.Groups["u"].Value);
    }
}
