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

        var nodes = new List<SpineNode>(flat.Count);
        for (var i = 0; i < flat.Count; i++)
        {
            var node = flat[i];
            var next = flat.Skip(i + 1).FirstOrDefault(f => f.Depth <= node.Depth);
            var end = next.Title == null ? doc.NumberOfPages : (next.Page > node.Page ? next.Page - 1 : node.Page);
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

    public static SpineNode? Find(IEnumerable<SpineNode> nodes, IReadOnlyList<string> path) =>
        nodes.FirstOrDefault(n => n.Path.Length == path.Count && n.Path.Zip(path).All(p => string.Equals(p.First.Trim(), p.Second.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>The bookmark with exactly this path, or null. A path that two bookmarks share fails the run: the taken range would silently be the first one's.</summary>
    public static SpineNode? FindUnique(IReadOnlyList<SpineNode> nodes, IReadOnlyList<string> path)
    {
        var found = nodes.Where(n => n.Path.Length == path.Count && n.Path.Zip(path).All(p => string.Equals(p.First.Trim(), p.Second.Trim(), StringComparison.OrdinalIgnoreCase))).Take(2).ToList();
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
