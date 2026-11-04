using Codex.Core.Book;
using UglyToad.PdfPig;

namespace Codex.Web.Book;

/// <summary>
/// Reads where the headings of a printed book landed, from the outline the browser writes into the PDF, so a table of contents can
/// carry page numbers. The browser cannot do it itself in CSS (<c>target-counter()</c> is not supported) and the page runs no script, so
/// the book is printed once, the pages are read back, and the page is printed again with the numbers in.
/// </summary>
public static class BookPages
{
    /// <summary>Heading id to the page number printed in the margin (the PDF's own page index, which is what <c>counter(page)</c> shows).
    /// A heading the outline does not hold is left out, so the table of contents shows no number rather than a wrong one.</summary>
    public static IReadOnlyDictionary<string, int> Map(byte[] pdf, IReadOnlyList<BookHeading> headings)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var document = PdfDocument.Open(pdf);
            if (!document.TryGetBookmarks(out var bookmarks))
            {
                return result;
            }

            var entries = bookmarks.GetNodes().OfType<UglyToad.PdfPig.Outline.DocumentBookmarkNode>().Select(n => (Title: Normalize(n.Title), n.PageNumber)).ToList();

            // Headings and outline entries are both in document order, and the outline also holds headings the book does not list
            // (its title, "Contents"): walk the outline once, taking an entry only for the next heading it matches.
            var next = 0;
            foreach (var heading in headings)
            {
                var wanted = Normalize(heading.Text);
                for (var i = next; i < entries.Count; i++)
                {
                    if (string.Equals(entries[i].Title, wanted, StringComparison.Ordinal))
                    {
                        result[heading.Id] = entries[i].PageNumber;
                        next = i + 1;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException)
        {
            // A PDF the reader cannot make sense of prints a book without numbers; it does not fail the book.
            result.Clear();
        }

        return result;
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Normalize(System.Text.NormalizationForm.FormC);
}
