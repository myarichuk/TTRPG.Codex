namespace Codex.ContentImport.Degrees;

/// <summary>Where one line of a block sits, and the bold run it starts with (a field label such as a degree name), or empty.</summary>
public sealed record LineInfo(double Left, double Right, string Label);

/// <summary>
/// A run of lines of one role inside one column of one page. A <c>table</c> block has <see cref="Rows"/> (the first row is the header) and its
/// <see cref="Lines"/> are the rows joined with " | ".
/// </summary>
public sealed record Block(int Page, int Column, string Role, double Left, double Bottom, double Right, double Top, List<string> Lines, List<LineInfo>? Info = null, List<List<string>>? Rows = null);

/// <param name="SuspectPages">One-column pages with plenty of text on both halves: a gutter was probably missed, so reading order may be garbled.</param>
/// <param name="Dropped">Sidebar and opener blocks that were split out. They are kept only for the denylist, never parsed.</param>
/// <param name="SidebarProblems">Sidebar pages where the split is not backed by the bookmarks (too few sidebars) or body text follows a dropped sidebar; a taken range must not contain one.</param>
/// <param name="RejectedTableHeaders">Two-cell block-header lines whose lines below did not line up as table rows (stat-block headers, mostly): counted, left as ordinary blocks.</param>
/// <param name="OneColumnPageNumbers">Every page with body text and no gutter; a taken range must not contain one.</param>
public sealed record BlocksResult(
    List<Block> Blocks,
    int OneColumnPages,
    int TwoColumnPages,
    int FurnitureWords,
    Dictionary<string, int> BlocksByRole,
    List<int> SuspectPages,
    List<Block>? Dropped = null,
    List<int>? OneColumnPageNumbers = null,
    int ThreeOrMoreColumnPages = 0,
    int SidebarBlocks = 0,
    int OpenerWords = 0,
    int Tables = 0,
    List<int>? SidebarProblems = null,
    int RejectedTableHeaders = 0);

/// <summary>What the take-list and the spine tell the blocks stage about pages. Defaults: two columns everywhere and no sidebar pages.</summary>
public sealed class BlocksOptions
{
    /// <summary>The most columns a page may be split into. Pages of a taken row with <c>columns: 3</c> or more get a gutter search inside each column.</summary>
    public Func<int, int> MaxColumns { get; init; } = _ => 2;

    /// <summary>Pages covered by a "Sidebar" bookmark. Only there is a block-header run split out as a sidebar and dropped.</summary>
    public IReadOnlySet<int> SidebarPages { get; init; } = new HashSet<int>();

    /// <summary>How many "Sidebar" bookmarks start on each page. A sidebar page that yields fewer sidebars than that is reported in <see cref="BlocksResult.SidebarProblems"/>.</summary>
    public IReadOnlyDictionary<int, int> SidebarStarts { get; init; } = new Dictionary<int, int>();
}

/// <summary>
/// Stage 3: the words of each page without its furniture and openers, split into columns by the gap between them, cut into lines by baseline,
/// and into blocks by role. Columns are split per page because the furniture, and sometimes the gutter, change sides between pages.
/// Tables are rebuilt from word boxes (see <see cref="BuildTable"/>). Sidebars are split out and dropped on pages the spine marks.
/// </summary>
public static class BlocksStage
{
    private const double GutterMin = 8;
    private const double LineTolerance = 2.5;
    private const double BlockGap = 6;
    private const int SuspectWords = 20;
    private const int SubGutterWords = 40;

    /// <summary>Two words on one line further apart than this belong to different table cells; the word gap inside a cell is a space.</summary>
    private const double CellGap = 10;

    /// <summary>Two heading words across a gutter are one heading when no more than a word space apart.</summary>
    private const double SpanGap = 7;

    /// <summary>A table row line may sit at most this far (in points) below the line above it.</summary>
    private const double TableRowGap = 14;

    /// <summary>A word belongs to the last column whose header starts at or before the word's left edge, give or take this much.</summary>
    private const double CellSlack = 4;

    /// <summary>Roles that never become blocks: page furniture, slug text outside the page box, and production metadata. Openers are split out separately.</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "furniture", LayoutStage.OffPage, "meta" };

    private static readonly HashSet<string> TableRoles = new(StringComparer.Ordinal) { "body", "rules", "tag", "icon", "block-header" };

    private static readonly HashSet<string> SidebarRoles = new(StringComparer.Ordinal) { "block-header", "body", "tag", "icon", "display" };

    public static BlocksResult Run(IEnumerable<LayoutPage> pages, BlocksOptions? options = null)
    {
        options ??= new BlocksOptions();
        var blocks = new List<Block>();
        var dropped = new List<Block>();
        var oneNumbers = new List<int>();
        int one = 0, multi = 0, three = 0, furniture = 0, openerWords = 0, sidebarBlocks = 0;
        var suspect = new List<int>();
        var problems = new List<int>();
        var rejected = new int[1];
        foreach (var page in pages)
        {
            var openers = page.Words.Where(w => w.Role == "opener").ToList();
            var body = page.Words.Where(w => !Excluded.Contains(w.Role) && w.Role != "opener").ToList();
            furniture += page.Words.Count - body.Count - openers.Count;
            openerWords += openers.Count(w => !string.IsNullOrWhiteSpace(w.Text));
            if (openers.Count > 0)
            {
                dropped.AddRange(ColumnBlocks(page.Number, 0, openers, rejected));
            }

            if (body.Count == 0)
            {
                continue;
            }

            var gutters = FindGutters(body, page.Width, options.MaxColumns(page.Number));
            if (gutters.Count == 0)
            {
                one++;
                oneNumbers.Add(page.Number);
                var half = page.Width / 2;
                if (body.Count(w => Centre(w) < half) >= SuspectWords && body.Count(w => Centre(w) >= half) >= SuspectWords)
                {
                    suspect.Add(page.Number);
                }
            }
            else
            {
                multi++;
                if (gutters.Count >= 2)
                {
                    three++;
                }
            }

            var which = AssignColumns(body, gutters);
            var columns = Enumerable.Range(0, gutters.Count + 1)
                .Select(c => body.Where((_, i) => which[i] == c).ToList())
                .ToList();
            var sidebarPage = options.SidebarPages.Contains(page.Number);
            var runs = 0;
            var bodyAfterSidebar = false;
            var endedInside = false;
            for (var c = 0; c < columns.Count; c++)
            {
                var found = ColumnBlocks(page.Number, c, columns[c], rejected).ToList();
                if (sidebarPage)
                {
                    var split = SplitSidebars(found, endedInside);
                    endedInside = split.EndsInside;
                    sidebarBlocks += split.Dropped.Count;
                    dropped.AddRange(split.Dropped);
                    runs += split.Runs;
                    bodyAfterSidebar |= split.BodyAfter;
                    found = split.Kept;
                }

                blocks.AddRange(found);
            }

            // Every sidebar bookmark that starts on this page must have found at least one sidebar, and body text must not run straight on from one.
            if (sidebarPage && (bodyAfterSidebar || runs < options.SidebarStarts.GetValueOrDefault(page.Number)))
            {
                problems.Add(page.Number);
            }
        }

        var byRole = blocks.GroupBy(b => b.Role).ToDictionary(g => g.Key, g => g.Count());
        return new BlocksResult(blocks, one, multi, furniture, byRole, suspect, dropped, oneNumbers, three, sidebarBlocks, openerWords, blocks.Count(b => b.Role == "table"), problems, rejected[0]);
    }

    /// <summary>
    /// The column of each word: how many gutters lie left of its centre. A heading in the section or display font that runs across a gutter (a title
    /// centred over both columns) is one line, so all of it goes to the first column it touches.
    /// </summary>
    private static int[] AssignColumns(List<LayoutWord> words, List<double> gutters)
    {
        var which = words.Select(w => gutters.Count(g => Centre(w) >= g)).ToArray();
        var headings = Enumerable.Range(0, words.Count).Where(i => words[i].Role is "section" or "display").OrderByDescending(i => words[i].Bottom).ToList();
        var line = new List<int>();
        void Flush()
        {
            if (line.Count > 1 && line.Select(i => which[i]).Distinct().Count() > 1)
            {
                var ordered = line.OrderBy(i => words[i].Left).ToList();
                if (Enumerable.Range(1, ordered.Count - 1).All(k => words[ordered[k]].Left - words[ordered[k - 1]].Right <= SpanGap))
                {
                    var first = ordered.Min(i => which[i]);
                    foreach (var i in ordered)
                    {
                        which[i] = first;
                    }
                }
            }

            line.Clear();
        }

        foreach (var i in headings)
        {
            if (line.Count > 0 && Math.Abs(words[line[0]].Bottom - words[i].Bottom) > LineTolerance)
            {
                Flush();
            }

            line.Add(i);
        }

        Flush();
        return which;
    }

    /// <summary>The x of the middle of the widest empty vertical band near the centre of the page, or null when the page has one column.</summary>
    public static double? FindGutter(IReadOnlyList<LayoutWord> words, double pageWidth)
    {
        var gap = BestGap(words, pageWidth, pageWidth * 0.3, pageWidth * 0.7);
        return gap?.X;
    }

    /// <summary>
    /// The x of every gutter on a page, left to right. The first is the central one (<see cref="FindGutter"/>). While the page may have more columns,
    /// the widest empty band in the middle of any one column is a further gutter.
    /// </summary>
    public static List<double> FindGutters(IReadOnlyList<LayoutWord> words, double pageWidth, int maxColumns)
    {
        var gutters = new List<double>();
        if (maxColumns < 2 || FindGutter(words, pageWidth) is not { } first)
        {
            return gutters;
        }

        gutters.Add(first);
        while (gutters.Count + 1 < maxColumns)
        {
            (double X, double Width)? best = null;
            for (var c = 0; c <= gutters.Count; c++)
            {
                var column = words.Where(w => gutters.Count(g => Centre(w) >= g) == c).ToList();
                if (column.Count < SubGutterWords)
                {
                    continue;
                }

                var left = column.Min(w => w.Left);
                var right = column.Max(w => w.Right);
                var gap = BestGap(column, pageWidth, left + (right - left) * 0.2, right - (right - left) * 0.2);
                if (gap != null && (best == null || gap.Value.Width > best.Value.Width))
                {
                    best = gap;
                }
            }

            if (best == null)
            {
                break;
            }

            gutters.Add(best.Value.X);
            gutters.Sort();
        }

        return gutters;
    }

    private static (double X, double Width)? BestGap(IReadOnlyList<LayoutWord> words, double pageWidth, double loX, double hiX)
    {
        const double Bin = 2;
        var bins = (int)Math.Ceiling(pageWidth / Bin) + 1;
        var cover = new int[bins];
        foreach (var w in words)
        {
            var from = Math.Max(0, (int)(w.Left / Bin));
            var to = Math.Min(bins - 1, (int)(w.Right / Bin));
            for (var b = from; b <= to; b++)
            {
                cover[b]++;
            }
        }

        var allowed = Math.Max(1, words.Count / 100);
        var lo = Math.Max(0, (int)(loX / Bin));
        var hi = Math.Min(bins - 1, (int)(hiX / Bin));
        (int Start, int Length) best = (0, 0);
        for (var b = lo; b <= hi;)
        {
            if (cover[b] > allowed)
            {
                b++;
                continue;
            }

            var start = b;
            while (b <= hi && cover[b] <= allowed)
            {
                b++;
            }

            if (b - start > best.Length)
            {
                best = (start, b - start);
            }
        }

        return best.Length * Bin >= GutterMin ? ((best.Start + best.Length / 2.0) * Bin, best.Length * Bin) : null;
    }

    private static double Centre(LayoutWord w) => (w.Left + w.Right) / 2;

    private sealed record Line(List<LayoutWord> Words, string Role, double Top, double Bottom, double Left, double Right, string Text, string Label);

    private static List<Line> Lines(List<LayoutWord> words)
    {
        var rows = new List<List<LayoutWord>>();
        // The extractor yields each space as a word of its own. A line is its real words: the spaces would hide the gaps between table cells.
        foreach (var w in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderByDescending(w => w.Bottom))
        {
            var row = rows.Count > 0 && Math.Abs(rows[^1][0].Bottom - w.Bottom) <= LineTolerance ? rows[^1] : null;
            if (row == null)
            {
                rows.Add(row = new List<LayoutWord>());
            }

            row.Add(w);
        }

        return rows.Select(row =>
        {
            var ordered = row.OrderBy(w => w.Left).ToList();
            return new Line(ordered, LineRole(ordered), ordered.Max(w => w.Top), ordered.Min(w => w.Bottom), ordered[0].Left, ordered.Max(w => w.Right),
                string.Join(' ', ordered.Select(w => w.Text)), LeadingLabel(ordered));
        }).ToList();
    }

    private static IEnumerable<Block> ColumnBlocks(int page, int column, List<LayoutWord> words, int[] rejected)
    {
        var lines = Lines(words);
        Block? open = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Role == "block-header" && Cells(line.Words).Count >= 2)
            {
                var end = TableEnd(lines, i);
                if (end > 0)
                {
                    if (open != null)
                    {
                        yield return open;
                        open = null;
                    }

                    yield return BuildTable(page, column, lines.GetRange(i, end - i));
                    i = end - 1;
                    continue;
                }

                rejected[0]++;
            }

            var info = new LineInfo(line.Left, line.Right, line.Label);
            if (open != null && open.Role == line.Role && open.Bottom - line.Top <= BlockGap)
            {
                open.Lines.Add(line.Text);
                open.Info!.Add(info);
                open = open with { Left = Math.Min(open.Left, line.Left), Right = Math.Max(open.Right, line.Right), Bottom = line.Bottom };
                continue;
            }

            if (open != null)
            {
                yield return open;
            }

            open = new Block(page, column, line.Role, line.Left, line.Bottom, line.Right, line.Top, new List<string> { line.Text }, new List<LineInfo> { info });
        }

        if (open != null)
        {
            yield return open;
        }
    }

    /// <summary>
    /// Where the table that starts at the header line <paramref name="start"/> ends (exclusive), or -1 when it is not a table. A line is a row when it is close
    /// below the line before, is a body line (or a multi-cell header), and its cells line up with the header's: each sits under exactly one header cell
    /// (so prose that runs across the columns ends the table). A table needs at least one row with two or more cells; a
    /// stat-block header (a name and a badge) with prose under it is therefore not one, and is counted as rejected.
    /// </summary>
    private static int TableEnd(List<Line> lines, int start)
    {
        var starts = Cells(lines[start].Words);
        var end = start + 1;
        var multi = false;
        while (end < lines.Count
            && lines[end - 1].Bottom - lines[end].Top <= TableRowGap
            && TableRoles.Contains(lines[end].Role)
            && (lines[end].Role != "block-header" || Cells(lines[end].Words).Count >= 2)
            && Aligned(lines[end], starts))
        {
            multi |= Cells(lines[end].Words).Count >= 2;
            end++;
        }

        return multi ? end : -1;
    }

    private static bool Aligned(Line line, List<(double Left, double Right, string Text)> header)
    {
        // A row cell lines up when it sits under exactly one header cell (centred, left or right aligned alike). Prose that runs across two of them does not.
        foreach (var cell in Cells(line.Words))
        {
            var under = header.Count(h => cell.Left <= h.Right + CellSlack && cell.Right >= h.Left - CellSlack);
            if (under != 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The cells of a line: its words cut wherever two words are more than <see cref="CellGap"/> apart.</summary>
    private static List<(double Left, double Right, string Text)> Cells(List<LayoutWord> ordered)
    {
        var cells = new List<(double Left, double Right, List<string> Words)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i == 0 || ordered[i].Left - ordered[i - 1].Right > CellGap)
            {
                cells.Add((ordered[i].Left, ordered[i].Right, new List<string>()));
            }

            var last = cells[^1];
            cells[^1] = (last.Left, ordered[i].Right, last.Words);
            last.Words.Add(ordered[i].Text);
        }

        return cells.Select(c => (c.Left, c.Right, string.Join(' ', c.Words))).ToList();
    }

    /// <summary>
    /// A table from its lines. The first line is the header row: its cells give the column starts. Every later line's words go to the column
    /// they start in; a line that has a word in the first column starts a row, any other line continues the row above it (a wrapped cell).
    /// </summary>
    private static Block BuildTable(int page, int column, List<Line> lines)
    {
        var starts = Cells(lines[0].Words).Select(c => c.Left).ToList();
        var rows = new List<List<string>> { Cells(lines[0].Words).Select(c => c.Text).ToList() };
        foreach (var line in lines.Skip(1))
        {
            var cells = Enumerable.Repeat(string.Empty, starts.Count).ToList();
            foreach (var w in line.Words)
            {
                var col = 0;
                for (var s = 0; s < starts.Count; s++)
                {
                    if (w.Left >= starts[s] - CellSlack)
                    {
                        col = s;
                    }
                }

                cells[col] = cells[col].Length == 0 ? w.Text : cells[col] + " " + w.Text;
            }

            if (cells[0].Length == 0 && rows.Count > 1)
            {
                var previous = rows[^1];
                for (var c = 0; c < cells.Count; c++)
                {
                    if (cells[c].Length > 0)
                    {
                        previous[c] = previous[c].Length == 0 ? cells[c] : previous[c] + " " + cells[c];
                    }
                }
            }
            else
            {
                rows.Add(cells);
            }
        }

        return new Block(page, column, "table", lines.Min(l => l.Left), lines.Min(l => l.Bottom), lines.Max(l => l.Right), lines.Max(l => l.Top),
            rows.Select(r => string.Join(" | ", r)).ToList(), null, rows);
    }

    /// <summary>
    /// A sidebar starts at a heading in the block-header font and runs through the structured-text blocks after it, up to the first block of any other role.
    /// It is only looked for on pages a "Sidebar" bookmark covers.
    /// </summary>
    private static (List<Block> Kept, List<Block> Dropped, int Runs, bool BodyAfter, bool EndsInside) SplitSidebars(List<Block> blocks, bool previousColumnEndedInside)
    {
        var kept = new List<Block>();
        var gone = new List<Block>();
        var inside = false;
        var runs = 0;

        // A column that opens with body text straight after a column that ended inside a sidebar is that sidebar's continuation, which has no heading to find.
        var bodyAfter = previousColumnEndedInside && blocks.Count > 0 && blocks[0].Role == "body";
        foreach (var b in blocks)
        {
            if (b.Role == "block-header" || (inside && SidebarRoles.Contains(b.Role)))
            {
                if (!inside)
                {
                    runs++;
                }

                inside = true;
                gone.Add(b);
                continue;
            }

            inside = false;
            kept.Add(b);
        }

        return (kept, gone, runs, bodyAfter, inside);
    }

    /// <summary>The bold words a line starts with when it is a labelled paragraph start, such as a degree name. A line that is bold throughout counts only when it is short.</summary>
    private static string LeadingLabel(List<LayoutWord> ordered)
    {
        bool Plain(LayoutWord w) => w.Role is "rules" or "body";
        var run = ordered.TakeWhile(w => Plain(w) && w.Font.Contains("Bold", StringComparison.OrdinalIgnoreCase)).ToList();
        if (run.Count == 0 || (run.Count == ordered.Count && run.Count > 3))
        {
            return string.Empty;
        }

        return string.Join(' ', run.Select(w => w.Text));
    }

    /// <summary>The role most of the line's letters have. Icons are ignored unless the line has nothing else.</summary>
    private static string LineRole(List<LayoutWord> line)
    {
        var groups = line.Where(w => w.Role != "icon").GroupBy(w => w.Role).ToList();
        if (groups.Count == 0)
        {
            return "icon";
        }

        return groups.OrderByDescending(g => g.Sum(w => w.Text.Length)).First().Key;
    }
}
