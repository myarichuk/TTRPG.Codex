namespace Codex.ContentImport.Degrees;

/// <summary>The blocks of one taken bookmark path: <c>[Start, End)</c> in <see cref="BlocksResult.Blocks"/>.</summary>
/// <param name="Own">The range is a section's own text before its first child (<see cref="TakeRow.Own"/>); <paramref name="PathNumber"/> then counts on after the row's <see cref="TakeRow.Paths"/>.</param>
public sealed record TakenRange(TakeRow Row, int PathNumber, SpineNode Node, int Start, int End, int RowNumber = 0, bool Own = false);

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
        ["rules"] = new[] { "rules", "body", "tag", "icon", "table" },
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
        var child = spine.Skip(IndexOf(spine, node) + 1).FirstOrDefault();
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
                ranges.Add(OwnRange(spine, blocks, row, number, node) with { RowNumber = r + 1 });
            }
        }

        CheckOverlap(ranges);
        CheckOneColumnPages(spine, blocks, rowList);
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
        var where = $"Chapter {row.Chapter}, path {pathNumber}";
        var title = FindTitle(blocks.Blocks, node, 0) ?? throw new DegreesException($"{where}: no block on page {node.PageStart} has the bookmark's title.");
        var start = blocks.Blocks[title].Role == "entry-header" ? title : title + 1;

        var index = IndexOf(spine, node);
        var after = spine.Skip(index + 1).FirstOrDefault(n => n.Depth <= node.Depth);
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

            var at = FindTitle(blocks.Blocks, after, end) ?? throw new DegreesException($"{where}: the next section's title is not a block on page {after.PageStart}, so the end of the range is unknown.");
            if (Enumerable.Range(end, at - end).Any(i => Claimable.Contains(blocks.Blocks[i].Role)))
            {
                throw new DegreesException($"{where}: text sits above the next section's title on page {after.PageStart}, so the end of the range is unknown.");
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

    private static void CheckOneColumnPages(IReadOnlyList<SpineNode> spine, BlocksResult blocks, List<TakeRow> rows)
    {
        var one = new HashSet<int>(blocks.OneColumnPageNumbers ?? new List<int>());
        foreach (var row in rows.Where(r => r.Columns > 1))
        {
            var bad = PagesOf(spine, row).Where(one.Contains).ToList();
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
        var allowed = Claims[range.Row.Parser];
        var stray = new List<string>();
        for (var i = range.Start; i < range.End; i++)
        {
            var b = blocks.Blocks[i];
            if (!allowed.Contains(b.Role) || (i == range.Start && range.Row.Parser == "entries" && b.Role != "entry-header"))
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
        blocks.Blocks.Where((_, i) => !ranges.Any(r => i >= r.Start && i < r.End) && !(planned ?? Array.Empty<(int Start, int End)>()).Any(r => i >= r.Start && i < r.End))
            .Concat(blocks.Dropped ?? new List<Block>());
}
