using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace Codex.ContentImport.Degrees;

/// <summary>One bookmark with the pages it covers. <c>PageEnd</c> is inclusive and shares a page with the next section when both start there.</summary>
public sealed record SpineNode(int Depth, string Title, string[] Path, int PageStart, int PageEnd, bool Sidebar);

/// <summary>Stage 2: the bookmark tree, flattened in document order with page ranges, and the counts the fingerprint compares.</summary>
public static class SpineStage
{
    public static List<SpineNode> Run(PdfDocument doc)
    {
        var flat = new List<(int Depth, string Title, string[] Path, int Page)>();
        if (doc.TryGetBookmarks(out var bookmarks))
        {
            foreach (var root in bookmarks.Roots)
            {
                Walk(root, 0, Array.Empty<string>(), flat);
            }
        }

        return Build(flat, doc.NumberOfPages);
    }

    /// <summary>The nodes of a flattened bookmark tree (depth, title, path and the page each points at), with their page ranges. The part of <see cref="Run"/> that needs no PDF.</summary>
    public static List<SpineNode> Build(List<(int Depth, string Title, string[] Path, int Page)> flat, int numberOfPages)
    {
        flat = flat.ToList();
        // A section's bookmark may point past the page its title and first child are on (an ancestry's points at its statistics page, one after its flavor page): it starts on the earliest page of itself and its children.
        for (var i = 0; i < flat.Count; i++)
        {
            var first = flat[i].Page;
            for (var j = i + 1; j < flat.Count && flat[j].Depth > flat[i].Depth; j++)
            {
                if (flat[j].Page > 0 && flat[j].Page < first)
                {
                    first = flat[j].Page;
                }
            }

            flat[i] = flat[i] with { Page = first };
        }

        var nodes = new List<SpineNode>(flat.Count);
        for (var i = 0; i < flat.Count; i++)
        {
            var node = flat[i];
            var next = flat.Skip(i + 1).FirstOrDefault(f => f.Depth <= node.Depth);
            var end = next.Title == null ? numberOfPages : (next.Page > node.Page ? next.Page - 1 : node.Page);
            nodes.Add(new SpineNode(node.Depth, node.Title, node.Path, node.Page, Math.Max(end, node.Page), node.Title.StartsWith("Sidebar", StringComparison.OrdinalIgnoreCase)));
        }

        return nodes;
    }

    /// <summary>Bookmark count at each depth, from the top level down.</summary>
    public static List<int> CountsByDepth(IEnumerable<SpineNode> nodes)
    {
        var counts = new List<int>();
        foreach (var n in nodes)
        {
            while (counts.Count <= n.Depth)
            {
                counts.Add(0);
            }

            counts[n.Depth]++;
        }

        return counts;
    }

    /// <summary>
    /// The take-list's name for a bookmark title it must not hold (a title that is a reserved name): <c>#</c> and the lowercase hex SHA-256 of the title's slug, which is also the <c>entryHash</c> a redaction row
    /// gives for the entry made from it.
    /// </summary>
    public static string HashSegment(string title) => "#" + SpanText.Sha256(EntriesStage.Slug(title));

    /// <summary>True for a path segment written as a hash (see <see cref="HashSegment"/>).</summary>
    public static bool IsHashSegment(string segment) => segment.Length == 65 && segment[0] == '#';

    /// <summary>Whether a path segment of the take-list names a bookmark title: by the title (case and edge spaces aside), or by its hash.</summary>
    public static bool SegmentIs(string segment, string title) =>
        IsHashSegment(segment.Trim()) ? string.Equals(segment.Trim(), HashSegment(title), StringComparison.Ordinal)
            : string.Equals(title.Trim(), segment.Trim(), StringComparison.OrdinalIgnoreCase);

    public static SpineNode? Find(IEnumerable<SpineNode> nodes, IReadOnlyList<string> path) =>
        nodes.FirstOrDefault(n => n.Path.Length == path.Count && n.Path.Zip(path).All(p => SegmentIs(p.Second, p.First)));

    /// <summary>The bookmark with exactly this path, or null. A path that two bookmarks share fails the run: the taken range would silently be the first one's.</summary>
    public static SpineNode? FindUnique(IReadOnlyList<SpineNode> nodes, IReadOnlyList<string> path)
    {
        var found = nodes.Where(n => n.Path.Length == path.Count && n.Path.Zip(path).All(p => SegmentIs(p.Second, p.First))).Take(2).ToList();
        return found.Count > 1 ? throw new DegreesException($"{found.Count} bookmarks have one path that the take-list names, so the taken range is ambiguous.") : found.FirstOrDefault();
    }

    private static void Walk(BookmarkNode node, int depth, string[] parent, List<(int, string, string[], int)> output)
    {
        var path = parent.Append(node.Title).ToArray();
        output.Add((depth, node.Title, path, node is DocumentBookmarkNode d ? d.PageNumber : 0));
        foreach (var child in node.Children)
        {
            Walk(child, depth + 1, path, output);
        }
    }
}
