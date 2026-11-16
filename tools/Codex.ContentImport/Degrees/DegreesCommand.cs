using System.Text.Json;
using UglyToad.PdfPig;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// The <c>degrees</c> command: the base of a local-only importer. It writes no pack output; stages 1 to 3 leave JSON in the work folder,
/// outside the git work tree, for a reviewer and for the chapter parsers that come later.
/// </summary>
public static class DegreesCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var options = DegreesOptions.Parse(args);
        if (options == null)
        {
            error.WriteLine(DegreesOptions.Usage);
            return 2;
        }

        try
        {
            PathGuard.EnsureOutsideGitTree(options.Pdf, "--pdf");
            PathGuard.EnsureOutsideGitTree(options.Work, "--work");
            if (!File.Exists(options.Pdf))
            {
                throw new FileNotFoundException("--pdf does not name a file.");
            }

            var config = BooksConfig.Embedded();
            var book = config.Books.GetValueOrDefault(options.Book) ?? throw new InvalidOperationException($"No fingerprint is committed for book {options.Book}.");
            using var doc = PdfDocument.Open(options.Pdf);
            var spine = SpineStage.Run(doc);

            var fingerprint = CheckFingerprint(doc, spine, book, output);
            if (!fingerprint)
            {
                error.WriteLine("different printing; re-review every chapter");
                return 1;
            }

            if (options.Action == "fingerprint")
            {
                return 0;
            }

            if (options.Action == "notice")
            {
                return ReportNotice(NoticeCheck.Run(doc, spine, book), output, error);
            }

            Directory.CreateDirectory(options.Work);
            Write(options.Work, "spine.json", spine);
            output.WriteLine($"spine: {spine.Count} bookmarks, {spine.Count(n => n.Sidebar)} sidebar bookmarks");

            var layout = LayoutStage.Run(doc, FontRoles.Embedded());
            Write(options.Work, "layout.json", layout.Pages);
            output.WriteLine($"layout: {layout.Pages.Sum(p => p.Words.Count)} words, {layout.Letters} letters; letters by role: " +
                string.Join(", ", layout.LettersByRole.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}")));
            if (layout.Unclassified.Count > 0)
            {
                foreach (var u in layout.Unclassified)
                {
                    error.WriteLine($"unclassified font: {u.Font} {u.Size}pt, {u.Letters} letters, first pages {string.Join(",", u.FirstPages)}");
                }

                error.WriteLine($"{layout.Unclassified.Sum(u => u.Letters)} letters have no font role; add rules to degrees-fonts.yaml.");
                return 1;
            }

            var blocks = BlocksStage.Run(layout.Pages);
            Write(options.Work, "blocks.json", blocks.Blocks);
            output.WriteLine($"blocks: {blocks.Blocks.Count} blocks ({string.Join(", ", blocks.BlocksByRole.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"))}); " +
                $"{blocks.TwoColumnPages} two-column pages, {blocks.OneColumnPages} one-column pages, {blocks.FurnitureWords} furniture words dropped");

            return ReportNotice(NoticeCheck.Run(doc, spine, book), output, error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool CheckFingerprint(PdfDocument doc, IReadOnlyList<SpineNode> spine, BookFingerprint book, TextWriter output)
    {
        var counts = SpineStage.CountsByDepth(spine);
        var ok = doc.NumberOfPages == book.Pages && counts.SequenceEqual(book.Bookmarks);
        output.WriteLine($"fingerprint: {doc.NumberOfPages} pages, bookmarks per depth [{string.Join(", ", counts)}]; " + (ok ? "matches printing " + book.Printing : "does not match"));
        return ok;
    }

    private static int ReportNotice(NoticeResult result, TextWriter output, TextWriter error)
    {
        (result.Ok ? output : error).WriteLine(result.Message);
        return result.Ok ? 0 : 1;
    }

    private static void Write<T>(string folder, string name, T value) => File.WriteAllText(Path.Combine(folder, name), JsonSerializer.Serialize(value, JsonOptions));
}
