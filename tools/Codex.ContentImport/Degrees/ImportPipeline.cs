using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// The denylist waivers (<c>Data/degrees-denylist-waivers.txt</c>): game terms a person has reviewed and decided the packs may say although a left-out section
/// capitalizes them too. One lowercase word per line, `#` lines are comments. Each line is a reviewed decision, not a convenience.
/// </summary>
public static class Waivers
{
    public static HashSet<string> Parse(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public static HashSet<string> Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-denylist-waivers.txt"));
}

/// <summary>What stages 3 to 5 made from one book's taken rows.</summary>
public sealed record PipelineOutput(List<TakenRange> Ranges, List<ParsedEntry> Parsed, List<FinalEntry> Final, RedactionReport Redaction, List<ReportRow> Report);

/// <summary>The part of the run that needs no PDF: blocks in, entries and report rows out. The tests drive it with synthetic books.</summary>
public static partial class ImportPipeline
{
    /// <summary>The options the blocks stage needs from the take-list and the spine: how many columns a taken page may have, and where sidebars may be.</summary>
    public static BlocksOptions OptionsFor(IReadOnlyList<SpineNode> spine, IEnumerable<TakeRow> rows)
    {
        var columns = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            foreach (var page in TakeStage.PagesOf(spine, row))
            {
                columns[page] = Math.Max(columns.GetValueOrDefault(page), row.Columns);
            }
        }

        var sidebars = spine.Where(n => n.Sidebar).SelectMany(n => Enumerable.Range(n.PageStart, n.PageEnd - n.PageStart + 1)).ToHashSet();
        var starts = spine.Where(n => n.Sidebar).GroupBy(n => n.PageStart).ToDictionary(g => g.Key, g => g.Count());
        return new BlocksOptions { MaxColumns = page => columns.GetValueOrDefault(page, 2), SidebarPages = sidebars, SidebarStarts = starts };
    }

    public static PipelineOutput Run(BlocksResult blocks, IReadOnlyList<SpineNode> spine, IReadOnlyList<TakeRow> rows, RedactionTable redactions, string book)
    {
        var ranges = TakeStage.Resolve(spine, blocks, rows);
        CheckEntryCounts(spine, blocks, ranges);
        CheckGlossaryCounts(blocks, ranges);
        CheckLevelCounts(blocks, ranges);
        var parsed = EntriesStage.Run(blocks, ranges, book);

        // Only this book's rows apply, so one book's rows are never "unmatched" in the other's run.
        var scoped = new RedactionTable { Normalization = redactions.Normalization, Rows = redactions.Rows.Where(r => r.Book == null || r.Book == book).ToList() };
        var redactable = parsed.Select(e => new RedactionEntry(e.Id, e.RedactionFields())).ToList();
        var report = RedactionStage.Run(redactable, scoped);
        var kept = redactable.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var final = parsed.Where(e => kept.ContainsKey(e.Id)).Select(e => new FinalEntry
        {
            Id = e.Id,
            Type = e.Type,
            Folder = e.Folder,
            Chapter = e.Chapter,
            Source = e.Source,
            Category = e.Category,
            Approved = e.Approved,
            RowNumber = e.RowNumber,
            Name = kept[e.Id].Fields["name"],
            Description = kept[e.Id].Fields["description"],
            Level = e.Level,
            Actions = e.Actions,
            Traits = e.Traits.ToList(),
            Extracted = e.Extracted.Select(x => new LabelledText(x.Label, kept[e.Id].Fields[x.Label])).ToList(),
        }).ToList();

        return new PipelineOutput(ranges, parsed, final, report, ReportRows(blocks, spine, rows, redactions, book, parsed, final));
    }

    /// <summary>
    /// Counts the entries of every taken <c>entries</c> range two ways, from the document: the bookmark children of the taken section (minus the sidebar bookmarks) and the
    /// blocks in the entry-header font in the range. They must agree, or the row says why in <see cref="TakeRow.CountsDiffer"/>; otherwise the run fails with both counts.
    /// A section with no child bookmarks has nothing to compare (header font only). Returns the (bookmarks, headers) pairs for the ranges it compared.
    /// </summary>
    public static List<(int Bookmarks, int Headers)> CheckEntryCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges)
    {
        var compared = new List<(int, int)>();
        foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "entries"))
        {
            var node = range.Node;
            var children = spine.Where(n => n.Depth == node.Depth + 1 && n.Path.Length == node.Path.Length + 1 && n.Path.Take(node.Path.Length).SequenceEqual(node.Path) && !n.Sidebar).Count();
            if (children == 0)
            {
                continue;
            }

            var headers = blocks.Blocks.Skip(range.Start).Take(range.End - range.Start).Count(b => b.Role == "entry-header");
            compared.Add((children, headers));
            if (children != headers && string.IsNullOrWhiteSpace(range.Row.CountsDiffer))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the taken section has {children} bookmark children (sidebars left out) but {headers} entry-header blocks in its range, and the row gives no countsDiffer reason.");
            }
        }

        return compared;
    }

    /// <summary>
    /// Counts the trait entries of every taken <c>glossary</c> range two ways, from the document: the definitions and the pointer-only trait lines the parser finds at entry
    /// starts, and every occurrence of the word "(trait)" in the range. They must agree, or the row says why in <see cref="TakeRow.CountsDiffer"/>; otherwise the run fails
    /// with both counts. Returns the (definitions, pointers, occurrences) of each range.
    /// </summary>
    public static List<(int Definitions, int Pointers, int Tokens)> CheckGlossaryCounts(BlocksResult blocks, IEnumerable<TakenRange> ranges)
    {
        var compared = new List<(int, int, int)>();
        foreach (var range in ranges.Where(r => r.Row.Parser == "glossary"))
        {
            var scan = EntriesStage.ScanGlossary(range.BlocksIn(blocks), new HashSet<string>(StringComparer.Ordinal));
            compared.Add((scan.Definitions.Count, scan.Pointers, scan.TraitTokens));
            if (scan.Definitions.Count + scan.Pointers != scan.TraitTokens && string.IsNullOrWhiteSpace(range.Row.CountsDiffer))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the glossary range has {scan.Definitions.Count} trait definitions and {scan.Pointers} pointer-only trait lines at entry starts, but {scan.TraitTokens} occurrences of the trait mark, and the row gives no countsDiffer reason.");
            }
        }

        return compared;
    }

    /// <summary>
    /// Counts the entries of every taken <c>entries</c> range whose headers end in a word and a level (<see cref="TakeRow.LevelWord"/>) three ways, from the document, because such a section has
    /// no child bookmarks: the header-font blocks of the header shape, the trait-line blocks (one follows each header), and the lines in the range that end in the capitalized word and a number
    /// (the level markers, read line by line, whatever block they sit in). They must agree, or the row says why in <see cref="TakeRow.CountsDiffer"/>; otherwise the run fails with the three counts.
    /// Returns the (headers, trait lines, level markers) of each range.
    /// </summary>
    public static List<(int Headers, int TraitLines, int Markers, int TableRows)> CheckLevelCounts(BlocksResult blocks, IEnumerable<TakenRange> ranges)
    {
        var compared = new List<(int, int, int, int)>();
        foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "entries" && r.Row.LevelWord != null))
        {
            var slice = range.BlocksIn(blocks);
            var marker = new Regex(@"\s" + Regex.Escape(range.Row.LevelWord!.ToUpperInvariant()) + @"\s+\d+$", RegexOptions.CultureInvariant);
            var headers = slice.Count(range.Row.IsHeader);
            var traitLines = slice.Count(b => b.Role == "tag");
            var markers = slice.SelectMany(b => b.Lines).Count(l => marker.IsMatch(SpanText.Normalize(l)));

            // The overview tables before the first header list every entry with its level: a count that no header, trait line or classification step touches. Not every chapter has them.
            var table = LeadTableRows(blocks, range);
            var pairs = slice.Where(range.Row.IsHeader).Select(b => HeaderPair(b, range.Row)).ToHashSet();
            var tableRows = table.Count;
            var tableAgrees = tableRows == 0 || table.SetEquals(pairs);
            compared.Add((headers, traitLines, markers, tableRows));
            if ((headers != traitLines || headers != markers || !tableAgrees) && string.IsNullOrWhiteSpace(range.Row.CountsDiffer))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the range has {headers} header blocks, {traitLines} trait-line blocks and {markers} level markers, " +
                    $"and its overview tables list {tableRows} entries ({table.Except(pairs).Count()} not among the headers, {pairs.Except(table).Count()} headers not in the tables), and the row gives no countsDiffer reason.");
            }
        }

        return compared;
    }

    /// <summary>(name in title case, level) of a header block.</summary>
    private static (string Name, int Level) HeaderPair(Block header, TakeRow row)
    {
        var m = Regex.Match(Regex.Replace(EntriesStage.Clean(string.Join(' ', header.Lines)), @"\s*\[[^\]]+\]", string.Empty), @"^(?<name>.+?)\s+" + Regex.Escape(row.LevelWord!) + @"\s+(?<level>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return (EntriesStage.TitleCase(m.Groups["name"].Value), int.Parse(m.Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The (name, level) of every row of the overview tables in a range's lead: a line at its block's left edge that is a name (every word capitalized, or a small joining word) then a level.
    /// A row's continuation lines are set in from the edge, and the text columns are not names, so neither is read.
    /// </summary>
    private static HashSet<(string Name, int Level)> LeadTableRows(BlocksResult blocks, TakenRange range)
    {
        var rows = new HashSet<(string, int)>();
        if (range.LeadStart < 0)
        {
            return rows;
        }

        var name = new Regex(@"^(?<name>[A-Z][^0-9]*?) (?<level>\d{1,2})(?: |$)", RegexOptions.CultureInvariant);
        for (var i = range.LeadStart; i < range.Start; i++)
        {
            var block = blocks.Blocks[i];
            if (block.Role != "body" || block.Info == null)
            {
                continue;
            }

            for (var k = 0; k < block.Lines.Count; k++)
            {
                var line = SpanText.Normalize(block.Lines[k]);
                var m = name.Match(line);
                if (block.Info[k].Left - block.Left <= 2 && m.Success && !line.Contains(" Level ", StringComparison.Ordinal)
                    && m.Groups["name"].Value.Split(' ').All(w => EntriesStage.IsNameWord(w)))
                {
                    rows.Add((m.Groups["name"].Value, int.Parse(m.Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        return rows;
    }

    private static List<ReportRow> ReportRows(BlocksResult blocks, IReadOnlyList<SpineNode> spine, IReadOnlyList<TakeRow> rows, RedactionTable redactions, string book, List<ParsedEntry> parsed, List<FinalEntry> final)
    {
        var result = new List<ReportRow>();
        var counted = new HashSet<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.Approved)
            {
                continue;
            }

            // A page two rows share is counted for the first of them only.
            var pages = TakeStage.PagesOf(spine, row).Where(counted.Add).ToHashSet();

            var mine = final.Where(e => e.RowNumber == i + 1).ToList();
            // Redaction rows are listed by the entries the row parsed, so a row that dropped its entry still shows.
            var ids = parsed.Where(e => e.RowNumber == i + 1).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var dropped = (blocks.Dropped ?? new List<Block>()).Where(b => pages.Contains(b.Page)).ToList();
            result.Add(new ReportRow(
                row.Chapter,
                row.Parser,
                row.Target,
                mine.Count,
                mine.Select(e => e.Id).ToList(),
                dropped.Count(b => b.Role != "opener"),
                dropped.Count(b => b.Role == "opener"),
                redactions.Rows.Select((r, n) => (r, n)).Where(x => (x.r.Book == null || x.r.Book == book) && ids.Any(x.r.Names)).Select(x => x.n + 1).ToList(),
                mine.Count(e => PageReference().IsMatch(e.Description) || e.Extracted.Any(x => PageReference().IsMatch(x.Text)))));
        }

        return result;
    }

    [GeneratedRegex(@"\bpages? \d+", RegexOptions.IgnoreCase)]
    private static partial Regex PageReference();

    /// <summary>The lowercase words the book writes in lowercase at least <paramref name="commonAfter"/> times anywhere in its text (taken, left out, planned, dropped).</summary>
    public static List<string> CommonWords(BlocksResult blocks, int commonAfter = 3)
    {
        var lower = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in blocks.Blocks.Concat(blocks.Dropped ?? new List<Block>()))
        {
            foreach (var word in block.Lines.SelectMany(PackWordCheck.Words).Where(w => !char.IsUpper(w[0])).Select(w => w.ToLowerInvariant()))
            {
                lower[word] = lower.GetValueOrDefault(word) + 1;
            }
        }

        return lower.Where(kv => kv.Value >= commonAfter).Select(kv => kv.Key).OrderBy(w => w, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The lowercase words the book capitalizes at the start of a sentence and never inside one, and also writes in lowercase somewhere: such a
    /// word is not a name, so the allowlist gate lets a sentence open with it, unless the denylist has it. A glossary range is read as its definitions and its dropped lines,
    /// each its own paragraph, so that an entry's term in front of the definition does not make the definition's first word look like the middle of a sentence.
    /// The words go into the common-word file with <see cref="CommonWords"/>; only the common words are left off the denylist, so a name that is only ever capitalized at a
    /// sentence start in a left-out section is still hashed.
    /// </summary>
    public static List<string> OpenerWords(BlocksResult blocks, IReadOnlyList<TakenRange> ranges)
    {
        var glossary = ranges.Where(r => r.Row.Parser == "glossary").ToList();
        var inGlossary = glossary.SelectMany(r => r.Indexes).ToHashSet();
        var paragraphs = new List<string>();
        for (var i = 0; i < blocks.Blocks.Count; i++)
        {
            if (!inGlossary.Contains(i))
            {
                paragraphs.Add(string.Join(' ', blocks.Blocks[i].Lines));
            }
        }

        foreach (var range in glossary)
        {
            var scan = EntriesStage.ScanGlossary(range.BlocksIn(blocks), new HashSet<string>(StringComparer.Ordinal));
            paragraphs.AddRange(scan.Definitions.Select(d => d.Text));
            paragraphs.AddRange(scan.Dropped);
        }

        paragraphs.AddRange((blocks.Dropped ?? new List<Block>()).Select(b => string.Join(' ', b.Lines)));

        var starts = new HashSet<string>(StringComparer.Ordinal);
        var inside = new HashSet<string>(StringComparer.Ordinal);
        var lower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paragraph in paragraphs)
        {
            var text = SpanText.Normalize(paragraph);
            foreach (var sentence in SpanText.Sentences(text))
            {
                var first = true;
                foreach (var word in PackWordCheck.Words(text.Substring(sentence.Start, sentence.Length)))
                {
                    if (PackWordCheck.IsCapital(word))
                    {
                        (first ? starts : inside).Add(word.ToLowerInvariant());
                    }
                    else
                    {
                        lower.Add(word.ToLowerInvariant());
                    }

                    first = false;
                }
            }
        }

        // A word the book never writes in lowercase may be a name that only ever opens a sentence (the first name of a list of sample names, say), so it needs a lowercase use.
        return starts.Where(w => w.Length > 1 && !inside.Contains(w) && lower.Contains(w)).OrderBy(w => w, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// What the redaction rows say is not for the packs, as words for the denylist: the capitalized words of every source span a row matches (not the first word of the span, which is
    /// only capitalized as a sentence start), and the lowercase words of the name of every entry a drop-entry row removes. A redacted sentence is left-out text by definition, even though
    /// it sits in a taken range.
    /// </summary>
    public static (List<string> SpanWords, List<string> Names) RedactedWords(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, RedactionTable redactions, string book)
    {
        var parsed = EntriesStage.Run(blocks, ranges, book);
        var entries = parsed.Select(e => new RedactionEntry(e.Id, e.RedactionFields())).ToList();
        var scoped = new RedactionTable { Normalization = redactions.Normalization, Rows = redactions.Rows.Where(r => r.Book == null || r.Book == book).ToList() };
        var spans = RedactionStage.SourceSpans(entries, scoped);
        var spanWords = spans
            .SelectMany(s => PackWordCheck.Words(s.Text).Skip(1))
            .Where(w => w.Length > 1 && PackWordCheck.IsCapital(w))
            .Select(w => w.ToLowerInvariant())
            // Every word a row takes out, in any case: a coined word written in lowercase is as reserved as a capitalized one. A word that stays in the row's replacement is not removed.
            // The ordinary words among them (the book writes them in lowercase at least three times) are left off the denylist with the other common words.
            .Concat(spans.SelectMany(s => RemovedWords(s.Row, s.Text)))
            .Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToList();
        var names = spans.Where(s => s.Row.Action == RedactionRow.DropEntry)
            .SelectMany(s => PackWordCheck.Words(parsed.First(e => e.Id == s.Id).Name))
            .Select(w => w.ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToList();
        return (spanWords, names);
    }

    /// <summary>The lowercase words of a redacted span that the row takes out: all of them for a cut or a drop, those not in the replacement text for a replace.</summary>
    public static IEnumerable<string> RemovedWords(RedactionRow row, string span)
    {
        if (row.Action == RedactionRow.DropEntry)
        {
            return Enumerable.Empty<string>(); // the entry's name is hashed by the caller; the rest of that sentence stays what it was
        }

        var kept = row.Action == RedactionRow.Replace && row.Text != null
            ? PackWordCheck.Words(row.Text).Select(w => w.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return PackWordCheck.Words(span).Select(w => w.ToLowerInvariant()).Where(w => w.Length > 1 && !kept.Contains(w));
    }

    /// <summary>
    /// The lowercase words whose hashes go on the denylist: the capitalized words of everything the take-list leaves out (blocks no range takes, dropped sidebars
    /// and openers), minus the common ones. A word is common, and left off, when it also appears in lowercase at least <paramref name="commonAfter"/> times anywhere in
    /// the book's text. One-letter words are left off. Nothing else is, except a word in <paramref name="waivers"/> (<c>Data/degrees-denylist-waivers.txt</c>): a game term
    /// a person has reviewed and decided the packs may say.
    /// </summary>
    public static (List<string> Words, int Excluded) DenylistWords(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlySet<string> waivers, int commonAfter = 3, IReadOnlyList<(int Start, int End)>? planned = null,
        (IReadOnlyList<string> SpanWords, IReadOnlyList<string> Names)? redacted = null)
    {
        var leftOut = TakeStage.LeftOut(blocks, ranges, planned).ToList();

        // Only common words, the book keys and waived game terms stay off the list. A word that is in a taken range is NOT left off: if the packs say it and the book
        // keeps it in a left-out section too, the gate must fire, and a person decides in the waiver file whether it is a game term.
        var exclude = new HashSet<string>(waivers, StringComparer.Ordinal);
        exclude.UnionWith(CommonWords(blocks, commonAfter));
        exclude.UnionWith(DegreesOptions.Books); // the book keys are in every `source:` line, and are not names
        // The words of the take-list's own titles are in every `source:` line, and the take-list test already holds them to the allowlist gate: they are not setting names.
        exclude.UnionWith(ranges.SelectMany(r => r.Row.Paths.Concat(r.Row.Own)).SelectMany(p => p).SelectMany(PackWordCheck.Words).Select(w => w.ToLowerInvariant()));

        // A glossary range is taken only for its trait definitions: the rest of the list (the index, the other entries, the pointer-only trait lines) is left out.
        var glossaryLeftOut = ranges.Where(r => r.Row.Parser == "glossary").SelectMany(r => EntriesStage.ScanGlossary(r.BlocksIn(blocks), new HashSet<string>(StringComparer.Ordinal)).Dropped);
        var candidates = leftOut.SelectMany(b => b.Lines).Concat(glossaryLeftOut).SelectMany(PackWordCheck.Words).Where(w => w.Length > 1 && char.IsUpper(w[0]))
            .Select(w => w.ToLowerInvariant()).Concat(redacted?.SpanWords ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
        var words = candidates.Where(w => !exclude.Contains(w)).ToList();

        // The names of the entries a drop-entry row removes are reserved: they go on the list whatever their case or how often the book writes them in lowercase, with the plural
        // a trait is written in, so neither gate can pass one that a person once decided to drop.
        var reserved = (redacted?.Names ?? Array.Empty<string>()).SelectMany(n => new[] { n, n + "s", n + "es" }).ToList();
        words = words.Concat(reserved).Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToList();
        return (words, candidates.Count - candidates.Count(w => words.Contains(w)));
    }
}
