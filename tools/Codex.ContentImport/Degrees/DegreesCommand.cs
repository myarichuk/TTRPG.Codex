using System.Text.Json;
using UglyToad.PdfPig;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// The <c>degrees</c> command: the base of a local-only importer. It writes no pack output; stages 1 to 3 leave JSON in the work folder,
/// outside the git work tree, for a reviewer and for the chapter parsers that come later.
/// Everything printed is a message written here, a count or a position. A file name, a path, a library exception message or book
/// text never reaches the output: an unexpected failure prints the exception type only.
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
                throw new DegreesException("--pdf does not name a file.");
            }

            var config = BooksConfig.Embedded();
            var book = config.Books.GetValueOrDefault(options.Book) ?? throw new DegreesException($"No fingerprint is committed for book {options.Book}.");
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(options.Pdf);
            }
            catch (Exception)
            {
                throw new DegreesException("--pdf could not be read as a PDF.");
            }

            using (doc)
            {
                var spine = SpineStage.Run(doc);
                if (!CheckFingerprint(doc, spine, book, output))
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

                var code = RunStages(doc, spine, FontRoles.Embedded(), options.Work, output, error);
                return code != 0 ? code : ReportNotice(NoticeCheck.Run(doc, spine, book), output, error);
            }
        }
        catch (DegreesException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            error.WriteLine($"degrees failed: {ex.GetType().Name}");
            return 1;
        }
    }

    /// <summary>Stages 1 to 3 into <paramref name="work"/>. The work folder and every file in it are checked against the git tree first.</summary>
    public static int RunStages(PdfDocument doc, IReadOnlyList<SpineNode> spine, FontRoles roles, string work, TextWriter output, TextWriter error)
    {
        PathGuard.EnsureOutsideGitTree(work, "--work");
        Directory.CreateDirectory(work);
        Write(work, "spine.json", spine);
        output.WriteLine($"spine: {spine.Count} bookmarks, {spine.Count(n => n.Sidebar)} sidebar bookmarks");

        var layout = LayoutStage.Run(doc, roles);
        Write(work, "layout.json", layout.Pages);
        output.WriteLine($"layout: {layout.Pages.Sum(p => p.Words.Count)} words, {layout.Letters} letters; letters by role: " +
            string.Join(", ", layout.LettersByRole.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}")));
        if (layout.Unclassified.Count > 0)
        {
            // The font names go to a file in the work folder, not to the console: one of them contains the trademark.
            Write(work, "unclassified-fonts.json", layout.Unclassified);
            for (var i = 0; i < layout.Unclassified.Count; i++)
            {
                var u = layout.Unclassified[i];
                error.WriteLine($"unclassified font #{i + 1}: {u.Size}pt, {u.Letters} letters, first pages {string.Join(",", u.FirstPages)}");
            }

            error.WriteLine($"{layout.Unclassified.Sum(u => u.Letters)} letters have no font role; see unclassified-fonts.json in the work folder and add rules to degrees-fonts.yaml.");
            return 1;
        }

        var blocks = BlocksStage.Run(layout.Pages);
        Write(work, "blocks.json", blocks.Blocks);
        output.WriteLine($"blocks: {blocks.Blocks.Count} blocks ({string.Join(", ", blocks.BlocksByRole.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"))}); " +
            $"{blocks.TwoColumnPages} two-column pages, {blocks.OneColumnPages} one-column pages, {blocks.FurnitureWords} furniture or off-page words dropped");
        if (blocks.SuspectPages.Count > 0)
        {
            output.WriteLine($"warning: {blocks.SuspectPages.Count} one-column page(s) have text on both halves and may read out of order: {string.Join(",", blocks.SuspectPages)}");
        }

        return 0;
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

    /// <summary>Writes one output file after guarding its real path. A file already there is deleted first, so a planted link is replaced, not followed.</summary>
    private static void Write<T>(string folder, string name, T value)
    {
        var path = Path.Combine(folder, name);
        PathGuard.EnsureOutsideGitTree(path, "An output file");
        File.Delete(path);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}
