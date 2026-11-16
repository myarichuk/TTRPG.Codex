using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// The <c>degrees</c> command: a local-only importer for two licensed books. Stages 1 to 5 leave JSON in the work folder, outside the git work tree, for a
/// reviewer; <c>--out</c> writes the pack for the approved take-list rows and <c>--check</c> compares it with what a fresh run would write.
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
            // The committed take-list, redaction, denylist and allowlist files are validated before anything is read, so a malformed one fails the run here.
            var take = TakeList.Embedded();
            var redactions = RedactionTable.Embedded();
            Denylist.Embedded();
            Allowlist.Embedded();
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

                if (options.Action == "denylist")
                {
                    return RunDenylist(doc, spine, FontRoles.Embedded(), take, options, output);
                }

                var code = RunStages(doc, spine, FontRoles.Embedded(), options.Work, output, error, take, options.Book, redactions, options.Out, options.Check);
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
    public static int RunStages(PdfDocument doc, IReadOnlyList<SpineNode> spine, FontRoles roles, string work, TextWriter output, TextWriter error) =>
        RunStages(doc, spine, roles, work, output, error, null, string.Empty, null, null, false);

    /// <summary>
    /// Stages 1 to 3 and, when the take-list has rows for <paramref name="book"/>, stages 4 and 5: the ranges, the entries, the redaction and the report.
    /// With <paramref name="packDir"/> the pack is written (or, with <paramref name="check"/>, compared) for the approved rows.
    /// </summary>
    public static int RunStages(PdfDocument doc, IReadOnlyList<SpineNode> spine, FontRoles roles, string work, TextWriter output, TextWriter error,
        TakeList? take, string book, RedactionTable? redactions, string? packDir, bool check)
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

        var rows = take?.For(book).ToList() ?? new List<TakeRow>();
        var blocks = BlocksStage.Run(layout.Pages, ImportPipeline.OptionsFor(spine, rows));
        Write(work, "blocks.json", blocks.Blocks);
        output.WriteLine($"blocks: {blocks.Blocks.Count} blocks ({string.Join(", ", blocks.BlocksByRole.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"))}); " +
            $"{blocks.TwoColumnPages} multi-column pages ({blocks.ThreeOrMoreColumnPages} with three or more), {blocks.OneColumnPages} one-column pages, " +
            $"{blocks.FurnitureWords} furniture or off-page words dropped, {blocks.OpenerWords} opener words and {blocks.SidebarBlocks} sidebar blocks split out");
        if (blocks.SuspectPages.Count > 0)
        {
            output.WriteLine($"warning: {blocks.SuspectPages.Count} one-column page(s) have text on both halves and may read out of order: {string.Join(",", blocks.SuspectPages)}");
        }

        if (rows.Count == 0)
        {
            if (packDir != null)
            {
                throw new DegreesException("--out: the take-list has no rows for this book.");
            }

            return 0;
        }

        var result = ImportPipeline.Run(blocks, spine, rows, redactions ?? RedactionTable.Embedded(), book);
        Write(work, "entries.json", result.Parsed);
        Write(work, "redacted.json", result.Final);
        var files = PackEmitter.Files(result.Final.Where(e => e.Approved), result.Report);
        WriteText(work, PackEmitter.ReportFile, files[PackEmitter.ReportFile]);
        output.WriteLine($"take-list: {result.Ranges.Count} range(s) in {rows.Count} row(s), {rows.Count(r => r.Approved)} approved; every block in them is claimed");
        output.WriteLine($"entries: {result.Parsed.Count} parsed, {result.Final.Count} after redaction ({result.Final.Count(e => e.Approved)} approved); " + result.Redaction);

        if (packDir == null)
        {
            return 0;
        }

        if (!result.Final.Any(e => e.Approved))
        {
            throw new DegreesException("--out: no approved row has any entry, so there is nothing to write. The pack folder was not touched.");
        }

        if (check)
        {
            var differences = PackEmitter.Check(packDir, files);
            if (differences.Count > 0)
            {
                error.WriteLine($"check failed: the pack differs from a fresh run in {differences.Count} file(s):");
                foreach (var d in differences)
                {
                    error.WriteLine("  " + d);
                }

                return 1;
            }

            output.WriteLine($"check: the pack is exactly what a fresh run writes ({files.Count} files)");
            return 0;
        }

        PackEmitter.Write(packDir, files);
        output.WriteLine($"pack: wrote {files.Count} file(s)");
        return 0;
    }

    /// <summary>
    /// The denylist step: the hashed capitalized words of everything the take-list leaves out (see <see cref="ImportPipeline.DenylistWords"/>), merged into the
    /// hash file given by <c>--out</c> (default: <c>denylist.sha256</c> in the work folder). Only counts are printed.
    /// </summary>
    private static int RunDenylist(PdfDocument doc, IReadOnlyList<SpineNode> spine, FontRoles roles, TakeList take, DegreesOptions options, TextWriter output)
    {
        var rows = take.For(options.Book).ToList();
        if (rows.Count == 0)
        {
            throw new DegreesException("The take-list has no rows for this book, so every section would count as left out.");
        }

        var layout = LayoutStage.Run(doc, roles);
        if (layout.Unclassified.Count > 0)
        {
            throw new DegreesException($"{layout.Unclassified.Sum(u => u.Letters)} letters have no font role; run the default command to see where.");
        }

        var blocks = BlocksStage.Run(layout.Pages, ImportPipeline.OptionsFor(spine, rows));
        var ranges = TakeStage.Resolve(spine, blocks, rows);
        var (words, excluded) = ImportPipeline.DenylistWords(blocks, ranges, Allowlist.Embedded());
        var path = options.Out ?? Path.Combine(options.Work, "denylist.sha256");
        if (options.Out == null)
        {
            PathGuard.EnsureOutsideGitTree(options.Work, "--work");
            Directory.CreateDirectory(options.Work);
        }

        var before = MergeDenylist(path, Denylist.Format(words), out var total);
        output.WriteLine($"denylist: {words.Count} word(s) hashed from the sections left out, {excluded} common or game-term word(s) left off; the file now holds {total} hash(es) ({total - before} new)");
        return 0;
    }

    /// <summary>Writes the union of the hashes already in <paramref name="path"/> and <paramref name="hashes"/>, under the file's own comment header. Returns how many were there before.</summary>
    public static int MergeDenylist(string path, string hashes, out int total)
    {
        const string DefaultHeader = "# The hashed denylist of the local book importer: one lowercase hex SHA-256 of a lowercased word per line, no names.\n";
        var header = new StringBuilder();
        var existing = new List<string>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllText(path).Split('\n'))
            {
                if (line.StartsWith('#'))
                {
                    header.Append(line).Append('\n');
                }
                else if (line.Trim().Length > 0)
                {
                    existing.Add(line.Trim());
                }
            }
        }

        var all = existing.Concat(hashes.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToList();
        File.WriteAllText(path, (header.Length > 0 ? header.ToString() : DefaultHeader) + string.Concat(all.Select(h => h + "\n")), new UTF8Encoding(false));
        total = all.Count;
        return existing.Distinct(StringComparer.Ordinal).Count();
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
    private static void Write<T>(string folder, string name, T value) => WriteText(folder, name, JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteText(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        PathGuard.EnsureOutsideGitTree(path, "An output file");
        File.Delete(path);
        File.WriteAllText(path, text);
    }
}
