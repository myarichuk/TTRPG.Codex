using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>One row of a stat table (an armor, a shield or a weapon), read from the <c>stat-table</c> blocks (see <see cref="StatTables"/>).</summary>
/// <param name="Name">The name as printed, without its item level, footnote marks and star.</param>
/// <param name="Category">The band's category: for armor unarmored, light, medium or heavy; for weapons simple, martial, advanced or unarmed.</param>
/// <param name="Cells">The row's cells by column label, with their footnote marks taken off.</param>
/// <param name="Notes">The footnotes that apply: those on the row's cells and those on the table's header labels, in mark order.</param>
public sealed record ItemRow(string Name, int? Level, string Category, bool Uncommon, string? Attack, Dictionary<string, string> Cells, List<string> Notes, string? Ammunition, int Page);

/// <summary>Reads the rows of the stat tables an <c>items</c> row names.</summary>
public static partial class ItemRows
{
    /// <summary>The words of a band's name that are not its category.</summary>
    private static readonly HashSet<string> BandNoise = new(StringComparer.Ordinal) { "armor", "weapons", "attack" };

    private static readonly Dictionary<string, string[]> Categories = new(StringComparer.Ordinal)
    {
        ["armor"] = new[] { "unarmored", "light", "medium", "heavy" },
        ["shield"] = new[] { "shield" },
        ["weapon"] = new[] { "simple", "martial", "advanced", "unarmed" },
    };

    /// <summary>The pages the row's <see cref="TakeRow.Tables"/> sections cover.</summary>
    public static HashSet<int> PagesOf(IReadOnlyList<SpineNode> spine, TakeRow row) =>
        row.Tables.SelectMany(p => SpineStage.FindUnique(spine, p) is { } n ? Enumerable.Range(n.PageStart, n.PageEnd - n.PageStart + 1) : Enumerable.Empty<int>()).ToHashSet();

    /// <summary>The <c>stat-table</c> blocks of the row's pages whose header labels are one of the row's lists.</summary>
    public static List<Block> TablesOf(BlocksResult blocks, IReadOnlyList<SpineNode> spine, TakeRow row)
    {
        var pages = PagesOf(spine, row);
        return blocks.Blocks.Where(b => b.Role == StatTables.Role && pages.Contains(b.Page) && b.Rows != null && b.Rows.Where(r => r[0] == "head").Select(r => Labels(r)).Any(l => row.TableColumns.Any(c => c.SequenceEqual(l)))).ToList();
    }

    /// <summary>The column labels of a head row: its cells after the name cell, with marks off.</summary>
    public static List<string> Labels(List<string> head) => head.Skip(2).Select(c => SplitMarks(c).Text).ToList();

    private static readonly Regex MarkTail = new(@"^(?<text>.*?)(?<marks>(?: \^[^\s^]+)+)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>A cell with its footnote marks (the text <c>^n</c> the table reader adds) taken off.</summary>
    public static (string Text, List<string> Marks) SplitMarks(string cell)
    {
        var m = MarkTail.Match(cell);
        return m.Success
            ? (m.Groups["text"].Value.Trim(), m.Groups["marks"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x[1..]).ToList())
            : (cell.Trim(), new List<string>());
    }

    /// <summary>The rows of every table of the <paramref name="row"/> in page order, each with its band's category, rarity and footnotes.</summary>
    public static List<ItemRow> Read(BlocksResult blocks, IReadOnlyList<SpineNode> spine, TakeRow row)
    {
        var result = new List<ItemRow>();
        foreach (var table in TablesOf(blocks, spine, row))
        {
            string caption = string.Empty, category = string.Empty;
            List<string>? labels = null;
            var bandRare = false;
            var pendingRare = false;
            var headMarks = new List<string>();
            var notes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in table.Rows!.Where(r => r[0] == "note"))
            {
                notes[r[1]] = r[2];
            }

            List<string> NotesFor(IEnumerable<string> marks) => marks.Where(m => m != "*").Distinct().OrderBy(m => m, StringComparer.Ordinal)
                .Select(m => notes.TryGetValue(m, out var n) ? n : throw new DegreesException($"Page {table.Page}: a footnote mark has no note under its table.")).ToList();

            foreach (var r in table.Rows!)
            {
                switch (r[0])
                {
                    case "title":
                        caption = r[1];
                        break;
                    case "head":
                    {
                        var these = Labels(r);
                        if (labels != null && !labels.SequenceEqual(these))
                        {
                            throw new DegreesException($"Page {table.Page}: a stat table changes its columns half way.");
                        }

                        labels = these;
                        headMarks = r.Skip(2).SelectMany(c => SplitMarks(c).Marks).ToList();
                        var band = SplitMarks(r[1]).Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                        bandRare = pendingRare || band[0] == "uncommon";
                        pendingRare = false;
                        category = band.Where(w => w != "uncommon" && !BandNoise.Contains(w)).DefaultIfEmpty(string.Empty).First();
                        if (!Categories[row.ItemKind!].Contains(category))
                        {
                            throw new DegreesException($"Page {table.Page}: a stat table band names a category the {row.ItemKind} rows do not have.");
                        }

                        break;
                    }

                    case "band":
                        if (!r[1].Equals("Uncommon", StringComparison.Ordinal))
                        {
                            throw new DegreesException($"Page {table.Page}: a stat table band other than the rarity heading.");
                        }

                        pendingRare = true;
                        break;
                    case "row":
                    {
                        if (labels == null)
                        {
                            throw new DegreesException($"Page {table.Page}: a stat table row comes before its header.");
                        }

                        if (pendingRare)
                        {
                            throw new DegreesException($"Page {table.Page}: a rarity heading is followed by a row, not by a header, so it would be lost.");
                        }

                        var (name, nameMarks) = SplitMarks(r[1]);
                        var marks = nameMarks.ToList();
                        if (name.EndsWith('*'))
                        {
                            name = name.TrimEnd('*').TrimEnd();
                            marks.Add("*");
                        }

                        var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                        for (var c = 0; c < labels.Count; c++)
                        {
                            var (text, cellMarks) = SplitMarks(r[c + 2]);
                            cells[labels[c]] = text;
                            marks.AddRange(cellMarks);
                        }

                        int? level = null;
                        var levelMatch = Level().Match(name);
                        if (levelMatch.Success)
                        {
                            level = int.Parse(levelMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                            name = name.Remove(levelMatch.Index, levelMatch.Length).Trim();
                        }

                        string? attack = null;
                        if (row.ItemKind == "weapon")
                        {
                            var up = caption.ToLowerInvariant();
                            attack = up.Contains("ranged", StringComparison.Ordinal) ? "ranged" : up.Contains("melee", StringComparison.Ordinal) || up.Contains("unarmed", StringComparison.Ordinal) ? "melee" : throw new DegreesException($"Page {table.Page}: a weapon table's caption says neither melee, ranged nor unarmed.");
                            if (attack == "ranged" != labels.Contains("Range"))
                            {
                                throw new DegreesException($"Page {table.Page}: a weapon table's caption and its Range column disagree on whether it is ranged.");
                            }
                        }

                        result.Add(new ItemRow(name, level, category, bandRare, attack, cells, NotesFor(marks.Concat(headMarks)), null, table.Page));
                        break;
                    }

                    case "ammo":
                    {
                        var last = result.Count > 0 ? result[^1] : throw new DegreesException($"Page {table.Page}: an ammunition line comes before any weapon.");
                        var text = $"{SplitMarks(r[1]).Text}, {SplitMarks(r[2]).Text}, Bulk {SplitMarks(r[3]).Text}";
                        if (last.Ammunition != null)
                        {
                            throw new DegreesException($"Page {table.Page}: a weapon has two ammunition lines.");
                        }

                        result[^1] = last with { Ammunition = text };
                        break;
                    }
                }
            }

            if (pendingRare)
            {
                throw new DegreesException($"Page {table.Page}: a table ends on a rarity heading, so it would be lost.");
            }
        }

        return result;
    }

    /// <summary>The key two names are joined by: the lowercase letters and digits of the name, with an item level and a star left off.</summary>
    public static string Key(string name) => new(Level().Replace(SpanText.Normalize(name), string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"\s*\(level (\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Level();
}
