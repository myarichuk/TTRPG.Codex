using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>Where one line of a block sits, and the bold run it starts with (a field label such as a degree name), or empty.</summary>
/// <param name="Bold">The bold runs of the line after its leading label, in order (a field label in the middle of a line, such as the second label of a line of the shape "Label value; Label value"). Null when there are none.</param>
public sealed record LineInfo(double Left, double Right, string Label, List<LineCell>? Cells = null, List<string>? Bold = null);

/// <summary>One cell of a line that has more than one: the words between two gaps wider than a word space, and where the cell starts.</summary>
public sealed record LineCell(double Left, string Text);

/// <summary>
/// A run of lines of one role inside one column of one page. A <c>table</c> block has <see cref="Rows"/> (the first row is the header) and its
/// <see cref="Lines"/> are the rows joined with " | ".
/// </summary>
public sealed record Block(int Page, int Column, string Role, double Left, double Bottom, double Right, double Top, List<string> Lines, List<LineInfo>? Info = null, List<List<string>>? Rows = null);

/// <param name="SuspectPages">One-column pages with plenty of text on both halves: a gutter was probably missed, so reading order may be garbled.</param>
/// <param name="Dropped">Sidebar and opener blocks that were split out. They are kept only for the denylist, never parsed.</param>
/// <param name="SidebarProblems">Sidebar pages where the split is not backed by the bookmarks (too few sidebars) or body text follows a dropped sidebar; a taken range must not contain one.</param>
/// <param name="RejectedTableHeaders">Two-cell block-header lines whose lines below did not line up as table rows (stat-block headers, mostly): counted, left as ordinary blocks.</param>
/// <param name="LegacyDropped">Blocks the old sidebar rule (every heading in the block-header font starts a sidebar) would have dropped on a page the new, title-based rule keeps whole. Only for the denylist, and only on pages no row takes.</param>
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
    int RejectedTableHeaders = 0,
    List<Block>? LegacyDropped = null,
    Dictionary<string, List<Block>>? TakenSidebars = null);

/// <summary>What the take-list and the spine tell the blocks stage about pages. Defaults: two columns everywhere and no sidebar pages.</summary>
public sealed class BlocksOptions
{
    /// <summary>The most columns a page may be split into. Pages of a taken row with <c>columns: 3</c> or more get a gutter search inside each column.</summary>
    public Func<int, int> MaxColumns { get; init; } = _ => 2;

    /// <summary>Pages covered by a "Sidebar" bookmark. Only there is a block-header run split out as a sidebar and dropped.</summary>
    public IReadOnlySet<int> SidebarPages { get; init; } = new HashSet<int>();

    /// <summary>How many "Sidebar" bookmarks start on each page. A sidebar page that yields fewer sidebars than that is reported in <see cref="BlocksResult.SidebarProblems"/>.</summary>
    public IReadOnlyDictionary<int, int> SidebarStarts { get; init; } = new Dictionary<int, int>();

    /// <summary>
    /// The titles of the sidebars that start on each page, one per sidebar: a bookmark called "Sidebars: X &amp; Y" is two. A page that has titles is split by them (see
    /// <see cref="BlocksStage"/>): a heading is a sidebar's only if it matches one, so an action's or a table's heading on the same page is not taken for one.
    /// A page without titles falls back to treating every heading in the block-header font as a sidebar's.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> SidebarTitles { get; init; } = new Dictionary<int, IReadOnlyList<string>>();

    /// <summary>
    /// Pages whose stat tables (armor, shields, weapons) are carved out before the page is cut into columns (see <see cref="StatTables"/>), each with the column label lists a header row may carry.
    /// Only the pages a take-list row names: every other page is read as before.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyList<string>>> StatTablePages { get; init; } = new Dictionary<int, IReadOnlyList<IReadOnlyList<string>>>();

    /// <summary>By page, the keys (see <see cref="SidebarTitleSet.Key"/>) of the titles of sidebars the take-list takes as rules, only on the pages of the bookmark it names (a sidebar of the same title elsewhere is not taken): their blocks are kept apart (<see cref="BlocksResult.TakenSidebars"/>) and not dropped.</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<string>> TakenSidebarKeys { get; init; } = new Dictionary<int, IReadOnlySet<string>>();

    /// <summary>
    /// Pages whose one gutter a take-list row pins by its x (see <see cref="TakeRow.GutterAt"/>): a page whose text has a wider empty band than the real gutter (a feat set around a piece of art, with a gap in
    /// every line) would otherwise be cut there and merge the two real columns' lines.
    /// </summary>
    public IReadOnlyDictionary<int, double> PinnedGutters { get; init; } = new Dictionary<int, double>();

    /// <summary>Pages where a sidebar fills a whole column (<see cref="TakeRow.SidebarFillsColumn"/>): the text that opens the next column is the page's own, not the sidebar's continuation.</summary>
    public IReadOnlySet<int> SidebarFillsColumn { get; init; } = new HashSet<int>();

    /// <summary>The pages whose gear tables are carved out (see <see cref="GearTables"/>), with the column label lists a header row there may carry and whether the table is set in two panels.</summary>
    public IReadOnlyDictionary<int, GearPage> GearTablePages { get; init; } = new Dictionary<int, GearPage>();
}

/// <summary>A page an items row of kind gear names for its tables: the label lists a header may carry, and whether its table is set in two panels side by side.</summary>
public sealed record GearPage(IReadOnlyList<IReadOnlyList<string>> Sets, bool TwoUp);

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

    /// <summary>Body text this far (in points) left of a sidebar's own text is no longer inside it.</summary>
    private const double SidebarIndent = 4;

    /// <summary>The bold labels, in lowercase, that open a line of an action's text: the four degrees of success and the labelled lines. A sidebar of rules never starts a line with one.</summary>
    private static readonly HashSet<string> ActionLabels = new(StringComparer.Ordinal)
    {
        "critical success", "success", "failure", "critical failure", "requirements", "trigger", "frequency",
    };

    /// <summary>A block cut before its line <paramref name="at"/>: the lines above, and the lines from there on.</summary>
    private static (Block Head, Block Tail) CutBlock(Block b, int at)
    {
        var info = b.Info!;
        Block Part(int from, int to) => b with
        {
            Lines = b.Lines.GetRange(from, to - from),
            Info = info.GetRange(from, to - from),
            Left = info.Skip(from).Take(to - from).Min(x => x.Left),
            Right = info.Skip(from).Take(to - from).Max(x => x.Right),
        };

        return (Part(0, at), Part(at, b.Lines.Count));
    }

    private static readonly System.Text.RegularExpressions.Regex ActionIcon = new(@"\[(?:(?:one|two|three|free)-actions?|reaction)\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The sidebars a page is known to have from the bookmarks (one title each) and which of them a heading has been found for. A heading matches a title when its letters are
    /// the title's, within two typos, or a part of it at either end (a title set across a gutter is two headings, one in each column).
    /// </summary>
    internal sealed class SidebarTitleSet
    {
        private readonly List<string> _keys;
        private readonly HashSet<int> _matched = new();

        private readonly HashSet<int> _exact;

        /// <param name="titles">The page's sidebar titles. One that starts with `=` came from splitting a bookmark on the word "and": it must match its heading exactly, not by a part of it, so a wrong split fails loudly.</param>
        public SidebarTitleSet(IEnumerable<string> titles)
        {
            var list = titles.ToList();
            _keys = list.Select(Key).ToList();
            _exact = list.Select((t, i) => t.StartsWith('=') ? i : -1).Where(i => i >= 0).ToHashSet();
        }

        /// <summary>How many of the page's sidebars no heading was found for.</summary>
        public int Missing => _keys.Count - _matched.Count;

        public bool Match(Block heading)
        {
            var key = Key(string.Join(' ', heading.Lines));
            for (var i = 0; i < _keys.Count; i++)
            {
                var title = _keys[i];
                if (key == title)
                {
                    _matched.Add(i);
                    return true;
                }

                if (_exact.Contains(i))
                {
                    continue;
                }

                if ((key.Length >= 8 && title.Length >= 8 && Distance(key, title) <= 2)
                    || (key.Length >= 4 && key.Length < title.Length && (title.StartsWith(key, StringComparison.Ordinal) || title.EndsWith(key, StringComparison.Ordinal))))
                {
                    _matched.Add(i);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Like <see cref="Match"/> but only by the whole title: for a heading in a font that is not the sidebar headings' own, which a part of a title could match by chance.</summary>
        public bool MatchWhole(Block heading)
        {
            var key = Key(string.Join(' ', heading.Lines));
            for (var i = 0; i < _keys.Count; i++)
            {
                if (key == _keys[i])
                {
                    _matched.Add(i);
                    return true;
                }
            }

            return false;
        }

        public static string Key(string text) => new(SpanText.Normalize(text).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private static int Distance(string a, string b)
        {
            var row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 1; i <= a.Length; i++)
            {
                var diagonal = row[0];
                row[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }

            return row[b.Length];
        }
    }

    public static BlocksResult Run(IEnumerable<LayoutPage> pages, BlocksOptions? options = null)
    {
        options ??= new BlocksOptions();
        var blocks = new List<Block>();
        var dropped = new List<Block>();
        var takenSidebars = new Dictionary<string, List<Block>>(StringComparer.Ordinal);
        var legacy = new List<Block>();
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

            if (options.GearTablePages.TryGetValue(page.Number, out var gearPage))
            {
                // The gear tables: carved the same way, by a reader that knows panels, headings and indented versions.
                var gear = GearTables.Carve(page.Number, page.Width, body, gearPage.Sets, gearPage.TwoUp);
                blocks.AddRange(gear.Tables);
                body = gear.Words;
            }
            else if (options.StatTablePages.TryGetValue(page.Number, out var labelSets))
            {
                // A page a row names for its stat tables: they are carved out first, before the gutter is looked for (a table spans both columns).
                var carved = StatTables.Carve(page.Number, body, labelSets);
                blocks.AddRange(carved.Tables);
                body = carved.Words;
            }

            if (body.Count == 0)
            {
                continue;
            }

            if (options.PinnedGutters.TryGetValue(page.Number, out var pin) && pin >= page.Width)
            {
                throw new DegreesException($"A gutter is pinned at x {pin} on page {page.Number}, which is {page.Width} points wide.");
            }

            var gutters = options.PinnedGutters.TryGetValue(page.Number, out var pinned) ? new List<double> { pinned } : FindGutters(body, page.Width, options.MaxColumns(page.Number));
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
            var titles = options.SidebarTitles.TryGetValue(page.Number, out var pageTitles) && pageTitles.Count > 0 ? new SidebarTitleSet(pageTitles) : null;
            var pageColumns = columns.Select((words, c) => ColumnBlocks(page.Number, c, words, rejected).ToList()).ToList();

            // The split of the page's columns, by the sidebars' titles when the page has them, or by the old rule (every heading in the block-header font starts one).
            (List<Block> Kept, List<Block> Dropped, int Runs, bool BodyAfter, List<(string Key, Block Block)> Taken) SplitPage(SidebarTitleSet? by)
            {
                var kept = new List<Block>();
                var gone = new List<Block>();
                var takenHere = new List<(string Key, Block Block)>();
                var runs = 0;
                var bodyAfter = false;
                var endedInside = false;
                foreach (var found in pageColumns)
                {
                    var split = SplitSidebars(found, endedInside && !options.SidebarFillsColumn.Contains(page.Number), by, options.TakenSidebarKeys.GetValueOrDefault(page.Number), takenHere);
                    endedInside = split.EndsInside;
                    gone.AddRange(split.Dropped);
                    runs += split.Runs;
                    bodyAfter |= split.BodyAfter;
                    kept.AddRange(split.Kept);
                }

                return (kept, gone, runs, bodyAfter, takenHere);
            }

            if (!sidebarPage)
            {
                blocks.AddRange(pageColumns.SelectMany(c => c));
                continue;
            }

            var result = SplitPage(titles);
            var flagged = result.BodyAfter || (titles != null ? titles.Missing > 0 : result.Runs < options.SidebarStarts.GetValueOrDefault(page.Number));
            if (titles != null && titles.Missing > 0)
            {
                // A heading did not match its sidebar's title (a title that differs from its heading): the page is flagged, so no taken range may hold it, and its sidebars are
                // split out by the old rule, which hashes more of the left-out text for the denylist, never less.
                result = SplitPage(null);
            }
            else if (titles != null)
            {
                // What the old rule would have dropped from this page is kept for the denylist: a sidebar or a box that no bookmark names, on a page no row takes, must still be
                // hashed as left-out text (the split by titles keeps it as the page's own).
                legacy.AddRange(SplitPage(null).Dropped);
            }

            blocks.AddRange(result.Kept);
            dropped.AddRange(result.Dropped);
            sidebarBlocks += result.Dropped.Count;
            foreach (var group in result.Taken.GroupBy(t => t.Key, StringComparer.Ordinal))
            {
                // One sidebar, once: a second set of blocks under one key (the sidebar is split over two pages, or the bookmark's pages hold two of that title) fails instead of replacing the first.
                if (!takenSidebars.TryAdd(group.Key, group.Select(t => t.Block).ToList()))
                {
                    throw new DegreesException("A sidebar the take-list takes was found twice (on page " + page.Number + "), so it is not known which is meant.");
                }
            }

            // Every sidebar bookmark that starts on this page must have found a heading, and body text must not run straight on from one.
            if (flagged)
            {
                problems.Add(page.Number);
            }
        }

        var byRole = blocks.GroupBy(b => b.Role).ToDictionary(g => g.Key, g => g.Count());
        return new BlocksResult(blocks, one, multi, furniture, byRole, suspect, dropped, oneNumbers, three, sidebarBlocks, openerWords, blocks.Count(b => b.Role == "table"), problems, rejected[0], legacy, takenSidebars);
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

    private sealed record Line(List<LayoutWord> Words, string Role, double Top, double Bottom, double Left, double Right, string Text, string Label, List<string>? Bold = null);

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
                string.Join(' ', ordered.Select(w => w.Text)), LeadingLabel(ordered), BoldRuns(ordered));
        }).ToList();
    }

    /// <summary>The last cell of an entry header: the word the header sets before its rank (spell, cantrip, focus spell, ritual, feat) and the rank.</summary>
    private static readonly Regex EntryHeaderCell = new(@"^(?:SPELL|CANTRIP|FOCUS|RITUAL|FEAT)\s+\d+$", RegexOptions.CultureInvariant);

    private static IEnumerable<Block> ColumnBlocks(int page, int column, List<LayoutWord> words, int[] rejected)
    {
        var lines = Lines(words);
        Block? open = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // An entry's header (a name and icon, then the kind word and rank in a cell of their own) is never a table's header row, whatever sits under it: a header over a stat line
            // that has two cells (a range and a target set apart) would otherwise be read as a table and the entry lost.
            if (line.Role == "block-header" && Cells(line.Words) is { Count: >= 2 } headerCells && !EntryHeaderCell.IsMatch(headerCells[^1].Text))
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

            var lineCells = Cells(line.Words);
            var info = new LineInfo(line.Left, line.Right, line.Label, lineCells.Count >= 2 ? lineCells.Select(c => new LineCell(c.Left, c.Text)).ToList() : null, line.Bold);
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
            // The row with two cells must be a body line: another header line with two cells (a stat-block header under a stat-block header) is no proof of a table.
            multi |= lines[end].Role != "block-header" && Cells(lines[end].Words).Count >= 2;
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
    private static (List<Block> Kept, List<Block> Dropped, int Runs, bool BodyAfter, bool EndsInside) SplitSidebars(List<Block> blocks, bool previousColumnEndedInside, SidebarTitleSet? titles,
        IReadOnlySet<string>? takenKeys = null, List<(string Key, Block Block)>? taken = null)
    {
        var kept = new List<Block>();
        var gone = new List<Block>();
        var runs = 0;
        var left = double.NaN;

        var inside = false;
        var afterEntryTitle = false;

        // A sidebar the take-list takes (see TakeRow.Sidebars) is not dropped: its blocks, its title among them, are kept apart under the title's key.
        string? takingKey = null;
        void Drop(Block dropped)
        {
            if (takingKey != null && taken != null)
            {
                taken.Add((takingKey, dropped));
            }
            else
            {
                gone.Add(dropped);
            }
        }

        // A column that opens with body text straight after a column that ended inside a sidebar is that sidebar's continuation, which has no heading to find.
        var bodyAfter = previousColumnEndedInside && blocks.Count > 0 && blocks[0].Role == "body";
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (titles == null)
            {
                // No sidebar title is known for the page: every heading in the block-header font starts a sidebar, which runs on through structured text.
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
                continue;
            }

            // With the sidebars' titles known, a heading is a sidebar's only when it matches one of them. Any other heading in the block-header font is the page's own: an action's
            // (an action icon in the line, or a trait line right below), a table's or a subsection's. A sidebar's own subheadings are not title matches but sit inside it.
            var next = i + 1 < blocks.Count ? blocks[i + 1].Role : string.Empty;
            if (b.Role == "entry-header" && titles.MatchWhole(b))
            {
                // A sidebar whose title is set in the entry-header font (the sample characters of a class's pages): it starts at the heading that is its whole title.
                inside = true;
                left = double.NaN;
                var key = SidebarTitleSet.Key(string.Join(' ', b.Lines));
                takingKey = takenKeys != null && takenKeys.Contains(key) ? key : null;
                Drop(b);
                afterEntryTitle = true;
                continue;
            }

            if (afterEntryTitle)
            {
                // The line under such a title names the sample's build, set in capitals in the block-header font: part of the sidebar, though it matches no title.
                afterEntryTitle = false;
                if (b.Role == "block-header" && !IsActionHeader(blocks, i))
                {
                    Drop(b);
                    continue;
                }
            }

            if (b.Role == "block-header")
            {
                // An action's heading is the page's own whatever its letters are: tested before the titles, so a short action name that is part of a sidebar's title is not taken for it.
                if (IsActionHeader(blocks, i))
                {
                    inside = false;
                    kept.Add(b);
                    continue;
                }

                if (titles.Match(b))
                {
                    inside = true;
                    left = double.NaN;
                    var key = SidebarTitleSet.Key(string.Join(' ', b.Lines));
                    takingKey = takenKeys != null && takenKeys.Contains(key) ? key : null;
                    Drop(b);
                    continue;
                }

                // A sidebar's subheadings are set in title case; the page's own headings (an action's, a table's caption) and the sidebars' titles are set in capitals.
                if (inside && !IsActionHeader(blocks, i) && b.Lines.Any(l => l.Any(char.IsLower)) && next is "body" or "block-header" or "")
                {
                    Drop(b); // a subheading of the sidebar
                    continue;
                }

                inside = false;
                kept.Add(b);
                continue;
            }

            if (inside && SidebarRoles.Contains(b.Role))
            {
                // A body line that opens with a bold label an action's text has (a degree of success, a requirements line) is the page's own text going on after the sidebar,
                // even when the sidebar's text is set at the column's edge too (so the indent test cannot see the end). The block is cut there.
                var setIn = b.Role == "body" && !double.IsNaN(left) && b.Left < left - SidebarIndent; // already at the page's own edge: the indent test below ends the sidebar before this block
                var label = b.Role == "body" && b.Info != null && !setIn ? Enumerable.Range(0, b.Lines.Count).Where(k => ActionLabels.Contains(SpanText.Normalize(b.Info[k].Label).Trim().ToLowerInvariant())).Cast<int?>().FirstOrDefault() : null;
                if (label is { } at)
                {
                    inside = false;
                    if (at > 0)
                    {
                        var (head, tail) = CutBlock(b, at);
                        Drop(head);
                        kept.Add(tail);
                    }
                    else
                    {
                        kept.Add(b);
                    }

                    continue;
                }

                if (double.IsNaN(left) && b.Role == "body")
                {
                    // A single line at the top of a column that follows one that ended inside the sidebar is no guide to where the sidebar's text is set in: it is the end of a
                    // sentence that ran over from the other column, set at its own place.
                    if (!(i <= 1 && previousColumnEndedInside && b.Lines.Count == 1))
                    {
                        left = b.Left;
                    }
                }
                else if (b.Role == "body" && b.Left < left - SidebarIndent)
                {
                    // Body text at the column's edge, left of where the sidebar's text is set in: the page's own text going on after the sidebar.
                    inside = false;
                    kept.Add(b);
                    continue;
                }

                Drop(b);
                continue;
            }

            inside = false;
            kept.Add(b);
        }

        return (kept, gone, runs, bodyAfter, inside);
    }

    /// <summary>
    /// True for the heading of an action: a heading in the block-header font with an action icon in it, or with a trait line (a block in the tag font) right below it.
    /// A sidebar's heading has neither.
    /// </summary>
    internal static bool IsActionHeader(IReadOnlyList<Block> blocks, int index) =>
        blocks[index].Role == "block-header"
        && (blocks[index].Lines.Any(l => ActionIcon.IsMatch(l)) || (index + 1 < blocks.Count && blocks[index + 1].Role == "tag"));

    /// <summary>The bold words a line starts with when it is a labelled paragraph start, such as a degree name. A line that is bold throughout counts only when it is short.</summary>
    private static string LeadingLabel(List<LayoutWord> ordered)
    {
        bool Plain(LayoutWord w) => w.Role is "rules" or "body";

        // A list item's bullet is set in a plain face before the bold heading that runs in: the bullet is part of the label.
        var bullet = ordered.Count > 1 && ordered[0].Text == "•" && Plain(ordered[0]) && !ordered[0].Font.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var run = ordered.Skip(bullet).TakeWhile(w => Plain(w) && w.Font.Contains("Bold", StringComparison.OrdinalIgnoreCase)).ToList();
        if (run.Count == 0 || (run.Count == ordered.Count - bullet && run.Count > 3))
        {
            return string.Empty;
        }

        return (bullet == 1 ? "• " : string.Empty) + string.Join(' ', run.Select(w => w.Text));
    }

    /// <summary>The bold runs that follow plain words on a line (a field label in the middle of it), or null when there are none. The line's leading bold run is its label and not listed.</summary>
    private static List<string>? BoldRuns(List<LayoutWord> ordered)
    {
        bool Bold(LayoutWord w) => w.Role is "rules" or "body" && w.Font.Contains("Bold", StringComparison.OrdinalIgnoreCase);

        var runs = new List<string>();
        var seenPlain = false;
        var i = 0;
        while (i < ordered.Count)
        {
            if (!Bold(ordered[i]))
            {
                seenPlain = true;
                i++;
                continue;
            }

            var j = i;
            while (j < ordered.Count && Bold(ordered[j]))
            {
                j++;
            }

            if (seenPlain)
            {
                runs.Add(string.Join(' ', ordered.Skip(i).Take(j - i).Select(w => w.Text)));
            }

            i = j;
        }

        return runs.Count == 0 ? null : runs;
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
