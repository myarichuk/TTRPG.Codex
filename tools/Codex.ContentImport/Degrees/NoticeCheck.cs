using System.Text;
using UglyToad.PdfPig;

namespace Codex.ContentImport.Degrees;

public sealed record NoticeResult(bool Ok, string Message);

/// <summary>
/// Compares the committed credit line with the book's own notice page. The credit is broken across lines, columns and pages, so the
/// comparison ignores whitespace. It is an equality, not a containment: each segment must be found in order, the credit must start
/// where a sentence starts, and the book's text must not continue the author list after the last segment. Only the failing segment
/// number is reported: no book text is ever printed.
/// </summary>
public static class NoticeCheck
{
    public static NoticeResult Run(PdfDocument doc, IReadOnlyList<SpineNode> spine, BookFingerprint book) =>
        Run(p => PageText(doc, p), doc.NumberOfPages, spine, book);

    public static NoticeResult Run(Func<int, string> pageText, int pageCount, IReadOnlyList<SpineNode> spine, BookFingerprint book)
    {
        if (book.Notice.Count == 0 || book.Credit.Count == 0)
        {
            return new NoticeResult(false, "notice: the committed notice path or credit line is empty.");
        }

        var node = SpineStage.Find(spine, book.Notice);
        if (node == null || node.PageStart < 1 || node.PageStart > pageCount)
        {
            return new NoticeResult(false, "notice: the bookmark path of the notice page is not in this book.");
        }

        var last = Math.Min(pageCount, node.PageStart + 1);
        var text = new StringBuilder();
        for (var p = node.PageStart; p <= last; p++)
        {
            text.Append(pageText(p)).Append('\n');
        }

        return Compare(text.ToString(), book.Credit, node.PageStart, last);
    }

    public static NoticeResult Compare(string pageText, IReadOnlyList<string> credit, int firstPage = 0, int lastPage = 0)
    {
        var (squashed, map) = Squash(pageText);
        var from = 0;
        var firstStart = -1;
        var lastEnd = -1;
        for (var i = 0; i < credit.Count; i++)
        {
            var wanted = Squash(credit[i]).Text;
            var at = wanted.Length == 0 ? -1 : squashed.IndexOf(wanted, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return new NoticeResult(false, $"notice: credit segment {i + 1} of {credit.Count} does not match the notice page.");
            }

            if (i == 0)
            {
                firstStart = map[at];
            }

            from = at + wanted.Length;
            lastEnd = map[from - 1] + 1;
        }

        if (!StartsAtBoundary(pageText, firstStart))
        {
            return new NoticeResult(false, "notice: the credit line does not start where the book's credit starts.");
        }

        if (ContinuesList(pageText, lastEnd))
        {
            return new NoticeResult(false, "notice: the book's credit continues past the committed credit line.");
        }

        return new NoticeResult(true, $"notice: all {credit.Count} credit segment(s) match the notice on page {firstPage}" + (lastPage > firstPage ? $" to {lastPage}." : "."));
    }

    public static string PageText(PdfDocument doc, int page) => string.Concat(doc.GetPage(page).Letters.Select(l => l.Value));

    /// <summary>The text without whitespace and soft hyphens, which are where the extractor and the typesetter differ, and for each kept
    /// character its index in the original.</summary>
    private static (string Text, int[] Map) Squash(string text)
    {
        var kept = new StringBuilder();
        var map = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]) && text[i] != '­')
            {
                kept.Append(text[i]);
                map.Add(i);
            }
        }

        return (kept.ToString(), map.ToArray());
    }

    /// <summary>The credit begins a sentence: whatever precedes it is not a letter, a digit or a list comma.</summary>
    private static bool StartsAtBoundary(string text, int start)
    {
        var i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i < 0 || !(char.IsLetterOrDigit(text[i]) || text[i] is ',' or ';');
    }

    /// <summary>After the last segment, the book carries on with another author (a comma, "and", or a lowercase continuation) instead of ending the credit.</summary>
    private static bool ContinuesList(string text, int end)
    {
        var rest = text[end..].TrimStart();
        if (rest.StartsWith('.'))
        {
            rest = rest[1..].TrimStart();
        }

        return rest.StartsWith(',') || rest.StartsWith(';') || rest.StartsWith("and ", StringComparison.OrdinalIgnoreCase) || (rest.Length > 0 && char.IsLower(rest[0]));
    }
}
