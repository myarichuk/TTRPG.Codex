using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

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
        return new BlocksOptions { MaxColumns = page => columns.GetValueOrDefault(page, 2), SidebarPages = sidebars };
    }

    public static PipelineOutput Run(BlocksResult blocks, IReadOnlyList<SpineNode> spine, IReadOnlyList<TakeRow> rows, RedactionTable redactions, string book)
    {
        var ranges = TakeStage.Resolve(spine, blocks, rows);
        var parsed = EntriesStage.Run(blocks, ranges, book);

        var redactable = parsed.Select(e => new RedactionEntry(e.Id, new Dictionary<string, string> { ["name"] = e.Name, ["description"] = e.Render() })).ToList();
        var report = RedactionStage.Run(redactable, redactions);
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
        }).ToList();

        return new PipelineOutput(ranges, parsed, final, report, ReportRows(blocks, spine, rows, redactions, final));
    }

    private static List<ReportRow> ReportRows(BlocksResult blocks, IReadOnlyList<SpineNode> spine, IReadOnlyList<TakeRow> rows, RedactionTable redactions, List<FinalEntry> final)
    {
        var result = new List<ReportRow>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.Approved)
            {
                continue;
            }

            var mine = final.Where(e => e.RowNumber == i + 1).ToList();
            var ids = mine.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var pages = TakeStage.PagesOf(spine, row).ToHashSet();
            var dropped = (blocks.Dropped ?? new List<Block>()).Where(b => pages.Contains(b.Page)).ToList();
            result.Add(new ReportRow(
                row.Chapter,
                row.Parser,
                row.Target,
                mine.Count,
                ids.ToList(),
                dropped.Count(b => b.Role != "opener"),
                dropped.Count(b => b.Role == "opener"),
                redactions.Rows.Select((r, n) => (r, n)).Where(x => ids.Contains(x.r.Entry)).Select(x => x.n + 1).ToList(),
                mine.Count(e => PageReference().IsMatch(e.Description))));
        }

        return result;
    }

    [GeneratedRegex(@"\bpages? \d+", RegexOptions.IgnoreCase)]
    private static partial Regex PageReference();

    /// <summary>
    /// The lowercase words whose hashes go on the denylist: the capitalized words of everything the take-list leaves out (blocks no range takes, dropped sidebars
    /// and openers), minus the common ones. A word is common, and left off, when it also appears in lowercase at least <paramref name="commonAfter"/> times anywhere in
    /// the book's text, when it appears in a taken range (the packs will say it), or when it is a game term on the allowlist. One-letter words are left off.
    /// </summary>
    public static (List<string> Words, int Excluded) DenylistWords(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlySet<string> gameTerms, int commonAfter = 3)
    {
        var leftOut = TakeStage.LeftOut(blocks, ranges).ToList();
        var taken = Enumerable.Range(0, blocks.Blocks.Count).Where(i => ranges.Any(r => i >= r.Start && i < r.End)).Select(i => blocks.Blocks[i]);
        var lower = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in leftOut.Concat(taken))
        {
            foreach (var word in block.Lines.SelectMany(PackWordCheck.Words).Where(w => !char.IsUpper(w[0])).Select(w => w.ToLowerInvariant()))
            {
                lower[word] = lower.GetValueOrDefault(word) + 1;
            }
        }

        var exclude = new HashSet<string>(gameTerms, StringComparer.Ordinal);
        exclude.UnionWith(lower.Where(kv => kv.Value >= commonAfter).Select(kv => kv.Key));
        foreach (var block in taken)
        {
            exclude.UnionWith(block.Lines.SelectMany(PackWordCheck.Words).Select(w => w.ToLowerInvariant()));
        }

        var candidates = leftOut.SelectMany(b => b.Lines).SelectMany(PackWordCheck.Words).Where(w => w.Length > 1 && char.IsUpper(w[0]))
            .Select(w => w.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        var words = candidates.Where(w => !exclude.Contains(w)).OrderBy(w => w, StringComparer.Ordinal).ToList();
        return (words, candidates.Count - words.Count);
    }
}
