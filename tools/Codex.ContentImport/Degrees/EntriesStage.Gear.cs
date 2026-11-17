using System.Text;

namespace Codex.ContentImport.Degrees;

public static partial class EntriesStage
{
    /// <summary>The small words a name keeps in lowercase (but for its first word), as the book's own names of items do.</summary>
    private static readonly HashSet<string> GearSmallWords = new(StringComparer.Ordinal) { "a", "an", "and", "of", "or", "the", "to", "in", "on", "for", "with" };

    /// <summary>
    /// A gear row's name in title case: the table prints them in sentence case (<c>Alchemist's lab</c>), and the descriptions in title case (<c>Alchemist's Lab</c>). Each word starts with a capital, after a hyphen too,
    /// but for the small words; a bracket (a quantity, <c>blank</c>) keeps its words as printed.
    /// </summary>
    public static string GearTitle(string name)
    {
        var sb = new StringBuilder();
        var bracket = 0;
        var first = true;
        foreach (var word in SpanText.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bracket += word.Count(c => c == '(') - word.Count(c => c == ')');
            var inside = bracket > 0 || word.StartsWith('(');
            var cased = inside || (!first && GearSmallWords.Contains(word))
                ? word
                : string.Join('-', word.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
            sb.Append(sb.Length == 0 ? string.Empty : " ").Append(cased);
            first = false;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The section a gear row comes from, as a bookmark path: the section of its table (the adventuring gear, each of the three alchemical sections, the assistive items) among the row's ranges, else the row's first;
    /// the barding's table is under the animals' section, a sibling of the ranges' own, so its path is built from theirs.
    /// </summary>
    private static List<string> SourcePath(GearRow item, List<TakenRange> rowRanges)
    {
        var first = rowRanges[0].Row.PathOf(rowRanges[0]);
        if (item.Kind == "barding")
        {
            return first.Take(first.Count - 1).Concat(new[] { "Animals", "Barding" }).ToList();
        }

        var title = item.Kind switch
        {
            "gear" => "Adventuring Gear",
            "bomb" => "Alchemical Bombs",
            "elixir" => "Elixirs",
            "alchemical-tool" => "Alchemical Tools",
            "assistive" => "Assistive Items",
            _ => string.Empty,
        };
        var match = rowRanges.Select(r => r.Row.PathOf(r)).FirstOrDefault(p => p[^1].Equals(title, StringComparison.OrdinalIgnoreCase));
        return match ?? first;
    }

    /// <summary>
    /// The entries of an <c>items</c> row of kind <c>gear</c>. The row has several ranges (the sections whose paragraphs describe the items), and the entries are made once, from the first, out of every range's descriptions.
    /// A description is a paragraph that opens with a bold name and a colon, and is joined by name to its row of a gear table (the item level and any bracket left off). A heading of a table (<c>Lantern</c>) is joined too, and
    /// its description is the description of the indented rows under it, which have none of their own; so is the description of a row for the indented versions of that row. A row with no description of its own or
    /// of its heading or row, a description with no row or heading, or two of either with one name fails the run, unless the row's <see cref="TakeRow.Undescribed"/> names the row (and says why in its countsDiffer).
    /// </summary>
    private static List<ParsedEntry> ParseGear(TakenRange range, List<TakenRange> rowRanges, BlocksResult blocks, IReadOnlyList<SpineNode> spine, string book, HashSet<string> compounds)
    {
        var row = range.Row;
        var result = new List<ParsedEntry>();
        var here = ReadDescriptionsAndLead(range.BlocksIn(blocks), row, compounds);
        var path = row.PathOf(range);
        if (row.LeadRules.Any(p => p.SequenceEqual(path, StringComparer.OrdinalIgnoreCase)))
        {
            if (here.LeadParagraphs.Count == 0)
            {
                throw new DegreesException($"Chapter {row.Chapter}: a section the row takes the lead of has no text before its first description.");
            }

            var rule = NewEntry(range, book, Clean(path[^1]), compounds, "rule", "rules", row.RuleCategory);
            foreach (var p in here.LeadParagraphs)
            {
                rule.Parts.Add(new LabelledText(string.Empty, p));
                rule.Paragraphs.Add(p);
            }

            result.Add(rule);
        }

        if (range != rowRanges[0])
        {
            return result;
        }

        var descriptions = rowRanges.SelectMany(r => ReadDescriptions(r.BlocksIn(blocks), row, compounds).Descriptions).ToList();
        var (rows, bands) = GearRows.Read(blocks, spine, row);
        if (descriptions.GroupBy(d => GearRows.Key(d.Name)).Any(g => g.Count() > 1) || rows.GroupBy(r => GearRows.Key(r.Name) + "|" + r.Kind + "|" + r.Group).Any(g => g.Count() > 1))
        {
            throw new DegreesException($"Chapter {row.Chapter}: a name is repeated among the gear table rows or among the descriptions.");
        }

        var rowKeys = rows.Select(r => GearRows.Key(r.Name)).ToHashSet(StringComparer.Ordinal);
        var bandKeys = bands.Select(GearRows.Key).ToHashSet(StringComparer.Ordinal);
        var byKey = descriptions.ToDictionary(d => GearRows.Key(d.Name), StringComparer.Ordinal);
        var noRow = descriptions.Where(d => !rowKeys.Contains(GearRows.Key(d.Name)) && !bandKeys.Contains(GearRows.Key(d.Name))).Select(d => d.Name).ToList();

        // The description a row reads: its own, else the one of the row or heading it is listed under.
        ItemDescription? DescriptionOf(GearRow r) =>
            byKey.GetValueOrDefault(GearRows.Key(r.Name)) ?? (r.Parent != null ? byKey.GetValueOrDefault(GearRows.Key(r.Parent)) : null);

        var undescribed = row.Undescribed.Select(GearRows.Key).ToHashSet(StringComparer.Ordinal);
        var withDescription = rows.Where(r => DescriptionOf(r) != null).Select(r => GearRows.Key(r.Name)).ToHashSet(StringComparer.Ordinal);
        if (undescribed.Any(k => !rowKeys.Contains(k) || withDescription.Contains(k)))
        {
            throw new DegreesException($"Chapter {row.Chapter}: an undescribed name has a description (its own or its heading's) or no row.");
        }

        var noDescription = rows.Where(r => DescriptionOf(r) == null && !undescribed.Contains(GearRows.Key(r.Name))).Select(r => r.Name).ToList();
        if (noDescription.Count > 0 || noRow.Count > 0)
        {
            throw new DegreesException($"Chapter {row.Chapter}: {rows.Count} gear table rows and {descriptions.Count} descriptions; {noDescription.Count} row(s) have no description ({string.Join("; ", noDescription)}) and {noRow.Count} description(s) have no row ({string.Join("; ", noRow)}).");
        }

        foreach (var item in rows)
        {
            var description = DescriptionOf(item);
            // An indented row under a heading is named by both, unless its own name already says what the heading does (Poor lock under Lock).
            var headingWord = item.Parent?[(item.Parent.LastIndexOf(' ') + 1)..] ?? string.Empty;
            var printed = item.Sub && item.ParentIsBand && !item.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(headingWord, StringComparer.OrdinalIgnoreCase)
                ? item.Parent + ", " + item.Name
                : item.Name;
            if (item.Group.Length > 0)
            {
                printed = item.Group + ", " + item.Name;
            }

            var entry = NewEntry(range, book, GearTitle(printed), compounds);
            entry.Source = SpanText.Normalize(book + " > " + string.Join(" > ", SourcePath(item, rowRanges)));
            entry.Level = item.Level;
            Raw(entry, "kind", PackEmitter.Scalar(item.Kind, 6));
            RawText(entry, "price", item.Cells.FirstOrDefault(c => c.Label == "Price").Text);
            RawText(entry, "bulk", item.Cells.FirstOrDefault(c => c.Label == "Bulk").Text);
            RawText(entry, "hands", item.Cells.FirstOrDefault(c => c.Label == "Hands").Text);
            if (item.Parent != null)
            {
                Raw(entry, "variantOf", PackEmitter.Scalar(GearTitle(item.Parent), 6));
            }

            if (description != null)
            {
                foreach (var p in description.Paragraphs)
                {
                    entry.Parts.Add(new LabelledText(string.Empty, p));
                    entry.Paragraphs.Add(p);
                }
            }

            var traitCell = item.Cells.FirstOrDefault(c => c.Label == "Item Traits").Text;
            if (TraitCell(traitCell) is { Count: > 0 } traits)
            {
                foreach (var t in traits)
                {
                    var key = TraitKey(t);
                    if (!entry.Traits.Contains(key))
                    {
                        entry.Traits.Add(key);
                    }
                }

                var part = new LabelledText("Traits", string.Join(", ", traits).ToLowerInvariant());
                entry.Parts.Add(part);
                entry.Fields.Add(part);
            }

            // The note under the alchemical tables says all their items have the alchemical and consumable traits: the traits are added from it, and only while it says so.
            if (item.Kind is "bomb" or "elixir" or "alchemical-tool")
            {
                var note = item.Notes.Count == 1 ? item.Notes[0].ToLowerInvariant() : string.Empty;
                if (!note.Contains("alchemical", StringComparison.Ordinal) || !note.Contains("consumable", StringComparison.Ordinal) || !note.Contains("all", StringComparison.Ordinal))
                {
                    throw new DegreesException($"Chapter {row.Chapter}: the note under an alchemical table no longer says that all its items are alchemical and consumable.");
                }

                foreach (var t in new[] { "alchemical", "consumable" })
                {
                    if (!entry.Traits.Contains(t))
                    {
                        entry.Traits.Add(t);
                    }
                }
            }

            // The statistics of the row as the table prints them, so an item with no paragraph still has its text; a dash is none.
            foreach (var (label, text) in item.Cells.Where(c => c.Label != "Item Traits"))
            {
                if (Dash(text).Length > 0)
                {
                    var stat = new LabelledText(label, Clean(Dash(text)));
                    entry.Parts.Add(stat);
                    entry.Fields.Add(stat);
                }
            }

            if (item.Level is { } level)
            {
                var stat = new LabelledText("Level", level.ToString(System.Globalization.CultureInfo.InvariantCulture));
                entry.Parts.Add(stat);
                entry.Fields.Add(stat);
            }

            foreach (var n in item.Notes)
            {
                var part = new LabelledText("Note", Clean(n));
                entry.Parts.Add(part);
                entry.Fields.Add(part);
            }

            if (item.Uncommon && !entry.Traits.Contains("uncommon"))
            {
                entry.Traits.Add("uncommon");
            }

            result.Add(entry);
        }

        return result;
    }
}
