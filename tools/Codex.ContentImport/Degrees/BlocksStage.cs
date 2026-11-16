namespace Codex.ContentImport.Degrees;

/// <summary>A run of lines of one role inside one column of one page.</summary>
public sealed record Block(int Page, int Column, string Role, double Left, double Bottom, double Right, double Top, List<string> Lines);

public sealed record BlocksResult(List<Block> Blocks, int OneColumnPages, int TwoColumnPages, int FurnitureWords, Dictionary<string, int> BlocksByRole);

/// <summary>
/// Stage 3: the words of each page without its furniture, split into columns by the gap between them, cut into lines by baseline,
/// and into blocks by role. Columns are split per page because the furniture, and sometimes the gutter, change sides between pages.
/// </summary>
public static class BlocksStage
{
    private const double GutterMin = 8;
    private const double LineTolerance = 2.5;
    private const double BlockGap = 6;

    public static BlocksResult Run(IEnumerable<LayoutPage> pages)
    {
        var blocks = new List<Block>();
        int one = 0, two = 0, furniture = 0;
        foreach (var page in pages)
        {
            var body = page.Words.Where(w => w.Role != "furniture").ToList();
            furniture += page.Words.Count - body.Count;
            if (body.Count == 0)
            {
                continue;
            }

            var gutter = FindGutter(body, page.Width);
            if (gutter == null)
            {
                one++;
            }
            else
            {
                two++;
            }

            var columns = gutter == null
                ? new[] { body }
                : new[] { body.Where(w => Centre(w) < gutter).ToList(), body.Where(w => Centre(w) >= gutter).ToList() };
            for (var c = 0; c < columns.Length; c++)
            {
                blocks.AddRange(ColumnBlocks(page.Number, c, columns[c]));
            }
        }

        var byRole = blocks.GroupBy(b => b.Role).ToDictionary(g => g.Key, g => g.Count());
        return new BlocksResult(blocks, one, two, furniture, byRole);
    }

    /// <summary>The x of the middle of the widest empty vertical band near the centre of the page, or null when the page has one column.</summary>
    public static double? FindGutter(IReadOnlyList<LayoutWord> words, double pageWidth)
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
        var lo = (int)(pageWidth * 0.3 / Bin);
        var hi = (int)(pageWidth * 0.7 / Bin);
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

        return best.Length * Bin >= GutterMin ? (best.Start + best.Length / 2.0) * Bin : null;
    }

    private static double Centre(LayoutWord w) => (w.Left + w.Right) / 2;

    private static IEnumerable<Block> ColumnBlocks(int page, int column, List<LayoutWord> words)
    {
        var lines = new List<List<LayoutWord>>();
        foreach (var w in words.OrderByDescending(w => w.Bottom))
        {
            var line = lines.Count > 0 && Math.Abs(lines[^1][0].Bottom - w.Bottom) <= LineTolerance ? lines[^1] : null;
            if (line == null)
            {
                lines.Add(line = new List<LayoutWord>());
            }

            line.Add(w);
        }

        Block? open = null;
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(w => w.Left).ToList();
            var role = LineRole(ordered);
            var top = ordered.Max(w => w.Top);
            var bottom = ordered.Min(w => w.Bottom);
            var left = ordered[0].Left;
            var right = ordered.Max(w => w.Right);
            var text = string.Join(' ', ordered.Select(w => w.Text));
            if (open != null && open.Role == role && open.Bottom - top <= BlockGap)
            {
                open.Lines.Add(text);
                open = open with { Left = Math.Min(open.Left, left), Right = Math.Max(open.Right, right), Bottom = bottom, Lines = open.Lines };
                continue;
            }

            if (open != null)
            {
                yield return open;
            }

            open = new Block(page, column, role, left, bottom, right, top, new List<string> { text });
        }

        if (open != null)
        {
            yield return open;
        }
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
