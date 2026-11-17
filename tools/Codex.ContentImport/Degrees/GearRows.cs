using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>One row of a gear table (see <see cref="GearTables"/>), read from the <c>stat-table</c> blocks.</summary>
/// <param name="Name">The name as printed, without its item level and footnote star; a quantity in brackets stays (<c>Candle (10)</c>).</param>
/// <param name="Kind">What the table is: <c>gear</c>, <c>assistive</c>, <c>bomb</c>, <c>elixir</c>, <c>alchemical-tool</c> or <c>barding</c>.</param>
/// <param name="Sub">True for an indented row: a version of the row above, or a row of the heading above.</param>
/// <param name="Parent">For a sub row: the name of the row or heading it is listed under.</param>
/// <param name="Group">For barding: the table's own heading (light or heavy), which the row's size is a part of.</param>
/// <param name="Cells">The row's cells in the table's order, by their labels (a footnote star taken off), a dash left as it is.</param>
/// <param name="Notes">The footnotes that apply: the one under the table, when the row or its header carries the star.</param>
public sealed record GearRow(string Name, int? Level, string Kind, bool Uncommon, bool Sub, string? Parent, bool ParentIsBand, string Group, List<(string Label, string Text)> Cells, List<string> Notes, int Page);

/// <summary>Reads the rows of the gear tables an <c>items</c> row of kind gear names.</summary>
public static partial class GearRows
{
    /// <summary>The key a row and a description are joined by: the lowercase letters and digits of the name, with every bracket (an item level, a quantity, a word like blank) left off.</summary>
    public static string Key(string name) => new(Brackets().Replace(SpanText.Normalize(name), string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The rows of every gear table of the row in page order, and the headings (bands) the tables set above their versions.</summary>
    public static (List<GearRow> Rows, List<string> Bands) Read(BlocksResult blocks, IReadOnlyList<SpineNode> spine, TakeRow row)
    {
        var rows = new List<GearRow>();
        var bands = new List<string>();
        foreach (var table in ItemRows.TablesOf(blocks, spine, row))
        {
            string title = string.Empty, kind = string.Empty, group = string.Empty;
            var uncommon = false;
            // The note under a block of tables is the note of every table in the block: one star on any header (the first of the alchemical tables) says that the note covers them all, as its own words do.
            var tableStar = table.Rows!.Any(r => r[0] == "head" && r.Skip(2).Any(c => c.Trim().EndsWith('*')));
            List<string>? labels = null;
            string? parent = null;
            var parentIsBand = false;
            var emptyBand = false;
            var notes = table.Rows!.Where(r => r[0] == "note").Select(r => r[2]).ToList();
            if (notes.Count > 1)
            {
                throw new DegreesException($"Page {table.Page}: a gear table has more than one footnote.");
            }

            foreach (var r in table.Rows!)
            {
                switch (r[0])
                {
                    case "title":
                        title = r[1].ToLowerInvariant();
                        break;
                    case "head":
                        if (emptyBand)
                        {
                            throw new DegreesException($"Page {table.Page}: a gear table heading has no indented row under it.");
                        }

                        labels = r.Skip(2).Select(c => c.Trim().TrimEnd('*').Trim()).ToList();
                        var head = r[1].Trim();
                        group = string.Empty;
                        uncommon = false;
                        switch (head)
                        {
                            case "Item" when title.Contains("assistive", StringComparison.Ordinal):
                                kind = "assistive";
                                break;
                            case "Item" when title.Contains("adventuring gear", StringComparison.Ordinal):
                                kind = "gear";
                                uncommon = title.StartsWith("uncommon", StringComparison.Ordinal);
                                break;
                            case "Alchemical Bombs":
                                kind = "bomb";
                                break;
                            case "Elixirs":
                                kind = "elixir";
                                break;
                            case "Alchemical Tools":
                                kind = "alchemical-tool";
                                break;
                            case "Light Barding" or "Heavy Barding":
                                kind = "barding";
                                group = head;
                                break;
                            default:
                                throw new DegreesException($"Page {table.Page}: a gear table's header names a table this reader does not know.");
                        }

                        parent = null;
                        break;
                    case "band":
                        // A heading is followed by the rows set in under it. A line that is a name alone and has none under it is a name that wrapped (or a heading that heads nothing), and fails.
                        if (emptyBand)
                        {
                            throw new DegreesException($"Page {table.Page}: a gear table heading has no indented row under it.");
                        }

                        emptyBand = true;
                        bands.Add(r[1]);
                        parent = r[1];
                        parentIsBand = true;
                        break;
                    case "row" or "sub":
                    {
                        if (labels == null)
                        {
                            throw new DegreesException($"Page {table.Page}: a gear table row comes before its header.");
                        }

                        var sub = r[0] == "sub";
                        if (emptyBand && !sub)
                        {
                            throw new DegreesException($"Page {table.Page}: a gear table heading has no indented row under it.");
                        }

                        emptyBand = false;
                        var marked = false;
                        var name = r[1].Trim();
                        if (name.EndsWith('*'))
                        {
                            name = name.TrimEnd('*').TrimEnd();
                            marked = true;
                        }

                        var cells = new List<(string, string)>();
                        for (var c = 0; c < labels.Count; c++)
                        {
                            var text = r[c + 2].Trim();
                            if (text.EndsWith('*'))
                            {
                                text = text.TrimEnd('*').TrimEnd();
                                marked = true;
                            }

                            cells.Add((labels[c], text));
                        }

                        int? level = null;
                        var m = LevelBracket().Match(name);
                        if (m.Success)
                        {
                            level = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                            name = name.Remove(m.Index, m.Length).Trim();
                        }

                        if (sub && parent == null)
                        {
                            throw new DegreesException($"Page {table.Page}: an indented gear row has no row or heading above it.");
                        }

                        var rowNotes = new List<string>();
                        if (marked || tableStar)
                        {
                            rowNotes.Add(notes.Count == 1 ? notes[0] : throw new DegreesException($"Page {table.Page}: a gear row carries a footnote star and its table has no footnote."));
                        }

                        rows.Add(new GearRow(name, level, kind, uncommon, sub, sub ? parent : null, sub && parentIsBand, group, cells, rowNotes, table.Page));
                        if (!sub)
                        {
                            parent = name;
                            parentIsBand = false;
                        }

                        break;
                    }
                }
            }

            if (emptyBand)
            {
                throw new DegreesException($"Page {table.Page}: a gear table ends on a heading with no indented row under it.");
            }
        }

        return (rows, bands);
    }

    [GeneratedRegex(@"\s*\(level (\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex LevelBracket();

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex Brackets();
}
