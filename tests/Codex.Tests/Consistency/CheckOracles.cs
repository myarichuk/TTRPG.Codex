using Codex.Core;
using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

// The checks as they were before the performance pass (fa02e24), kept word for word as oracles: the differential tests
// (CheckDifferentialTests) run them beside the current checks on generated and adversarial worlds and require byte-identical issues.
// Never used by the app.

/// <summary><see cref="PlaceTreeCheck"/> at fa02e24: every place's chain walked to its end, one walk per place.</summary>
internal sealed class OraclePlaceTreeCheck : IConsistencyCheck
{
    public string Name => CheckNames.PlaceTree;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var inRegion = s.Places.GroupBy(p => p.RegionId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Id, StringComparer.Ordinal), StringComparer.Ordinal);
        var everywhere = s.Places.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var region in inRegion.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var tidy = new HashSet<string>(StringComparer.Ordinal);
            foreach (var place in region.Value.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(place.ParentId)) continue;
                if (place.ParentId == place.Id)
                {
                    yield return Issue(place, "self", "Self", new[] { place.Name }, $"The place \"{Make.Quote(place.Name)}\" is inside itself.");
                }
                else if (region.Value.ContainsKey(place.ParentId))
                {
                    tidy.Add(place.Id);
                }
                else if (everywhere.TryGetValue(place.ParentId, out var other))
                {
                    yield return Issue(place, "cross-region:" + place.ParentId, "CrossRegion", new[] { place.Name, other.Name },
                        $"The place \"{Make.Quote(place.Name)}\" is inside \"{Make.Quote(other.Name)}\", which is in another region; a place can only be inside one of its own region.");
                }
                else
                {
                    yield return Issue(place, "missing:" + place.ParentId, "Missing", new[] { place.Name, place.ParentId },
                        $"The place \"{Make.Quote(place.Name)}\" is inside a place ({place.ParentId}) that no longer exists.");
                }
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in region.Value.Values.Where(p => tidy.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                var path = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var at = start;
                while (at != null && tidy.Contains(at.Id) && seen.Add(at.Id))
                {
                    path.Add(at.Id);
                    at = region.Value.GetValueOrDefault(at.ParentId!);
                }

                if (at == null || !seen.Contains(at.Id) || !tidy.Contains(at.Id)) continue;
                var loop = path.Skip(path.IndexOf(at.Id)).ToList();
                var lowest = loop.Min(StringComparer.Ordinal)!;
                if (!reported.Add(lowest)) continue;
                var names = loop.OrderBy(id => id, StringComparer.Ordinal).Select(id => Make.Quote(region.Value[id].Name)).ToList();
                yield return Issue(region.Value[lowest], "cycle", "Cycle", new[] { string.Join(", ", names.Select(n => "\"" + n + "\"")) },
                    $"These places are inside one another in a loop, with no top: {string.Join(", ", names.Select(n => "\"" + n + "\""))}.");
            }
        }
    }

    private Issue Issue(PlaceView place, string detail, string code, IReadOnlyList<string> args, string message) =>
        Make.Issue(Name, IssueSeverity.Error, "place", place.Id, "parentId", detail, "Consistency.PlaceTree" + code, args, message,
            "Move the place to where it belongs with an edit of the place and its \"parent\" (a place of the same region, or \"none\" for the top of the region). Ask the DM where it goes; do not guess.",
            FixVia.Place);
}
