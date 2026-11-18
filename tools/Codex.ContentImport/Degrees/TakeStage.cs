namespace Codex.ContentImport.Degrees;

/// <summary>The blocks of one taken bookmark path: <c>[Start, End)</c> in <see cref="BlocksResult.Blocks"/>.</summary>
/// <param name="Own">The range is a section's own text before its first child (<see cref="TakeRow.Own"/>); <paramref name="PathNumber"/> then counts on after the row's <see cref="TakeRow.Paths"/>.</param>
/// <param name="Extra">Indexes of blocks outside <c>[Start, End)</c> that belong to the range too: the top of the page a row says its section spills onto (<see cref="TakeRow.SpillsOnto"/>).</param>
/// <param name="LeadStart">For a row that starts at its first entry header: the index of the first block after the section's title, which the row reads past (an introduction and overview tables). Negative when there is no such lead.</param>
/// <param name="TailEnd">For a row that ends at its first block-header heading (<see cref="TakeRow.TailFromHeader"/>): the end of the section. The blocks from <c>End</c> to there are read past.</param>
public sealed record TakenRange(TakeRow Row, int PathNumber, SpineNode Node, int Start, int End, int RowNumber = 0, bool Own = false, IReadOnlyList<int>? Extra = null, int LeadStart = -1, int TailEnd = -1)
{
    /// <summary>True for a block of the lead or the tail: read past, not an entry's text, and not setting material either (it sits in the taken section).</summary>
    public bool InLead(int index) => (LeadStart >= 0 && index >= LeadStart && index < Start) || (TailEnd >= 0 && index >= End && index < TailEnd);

    /// <summary>The indexes of every block of the range, in order.</summary>
    public IEnumerable<int> Indexes => Enumerable.Range(Start, End - Start).Concat(Extra ?? Array.Empty<int>());

    public bool Contains(int index) => (index >= Start && index < End) || (Extra?.Contains(index) ?? false);

    public List<Block> BlocksIn(BlocksResult blocks) => Indexes.Select(i => blocks.Blocks[i]).ToList();
}

/// <summary>
/// Turns take-list rows into block ranges. A path's range runs from just after its own title block to its next sibling's (or ancestor's) title block,
/// both found by the bookmark title on the bookmark's page; an entry whose title is its own entry-header keeps that header. Everything that
/// can make a range ambiguous fails the run with counts and positions only: a path that is not in the book, a title block that is not on its page,
/// a one-column page, two rows that take the same block, and a block in the range that the row's parser does not claim.
/// </summary>
public static class TakeStage
{
    /// <summary>Roles each parser accepts inside a range. A block of any other role is unclaimed.</summary>
    private static readonly Dictionary<string, string[]> Claims = new(StringComparer.Ordinal)
    {
        ["entries"] = new[] { "entry-header", "rules", "body", "tag", "icon", "table" },
        // A stat table (armor, shields, weapons) is carved out of its page for the items row to read; a range of another row that holds one reads past it.
        ["rules"] = new[] { "rules", "body", "tag", "icon", "table", StatTables.Role },
        // The descriptions of the items: paragraphs that open with a name. The stat tables of their pages are the row's other half.
        ["items"] = new[] { "rules", "body", "tag", "icon", StatTables.Role },
        ["glossary"] = new[] { "body" },
        // The tradition lists: a heading per rank (entry-header) and the lines of names and summaries under it.
        ["spelllists"] = new[] { "entry-header", "body", "tag", "table" },
        // A skill's heading (section), a general action's or subsection's title (entry-header), an action's or table caption's heading (block-header) and its text.
        ["actions"] = new[] { "section", "entry-header", "block-header", "rules", "body", "tag", "icon", "table" },
        // An ancestry's pages: its flavor (entry-header and rules), the titles of its two sections, its heritages (entry-header), the level bands and feats (block-header, tag, body) and its statistics box.
        ["ancestries"] = new[] { "section", "entry-header", "block-header", "rules", "body", "tag", "icon" },
        // A versatile heritage's pages are the same parts as an ancestry's, less the statistics box.
        ["versatile"] = new[] { "section", "entry-header", "block-header", "rules", "body", "tag", "icon" },
        // A class's pages: the statistics box, flavor and feature headings (entry-header), the initial proficiencies, the advancement table, the racket and action headings and the level bands and feats (block-header), the titles of its two sections.
        ["classes"] = new[] { "section", "entry-header", "block-header", "rules", "body", "tag", "icon" },
        // The backgrounds: each one's heading (block-header) and its text.
        ["backgrounds"] = new[] { "block-header", "rules", "body", "icon" },
    };

    private static readonly string[] Claimable = { "entry-header", "rules", "body", "tag", "table" };

    /// <summary>The pages a row covers, for the page-level checks that run before the blocks exist: columns and one-column pages.</summary>
    public static List<int> PagesOf(IReadOnlyList<SpineNode> spine, TakeRow row) =>
        row.Paths.SelectMany(p => PathNode(spine, row, p) is { } n ? Enumerable.Range(n.PageStart, n.PageEnd - n.PageStart + 1) : Enumerable.Empty<int>())
            .Concat(row.Own.Select(p => PathNode(spine, row, p)).Where(n => n != null).Select(n => n!.PageStart)) // own text sits on the section's first page
            .Distinct().OrderBy(p => p).ToList();

    /// <summary>The blocks of a section's own text: from after its title block to the title block of its first child section.</summary>
    private static TakenRange OwnRange(IReadOnlyList<SpineNode> spine, BlocksResult blocks, TakeRow row, int pathNumber, SpineNode node)
    {
        var where = $"Chapter {row.Chapter}, own-text path {pathNumber - row.Paths.Count}";
        var title = FindTitle(blocks.Blocks, node, 0) ?? throw new DegreesException($"{where}: no block on page {node.PageStart} has the bookmark's title.");
        // A sidebar bookmark between the section and its first child is no child: the sidebar is split out of the blocks, so its title is not one.
        var child = spine.Skip(IndexOf(spine, node) + 1).FirstOrDefault(n => !n.Sidebar);
        if (child == null || child.Depth != node.Depth + 1)
        {
            throw new DegreesException($"{where}: the section has no child section, so it has no 'own text before the first child'.");
        }

        var end = FindTitle(blocks.Blocks, child, title + 1) ?? throw new DegreesException($"{where}: the first child's title is not a block on page {child.PageStart}.");
        return new TakenRange(row, pathNumber, node, title + 1, Math.Max(title + 1, end), 0, true);
    }

    /// <summary>The block ranges of planned paths (<see cref="TakeList.Planned"/>): the whole section, children included. They are only read for the denylist.</summary>
    public static List<(int Start, int End)> PlannedRanges(IReadOnlyList<SpineNode> spine, BlocksResult blocks, string book, IEnumerable<PlannedRow> planned)
    {
        var result = new List<(int, int)>();
        var row = new TakeRow { Chapter = "planned", Book = book };
        foreach (var path in planned.Where(p => p.Book == book).SelectMany(p => p.Paths))
        {
            var node = SpineStage.FindUnique(spine, path) ?? throw new DegreesException("A planned bookmark path is not in the book.");
            try
            {
                var range = Range(spine, blocks, row, 0, node);
                result.Add((range.Start, range.End));
            }
            catch (DegreesException)
            {
                // The section's edges are not exact blocks (the next title is not found, say). A planned section is not parsed, so whole pages up to the page the next
                // section starts on will do: a page the two share is left out, which only hashes more words, never fewer.
                var next = spine.Skip(IndexOf(spine, node) + 1).FirstOrDefault(n => n.Depth <= node.Depth);
                var stop = next?.PageStart ?? int.MaxValue;
                var first = blocks.Blocks.FindIndex(b => b.Page >= node.PageStart && b.Page < stop);
                var last = blocks.Blocks.FindLastIndex(b => b.Page >= node.PageStart && b.Page < stop);
                if (first >= 0)
                {
                    result.Add((first, last + 1));
                }
            }
        }

        return result;
    }

    public static List<TakenRange> Resolve(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakeRow> rows)
    {
        var ranges = new List<TakenRange>();
        var rowList = rows.ToList();
        for (var r = 0; r < rowList.Count; r++)
        {
            var row = rowList[r];
            if (!TakeList.BuiltParsers.Contains(row.Parser))
            {
                throw new DegreesException($"Take-list row for chapter {row.Chapter}: the {row.Parser} parser is not built yet.");
            }

            for (var p = 0; p < row.Paths.Count; p++)
            {
                var node = PathNode(spine, row, row.Paths[p]) ?? throw new DegreesException($"Chapter {row.Chapter}, path {p + 1}: no bookmark has that path.");
                ranges.Add(Range(spine, blocks, row, p + 1, node) with { RowNumber = r + 1 });
            }

            for (var o = 0; o < row.Own.Count; o++)
            {
                var number = row.Paths.Count + o + 1;
                var node = PathNode(spine, row, row.Own[o]) ?? throw new DegreesException($"Chapter {row.Chapter}, own-text path {o + 1}: no bookmark has that path.");
                var own = OwnRange(spine, blocks, row, number, node);
                if (row.TailFromHeader)
                {
                    // An own-text range reads its tail past too (the languages' tables, between the section's text and its first child): it ends at its first block-header heading, which must exist.
                    var caption = Enumerable.Range(own.Start, Math.Max(0, own.End - own.Start)).Where(i => blocks.Blocks[i].Role == "block-header").Cast<int?>().FirstOrDefault();
                    own = caption is { } at ? own with { End = at, TailEnd = own.End } : throw new DegreesException($"Chapter {row.Chapter}, own-text path {o + 1}: the row reads the tail of its section past, but no heading in the block-header font starts one.");
                }

                ranges.Add(own with { RowNumber = r + 1 });
            }
        }

        CheckOverlap(ranges);
        CheckOneColumnPages(spine, blocks, rowList, ranges);
        CheckSidebarPages(spine, blocks, rowList);
        foreach (var range in ranges)
        {
            CheckClaimed(blocks, range);
        }

        return ranges;
    }

    private static SpineNode? PathNode(IReadOnlyList<SpineNode> spine, TakeRow row, List<string> path) => SpineStage.FindUnique(spine, path);

    private static TakenRange Range(IReadOnlyList<SpineNode> spine, BlocksResult blocks, TakeRow row, int pathNumber, SpineNode node)
    {
        var taken = RangeFromTitle(spine, blocks, row, pathNumber, node);
        if (row.TailFromHeader)
        {
            var caption = Enumerable.Range(taken.Start, Math.Max(0, taken.End - taken.Start)).Where(i => blocks.Blocks[i].Role == "block-header").Cast<int?>().FirstOrDefault();
            // A row that reads a tail past must find where it starts: a range with no heading in it would silently be taken whole.
            return caption is { } at ? taken with { End = at, TailEnd = taken.End } : throw new DegreesException($"Chapter {row.Chapter}, path {pathNumber}: the row reads the tail of its section past, but no heading in the block-header font starts one.");
        }

        // A row with groups starts at the section's first group heading, which is the first block after its title: nothing of the section comes before the headings.
        if (row.LevelWord == null || row.Groups)
        {
            return taken;
        }

        var first = Enumerable.Range(taken.Start, Math.Max(0, taken.End - taken.Start)).Where(i => row.IsHeader(blocks.Blocks[i])).Cast<int?>().FirstOrDefault()
            ?? throw new DegreesException($"Chapter {row.Chapter}, path {pathNumber}: no block of the range has an entry header's shape, so the first entry is unknown.");
        return taken with { Start = first, LeadStart = taken.Start };
    }

    private static TakenRange RangeFromTitle(IReadOnlyList<SpineNode> spine, BlocksResult blocks, TakeRow row, int pathNumber, SpineNode node)
    {
        var where = $"Chapter {row.Chapter}, path {pathNumber}";
        var title = FindTitle(blocks.Blocks, node, 0) ?? throw new DegreesException($"{where}: no block on page {node.PageStart} has the bookmark's title.");
        // An entry keeps its own header block; any other section (a rule's, an actions section's) starts after its title.
        var start = row.Parser == "entries" && blocks.Blocks[title].Role == (row.HeaderRole ?? "entry-header") ? title : title + 1;

        var index = IndexOf(spine, node);
        // A sidebar bookmark does not end a section: the sidebar is split out of the blocks, so its title is not one, and the section goes on past it.
        var after = spine.Skip(index + 1).FirstOrDefault(n => n.Depth <= node.Depth && !n.Sidebar);
        if (row.SpillsOnto != null && after?.PageStart != row.SpillsOnto)
        {
            throw new DegreesException($"{where}: the row says the section spills onto page {row.SpillsOnto}, but the next section does not start on page {row.SpillsOnto}.");
        }
        int end;
        if (after == null)
        {
            end = blocks.Blocks.Count;
        }
        else if (after.PageStart > node.PageEnd)
        {
            // The next section starts on a page of its own: cut at the page boundary, as the spine's page ranges do. But if its title block is not first on
            // the page and text this parser would read sits above it, the section starts mid-page and the boundary is not known.
            end = blocks.Blocks.FindIndex(title + 1, b => b.Page >= after.PageStart);
            if (end < 0)
            {
                end = blocks.Blocks.Count;
            }

            if (row.SpillsOnto == after.PageStart)
            {
                // The row says its section's last lines sit on the page the next section starts on, above that section's own text. The edge is found by position, below.
                return new TakenRange(row, pathNumber, node, start, Math.Max(start, end), 0, false, SpillBlocks(blocks.Blocks, row.SpillsOnto.Value, end, where));
            }

            if (row.EndsAtPageFor(node.Path))
            {
                // The row says the next section's page opens with no title block of its own (class summaries open with a list of entries): the section ends where its page does.
                return new TakenRange(row, pathNumber, node, start, Math.Max(start, end));
            }

            var at = FindTitle(blocks.Blocks, after, end) ?? throw new DegreesException($"{where}: the next section's title is not a block on page {after.PageStart}, so the end of the range is unknown.");
            if (Enumerable.Range(end, at - end).Any(i => Claimable.Contains(blocks.Blocks[i].Role)))
            {
                if (!row.RunsToTitleFor(node.Path))
                {
                    throw new DegreesException($"{where}: text sits above the next section's title on page {after.PageStart}, so the end of the range is unknown.");
                }

                // The row says its sections' text goes on over a page break (past a page of sidebars, say) above the next title: the blocks are in reading order, so the range runs to the title.
                end = at;
            }
        }
        else if (FindTitle(blocks.Blocks, after, title + 1) is { } next)
        {
            end = next;
        }
        else
        {
            throw new DegreesException($"{where}: the next section's title is not a block on page {after.PageStart}, so the end of the range is unknown.");
        }

        return new TakenRange(row, pathNumber, node, start, Math.Max(start, end));
    }

    /// <summary>
    /// The blocks of a section that spills onto a page it shares with the next section: on that page, the first block of each column, when it starts at the page's top edge
    /// (within three points of the highest block on the page before). A column whose first block starts lower holds none of the section. <paramref name="from"/> is the
    /// index of the first block of the shared page.
    /// </summary>
    private static List<int> SpillBlocks(List<Block> blocks, int page, int from, string where)
    {
        var top = blocks.Where(b => b.Page == page - 1).Select(b => b.Top).DefaultIfEmpty(double.MinValue).Max();
        var result = new List<int>();
        var seen = new HashSet<int>();
        for (var i = from; i < blocks.Count && blocks[i].Page == page; i++)
        {
            if (seen.Add(blocks[i].Column) && blocks[i].Top >= top - 3)
            {
                result.Add(i);
            }
        }

        if (result.Count == 0)
        {
            throw new DegreesException($"{where}: the row says the section spills onto page {page}, but no column there starts at the page's top edge.");
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<SpineNode> spine, SpineNode node)
    {
        for (var i = 0; i < spine.Count; i++)
        {
            if (ReferenceEquals(spine[i], node))
            {
                return i;
            }
        }

        throw new DegreesException("A bookmark is not in the spine.");
    }

    /// <summary>The first block, from <paramref name="from"/> on, on the node's first page whose text is the node's title (case, spacing and punctuation aside).</summary>
    private static int? FindTitle(List<Block> blocks, SpineNode node, int from)
    {
        var want = Key(node.Title);
        for (var i = from; i < blocks.Count; i++)
        {
            if (blocks[i].Page > node.PageStart)
            {
                break;
            }

            if (blocks[i].Page == node.PageStart && blocks[i].Role != "table" && Key(string.Join(' ', blocks[i].Lines)) == want)
            {
                return i;
            }

            // A chapter's title is two blocks in the section font ("CHAPTER 5:" above "FEATS"): a run of them matches, and the last of the run is the title block.
            if (blocks[i].Page == node.PageStart && blocks[i].Role == "section")
            {
                var text = string.Empty;
                for (var j = i; j < blocks.Count && blocks[j].Page == node.PageStart && blocks[j].Role == "section"; j++)
                {
                    text += string.Join(' ', blocks[j].Lines);
                    if (j > i && Key(text) == want)
                    {
                        return j;
                    }
                }
            }
        }

        return null;
    }

    private static string Key(string text) => new(SpanText.Normalize(text).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void CheckOverlap(List<TakenRange> ranges)
    {
        for (var a = 0; a < ranges.Count; a++)
        {
            for (var b = a + 1; b < ranges.Count; b++)
            {
                if (ranges[a].Start < ranges[b].End && ranges[b].Start < ranges[a].End)
                {
                    throw new DegreesException($"Chapter {ranges[a].Row.Chapter} path {ranges[a].PathNumber} and chapter {ranges[b].Row.Chapter} path {ranges[b].PathNumber} take the same blocks.");
                }
            }
        }
    }

    private static void CheckOneColumnPages(IReadOnlyList<SpineNode> spine, BlocksResult blocks, List<TakeRow> rows, List<TakenRange> ranges)
    {
        var one = new HashSet<int>(blocks.OneColumnPageNumbers ?? new List<int>());
        foreach (var row in rows.Where(r => r.Columns > 1))
        {
            // The pages of a lead (the introduction before the first entry) are read past, so their reading order does not matter.
            var firstEntryPage = ranges.Where(r => r.Row == row && r.LeadStart >= 0).Select(r => blocks.Blocks[r.Start].Page).DefaultIfEmpty(0).Min();
            var bad = PagesOf(spine, row).Where(p => p >= firstEntryPage && one.Contains(p)).ToList();
            if (bad.Count > 0)
            {
                throw new DegreesException($"Chapter {row.Chapter}: {bad.Count} one-column page(s) in a taken range, so reading order is not known (pages {string.Join(",", bad)}).");
            }
        }
    }

    private static void CheckSidebarPages(IReadOnlyList<SpineNode> spine, BlocksResult blocks, List<TakeRow> rows)
    {
        var problems = new HashSet<int>(blocks.SidebarProblems ?? new List<int>());
        foreach (var row in rows)
        {
            var bad = PagesOf(spine, row).Where(problems.Contains).ToList();
            if (bad.Count > 0)
            {
                throw new DegreesException($"Chapter {row.Chapter}: the sidebar split is not backed by the bookmarks on {bad.Count} page(s) of a taken range, so sidebar text may be dropped or kept wrongly (pages {string.Join(",", bad)}).");
            }
        }
    }

    private static void CheckClaimed(BlocksResult blocks, TakenRange range)
    {
        var allowed = Claims[range.Row.Parser].Concat(range.Row.HeaderRole == null ? Array.Empty<string>() : new[] { range.Row.HeaderRole })
            .Concat(range.Row.Subheadings ? new[] { "block-header" } : Array.Empty<string>())
            .Concat(range.Row.FoldChildren ? new[] { "entry-header" } : Array.Empty<string>())
            .Concat(range.Row.Groups ? new[] { "section" } : Array.Empty<string>()).ToArray();
        // A row that names its boxes (or says how many it reads past) holds headings of the header role that have no entry's shape: each starts a box.
        var boxes = range.Row.Boxes.Count > 0 || range.Row.ReadPastBoxes > 0;
        var stray = new List<string>();
        foreach (var i in range.Indexes)
        {
            var b = blocks.Blocks[i];
            // With a header role shared with other things (the block-header font), a block of that role that is not a header's shape belongs to no entry.
            if (!allowed.Contains(b.Role) || (!boxes && b.Role == range.Row.HeaderRole && b.Role != "entry-header" && !range.Row.IsHeader(b))
                || (i == range.Start && range.Row.Parser == "entries" && !range.Row.Groups && !range.Row.IsHeader(b))
                || (range.Row.Groups && i == range.Start && b.Role is not ("section" or "entry-header")))
            {
                stray.Add($"{b.Role} on page {b.Page} column {b.Column}");
            }
        }

        if (stray.Count > 0)
        {
            throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: {stray.Count} block(s) in the range that the {range.Row.Parser} parser does not claim: {string.Join("; ", stray.Take(10))}.");
        }
    }

    /// <summary>Every block that no range takes, with the dropped sidebar and opener blocks: the text the denylist is made from.</summary>
    public static IEnumerable<Block> LeftOut(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlyList<(int Start, int End)>? planned = null) =>
        blocks.Blocks.Where((_, i) => !ranges.Any(r => r.Contains(i) || r.InLead(i)) && !(planned ?? Array.Empty<(int Start, int End)>()).Any(r => i >= r.Start && i < r.End))
            .Concat(blocks.Dropped ?? new List<Block>());
}
