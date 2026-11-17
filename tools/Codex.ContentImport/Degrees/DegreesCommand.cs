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
            $"{blocks.FurnitureWords} furniture or off-page words dropped, {blocks.OpenerWords} opener words and {blocks.SidebarBlocks} sidebar blocks split out; " +
            $"{blocks.Tables} table(s) rebuilt, {blocks.RejectedTableHeaders} two-cell header(s) whose lines did not line up were left as blocks");
        if (blocks.SidebarProblems is { Count: > 0 })
        {
            // Pages where a sidebar bookmark's heading was not found (or body text follows a sidebar): its text is kept as the page's own. Harmless in text no row takes, but the denylist hashes less.
            output.WriteLine($"warning: {blocks.SidebarProblems.Count} sidebar page(s) where the split is not backed by the bookmarks: {string.Join(",", blocks.SidebarProblems)}");
        }

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

        if (packDir != null)
        {
            // The pack folder sits at <system>/packs/<pack>, and the system's own declaration decides each target's folder.
            var systemFile = Path.Combine(Path.GetFullPath(packDir), "..", "..", "system.yaml");
            if (!File.Exists(systemFile))
            {
                throw new DegreesException("--out: no system.yaml two folders above the pack folder, so the take-list's targets cannot be checked.");
            }

            new TakeList { Rows = rows }.CheckAgainstSystem(File.ReadAllText(systemFile));
        }

        // The ids of the pack as committed: an id that has shipped keeps its entry when a later chapter brings another with the same name.
        var result = ImportPipeline.Run(blocks, spine, rows, redactions ?? RedactionTable.Embedded(), book, StableIds(packDir));
        Write(work, "entries.json", result.Parsed);
        Write(work, "redacted.json", result.Final);
        var files = PackEmitter.Files(result.Final.Where(e => e.Approved), result.Report);
        WriteText(work, PackEmitter.ReportFile, files[PackEmitter.ReportFile]);
        output.WriteLine($"take-list: {result.Ranges.Count} range(s) in {rows.Count} row(s), {rows.Count(r => r.Approved)} approved; every block in them is claimed");
        output.WriteLine($"entries: {result.Parsed.Count} parsed, {result.Final.Count} after redaction ({result.Final.Count(e => e.Approved)} approved); " + result.Redaction);
        if (result.Ranges.Any(r => r.Row.Parser == "actions"))
        {
            var counts = ImportPipeline.CheckActionCounts(spine, blocks, result.Ranges, result.Parsed);
            output.WriteLine($"actions: {counts.Children} bookmark children = {counts.Units} headings in the ranges; {counts.Headers} action headings, {counts.TraitLines} trait lines, {counts.Icons} action icons; " +
                $"the overview table lists {counts.TableNames} (name, proficiency) pairs, and the parsed actions have the same");
        }

        if (result.Ranges.Any(r => r.Row.Parser == "ancestries"))
        {
            var ancestries = ImportPipeline.CheckAncestryCounts(spine, blocks, result.Ranges, result.Parsed);
            output.WriteLine($"ancestries: {ancestries.Count} read, and the table of contents lists the same {ImportPipeline.CheckAncestryList(blocks, result.Parsed)}; per ancestry (lore headings, heritages, level bands, feats): " +
                string.Join("; ", ancestries.Select(a => $"{a.LoreHeadings}, {a.Heritages}, {a.Bands}, {a.Feats}")) + $"; feat headers = trait lines = level markers in each ({ancestries.Sum(a => a.FeatHeaders)} in all)");
        }

        if (packDir != null)
        {
            // An imported id may not equal an id of the homebrew pack beside it (in any type): the run fails, the ids are named (they are our own invented ids or a book's item names, never a reserved word).
            var clashes = PackEmitter.HomebrewClashes(result.Final, packDir);
            if (clashes.Count > 0)
            {
                throw new DegreesException($"{clashes.Count} imported id(s) equal an id of the homebrew pack: {string.Join(", ", clashes.Take(10))}.");
            }

            output.WriteLine($"homebrew: none of the {result.Final.Count} imported ids equals one of the {PackEmitter.HomebrewIds(packDir).Count} ids of the homebrew pack");
        }

        if (result.Ranges.Any(r => r.Row.Parser == "items"))
        {
            var folds = ImportPipeline.CheckFoldCounts(spine, blocks, result.Ranges);
            output.WriteLine($"equipment: folded sections {string.Join("; ", folds.Select(f => $"{f.Children} bookmark children = {f.Headings} headings"))}");
            foreach (var c in ImportPipeline.CheckItemCounts(spine, blocks, result.Ranges, result.Parsed))
            {
                output.WriteLine($"items ({c.Kind}): {c.Tables} stat table(s), {c.TableRows} table rows, {c.Descriptions} descriptions, {c.Excluded} excluded name(s), {c.Entries} entries; {c.ArtLabels} art label(s) and {c.LeadLines} lead line(s) skipped; " +
                    string.Join(", ", c.PerCategory.Select(p => $"{p.Value} {p.Key}")));
            }
        }

        if (ImportPipeline.CheckSpellCounts(blocks, result.Ranges, result.Parsed) is { } spells)
        {
            var (degrees, heightened) = ImportPipeline.CheckSpellDegrees(blocks, result.Ranges, result.Parsed);
            var sections = ImportPipeline.CheckRuleSectionCounts(spine, blocks, result.Ranges);
            output.WriteLine($"spells: {spells.Headers} headers = {spells.TraitLines} trait lines = {spells.TraditionLines} traditions lines; the four lists name {string.Join(", ", spells.PerTradition.Select(p => $"{p.Value} {p.Key}"))} " +
                $"({spells.Listed} in all), the parsed spells say the same ({spells.Parsed}); {spells.OnNoList} on no list; {spells.ArtLabels} labels of the page's art skipped; {degrees} degree sections, {heightened} heightened entries; " +
                $"rule sections: {sections.Sum(s => s.Children)} bookmark children = {sections.Sum(s => s.Headings)} headings in {sections.Count} section(s)");
        }

        if (result.Ranges.Any(r => r.Row.Groups || r.Row.ListHeading != null))
        {
            var levels = ImportPipeline.CheckLevelCounts(blocks, result.Ranges);
            var groups = ImportPipeline.CheckEntryCounts(spine, blocks, result.Ranges.Where(r => r.Row.Groups));
            var lists = ImportPipeline.CheckRankLists(blocks, result.Ranges, result.Parsed);
            output.WriteLine($"focus spells and rituals: the level-word ranges hold (headers, trait lines, level markers) {string.Join("; ", levels.Select(l => $"{l.Headers}, {l.TraitLines}, {l.Markers}"))}; " +
                $"group headings against the section's bookmark descendants {string.Join("; ", groups.Select(g => $"{g.Headers} = {g.Bookmarks}"))}; rank list against the entries {string.Join("; ", lists.Select(l => $"{l.Listed} = {l.Entries}"))}");
        }

        if (packDir == null)
        {
            return 0;
        }

        var anyApproved = result.Final.Any(e => e.Approved);
        if (!anyApproved && !check)
        {
            throw new DegreesException("--out: no approved row has any entry, so there is nothing to write. The pack folder was not touched.");
        }

        if (check)
        {
            // With nothing approved the pack should hold no generated file at all, so every file in it is a difference.
            var differences = PackEmitter.Check(packDir, anyApproved ? files : new SortedDictionary<string, string>(StringComparer.Ordinal));
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
        var planned = TakeStage.PlannedRanges(spine, blocks, options.Book, take.Planned);
        var redacted = ImportPipeline.RedactedWords(blocks, ranges, RedactionTable.Embedded(), options.Book, spine, StableIds(options.Pack));
        var redactedPairs = ImportPipeline.RedactedPairs(blocks, ranges, RedactionTable.Embedded(), options.Book, spine, StableIds(options.Pack));
        var (words, excluded) = ImportPipeline.DenylistWords(blocks, ranges, Waivers.Embedded(), 3, planned, redacted);
        var path = options.Out ?? Path.Combine(options.Work, "denylist.sha256");
        if (options.Out == null)
        {
            PathGuard.EnsureOutsideGitTree(options.Work, "--work");
            Directory.CreateDirectory(options.Work);
        }

        // A waiver lifts a hash only when this book's own left-out text would hash the word without it, and never a reserved name (see ImportPipeline.LiftedWaivers).
        var withoutWaivers = ImportPipeline.DenylistWords(blocks, ranges, new HashSet<string>(StringComparer.Ordinal), 3, planned, redacted).Words;
        var liftedWords = ImportPipeline.LiftedWaivers(Waivers.Embedded(), redacted.Names, withoutWaivers);
        var liftedPairs = ImportPipeline.LiftedPairWaivers(Waivers.Embedded(), ImportPipeline.DenylistPairs(blocks, ranges, planned, words, new HashSet<string>(StringComparer.Ordinal), redactedPairs));
        var pairsFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "degrees-pairs.sha256");
        var commonFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "degrees-common.sha256");
        if (options.Fresh)
        {
            // A regeneration from scratch is reviewed before anything is written: it never drops a hash of a redacted word or of a dropped entry's name, never drops a pair a waiver, a title or a
            // denied word does not explain, and says what it removes and what is new.
            ImportPipeline.RefuseFreshWithOtherBooks(take.Rows, options.Book);
            var never = redacted.SpanWords.Select(Denylist.Hash).Concat(redacted.Names.SelectMany(n => new[] { n, n + "s", n + "es" }).Select(Denylist.Hash)).ToHashSet(StringComparer.Ordinal);
            var reviews = new[]
            {
                ImportPipeline.ReviewFresh("denylist", ReadHashes(path), words.Select(Denylist.Hash).ToList(), liftedWords, never, null),
                ImportPipeline.ReviewFresh("pairs", ReadHashes(pairsFile), ImportPipeline.DenylistPairs(blocks, ranges, planned, words, Waivers.Embedded(), redactedPairs), liftedPairs, new HashSet<string>(StringComparer.Ordinal),
                    ImportPipeline.ExplainedPairs(blocks, ranges, planned, words)),
                ImportPipeline.ReviewFresh("common", ReadHashes(commonFile), Denylist.Format(ImportPipeline.CommonWords(blocks).Concat(ImportPipeline.OpenerWords(blocks, ranges)).Concat(Waivers.Embedded())).Split('\n', StringSplitOptions.RemoveEmptyEntries), Array.Empty<string>(),
                    new HashSet<string>(StringComparer.Ordinal), null),
            };
            foreach (var r in reviews)
            {
                output.WriteLine($"fresh {r.File}: writes {r.Written} (kept {r.Kept}, new {r.New}); removes {r.Waived + r.Taken + r.Titles}: {r.Waived} waived, {r.Taken} of left-out text since taken or common, {r.Titles} pair(s) of a title or of a denied word");
            }
        }

        var before = MergeDenylist(path, Denylist.Format(words), out var total, lifted: liftedWords, fresh: options.Fresh);
        output.WriteLine($"denylist: {words.Count} word(s) hashed from the sections left out ({planned.Count} planned section(s) are not left out), {excluded} common, waived or book-key word(s) left off; the file now holds {total} hash(es){(options.Fresh ? string.Empty : $" ({total - before} new)")}");

        // The pairs of capitalized words go beside it too: a name of two words that each pass the word gates (see ImportPipeline.DenylistPairs).
        var pairsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "degrees-pairs.sha256");
        var pairs = ImportPipeline.DenylistPairs(blocks, ranges, planned, words, Waivers.Embedded(), redactedPairs);
        var pairsBefore = MergeDenylist(pairsPath, string.Concat(pairs.Select(h => h + "\n")), out var pairsTotal, PairsHeader,
            ImportPipeline.LiftedPairWaivers(Waivers.Embedded(), ImportPipeline.DenylistPairs(blocks, ranges, planned, words, new HashSet<string>(StringComparer.Ordinal), redactedPairs)), options.Fresh);
        output.WriteLine($"pairs: {pairs.Count} pair(s) of capitalized words hashed from the sections left out; the file now holds {pairsTotal} hash(es){(options.Fresh ? string.Empty : $" ({pairsTotal - pairsBefore} new)")}");

        // The book's common lowercase words go beside it, as hashes too: the allowlist gate lets an ordinary word open a sentence.
        var commonPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "degrees-common.sha256");
        var common = ImportPipeline.CommonWords(blocks);
        var openers = ImportPipeline.OpenerWords(blocks, ranges);
        var commonBefore = MergeDenylist(commonPath, Denylist.Format(common.Concat(openers).Concat(Waivers.Embedded())), out var commonTotal, CommonHeader, fresh: options.Fresh);
        output.WriteLine($"common words: {common.Count} lowercase word(s) and {openers.Count} sentence-opener word(s) of this book hashed; the file now holds {commonTotal} hash(es){(options.Fresh ? string.Empty : $" ({commonTotal - commonBefore} new)")}");
        return 0;
    }

    /// <summary>The hashes a hash file holds (its comment lines and blank lines left out); none when the file is not there.</summary>
    public static HashSet<string> ReadHashes(string path) => File.Exists(path)
        ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The shipped ids a run reads: those of the pack folder it names, else those of the committed rules pack (null when neither is found; the entries then keep shipping order).</summary>
    public static IReadOnlyDictionary<string, string>? StableIds(string? packDir)
    {
        var dir = packDir ?? PackEmitter.CommittedPackDir();
        return dir == null ? null : PackEmitter.ShippedIds(dir);
    }

    private const string PairsHeader =
        "# The hashed pairs of the local book importer: one lowercase hex SHA-256 of two lowercased words with a space between, no words.\n" +
        "# A pair is two capitalized words that follow one another in the text the take-list leaves out, where neither word is on the word denylist and the pair is not game text.\n" +
        "# The denylist test fails a pack that says one: it catches a name of two words that each pass the word gates alone.\n";

    private const string CommonHeader =
        "# The hashed common words of the local book importer: one lowercase hex SHA-256 of a lowercased word per line, no words.\n" +
        "# A word is here when the book writes it in lowercase at least three times, or capitalizes it at the start of a sentence and nowhere else but also writes it in lowercase,\n" +
        "# or when a person reviewed it as an ordinary word (the waiver file).\n" +
        "# The allowlist gate lets such a word open a sentence, unless it is on the denylist. Only the first kind is left off the denylist.\n";

    /// <summary>
    /// Writes the union of the hashes already in <paramref name="path"/> and <paramref name="hashes"/>, under the file's own comment header, less the hashes in <paramref name="lifted"/>
    /// (the words and pairs a person has reviewed and waived: the file may not hold them, and a waiver written after the hash was committed lifts it here). Returns how many were there before.
    /// </summary>
    public static int MergeDenylist(string path, string hashes, out int total, string? defaultHeader = null, IEnumerable<string>? lifted = null, bool fresh = false)
    {
        var drop = (lifted ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        var DefaultHeader = defaultHeader ?? "# The hashed denylist of the local book importer: one lowercase hex SHA-256 of a lowercased word per line, no names.\n";
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
                    if (!System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), "^[0-9a-f]{64}$"))
                    {
                        throw new DegreesException("--out names a file that is not a hash list (a line is not a SHA-256 hash); nothing was written.");
                    }

                    if (!fresh)
                    {
                        existing.Add(line.Trim());
                    }
                }
            }
        }

        var all = existing.Concat(hashes.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).Where(h => !drop.Contains(h)).OrderBy(h => h, StringComparer.Ordinal).ToList();
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
