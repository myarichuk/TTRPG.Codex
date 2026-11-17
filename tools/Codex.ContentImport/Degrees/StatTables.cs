namespace Codex.ContentImport.Degrees;

/// <summary>
/// A reader for the stat tables of the equipment chapter (armor, shields, weapons): tables whose header row is a name cell and a committed list of column labels, set in the body font under a
/// caption in the block-header font, over the whole width of the page. The generic table detector needs a header row in the block-header font and cuts cells at one gap, which these tables
/// do not give (a column label list is told by its words, and a name and a price can sit closer than a gap). This reader runs on the words of a page the take-list names, before the gutter
/// is looked for, and carves each table out of the page: the caption, the header, the rows and the footnotes under it.
/// <list type="bullet">
/// <item>A header row is a line whose words from <c>Price</c> on are exactly one of the committed label lists. The columns are the header words' spans; a data cell goes to the column it overlaps most.</item>
/// <item>A row starts with a name. Every column must hold a cell (the book prints a dash for none), or the run fails. A line with no name cell continues the row above it (a wrapped trait list);
/// a lone line in parentheses continues the name (an item level that wraps); a name that starts with a digit is an ammunition row (its price and Bulk only); a lone line of words with no
/// digit is a band (a rarity heading). Anything else fails.</item>
/// <item>A band heading is a header line again (a category's name in the name cell): the columns are read again from it.</item>
/// <item>A footnote mark is a small raised digit or a star. It joins the cell to its left (the text <c>^n</c> is added to the cell), or opens a note line under the table.</item>
/// </list>
/// The result is a <c>stat-table</c> block whose <see cref="Block.Rows"/> are lists whose first element says what the row is: <c>title</c>, <c>head</c>, <c>band</c>, <c>row</c>, <c>ammo</c> or <c>note</c>.
/// </summary>
public static class StatTables
{
    public const string Role = "stat-table";

    /// <summary>A word set in a type smaller than this is a footnote mark.</summary>
    private const double MarkSize = 7;

    private const double LineTolerance = 2.5;

    /// <summary>The most a line may sit below the one above it and still belong to the table.</summary>
    private const double RowGap = 14.5;

    /// <summary>Two words on a line further apart than this are in different cells.</summary>
    private const double CellGap = 5;

    /// <summary>A cell goes to a column when it comes this close to the column's label.</summary>
    private const double Slack = 4;

    private sealed record StLine(List<LayoutWord> Words, double Bottom, double Top, bool IsNote = false);

    private sealed record Cell(double Left, double Right, string Text);

    private sealed class Geometry
    {
        public List<(double Left, double Right)> Columns { get; } = new();

        /// <summary>The label of each column; the name column's is empty.</summary>
        public List<string> Labels { get; } = new() { string.Empty };
    }

    private static List<StLine> Group(IEnumerable<LayoutWord> words)
    {
        var rows = new List<List<LayoutWord>>();
        foreach (var w in words.OrderByDescending(w => w.Bottom))
        {
            var row = rows.Count > 0 && Math.Abs(rows[^1][0].Bottom - w.Bottom) <= LineTolerance ? rows[^1] : null;
            if (row == null)
            {
                rows.Add(row = new List<LayoutWord>());
            }

            row.Add(w);
        }

        return rows.Select(r =>
        {
            var o = r.OrderBy(w => w.Left).ToList();
            return new StLine(o, o.Min(w => w.Bottom), o.Max(w => w.Top));
        }).ToList();
    }

    /// <summary>
    /// The header's column spans when the line is a header row of one of the label lists: the name column first, then one span per label. Null when the line is not one.
    /// </summary>
    private static Geometry? HeaderOf(StLine line, IReadOnlyList<IReadOnlyList<string>> sets)
    {
        for (var p = 1; p < line.Words.Count; p++)
        {
            if (line.Words[p].Text != "Price")
            {
                continue;
            }

            var tail = line.Words.Skip(p).Select(w => w.Text).ToList();
            foreach (var set in sets)
            {
                var tokens = set.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
                if (!tail.SequenceEqual(tokens))
                {
                    continue;
                }

                var geometry = new Geometry();
                geometry.Columns.Add((line.Words[0].Left, line.Words[p - 1].Right));
                var at = p;
                foreach (var label in set)
                {
                    var count = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    geometry.Columns.Add((line.Words[at].Left, line.Words[at + count - 1].Right));
                    geometry.Labels.Add(label);
                    at += count;
                }

                return geometry;
            }
        }

        return null;
    }

    private static List<Cell> Cells(IEnumerable<LayoutWord> words)
    {
        var cells = new List<(double Left, double Right, List<string> Words)>();
        LayoutWord? before = null;
        foreach (var w in words.OrderBy(w => w.Left))
        {
            if (before == null || w.Left - before.Right > CellGap)
            {
                cells.Add((w.Left, w.Right, new List<string>()));
            }

            var last = cells[^1];
            cells[^1] = (last.Left, w.Right, last.Words);
            last.Words.Add(w.Text);
            before = w;
        }

        return cells.Select(c => new Cell(c.Left, c.Right, string.Join(' ', c.Words))).ToList();
    }

    /// <summary>The column a cell belongs to: the one whose label it overlaps most (a column's span is widened by a few points). A cell that touches none, or two equally, fails the run.</summary>
    private static int ColumnOf(Cell cell, Geometry geometry, int page)
    {
        var best = -1;
        var bestOverlap = 0.0;
        var tie = false;
        for (var i = 0; i < geometry.Columns.Count; i++)
        {
            var (left, right) = geometry.Columns[i];
            var overlap = Math.Min(cell.Right, right + Slack) - Math.Max(cell.Left, left - Slack);
            if (overlap <= 0)
            {
                continue;
            }

            if (overlap > bestOverlap + 0.001)
            {
                best = i;
                bestOverlap = overlap;
                tie = false;
            }
            else if (Math.Abs(overlap - bestOverlap) <= 0.001)
            {
                tie = true;
            }
        }

        if (best < 0 || tie)
        {
            throw new DegreesException($"Page {page}: a stat table cell does not sit under exactly one column label.");
        }

        return best;
    }

    private static bool IsCaption(StLine line) => line.Words.All(w => w.Role == "block-header");

    /// <summary>
    /// Carves the stat tables out of one page's words. <paramref name="labelSets"/> are the column label lists a header row may carry. Returns the table blocks and the words that are left (the page's text).
    /// A page with no header row returns its words untouched.
    /// </summary>
    public static (List<Block> Tables, List<LayoutWord> Words) Carve(int page, List<LayoutWord> body, IReadOnlyList<IReadOnlyList<string>> labelSets)
    {
        var real = body.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var marks = real.Where(w => w.Size < MarkSize).ToList();
        var lines = Group(real.Where(w => w.Size >= MarkSize));
        var tables = new List<Block>();
        var taken = new HashSet<LayoutWord>();
        var consumedMarks = new HashSet<LayoutWord>();
        var consumedLines = new HashSet<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            if (consumedLines.Contains(i) || HeaderOf(lines[i], labelSets) is not { } firstGeometry)
            {
                continue;
            }

            var rows = new List<List<string>>();
            var members = new List<StLine>();
            var geometry = firstGeometry;
            // A rarity heading may sit between the caption and the header; it is the table's first line then.
            var first = i;
            if (i > 0 && !consumedLines.Contains(i - 1) && !IsCaption(lines[i - 1]) && lines[i - 1].Words.Count <= 2 && lines[i - 1].Words.All(w => w.Text.All(char.IsLetter))
                && lines[i - 1].Bottom - lines[i].Bottom <= RowGap && i > 1 && IsCaption(lines[i - 2]) && lines[i - 2].Bottom - lines[i - 1].Bottom <= RowGap)
            {
                first = i - 1;
            }

            if (first > 0 && !consumedLines.Contains(first - 1) && IsCaption(lines[first - 1]) && lines[first - 1].Bottom - lines[first].Bottom <= 20)
            {
                rows.Add(new List<string> { "title", string.Join(' ', lines[first - 1].Words.Select(w => w.Text)) });
                members.Add(lines[first - 1]);
                consumedLines.Add(first - 1);
            }

            var inNotes = false;
            var j = first;
            for (; j < lines.Count; j++)
            {
                var line = lines[j];
                if (j > first && lines[j - 1].Bottom - line.Bottom > RowGap)
                {
                    break;
                }

                var noteMark = marks.FirstOrDefault(m => !consumedMarks.Contains(m) && m.Bottom - line.Bottom is >= 0 and <= 6 && m.Left < line.Words[0].Left && line.Words[0].Left - m.Right <= 6);
                var starred = line.Words[0].Text.StartsWith('*');
                if (noteMark != null || starred)
                {
                    inNotes = true;
                    var mark = noteMark?.Text ?? "*";
                    var text = string.Join(' ', line.Words.Select(w => w.Text)).TrimStart('*').Trim();
                    rows.Add(new List<string> { "note", mark, text });
                    if (noteMark != null)
                    {
                        consumedMarks.Add(noteMark);
                    }

                    members.Add(line);
                    consumedLines.Add(j);
                    continue;
                }

                if (inNotes)
                {
                    // A note that wraps goes on in lowercase; anything else ends the table.
                    if (char.IsLower(line.Words[0].Text[0]) && rows[^1][0] == "note")
                    {
                        rows[^1][2] += " " + string.Join(' ', line.Words.Select(w => w.Text));
                        members.Add(line);
                        consumedLines.Add(j);
                        continue;
                    }

                    break;
                }

                if (HeaderOf(line, labelSets) is { } again)
                {
                    geometry = again;
                    rows.Add(HeadRow(line, geometry, marks, consumedMarks, page));
                    members.Add(line);
                    consumedLines.Add(j);
                    continue;
                }

                ReadRow(line, geometry, rows, marks, consumedMarks, page);
                members.Add(line);
                consumedLines.Add(j);
            }

            foreach (var m in members)
            {
                foreach (var w in m.Words)
                {
                    taken.Add(w);
                }
            }

            var all = members.SelectMany(m => m.Words).ToList();
            tables.Add(new Block(page, 0, Role, all.Min(w => w.Left), all.Min(w => w.Bottom), all.Max(w => w.Right), all.Max(w => w.Top),
                // The lines are the rows' cells without the row's kind (the word that says what a row is would be a word of the book to the denylist's counts).
                rows.Select(r => string.Join(" | ", r.Skip(1))).ToList(), null, rows));
        }

        if (tables.Count == 0)
        {
            return (tables, body);
        }

        // The words left are the page's text. Marks the table did not take (a raised digit in the text) stay with it. A space between carved words goes with them.
        var carvedBottoms = consumedLines.Select(k => lines[k].Bottom).ToList();
        var rest = body.Where(w =>
        {
            if (taken.Contains(w) || consumedMarks.Contains(w))
            {
                return false;
            }

            return !(string.IsNullOrWhiteSpace(w.Text) && carvedBottoms.Any(b => Math.Abs(b - w.Bottom) <= LineTolerance));
        }).ToList();
        return (tables, rest);
    }

    private static List<string> HeadRow(StLine line, Geometry geometry, List<LayoutWord> marks, HashSet<LayoutWord> consumed, int page)
    {
        var cells = new string?[geometry.Columns.Count];
        foreach (var cell in Cells(line.Words))
        {
            var column = ColumnOf(cell, geometry, page);
            cells[column] = cells[column] == null ? cell.Text : cells[column] + " " + cell.Text;
        }

        AttachMarks(line, geometry, cells, marks, consumed, page);
        var row = new List<string> { "head" };
        row.AddRange(cells.Select(c => c ?? string.Empty));
        return row;
    }

    private static void AttachMarks(StLine line, Geometry geometry, string?[] cells, List<LayoutWord> marks, HashSet<LayoutWord> consumed, int page)
    {
        foreach (var mark in marks.Where(m => !consumed.Contains(m) && m.Bottom - line.Bottom is >= 0 and <= 6 && m.Left >= line.Words[0].Left))
        {
            // The mark sits at the end of a cell, to its right: the cell whose right edge is nearest to the mark's left edge.
            var cell = Cells(line.Words).Where(c => c.Right <= mark.Left + 2.5).OrderByDescending(c => c.Right).FirstOrDefault();
            if (cell == null)
            {
                throw new DegreesException($"Page {page}: a footnote mark has no cell to its left on its line.");
            }

            var column = ColumnOf(cell, geometry, page);
            cells[column] += " ^" + mark.Text;
            consumed.Add(mark);
        }
    }

    private static void ReadRow(StLine line, Geometry geometry, List<List<string>> rows, List<LayoutWord> marks, HashSet<LayoutWord> consumed, int page)
    {
        var width = geometry.Columns.Count;
        var cells = new string?[width];
        foreach (var cell in Cells(line.Words))
        {
            var column = ColumnOf(cell, geometry, page);
            if (cells[column] != null)
            {
                throw new DegreesException($"Page {page}: two cells of one stat table line sit under one column label.");
            }

            cells[column] = cell.Text;
        }

        AttachMarks(line, geometry, cells, marks, consumed, page);
        var filled = Enumerable.Range(0, width).Where(c => cells[c] != null).ToList();
        var lastRow = rows.Count > 0 && rows[^1][0] == "row" ? rows[^1] : null;
        if (cells[0] == null)
        {
            // No name: a wrapped cell of the row above (a trait list that runs on).
            if (lastRow == null)
            {
                throw new DegreesException($"Page {page}: a stat table line with no name has no row above it to continue.");
            }

            // Only the last column (the trait list) wraps in these tables: a nameless line with a cell under any other label is a shifted value, and fails instead of joining a number to text.
            if (filled.Any(c => c != width - 1))
            {
                throw new DegreesException($"Page {page}: a stat table line with no name has a cell under a column other than the last, which is the only one that wraps.");
            }

            foreach (var c in filled)
            {
                lastRow[c + 1] = lastRow[c + 1].Length == 0 ? cells[c]! : lastRow[c + 1] + " " + cells[c];
            }

            return;
        }

        var name = cells[0]!;
        if (filled.Count == 1)
        {
            if (name.StartsWith('('))
            {
                // The item level wraps in the name column.
                if (lastRow == null)
                {
                    throw new DegreesException($"Page {page}: an item level on a line of its own has no row above it.");
                }

                lastRow[1] += " " + name;
                return;
            }

            if (name.Any(char.IsDigit) || name.Split(' ').Length > 2)
            {
                throw new DegreesException($"Page {page}: a stat table line has only a name cell, and it is neither a band nor a wrapped item level.");
            }

            rows.Add(new List<string> { "band", name });
            return;
        }

        if (char.IsDigit(name[0]))
        {
            // Ammunition of the weapon above: a quantity and a name, with its price and Bulk, and no other cell.
            var bulkColumn = geometry.Labels.IndexOf("Bulk");
            if (lastRow == null || bulkColumn < 0 || !filled.SequenceEqual(new[] { 0, 1, bulkColumn }))
            {
                throw new DegreesException($"Page {page}: an ammunition line has no weapon row above it, or more cells than a price and a Bulk.");
            }

            rows.Add(new List<string> { "ammo", cells[0]!, cells[1]!, cells[bulkColumn]! });
            return;
        }

        if (filled.Count != width)
        {
            throw new DegreesException($"Page {page}: a stat table row has {filled.Count} cells where its header has {width}.");
        }

        var row = new List<string> { "row" };
        row.AddRange(cells.Select(c => c!));
        rows.Add(row);
    }
}
