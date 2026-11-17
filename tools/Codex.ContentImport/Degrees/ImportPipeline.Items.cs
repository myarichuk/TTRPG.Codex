using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>What one <c>items</c> row read, counted from the document: the table rows and the descriptions each way, and what the row leaves out.</summary>
public sealed record ItemCounts(string Chapter, string Kind, int TableRows, int Descriptions, int Excluded, int Entries, int Tables, int ArtLabels, int LeadLines, Dictionary<string, int> PerCategory);

/// <summary>What a regeneration from scratch did to one hash file: how many it writes, keeps and adds, and why the others went (see <see cref="ImportPipeline.ReviewFresh"/>).</summary>
public sealed record FreshReview(string File, int Written, int Kept, int New, int Waived, int Taken, int Titles);

public static partial class ImportPipeline
{
    /// <summary>
    /// A section whose child sections are folded into one entry (<see cref="TakeRow.FoldChildren"/>): the bookmark children of the section against the headings in the entry-header font in its range, by title and in order.
    /// A heading with no bookmark or a bookmark with no heading fails the run. Returns the (children, headings) of each such range.
    /// </summary>
    public static List<(int Children, int Headings)> CheckFoldCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges)
    {
        var compared = new List<(int, int)>();
        foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "rules" && r.Row.FoldChildren))
        {
            var node = range.Node;
            var children = spine.Where(n => n.Path.Length == node.Path.Length + 1 && n.Path.Take(node.Path.Length).SequenceEqual(node.Path) && !n.Sidebar)
                .Select(n => BlocksStage.SidebarTitleSet.Key(n.Title)).ToList();
            var headings = Enumerable.Range(range.Start, Math.Max(0, range.End - range.Start)).Select(i => blocks.Blocks[i]).Where(b => b.Role == "entry-header")
                .Select(b => BlocksStage.SidebarTitleSet.Key(string.Join(' ', b.Lines))).ToList();
            compared.Add((children.Count, headings.Count));
            if (!children.SequenceEqual(headings))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the section has {children.Count} bookmark children and {headings.Count} headings in the entry-header font in its range, and their titles differ.");
            }
        }

        return compared;
    }

    /// <summary>
    /// The column labels of an items row's tables against the book: each label but the price must be a title of a bookmark of the chapter (or the short name in its parentheses: <c>Dexterity Modifier Cap (Dex Cap)</c>),
    /// so a header the take-list names is a statistic the chapter explains. A stat table on any page that no items row reads fails too.
    /// </summary>
    public static void CheckTableLabels(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakeRow> rows)
    {
        var itemRows = rows.Where(r => r.Parser == "items").ToList();
        // The gear tables' labels are the headers of a price list (a name, a price, a Bulk, a trait column), not the statistics the chapter's sections explain; they are counted by GearCounts instead.
        foreach (var row in itemRows.Where(r => r.ItemKind != "gear"))
        {
            var chapter = row.Paths[0][0];
            var titles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in spine.Where(n => n.Path.Length > 0 && n.Path[0].Equals(chapter, StringComparison.OrdinalIgnoreCase) && !n.Sidebar))
            {
                titles.Add(BlocksStage.SidebarTitleSet.Key(n.Title));
                var paren = Regex.Match(n.Title, @"\(([^)]+)\)");
                if (paren.Success)
                {
                    titles.Add(BlocksStage.SidebarTitleSet.Key(paren.Groups[1].Value));
                }

                // A title that is two of them (HP (BT)) is also its own words.
                titles.Add(BlocksStage.SidebarTitleSet.Key(Regex.Replace(n.Title, @"\s*\(.*\)", string.Empty)));
            }

            foreach (var label in row.TableColumns.SelectMany(c => c).Distinct().Where(l => l != "Price"))
            {
                if (!titles.Contains(BlocksStage.SidebarTitleSet.Key(label)) && !titles.Contains(BlocksStage.SidebarTitleSet.Key(Regex.Replace(label, @"\s*\(.*\)", string.Empty))))
                {
                    throw new DegreesException($"Chapter {row.Chapter}: a table column label of an items row is not the title of any bookmark of its chapter.");
                }
            }
        }

        var claimed = itemRows.SelectMany(r => ItemRows.TablesOf(blocks, spine, r)).ToHashSet();
        var unclaimed = blocks.Blocks.Where(b => b.Role == StatTables.Role && !claimed.Contains(b)).ToList();
        if (unclaimed.Count > 0)
        {
            throw new DegreesException($"{unclaimed.Count} stat table(s) on page(s) {string.Join(",", unclaimed.Select(b => b.Page).Distinct())} are read by no items row.");
        }
    }

    /// <summary>
    /// Counts an items row's entries independently, from the document: its table rows (read by position) against its descriptions (read by their bold names), less the rows the row excludes.
    /// <see cref="EntriesStage"/> fails the run when the two do not agree name by name; this reports the counts, and per category.
    /// </summary>
    public static List<ItemCounts> CheckItemCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var result = new List<ItemCounts>();
        foreach (var group in ranges.Where(r => r.Row.Parser == "items" && r.Row.ItemKind == "gear").GroupBy(r => r.RowNumber))
        {
            result.Add(GearCounts(spine, blocks, group.ToList(), parsed));
        }

        foreach (var range in ranges.Where(r => r.Row.Parser == "items" && r.Row.ItemKind != "gear"))
        {
            var rows = ItemRows.Read(blocks, spine, range.Row);
            var (descriptions, art, lead) = EntriesStage.ReadDescriptions(range.BlocksIn(blocks), range.Row, new HashSet<string>(StringComparer.Ordinal));
            var excluded = range.Row.Exclude.Select(ItemRows.Key).ToHashSet(StringComparer.Ordinal);
            var kept = rows.Where(r => !excluded.Contains(ItemRows.Key(r.Name))).ToList();
            var items = parsed.Count(e => e.RowNumber == range.RowNumber && e.Type == range.Row.Target);
            var described = descriptions.Count(d => !excluded.Contains(ItemRows.Key(d.Name)));
            result.Add(new ItemCounts(range.Row.Chapter, range.Row.ItemKind!, rows.Count, descriptions.Count, excluded.Count, items, ItemRows.TablesOf(blocks, spine, range.Row).Count, art, lead,
                kept.GroupBy(r => r.Category + (r.Attack == null ? string.Empty : "/" + r.Attack)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count())));
            // The rows and the descriptions, counted apart, make the same items: a description of a row, less the descriptions that belong to no row (ammunition), plus the rows with none.
            if (kept.Count != items || described - range.Row.RuleDescriptions.Count + range.Row.Undescribed.Count != items)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}: {kept.Count} table rows and {described} descriptions make {items} items.");
            }
        }

        return result;
    }

    /// <summary>
    /// The counts of a gear row, from the document: the table rows by position (and the headings they sit under), the descriptions by their bold names, and the rows that have none. The rows are the descriptions' rows,
    /// plus the rows that read the description of their heading or of the row above, plus the undescribed ones; any other total fails. Per kind, the rows are counted too.
    /// </summary>
    public static ItemCounts GearCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, List<TakenRange> rowRanges, IReadOnlyList<ParsedEntry> parsed)
    {
        var row = rowRanges[0].Row;
        var (rows, bands) = GearRows.Read(blocks, spine, row);
        var descriptions = new List<EntriesStage.ItemDescription>();
        int art = 0, lead = 0;
        foreach (var r in rowRanges)
        {
            var read = EntriesStage.ReadDescriptions(r.BlocksIn(blocks), row, new HashSet<string>(StringComparer.Ordinal));
            descriptions.AddRange(read.Descriptions);
            art += read.ArtLabels;
            lead += read.LeadLines;
        }

        var keys = descriptions.Select(d => GearRows.Key(d.Name)).ToHashSet(StringComparer.Ordinal);
        var own = rows.Count(r => keys.Contains(GearRows.Key(r.Name)));
        var inherited = rows.Count(r => !keys.Contains(GearRows.Key(r.Name)) && r.Parent != null && keys.Contains(GearRows.Key(r.Parent)));
        var ofBands = descriptions.Count(d => bands.Any(b => GearRows.Key(b) == GearRows.Key(d.Name)));
        var undescribedKeys = row.Undescribed.Select(GearRows.Key).ToHashSet(StringComparer.Ordinal);
        var undescribed = rows.Count(r => undescribedKeys.Contains(GearRows.Key(r.Name)));
        var items = parsed.Count(e => e.RowNumber == rowRanges[0].RowNumber && e.Type == row.Target);
        var perKind = rows.GroupBy(r => r.Kind + (r.Uncommon ? "/uncommon" : string.Empty)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        // The descriptions are of rows or of headings; a row reads one of its own, or its heading's or its row's, or is undescribed (a name may be two rows: the sizes of two barding tables).
        if (rows.Count != items || own + ofBands != descriptions.Count || own + inherited + undescribed != items)
        {
            throw new DegreesException($"Chapter {row.Chapter}: {rows.Count} gear table rows and {descriptions.Count} descriptions ({own} of rows, {ofBands} of headings) make {items} items, {inherited} reading a heading's or a row's description and {undescribed} undescribed.");
        }

        return new ItemCounts(row.Chapter, "gear", rows.Count, descriptions.Count, 0, items, ItemRows.TablesOf(blocks, spine, row).Count, art, lead, perKind);
    }
}
