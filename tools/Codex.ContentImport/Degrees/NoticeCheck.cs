using System.Text;
using UglyToad.PdfPig;

namespace Codex.ContentImport.Degrees;

public sealed record NoticeResult(bool Ok, string Message);

/// <summary>
/// Compares the committed credit line with the book's own notice page. The check is whitespace-insensitive, since the credit is broken
/// across lines, columns and pages, and it reports only which segment failed: no book text is ever printed.
/// </summary>
public static class NoticeCheck
{
    public static NoticeResult Run(PdfDocument doc, IReadOnlyList<SpineNode> spine, BookFingerprint book)
    {
        if (book.Notice.Count == 0 || book.Credit.Count == 0)
        {
            return new NoticeResult(false, "notice: the committed notice path or credit line is empty.");
        }

        var node = SpineStage.Find(spine, book.Notice);
        if (node == null || node.PageStart < 1)
        {
            return new NoticeResult(false, "notice: the bookmark path of the notice page is not in this book.");
        }

        var last = Math.Min(doc.NumberOfPages, node.PageStart + 1);
        var text = new StringBuilder();
        for (var p = node.PageStart; p <= last; p++)
        {
            text.Append(PageText(doc, p));
        }

        return Compare(text.ToString(), book.Credit, node.PageStart, last);
    }

    public static NoticeResult Compare(string pageText, IReadOnlyList<string> credit, int firstPage = 0, int lastPage = 0)
    {
        var haystack = Squash(pageText);
        for (var i = 0; i < credit.Count; i++)
        {
            if (!haystack.Contains(Squash(credit[i]), StringComparison.Ordinal))
            {
                return new NoticeResult(false, $"notice: credit segment {i + 1} of {credit.Count} does not match the notice page.");
            }
        }

        return new NoticeResult(true, $"notice: all {credit.Count} credit segment(s) match the notice on page {firstPage}" + (lastPage > firstPage ? $" to {lastPage}." : "."));
    }

    public static string PageText(PdfDocument doc, int page) => string.Concat(doc.GetPage(page).Letters.Select(l => l.Value));

    /// <summary>Drops whitespace and soft hyphens, which are where the extractor and the typesetter differ.</summary>
    public static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '­'));
}
