namespace Codex.Core.Consistency;

/// <summary>Which page a stored link reaches, as the wiki resolves it: the page with that slug first, else the one whose title makes that slug
/// (<c>WikiLinks.Target</c>). Built once per run, over pages already sorted by id, so a tie goes to the same page every time.</summary>
internal sealed class PageLookup
{
    private readonly Dictionary<string, PageView> bySlug = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageView> byTitleSlug = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PageView>> byCanonical = new(StringComparer.Ordinal);

    public PageLookup(IEnumerable<PageView> pages)
    {
        foreach (var p in pages)
        {
            bySlug.TryAdd(p.Slug, p);
            byTitleSlug.TryAdd(WikiText.Slugify(p.Title), p);
            var canonical = TextNorm.Canonical(p.Title);
            if (canonical.Length == 0) continue;
            if (!byCanonical.TryGetValue(canonical, out var list)) byCanonical[canonical] = list = new List<PageView>();
            list.Add(p);
        }
    }

    public PageView? Resolve(string slug) => bySlug.GetValueOrDefault(slug) ?? byTitleSlug.GetValueOrDefault(slug);

    /// <summary>The one page a written name stands for once articles and doubled words are ignored; null when none, or when two pages
    /// would both answer (a guess between them would be wrong half the time).</summary>
    public PageView? Unique(string canonical) => byCanonical.TryGetValue(canonical, out var list) && list.Count == 1 ? list[0] : null;

    public IEnumerable<KeyValuePair<string, List<PageView>>> Canonicals => byCanonical;
}

/// <summary>
/// <c>[[Title]]</c> links, in pages and in lore, that reach no page. When the link would reach a page if its articles and doubled words
/// were ignored ("[[The the Mere of Dead Men]]" against "The Mere of Dead Men"), the hint names that page.
/// </summary>
public sealed class BrokenLinkCheck : IConsistencyCheck
{
    public string Name => CheckNames.BrokenLink;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var lookup = new PageLookup(s.Pages);
        foreach (var p in s.Pages)
        {
            foreach (var issue in Of("page", p.Id, "body", p.Body, lookup)) yield return issue;
        }

        foreach (var f in s.Facts)
        {
            foreach (var issue in Of("fact", f.Id, "text", f.Summary + "\n" + f.Details, lookup)) yield return issue;
        }
    }

    private IEnumerable<Issue> Of(string kind, string entityId, string field, string text, PageLookup lookup)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var title in WikiText.ExtractLinks(text))
        {
            var slug = WikiText.Slugify(title);
            if (!seen.Add(slug) || lookup.Resolve(slug) != null) continue;

            var suggestion = lookup.Unique(TextNorm.Canonical(title));
            var fix = kind == "page" ? FixVia.Page : FixVia.Fact;
            yield return suggestion != null
                ? Make.Issue(Name, IssueSeverity.Warning, kind, entityId, field, slug, "Consistency.BrokenLink",
                    new[] { title, suggestion.Title },
                    $"The link [[{Make.Quote(title)}]] points to no page.",
                    $"The page \"{Make.Quote(suggestion.Title)}\" exists. Change the link to [[{Make.Quote(suggestion.Title)}]].", fix)
                : Make.Issue(Name, IssueSeverity.Warning, kind, entityId, field, slug, "Consistency.BrokenLink",
                    new[] { title },
                    $"The link [[{Make.Quote(title)}]] points to no page.",
                    $"Create a page titled \"{Make.Quote(title)}\", or change the link to a page that exists, or remove it. A red link the DM meant to leave can be dismissed.", fix);
        }
    }
}

/// <summary>One word of a page name in <see cref="UnlinkedMentionCheck"/>'s trie; <see cref="Page"/> is set where a whole name ends.</summary>
internal sealed class NameNode
{
    public Dictionary<string, NameNode>? Next;
    public PageView? Page;
}

/// <summary>
/// A page's text that names another page in plain words, where a <c>[[link]]</c> was meant. Only a name of two words or more counts (one word
/// is too often an ordinary one), written with a capital or three words long; a page already linking the target anywhere is left alone (links go
/// on the first mention, not every one); and nothing is suggested that would link a party page to a DM-only one.
/// </summary>
public sealed class UnlinkedMentionCheck : IConsistencyCheck
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
        var root = new NameNode();
        var candidates = 0;
        foreach (var (canonical, list) in lookup.Canonicals)
        {
            var words = canonical.Split(' ');
            if (words.Length < 2 || list.Count != 1) continue;
            var node = root;
            foreach (var word in words)
            {
                node.Next ??= new Dictionary<string, NameNode>(StringComparer.Ordinal);
                if (!node.Next.TryGetValue(word, out var next)) node.Next[word] = next = new NameNode();
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
                    NameNode? at = root;
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
    private static NameNode? Step(NameNode node, string norm)
    {
        if (norm.IndexOf(' ') < 0) return node.Next != null && node.Next.TryGetValue(norm, out var next) ? next : null;
        NameNode? at = node;
        foreach (var part in norm.Split(' '))
        {
            if (at.Next == null || !at.Next.TryGetValue(part, out at)) return null;
        }

        return at;
    }
}

/// <summary>
/// A page the party can read that links a page only the DM can open. Players see a link they cannot open as plain text, and a link with no
/// label as nothing at all (the page's title is never shown), so the sentence loses its words. Links inside DM-only secret blocks are not
/// counted: players never read those.
/// </summary>
public sealed class VisibilityLeakCheck : IConsistencyCheck
{
    public string Name => CheckNames.VisibilityLeak;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var lookup = new PageLookup(s.Pages);
        foreach (var page in s.Pages.Where(p => p.PartyVisible))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var title in WikiText.ExtractLinks(SecretText.ForPlayers(page.Body)))
            {
                var target = lookup.Resolve(WikiText.Slugify(title));
                if (target == null || target.PartyVisible || target.Id == page.Id || !seen.Add(target.Id)) continue;
                yield return Make.Issue(Name, IssueSeverity.Warning, "page", page.Id, "body", target.Id, "Consistency.VisibilityLeak",
                    new[] { title, target.Title },
                    $"This party-visible page links [[{Make.Quote(title)}]], the page \"{Make.Quote(target.Title)}\", which only the DM can open. Players see the words without the link, or nothing if the link has no label.",
                    $"Make \"{Make.Quote(target.Title)}\" visible to the party, or take the link out (or give it a label, [[{Make.Quote(title)}|words players read]]). Never reveal a page without the DM's say.",
                    FixVia.Page);
            }
        }
    }
}
