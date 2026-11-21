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

/// <summary>
/// The page names <see cref="UnlinkedMentionCheck"/> looks for, as an Aho-Corasick automaton over their words written backwards. Reading a run of
/// words from its end, the automaton's state after a word is the longest stretch of the text from that word on that ends some name; the names that
/// start at that word are that state and the states its output links lead to, longest first. So the longest name starting at every word of a text is
/// found in one pass, whatever the names' length: a position costs a few steps, never the length of the name it could be (a 401-word title that
/// almost matches everywhere used to cost 401 steps at every word). Built once per run, then only read, so it holds no state of a scan.
/// </summary>
internal sealed class NameAutomaton
{
    // A word of a name or of a text is compared part by part: its compared form split on spaces (U+FDFA's form is four words), as the names were
    // compared as the words joined by spaces. Every part of every name has a number; a part of a text that no name has is -1.
    private readonly Dictionary<string, int> parts = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> next = new(); // (state << 32 | part) -> state
    private readonly List<int> fail = new() { 0 };
    private readonly List<int> output = new() { -1 }; // the nearest state on the fail chain (itself excluded) where a name ends, or -1
    private readonly List<int> depth = new() { 0 }; // parts read to reach the state
    private readonly List<PageView?> page = new() { null }; // the page whose name ends at the state

    public int Names { get; private set; }

    public void Add(string[] nameParts, PageView target)
    {
        var state = 0;
        for (var k = nameParts.Length - 1; k >= 0; k--)
        {
            if (!parts.TryGetValue(nameParts[k], out var id)) parts[nameParts[k]] = id = parts.Count;
            var key = (long)state << 32 | (uint)id;
            if (!next.TryGetValue(key, out var to))
            {
                to = page.Count;
                next[key] = to;
                fail.Add(0);
                output.Add(-1);
                depth.Add(depth[state] + 1);
                page.Add(null);
            }

            state = to;
        }

        page[state] = target;
        Names++;
    }

    /// <summary>The fail and output links, breadth first. Called once, after the last <see cref="Add"/>.</summary>
    public void Complete()
    {
        var children = new List<(int Part, int To)>[page.Count];
        foreach (var (key, to) in next)
        {
            var from = (int)(key >> 32);
            (children[from] ??= new List<(int, int)>()).Add(((int)(uint)key, to));
        }

        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            if (children[u] == null) continue;
            foreach (var (part, v) in children[u])
            {
                var f = 0;
                if (u != 0)
                {
                    f = fail[u];
                    while (true)
                    {
                        if (next.TryGetValue((long)f << 32 | (uint)part, out var t)) { f = t; break; }
                        if (f == 0) break;
                        f = fail[f];
                    }
                }

                fail[v] = f;
                output[v] = page[f] != null ? f : output[f];
                queue.Enqueue(v);
            }
        }
    }

    public int Part(string part) => parts.TryGetValue(part, out var id) ? id : -1;

    public int Step(int state, int part)
    {
        if (part < 0) return 0;
        while (true)
        {
            if (next.TryGetValue((long)state << 32 | (uint)part, out var to)) return to;
            if (state == 0) return 0;
            state = fail[state];
        }
    }

    /// <summary>The names that start at the word just read, longest first, as states.</summary>
    public int FirstName(int state) => page[state] != null ? state : output[state];

    public int NextName(int state) => output[state];

    public int Depth(int state) => depth[state];

    public PageView Page(int state) => page[state]!;
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
        var names = new NameAutomaton();
        foreach (var (canonical, list) in lookup.Canonicals)
        {
            var nameParts = canonical.Split(' ');
            if (nameParts.Length < 2 || list.Count != 1) continue;
            names.Add(nameParts, list[0]);
        }

        if (names.Names == 0) yield break;
        names.Complete();

        // One run's words as parts, reused from run to run: the part numbers, the word each part belongs to, and whether a word starts at it.
        var ids = new List<int>();
        var wordOf = new List<int>();
        var wordStart = new List<bool>();
        var longest = new List<int>(); // per word of the run: the state of the longest name starting there, or -1
        var words = new List<int>(); // and how many words of the text it covers

        foreach (var page in s.Pages)
        {
            cancel.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(page.Body)) continue;
            var linked = WikiText.ExtractLinks(page.Body).Select(WikiText.Slugify).ToHashSet(StringComparer.Ordinal);
            var text = TextFields.WithoutLinksAndMarkers(page.Body);
            var reported = new HashSet<string>(StringComparer.Ordinal);

            foreach (var run in TextNorm.Runs(text))
            {
                if (run.Count < 2) continue;
                Longest(names, run, ids, wordOf, wordStart, longest, words, cancel);
                for (var i = 0; i < run.Count; i++)
                {
                    if ((i & 255) == 255) cancel.ThrowIfCancellationRequested();
                    // The longest name starting at this word wins; a name is two words of the text or more, ending where a word ends.
                    if (longest[i] < 0) continue;
                    var target = names.Page(longest[i]);
                    var length = words[i];
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

    /// <summary>For every word of <paramref name="run"/>, the state of the longest name that starts at it, ends where a word of the text ends and
    /// covers two words of the text or more (-1 when none), found by reading the run backwards through the automaton once.</summary>
    private static void Longest(NameAutomaton names, List<Token> run, List<int> ids, List<int> wordOf, List<bool> wordStart, List<int> longest, List<int> words, CancellationToken cancel)
    {
        ids.Clear();
        wordOf.Clear();
        wordStart.Clear();
        longest.Clear();
        words.Clear();
        for (var w = 0; w < run.Count; w++)
        {
            var norm = run[w].Norm;
            longest.Add(-1);
            words.Add(0);
            if (norm.IndexOf(' ') < 0)
            {
                ids.Add(names.Part(norm));
                wordOf.Add(w);
                wordStart.Add(true);
                continue;
            }

            var split = norm.Split(' ');
            for (var k = 0; k < split.Length; k++)
            {
                ids.Add(names.Part(split[k]));
                wordOf.Add(w);
                wordStart.Add(k == 0);
            }
        }

        var state = 0;
        for (var p = ids.Count - 1; p >= 0; p--)
        {
            if ((p & 1023) == 0) cancel.ThrowIfCancellationRequested();
            state = names.Step(state, ids[p]);
            if (!wordStart[p]) continue;
            var w = wordOf[p];
            for (var at = names.FirstName(state); at >= 0; at = names.NextName(at))
            {
                var end = p + names.Depth(at); // the part after the name
                if (end < ids.Count && !wordStart[end]) continue; // the name ends inside a word
                if (wordOf[end - 1] == w) continue; // one word of the text is not a name
                longest[w] = at;
                words[w] = wordOf[end - 1] - w + 1;
                break;
            }
        }
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
