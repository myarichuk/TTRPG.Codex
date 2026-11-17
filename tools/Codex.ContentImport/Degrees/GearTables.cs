namespace Codex.ContentImport.Degrees;

/// <summary>
/// A reader for the gear tables of the equipment chapter: the adventuring gear (set in two panels side by side, with headings and indented versions of an item), the uncommon adventuring gear, the three
/// alchemical tables (one under another, with a note under the last), the assistive items and the barding. It runs on the words of a page an items row of kind <c>gear</c> names, before the gutter is looked
/// for, and carves each table out of the page. It shares the line, cell and column machinery of <see cref="StatTables"/> and differs in what a row may be:
/// <list type="bullet">
/// <item>A table's words are the ones set in the body and block-header fonts: the page's running text is in the rules font, so a table beside a column of text is told from it by its font.</item>
/// <item>A page named <c>twoUp</c> holds one table in two panels: the right panel has no header of its own, its columns are the left panel's, shifted by the distance between the panels' first name
/// columns. The right panel is read as the rows that come after the left one (the gutter between the panels is looked for in the table's own words, as the column gutter is).</item>
/// <item>A row whose name cell starts further right than its header's name column is <c>sub</c>: a version of the row above (an expanded, a superior, a sized or a material one), or a row of the heading above it.
/// A line with a name and no other cell is a <c>band</c>, a heading the rows below it sit under (<c>Lantern</c>, <c>Lock</c>). Every other row has a cell in every column, or the run fails.</item>
/// </list>
/// The result is a <c>stat-table</c> block whose rows are lists whose first element says what the row is: <c>title</c>, <c>head</c>, <c>band</c>, <c>row</c>, <c>sub</c> or <c>note</c> (as
/// <see cref="StatTables"/> does, so <see cref="ItemRows"/> finds the header rows the same way).
/// </summary>
public static class GearTables
{
    /// <summary>A name cell this far right of its header's name column is an indented one.</summary>
    private const double IndentMin = 3;

    /// <summary>Carves the gear tables out of one page's words. A page with no header row of the given label lists returns its words untouched.</summary>
    public static (List<Block> Tables, List<LayoutWord> Words) Carve(int page, double pageWidth, List<LayoutWord> body, IReadOnlyList<IReadOnlyList<string>> labelSets, bool twoUp)
    {
        var candidates = body.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Role is "body" or "block-header").ToList();
        if (candidates.Any(w => w.Size < StatTables.MarkSize))
        {
            throw new DegreesException($"Page {page}: a gear table page has a footnote mark set small, which this reader does not read.");
        }

        // The words as the reader sees them (the right panel of a two-up page moved under the left one), and each one's original, for the words that are left.
        var origin = new Dictionary<LayoutWord, LayoutWord>();
        var work = twoUp ? Panels(page, pageWidth, candidates, labelSets, origin) : candidates;
        var lines = StatTables.Group(work);
        var tables = new List<Block>();
        var used = new HashSet<int>();
        var taken = new HashSet<LayoutWord>();

        for (var i = 0; i < lines.Count; i++)
        {
            if (used.Contains(i) || StatTables.HeaderOf(lines[i], labelSets) is not { } geometry)
            {
                continue;
            }

            var rows = new List<List<string>>();
            var members = new List<StatTables.StLine>();
            if (i > 0 && !used.Contains(i - 1) && StatTables.IsCaption(lines[i - 1]) && lines[i - 1].Bottom - lines[i].Bottom <= 20)
            {
                rows.Add(new List<string> { "title", string.Join(' ', lines[i - 1].Words.Select(w => w.Text)) });
                members.Add(lines[i - 1]);
                used.Add(i - 1);
            }

            var inNotes = false;
            for (var j = i; j < lines.Count; j++)
            {
                var line = lines[j];
                if (j > i && lines[j - 1].Bottom - line.Bottom > StatTables.RowGap)
                {
                    break;
                }

                if (line.Words[0].Text.StartsWith('*'))
                {
                    inNotes = true;
                    rows.Add(new List<string> { "note", "*", string.Join(' ', line.Words.Select(w => w.Text)).TrimStart('*').Trim() });
                }
                else if (inNotes)
                {
                    // A note that wraps goes on in lowercase; anything else ends the table.
                    if (char.IsLower(line.Words[0].Text[0]))
                    {
                        rows[^1][2] += " " + string.Join(' ', line.Words.Select(w => w.Text));
                    }
                    else
                    {
                        break;
                    }
                }
                else if (j > i && StatTables.HeaderOf(line, labelSets) is { } again)
                {
                    geometry = again;
                    rows.Add(Cells(line, geometry, page, "head"));
                }
                else if (j == i)
                {
                    rows.Add(Cells(line, geometry, page, "head"));
                }
                else
                {
                    rows.Add(ReadRow(line, geometry, page));
                }

                members.Add(line);
                used.Add(j);
            }

            var words = members.SelectMany(m => m.Words).Select(w => origin.GetValueOrDefault(w, w)).ToList();
            foreach (var w in words)
            {
                taken.Add(w);
            }

            tables.Add(new Block(page, 0, StatTables.Role, words.Min(w => w.Left), words.Min(w => w.Bottom), words.Max(w => w.Right), words.Max(w => w.Top),
                rows.Select(r => string.Join(" | ", r.Skip(1))).ToList(), null, rows));
        }

        if (tables.Count == 0)
        {
            return (tables, body);
        }

        // The words left are the page's text. A space between carved words goes with them.
        var carvedBottoms = taken.Select(w => w.Bottom).ToList();
        var rest = body.Where(w => !taken.Contains(w) && !(string.IsNullOrWhiteSpace(w.Text) && w.Role is "body" or "block-header" && carvedBottoms.Any(b => Math.Abs(b - w.Bottom) <= StatTables.LineTolerance))).ToList();
        return (tables, rest);
    }

    /// <summary>
    /// The words of a table set in two panels, as one table: the right panel's words moved left by the distance between the panels' first name columns, and down to the rows that follow the left panel's last one.
    /// The gutter between the panels is found in the table's own words.
    /// </summary>
    private static List<LayoutWord> Panels(int page, double pageWidth, List<LayoutWord> words, IReadOnlyList<IReadOnlyList<string>> labelSets, Dictionary<LayoutWord, LayoutWord> origin)
    {
        // The left panel's header sits on the same line as the first row of the right panel: its last label ends the left panel, and the first word after it starts the right one. The gutter is the middle of the two.
        // (The widest empty band of the table's words is no guide: a row's name and its price can sit further apart than the panels do.)
        double? gutter = null;
        foreach (var line in StatTables.Group(words))
        {
            for (var p = 1; p < line.Words.Count && gutter == null; p++)
            {
                if (line.Words[p].Text != "Price")
                {
                    continue;
                }

                foreach (var set in labelSets)
                {
                    var tokens = set.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
                    var tail = line.Words.Skip(p).Select(w => w.Text).ToList();
                    if (tail.Count > tokens.Count && tail.Take(tokens.Count).SequenceEqual(tokens))
                    {
                        var last = line.Words[p + tokens.Count - 1];
                        gutter = (last.Right + line.Words[p + tokens.Count].Left) / 2;
                    }
                }
            }
        }

        if (gutter == null)
        {
            // A page the row names two-up that has a table whose header has nothing after it is not what the row says: it fails, and is never read as one panel. A page with no table is returned as it is.
            if (StatTables.Group(words).Any(l => StatTables.HeaderOf(l, labelSets) != null))
            {
                throw new DegreesException($"Page {page}: the take-list says this table is set in two panels, but its header has no right panel on its line.");
            }

            return words;
        }

        var left = words.Where(w => (w.Left + w.Right) / 2 < gutter).ToList();
        var right = words.Where(w => (w.Left + w.Right) / 2 >= gutter).ToList();
        var header = StatTables.Group(left).Select(l => (Line: l, Geometry: StatTables.HeaderOf(l, labelSets))).FirstOrDefault(h => h.Geometry != null);
        if (header.Geometry == null || right.Count == 0)
        {
            throw new DegreesException($"Page {page}: the take-list says this table is set in two panels, but its left panel has no header or its right panel no words.");
        }

        // The right panel's rows run on from the left panel's, 12 points apart as the table's own rows are; its x moves by the gap between the two panels' name columns.
        var dx = right.Min(w => w.Left) - header.Line.Words[0].Left;
        var dy = right.Max(w => w.Bottom) - left.Min(w => w.Bottom) + 12;
        var result = new List<LayoutWord>(left);
        foreach (var w in right)
        {
            var moved = w with { Left = w.Left - dx, Right = w.Right - dx, Bottom = w.Bottom - dy, Top = w.Top - dy };
            origin[moved] = w;
            result.Add(moved);
        }

        return result;
    }

    private static List<string> Cells(StatTables.StLine line, StatTables.Geometry geometry, int page, string kind)
    {
        // A header's labels are read word by word: two labels set close together (Bulk, Hands) would be one cell.
        var cells = new string?[geometry.Columns.Count];
        foreach (var word in line.Words)
        {
            var column = StatTables.ColumnOf(new StatTables.Cell(word.Left, word.Right, word.Text), geometry, page);
            cells[column] = cells[column] == null ? word.Text : cells[column] + " " + word.Text;
        }

        var row = new List<string> { kind };
        row.AddRange(cells.Select(c => c ?? string.Empty));
        return row;
    }

    private static List<string> ReadRow(StatTables.StLine line, StatTables.Geometry geometry, int page)
    {
        var width = geometry.Columns.Count;
        var cells = new string?[width];
        var nameLeft = 0.0;
        foreach (var cell in StatTables.Cells(line.Words))
        {
            var column = StatTables.ColumnOf(cell, geometry, page);
            if (cells[column] != null)
            {
                throw new DegreesException($"Page {page}: two cells of one gear table line sit under one column label.");
            }

            cells[column] = cell.Text;
            if (column == 0)
            {
                nameLeft = cell.Left;
            }
        }

        if (cells[0] == null)
        {
            throw new DegreesException($"Page {page}: a gear table line has no name cell.");
        }

        var filled = cells.Count(c => c != null);
        if (filled == 1)
        {
            if (cells[0]!.Any(char.IsDigit) || cells[0]!.Split(' ').Length > 3)
            {
                throw new DegreesException($"Page {page}: a gear table line has only a name cell, and it is not a heading.");
            }

            return new List<string> { "band", cells[0]! };
        }

        if (filled != width)
        {
            throw new DegreesException($"Page {page}: a gear table row has {filled} cells where its header has {width}.");
        }

        var row = new List<string> { nameLeft > geometry.Columns[0].Left + IndentMin ? "sub" : "row" };
        row.AddRange(cells.Select(c => c!));
        return row;
    }
}
