namespace Codex.Persistence;

/// <summary>
/// The places of one region as a tree, by <see cref="LocationDocument.ParentId"/>. A parent the reader can't see (hidden from
/// players) or that is gone puts its children at the top, so nothing ever disappears from the list.
/// </summary>
public static class PlaceTree
{
    /// <summary>Every place once, parents before their children, each level by name, with how deep it sits (0 at the top).</summary>
    public static IReadOnlyList<(LocationDocument Place, int Depth)> Ordered(IReadOnlyCollection<LocationDocument> places)
    {
        var ids = places.Select(p => p.Id).ToHashSet();
        var children = places
            .GroupBy(p => !string.IsNullOrEmpty(p.ParentId) && p.ParentId != p.Id && ids.Contains(p.ParentId) ? p.ParentId : string.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
        var ordered = new List<(LocationDocument, int)>();
        var seen = new HashSet<string>();

        void Walk(string parent, int depth)
        {
            foreach (var place in children.GetValueOrDefault(parent) ?? new List<LocationDocument>())
            {
                if (!seen.Add(place.Id)) continue;
                ordered.Add((place, depth));
                Walk(place.Id, depth + 1);
            }
        }

        Walk(string.Empty, 0);
        // A loop of parents (only possible in a hand-edited document) has no top: list what is left flat rather than lose it.
        ordered.AddRange(places.Where(p => !seen.Contains(p.Id)).Select(p => (p, 0)));
        return ordered;
    }

    /// <summary>The places <paramref name="place"/> is inside, outermost first.</summary>
    public static IReadOnlyList<LocationDocument> Ancestors(IReadOnlyCollection<LocationDocument> places, LocationDocument place)
    {
        var byId = places.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var path = new List<LocationDocument>();
        var seen = new HashSet<string> { place.Id };
        for (var parentId = place.ParentId; !string.IsNullOrEmpty(parentId) && seen.Add(parentId) && byId.TryGetValue(parentId, out var parent); parentId = parent.ParentId)
        {
            path.Insert(0, parent);
        }

        return path;
    }

    /// <summary>Whether <paramref name="placeId"/> may go inside <paramref name="parentId"/>: not itself, and not one of the places
    /// inside it (that would make a loop). An empty parent (the region itself) is always fine.</summary>
    public static bool CanParent(IReadOnlyCollection<LocationDocument> places, string placeId, string? parentId)
    {
        if (string.IsNullOrEmpty(parentId)) return true;
        if (parentId == placeId) return false;
        var parent = places.FirstOrDefault(p => p.Id == parentId);
        return parent != null && Ancestors(places, parent).All(a => a.Id != placeId);
    }

    /// <summary>The kind a place inside <paramref name="parent"/> most likely is: one step smaller, or an area at the top.</summary>
    public static PlaceKind ChildKind(LocationDocument? parent) => parent?.Kind switch
    {
        null => PlaceKind.Area,
        PlaceKind.Area => PlaceKind.Settlement,
        PlaceKind.Settlement => PlaceKind.District,
        _ => PlaceKind.Site
    };
}
