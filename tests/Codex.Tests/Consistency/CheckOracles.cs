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

/// <summary>One word of a page name in the trie of <see cref="OracleUnlinkedMentionCheck"/>; <see cref="Page"/> is set where a whole name ends.</summary>
internal sealed class OracleNameNode
{
    public Dictionary<string, OracleNameNode>? Next;
    public PageView? Page;
}

/// <summary><see cref="UnlinkedMentionCheck"/> at fa02e24: a trie of the names' words walked from every word of a text.
/// A page's text that names another page in plain words, where a <c>[[link]]</c> was meant. Only a name of two words or more counts (one word
/// is too often an ordinary one), written with a capital or three words long; a page already linking the target anywhere is left alone (links go
/// on the first mention, not every one); and nothing is suggested that would link a party page to a DM-only one.
/// </summary>
internal sealed class OracleUnlinkedMentionCheck : IConsistencyCheck
{
    public string Name => CheckNames.UnlinkedMention;

    public IEnumerable<Issue> Run(WorldSnapshot s) => Run(s, CancellationToken.None);

    /// <summary>The scan, looking at <paramref name="cancel"/> at every page and every few hundred positions of a text, so a time budget is kept even
    /// for one huge page; cancelled, it throws and reports nothing more (the checker then returns nothing at all).</summary>
    public IEnumerable<Issue> Run(WorldSnapshot s, CancellationToken cancel)
    {
        var lookup = new PageLookup(s.Pages);
        // The names a page may be mentioned by, as a trie of their words: a position in a text walks it word by word and stops at the first word
        // that no name continues with, so the work at a position is the length of the name it could be, never the length of the longest title.
        var root = new OracleNameNode();
        var candidates = 0;
        foreach (var (canonical, list) in lookup.Canonicals)
        {
            var words = canonical.Split(' ');
            if (words.Length < 2 || list.Count != 1) continue;
            var node = root;
            foreach (var word in words)
            {
                node.Next ??= new Dictionary<string, OracleNameNode>(StringComparer.Ordinal);
                if (!node.Next.TryGetValue(word, out var next)) node.Next[word] = next = new OracleNameNode();
                node = next;
            }

            node.Page = list[0];
            candidates++;
        }

        if (candidates == 0) yield break;

        foreach (var page in s.Pages)
        {
            cancel.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(page.Body)) continue;
            var linked = WikiText.ExtractLinks(page.Body).Select(WikiText.Slugify).ToHashSet(StringComparer.Ordinal);
            var text = TextFields.WithoutLinksAndMarkers(page.Body);
            var reported = new HashSet<string>(StringComparer.Ordinal);

            foreach (var run in TextNorm.Runs(text))
            {
                for (var i = 0; i < run.Count; i++)
                {
                    if ((i & 255) == 255) cancel.ThrowIfCancellationRequested();
                    PageView? target = null;
                    var length = 0;
                    OracleNameNode? at = root;
                    for (var k = i; k < run.Count && (at = Step(at, run[k].Norm)) != null; k++)
                    {
                        // A name is two words of the text or more, ending where a word ends (as the names are compared: the words joined by spaces).
                        if (at.Page == null || k == i) continue;
                        target = at.Page; // the longest name wins: keep walking while a longer one is still possible
                        length = k - i + 1;
                    }

                    if (target == null) continue;
                    var first = run[i];
                    var last = run[i + length - 1];
                    // The words written before the name that are only articles ("the the Mere of Dead Men") belong to what the text says.
                    var before = i;
                    while (before > 0 && TextNorm.IsArticle(run[before - 1].Norm)) before--;
                    var start = run[before].Start;
                    i += length - 1; // the name is used up: a shorter name inside it is not a second mention

                    if (target.Id == page.Id) continue;
                    if (!first.Capitalized && length < 3) continue;
                    if (linked.Contains(target.Slug) || linked.Contains(WikiText.Slugify(target.Title))) continue;
                    if (page.PartyVisible && !target.PartyVisible) continue;
                    if (!reported.Add(target.Id)) continue;

                    var written = Make.Quote(page.Body.Substring(start, Math.Min(last.End, page.Body.Length) - start));
                    yield return Make.Issue(Name, IssueSeverity.Warning, "page", page.Id, "body", target.Id, "Consistency.UnlinkedMention",
                        new[] { written, target.Title },
                        $"The text says \"{written}\" in plain words, which is the name of the page \"{Make.Quote(target.Title)}\".",
                        $"If a link was meant, write [[{Make.Quote(target.Title)}]] (or [[{Make.Quote(target.Title)}|{written}]] to keep the words). If not, dismiss the issue.",
                        FixVia.Page);
                }
            }
        }
    }

    /// <summary>The trie node after one word of the text. A word's compared form can hold spaces (Unicode-compatibility normalising a ligature such as
    /// U+FDFA gives four words), and a name is compared as the words of the text joined by spaces, so such a word walks as many steps as it has parts:
    /// splitting the joined text on spaces is splitting each word and putting the parts one after the other.</summary>
    private static OracleNameNode? Step(OracleNameNode node, string norm)
    {
        if (norm.IndexOf(' ') < 0) return node.Next != null && node.Next.TryGetValue(norm, out var next) ? next : null;
        OracleNameNode? at = node;
        foreach (var part in norm.Split(' '))
        {
            if (at.Next == null || !at.Next.TryGetValue(part, out at)) return null;
        }

        return at;
    }
}

