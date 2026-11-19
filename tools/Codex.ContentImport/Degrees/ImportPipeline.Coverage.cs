namespace Codex.ContentImport.Degrees;

/// <summary>What <see cref="ImportPipeline.CheckBookmarkCoverage"/> counted below the sections a row takes.</summary>
/// <param name="Bookmarks">The bookmarks below (and among) the taken sections, sidebar bookmarks apart.</param>
/// <param name="Entries">Bookmarks that are an entry's own: a taken path or a section's own text.</param>
/// <param name="Folded">Bookmarks whose text is part of the taken path above them (a heading inside an entry).</param>
/// <param name="LeftOut">Bookmarks of sections the row names as left out, with their children.</param>
/// <param name="TakenSidebars">Sidebars below the taken sections that the row takes.</param>
/// <param name="LeftOutSidebars">Sidebars below the taken sections that stay out.</param>
public sealed record BookmarkCoverage(int Bookmarks, int Entries, int Folded, int LeftOut, int TakenSidebars, int LeftOutSidebars);

public static partial class ImportPipeline
{
    /// <summary>
    /// For a <c>rules</c> row that names <see cref="TakeRow.LeftOutSections"/>: every bookmark below a section the row takes is counted from the document, and each must be accounted for: an entry of its own (a taken
    /// path, or a section's own text), a heading inside a taken path (folded into its entry), or a section the row names as left out (with all below it). A bookmark that is none of these fails the run, so a section
    /// the take-list forgot is never dropped silently, and a section it names as left out must exist. Sidebar bookmarks are counted apart, by the sidebars they name. Returns one count for each such row.
    /// </summary>
    public static List<BookmarkCoverage> CheckBookmarkCoverage(IReadOnlyList<SpineNode> spine, IEnumerable<TakenRange> ranges)
    {
        var result = new List<BookmarkCoverage>();
        // A chapter may take its sections with several rows (the ones whose children are counted by their fonts, and the ones that are not): the sections are counted together, by the row that names the left-out ones.
        foreach (var group in ranges.Where(r => r.Row.Parser == "rules").GroupBy(r => r.Row.Chapter).Where(g => g.Any(r => r.Row.LeftOutSections != null)))
        {
            var row = group.First(r => r.Row.LeftOutSections != null).Row;
            var sidebarPaths = group.SelectMany(r => r.Row.Sidebars).Distinct().ToList();
            var taken = group.Select(r => r.Node).Distinct().ToList();
            var folding = group.Where(r => !r.Own).Select(r => r.Node).ToList();
            var left = new List<SpineNode>();
            foreach (var path in row.LeftOutSections!)
            {
                var node = SpineStage.FindUnique(spine, path) ?? throw new DegreesException($"Chapter {row.Chapter}: leftOutSections names a bookmark path that is not in the book.");
                if (!taken.Any(t => IsBelow(node, t)))
                {
                    throw new DegreesException($"Chapter {row.Chapter}: leftOutSections names a section that lies below no section the row takes.");
                }

                left.Add(node);
            }

            var scope = spine.Where(n => taken.Any(t => ReferenceEquals(n, t) || IsBelow(n, t))).ToList();
            int entries = 0, folded = 0, leftOut = 0, bookmarks = 0;
            foreach (var node in scope.Where(n => !n.Sidebar))
            {
                bookmarks++;
                if (taken.Any(t => ReferenceEquals(t, node)))
                {
                    entries++;
                }
                else if (folding.Any(f => IsBelow(node, f)))
                {
                    folded++;
                }
                else if (left.Any(l => ReferenceEquals(l, node) || IsBelow(node, l)))
                {
                    leftOut++;
                }
                else
                {
                    throw new DegreesException($"Chapter {row.Chapter}: a bookmark of a taken section is no entry's, is inside no taken path and is not named as left out (the take-list forgot a section).");
                }
            }

            var sidebars = scope.Where(n => n.Sidebar).SelectMany(n => SidebarTitles(n.Title).Select(t => (Node: n, Title: t))).ToList();
            var takenSidebars = sidebars.Count(s => sidebarPaths.Any(p => ResolveSidebar(spine, p) is { } hit && ReferenceEquals(hit.Node, s.Node) && BlocksStage.SidebarTitleSet.Key(hit.Title) == BlocksStage.SidebarTitleSet.Key(s.Title)));
            result.Add(new BookmarkCoverage(bookmarks, entries, folded, leftOut, takenSidebars, sidebars.Count - takenSidebars));
        }

        return result;
    }

    /// <summary>True when <paramref name="node"/> is below <paramref name="above"/> in the bookmark tree (its path starts with the other's path and is longer).</summary>
    private static bool IsBelow(SpineNode node, SpineNode above) =>
        node.Path.Length > above.Path.Length && node.Path.Take(above.Path.Length).SequenceEqual(above.Path);
}
