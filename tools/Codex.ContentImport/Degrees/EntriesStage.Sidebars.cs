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
            var node = SpineStage.FindUnique(spine, path) ?? throw new DegreesException($"Chapter {range.Row.Chapter}: a sidebar path of the row is not a bookmark of the book.");
            var titles = ImportPipeline.SidebarTitles(node.Title).ToList();
            if (!node.Sidebar || titles.Count != 1)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}: a sidebar path of the row must be one sidebar bookmark.");
            }

            var key = BlocksStage.SidebarTitleSet.Key(titles[0]);
            if (blocks.TakenSidebars == null || !blocks.TakenSidebars.TryGetValue(key, out var taken) || taken.Count == 0)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}: the sidebar the row takes was not found among the page's blocks.");
            }

            var entry = NewEntry(range, book, Clean(titles[0]), compounds);
            entry.Source = SpanText.Normalize(book + " > " + string.Join(" > ", path)); // the bookmark path as the outline has it, prefix and all
            var reader = new BodyReader(entry, compounds, false, range.Row.TableRows, null, range.Row.Subheadings, range.Row.ProseBoxes);
            foreach (var block in taken)
            {
                // The sidebar's own heading is its name; its paragraphs are read as running text.
                if (block.Role == "block-header" && BlocksStage.SidebarTitleSet.Key(string.Join(' ', block.Lines)) == key)
                {
                    continue;
                }

                reader.Read(block with { Role = "rules" });
            }

            reader.Finish();
            result.Add(entry);
        }

        return result;
    }
}
