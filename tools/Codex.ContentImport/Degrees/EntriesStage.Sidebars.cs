namespace Codex.ContentImport.Degrees;

public static partial class EntriesStage
{
    /// <summary>
    /// The sidebars a rules row takes as rules (<see cref="TakeRow.Sidebars"/>): one <c>rule</c> entry for each, named by the bookmark's title without its prefix, with the sidebar's blocks as its text. The blocks
    /// were kept apart by the blocks stage (<see cref="BlocksResult.TakenSidebars"/>); a sidebar that has none fails the run, so a row cannot take nothing.
    /// </summary>
    private static List<ParsedEntry> ParseSidebars(TakenRange range, BlocksResult blocks, IReadOnlyList<SpineNode> spine, string book, HashSet<string> compounds)
    {
        var result = new List<ParsedEntry>();
        foreach (var path in range.Row.Sidebars)
        {
            var (node, title) = ImportPipeline.ResolveSidebar(spine, path) ?? throw new DegreesException($"Chapter {range.Row.Chapter}: a sidebar path of the row must be one sidebar of the book.");
            title = title.TrimStart('=');
            var key = BlocksStage.SidebarTitleSet.Key(title);
            if (blocks.TakenSidebars == null || !blocks.TakenSidebars.TryGetValue(key, out var taken) || taken.Count == 0)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}: the sidebar the row takes ({title}) was not found among the page's blocks.");
            }

            var entry = NewEntry(range, book, Clean(range.Row.EntryNames.TryGetValue(path[^1], out var renamed) || range.Row.EntryNames.TryGetValue(title, out renamed) ? renamed : title), compounds);
            // The bookmark path as the outline has it, prefix and all; a title the take-list names by its hash (a reserved word) is not in the source either: the source says "Sidebar" there.
            entry.Source = SpanText.Normalize(book + " > " + string.Join(" > ", path.Select(s => SpineStage.IsHashSegment(s) ? "Sidebar" : s)));
            // A sidebar the row names has its body-font blocks read as the body of a rule is (ragged lines that run on, bold labels that open a paragraph), as the page's own text of that font is.
            var asBody = range.Row.Subheadings && range.Row.BodySidebars.Any(s => SpineStage.SegmentIs(s, title) || SpineStage.SegmentIs(s, node.Title));
            var reader = new BodyReader(entry, compounds, false, range.Row.TableRows, null, range.Row.Subheadings, range.Row.ProseBoxes) { LowerContinues = asBody, ListEndsAtStop = asBody };
            // A title set across the gutter is two headings, each a part of the title and each in its own column: a heading that is a part of the title but alone in its column is a subheading.
            bool PartOfTitle(Block block)
            {
                var headingKey = BlocksStage.SidebarTitleSet.Key(string.Join(' ', block.Lines));
                return block.Role == "block-header" && headingKey.Length >= 4 && key.Length > headingKey.Length && (key.StartsWith(headingKey, StringComparison.Ordinal) || key.EndsWith(headingKey, StringComparison.Ordinal));
            }

            var acrossGutter = taken.Where(PartOfTitle).Select(b => b.Column).Distinct().Count() > 1;
            foreach (var block in taken)
            {
                // The sidebar's own heading is its name; its paragraphs are read as running text.
                var headingKey = BlocksStage.SidebarTitleSet.Key(string.Join(' ', block.Lines));
                if (block.Role == "block-header" && (headingKey == key || (acrossGutter && PartOfTitle(block))))
                {
                    continue;
                }

                if (asBody && block.Role == "block-header")
                {
                    // A subheading of the sidebar is a paragraph of its own.
                    reader.EndParagraph();
                    reader.Read(block with { Role = "rules" });
                    reader.EndParagraph();
                    continue;
                }

                reader.Read(asBody && block.Role == "body" ? block : block with { Role = "rules" });
            }

            reader.Finish();
            result.Add(entry);
        }

        return result;
    }
}
