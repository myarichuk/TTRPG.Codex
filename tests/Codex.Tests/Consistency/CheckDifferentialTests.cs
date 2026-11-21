using Codex.Core;
using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>
/// The performance pass changed how some checks do their work, never what they find: each optimised check is run beside its version from before
/// (CheckOracles.cs) on seeded generated worlds and adversarial ones, and must yield the same issues, byte for byte, in the same order.
/// </summary>
public class CheckDifferentialTests
{
    private static void Same(IConsistencyCheck oracle, IConsistencyCheck current, WorldSnapshot snapshot, string label)
    {
        var expected = oracle.Run(snapshot).Select(Fx.Flat).ToList();
        var actual = current.Run(snapshot).Select(Fx.Flat).ToList();
        Assert.True(expected.SequenceEqual(actual), $"{label}: expected\n{string.Join("\n", expected)}\nactual\n{string.Join("\n", actual)}");
    }

    // ---- the words of a text ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TheWordsOfAText_AreWhatTheyWereBefore_WithAndWithoutTheCacheOfForms(int seed)
    {
        var r = new Random(seed);
        var pieces = Mixed.Concat(new[] { "'", "’", "‘", "ʼ", "men's", "'s", "o'", "\uD83D\uDE00", "\uD835\uDC00", "x\u0301", "e\u0301", "\u00C9", "ǅ", "Ⅻ", "²", "٣", "_", ":::", "[[", "]]", "\r\n", "\u200B", "ß", "ﬀ", "\uD83D", "\uDE00x" }).ToArray();
        var forms = new WordForms();
        for (var text = 0; text < 2000; text++)
        {
            var sb = new System.Text.StringBuilder();
            for (var k = r.Next(0, 60); k > 0; k--) sb.Append(pieces[r.Next(pieces.Length)]).Append(r.Next(3) == 0 ? "" : Separators[r.Next(Separators.Length)]);
            var written = sb.ToString();
            var expected = OracleTokenizer.Tokenize(written);
            Assert.Equal(expected, TextNorm.Tokenize(written));
            Assert.Equal(expected, TextNorm.Tokenize(written, forms));
        }
    }

    // ---- the unlinked-mention scan ----

    private static readonly string[] Mixed =
    {
        "the", "The", "a", "A", "an", "Mere", "mere", "of", "Of", "Dead", "dead", "Men", "men's", "Men’s", "İstanbul", "istanbul", "ﬁre", "fire", "Fire",
        "שלום", "Мир", "мир", "ＦＵＬＬ", "full", "x", "X", "صلى", "الله", "عليه", "وسلم", "ﷺ", "ﷻ", "جل", "جلاله", "Bo", "Ka", "bo"
    };

    private static readonly string[] Separators = { " ", " ", " ", " ", "  ", "\n", "\n\n", ". ", ", ", "-", "'", " [[Mere of Dead]] ", " [[x x]] ", "\n:::secret\n", "\n:::\n", "\t" };

    private static WorldSnapshot MentionWorld(Random r, string[] vocabulary, int maxPages, int maxTitleWords, int maxBodyWords)
    {
        var v = vocabulary.OrderBy(_ => r.Next()).Take(r.Next(3, vocabulary.Length + 1)).ToArray();
        string Words(int n) => string.Join(" ", Enumerable.Range(0, n).Select(_ => v[r.Next(v.Length)]));
        var pages = new List<PageView>();
        for (var i = 0; i < r.Next(1, maxPages); i++)
        {
            var title = Words(r.Next(1, maxTitleWords));
            var body = new System.Text.StringBuilder();
            for (var k = r.Next(0, maxBodyWords); k > 0; k--) body.Append(v[r.Next(v.Length)]).Append(r.Next(3) == 0 ? Separators[r.Next(Separators.Length)] : " ");
            if (r.Next(3) == 0 && pages.Count > 0) body.Append(pages[r.Next(pages.Count)].Title);
            pages.Add(new PageView($"pages/{i}", WikiText.Slugify(title), title, body.ToString(), r.Next(2) == 0));
        }

        return Fx.Snap(pages: pages);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void TheMentionScan_FindsWhatTheTrieFound_OnGeneratedWorlds(int seed)
    {
        var r = new Random(seed);
        var found = 0;
        for (var world = 0; world < 400; world++)
        {
            var snapshot = MentionWorld(r, Mixed, 10, 7, 80);
            found += new OracleUnlinkedMentionCheck().Run(snapshot).Count();
            Same(new OracleUnlinkedMentionCheck(), new UnlinkedMentionCheck(), snapshot, $"seed {seed} world {world}");
        }

        Assert.True(found > 0);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void TheMentionScan_FindsWhatTheTrieFound_WithArticlesAndLettersOfSeveralWords(int seed)
    {
        var r = new Random(seed);
        var articles = new[] { "the", "The", "A", "a", "B", "b", "an", "C" };
        var wide = new[] { "ﷺ", "ﷻ", "صلى", "الله", "عليه", "وسلم", "جل", "جلاله", "the", "The", "A", "x" };
        for (var world = 0; world < 400; world++)
        {
            Same(new OracleUnlinkedMentionCheck(), new UnlinkedMentionCheck(), MentionWorld(r, articles, 8, 9, 120), $"articles seed {seed} world {world}");
            Same(new OracleUnlinkedMentionCheck(), new UnlinkedMentionCheck(), MentionWorld(r, wide, 8, 9, 120), $"wide seed {seed} world {world}");
        }
    }

    [Fact]
    public void TheMentionScan_FindsWhatTheTrieFound_WhereNamesNestAndOverlap()
    {
        // Names that are prefixes, suffixes and middles of one another over two words, so every position starts several names and almost-names:
        // alternating titles of every length up to 40, with and without an end word, and texts made of the same two words with the end word here and there.
        var pages = new List<PageView>();
        for (var n = 2; n <= 40; n++)
        {
            var words = Enumerable.Range(0, n).Select(i => i % 2 == 0 ? "Bo" : "Ka").ToList();
            if (n % 3 == 0) words.Add("End");
            if (n % 5 == 0) words.Insert(0, "Ka");
            var title = string.Join(' ', words);
            pages.Add(Fx.Page($"wikipages/t{n:D2}", title, "x", party: n % 4 == 0));
        }

        var r = new Random(3);
        for (var p = 0; p < 30; p++)
        {
            var body = string.Join(r.Next(5) == 0 ? "  " : " ", Enumerable.Range(0, 400).Select(i => r.Next(30) == 0 ? "End" : r.Next(40) == 0 ? "bo" : i % 2 == 0 ? "Bo" : "Ka"));
            pages.Add(Fx.Page($"wikipages/b{p:D2}", "Body " + p, body, party: p % 2 == 0));
        }

        var snapshot = Fx.Snap(pages: pages);
        Assert.NotEmpty(new OracleUnlinkedMentionCheck().Run(snapshot));
        Same(new OracleUnlinkedMentionCheck(), new UnlinkedMentionCheck(), snapshot, "nested");
    }

    [Fact]
    public void TheMentionScan_FindsWhatTheTrieFound_WhenALongNameAlmostMatchesEverywhere()
    {
        // The reviewer's case, smaller: a long alternating title that the texts follow to its last word and then miss.
        var title = string.Join(' ', Enumerable.Range(0, 60).Select(i => i % 2 == 0 ? "Bo" : "Ka")) + " End";
        var body = string.Join(' ', Enumerable.Range(0, 300).Select(i => i % 2 == 0 ? "Bo" : "Ka"));
        var pages = new List<PageView> { Fx.Page("wikipages/0", title, "x"), Fx.Page("wikipages/1", "Bo Ka", "x") };
        for (var p = 2; p < 12; p++) pages.Add(Fx.Page($"wikipages/{p}", "Page " + p, p % 3 == 0 ? body + " " + title : body));
        var snapshot = Fx.Snap(pages: pages);
        Assert.NotEmpty(new OracleUnlinkedMentionCheck().Run(snapshot));
        Same(new OracleUnlinkedMentionCheck(), new UnlinkedMentionCheck(), snapshot, "almost");
    }

    // ---- the secret blocks ----

    private static readonly string[] SecretLines =
    {
        ":::dm", ":::DM", ":::party", ":::players", ":::partyy", ":::secret", ":::for [[actor:actors/1-A]]", ":::for [[actor:actors/9-Z]]", ":::for",
        ":::for [[actor:actors/1-A]] [[actor:gone]]", ":::dm 2024-05-01", "  :::dm", ":::", ":::", ":::", "::: ", "The keeper lies.", "The keeper lies.", "",
        "   ", "# Heading", "Plain text [[Some Page]].", "A very long first line of a secret block that is cut when it becomes a key of an issue id", "x"
    };

    private static string SecretText(Random r)
    {
        var lines = Enumerable.Range(0, r.Next(0, 25)).Select(_ => SecretLines[r.Next(SecretLines.Length)]);
        return string.Join(r.Next(4) == 0 ? "\r\n" : "\n", lines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void TheSecretChecks_FindWhatTheyFoundBefore_OnGeneratedWorlds(int seed)
    {
        var r = new Random(seed);
        var found = 0;
        for (var world = 0; world < 150; world++)
        {
            var pages = Enumerable.Range(0, r.Next(0, 6)).Select(i => Fx.Page($"wikipages/{i}-A", "Page " + i, SecretText(r))).ToList();
            var actors = Enumerable.Range(0, r.Next(0, 4)).Select(i => Fx.Actor($"actors/{i}-A", bio: SecretText(r), notes: r.Next(2) == 0 ? SecretText(r) : null)).ToList();
            var places = Enumerable.Range(0, r.Next(0, 3)).Select(i => new PlaceView("p" + i, "regions/1", "Place", SecretText(r), null)).ToList();
            var facts = Enumerable.Range(0, r.Next(0, 3)).Select(i => new FactView($"facts/{i}", SecretText(r), r.Next(2) == 0 ? SecretText(r) : null)).ToList();
            var snapshot = Fx.Snap(pages: pages, actors: actors, places: places, facts: facts);
            // Each check on a fresh snapshot and all three on one (where they share what is read), in both orders.
            foreach (var (oracle, current) in new (IConsistencyCheck, IConsistencyCheck)[]
                     {
                         (new OracleSecretUnclosedCheck(), new SecretUnclosedCheck()), (new OracleSecretUnknownAudienceCheck(), new SecretUnknownAudienceCheck()),
                         (new OracleSecretForMissingCheck(), new SecretForMissingCheck())
                     })
            {
                found += oracle.Run(snapshot).Count();
                Same(oracle, current, snapshot, $"seed {seed} world {world} {current.Name}");
                Same(oracle, current, Fx.Snap(pages: pages, actors: actors, places: places, facts: facts), $"seed {seed} world {world} {current.Name} fresh");
            }
        }

        Assert.True(found > 0);
    }

    // ---- the place tree ----

    private static List<PlaceView> RandomPlaces(Random r, int count, int regions)
    {
        var places = new List<PlaceView>();
        for (var i = 0; i < count; i++)
        {
            var region = "regions/" + r.Next(regions);
            var parent = r.Next(10) switch
            {
                0 => null,
                1 => "gone" + r.Next(3),
                2 => "p" + i, // itself
                _ => "p" + r.Next(count)
            };
            places.Add(new PlaceView("p" + i, region, "Place " + r.Next(count), null, null, parent));
        }

        return places;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ThePlaceTree_FindsWhatItFoundBefore_OnGeneratedWorlds(int seed)
    {
        var r = new Random(seed);
        for (var world = 0; world < 200; world++)
        {
            var places = RandomPlaces(r, r.Next(1, 40), r.Next(1, 4));
            Same(new OraclePlaceTreeCheck(), new PlaceTreeCheck(), Fx.Snap(places: places), $"seed {seed} world {world}");
        }
    }

    [Fact]
    public void ThePlaceTree_FindsWhatItFoundBefore_OnDeepChainsAndManyLoops()
    {
        var places = new List<PlaceView>();
        // A chain of 600 ending in a loop of 50, in an order where ids do not follow the chain; chains that run into it; loops of one length each;
        // a chain into a missing parent; a chain into another region.
        for (var i = 0; i < 600; i++) places.Add(new PlaceView($"c{(i * 7919) % 600:D4}", "regions/a", "C" + i, null, null, i == 599 ? $"c{(550 * 7919) % 600:D4}" : $"c{((i + 1) * 7919) % 600:D4}"));
        for (var i = 0; i < 100; i++) places.Add(new PlaceView($"t{i:D3}", "regions/a", "T" + i, null, null, i % 10 == 0 ? $"c{(i * 3) % 600:D4}" : $"t{i - 1:D3}"));
        for (var n = 2; n < 30; n++)
            for (var k = 0; k < n; k++) places.Add(new PlaceView($"l{n:D2}-{k:D2}", "regions/b", "L" + n + "." + k, null, null, $"l{n:D2}-{(k + 1) % n:D2}"));
        for (var i = 0; i < 300; i++) places.Add(new PlaceView($"m{i:D3}", "regions/b", "M" + i, null, null, i == 0 ? "nowhere" : $"m{i - 1:D3}"));
        for (var i = 0; i < 50; i++) places.Add(new PlaceView($"x{i:D3}", "regions/c", "X" + i, null, null, i == 0 ? "c0000" : $"x{i - 1:D3}"));
        Same(new OraclePlaceTreeCheck(), new PlaceTreeCheck(), Fx.Snap(places: places), "adversarial");
    }
}
