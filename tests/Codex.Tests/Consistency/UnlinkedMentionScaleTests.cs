using System.Diagnostics;
using Codex.Core;
using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>The unlinked-mention scan must cost what the text and the names it actually meets cost, never text times the longest title, and it
/// must find exactly what the first, simple version of it found (kept below as the oracle).</summary>
public class UnlinkedMentionScaleTests
{
    private static readonly string[] Vocabulary =
    {
        "black", "mere", "dead", "men", "the", "a", "of", "lord", "keeper", "salt", "road", "north", "old", "tower", "crown", "Ash", "Vale", "grey", "wolf", "Hall"
    };

    [Fact]
    public void ALongTitle_AmongManyPagesOfLongText_IsNotQuadratic()
    {
        // The reviewer's measured case: 500 pages of 1000 words and one 40-word title took 7.4 s with the joined-string scan.
        var rng = new Random(7);
        var title = string.Join(' ', Enumerable.Range(0, 40).Select(i => "Word" + i));
        var pages = new List<PageView> { Fx.Page("wikipages/0-A", title, "short") };
        for (var p = 1; p <= 500; p++)
        {
            pages.Add(Fx.Page($"wikipages/{p}-A", "Page " + p, string.Join(' ', Enumerable.Range(0, 1000).Select(_ => Vocabulary[rng.Next(Vocabulary.Length)]))));
        }

        var clock = Stopwatch.StartNew();
        var issues = new UnlinkedMentionCheck().Run(Fx.Snap(pages: pages)).ToList();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
        Assert.NotNull(issues);
    }

    [Fact]
    public void TheTextMayRepeatALongTitlesBeginning_AndOnlyTheWholeNameCounts()
    {
        var title = string.Join(' ', Enumerable.Range(0, 40).Select(i => "Word" + i));
        var almost = string.Join(' ', Enumerable.Range(0, 39).Select(i => "Word" + i)) + " other";
        var text = string.Join(". ", Enumerable.Repeat(almost, 50)) + ". Here: " + title + ".";
        var issue = Assert.Single(new UnlinkedMentionCheck().Run(Fx.Snap(pages: new[]
        {
            Fx.Page("wikipages/0-A", title, "short"), Fx.Page("wikipages/1-A", "Notes", text)
        })));
        Assert.Equal("unlinked-mention:page:wikipages/1-A:body:wikipages/0-A", issue.Id);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void OnGeneratedWorlds_TheScanFindsWhatTheFirstVersionFound(int seed)
    {
        var rng = new Random(seed);
        string Words(int n) => string.Join(rng.Next(6) == 0 ? ", " : " ", Enumerable.Range(0, n).Select(_ => Vocabulary[rng.Next(Vocabulary.Length)]));
        var pages = new List<PageView>();
        for (var p = 0; p < 40; p++)
        {
            var title = Words(rng.Next(1, 5)).Replace(",", "");
            var body = string.Join("\n", Enumerable.Range(0, 6).Select(_ => Words(rng.Next(5, 60)) + (rng.Next(4) == 0 ? " [[" + Vocabulary[rng.Next(Vocabulary.Length)] + " " + Vocabulary[rng.Next(Vocabulary.Length)] + "]]" : "")));
            pages.Add(Fx.Page($"wikipages/{p}-A", title, body, party: rng.Next(3) == 0));
        }

        var snapshot = Fx.Snap(pages: pages);
        var actual = new UnlinkedMentionCheck().Run(snapshot).Select(i => i.Id + "|" + string.Join("|", i.Args)).ToList();
        var expected = Oracle(snapshot).ToList();
        Assert.Equal(expected, actual);
        Assert.NotEmpty(expected);
    }

    // ---- the first version of the scan, as it was: for every word, the longest run of words that is a name, by joining strings ----
    private static IEnumerable<string> Oracle(WorldSnapshot s)
    {
        var lookup = new PageLookup(s.Pages);
        var candidates = new Dictionary<string, PageView>(StringComparer.Ordinal);
        var maxWords = 0;
        foreach (var (canonical, list) in lookup.Canonicals)
        {
            var words = canonical.Split(' ').Length;
            if (words < 2 || list.Count != 1) continue;
            candidates[canonical] = list[0];
            maxWords = Math.Max(maxWords, words);
        }

        foreach (var page in s.Pages)
        {
            if (string.IsNullOrWhiteSpace(page.Body)) continue;
            var linked = WikiText.ExtractLinks(page.Body).Select(WikiText.Slugify).ToHashSet(StringComparer.Ordinal);
            var text = TextFields.WithoutLinksAndMarkers(page.Body);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<(string Key, string Id, string Written, string Title)>();
            foreach (var run in TextNorm.Runs(text))
            {
                for (var i = 0; i < run.Count; i++)
                {
                    PageView? target = null;
                    var length = 0;
                    for (var n = Math.Min(maxWords, run.Count - i); n >= 2 && target == null; n--)
                    {
                        if (!candidates.TryGetValue(string.Join(' ', run.Skip(i).Take(n).Select(t => t.Norm)), out var hit)) continue;
                        target = hit;
                        length = n;
                    }

                    if (target == null) continue;
                    var first = run[i];
                    var last = run[i + length - 1];
                    var before = i;
                    while (before > 0 && TextNorm.IsArticle(run[before - 1].Norm)) before--;
                    var start = run[before].Start;
                    i += length - 1;
                    if (target.Id == page.Id) continue;
                    if (!first.Capitalized && length < 3) continue;
                    if (linked.Contains(target.Slug) || linked.Contains(WikiText.Slugify(target.Title))) continue;
                    if (page.PartyVisible && !target.PartyVisible) continue;
                    if (!reported.Add(target.Id)) continue;
                    var written = Make.Quote(page.Body.Substring(start, Math.Min(last.End, page.Body.Length) - start));
                    found.Add((Make.Id(CheckNames.UnlinkedMention, "page", page.Id, "body", target.Id), page.Id, written, target.Title));
                }
            }

            foreach (var f in found) yield return f.Key + "|" + f.Written + "|" + f.Title;
        }
    }
}
