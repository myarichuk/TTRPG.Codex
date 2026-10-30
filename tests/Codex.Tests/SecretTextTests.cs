using Codex.Core;

namespace Codex.Tests;

public class SecretTextTests
{
    [Fact]
    public void ATextWithoutMarkers_IsTheSameForEveryone()
    {
        const string text = "Just a village.\n\nWith *two* paragraphs.";
        Assert.Equal(text, SecretText.ForPlayers(text));
        Assert.False(SecretText.HasSecrets(text));
        Assert.Single(SecretText.Parse(text));
    }

    [Fact]
    public void PlayersReadEverythingButTheDmBlocks()
    {
        const string text = "Overview.\n:::dm\nThe keeper is a cultist.\n:::\nThe docks.\n\n:::party\nThe ship sank.\n:::\n:::players\nA quiet place.\n:::";
        var seen = SecretText.ForPlayers(text);
        Assert.DoesNotContain("cultist", seen);
        Assert.DoesNotContain(":::", seen);
        Assert.Contains("Overview.", seen);
        Assert.Contains("The docks.", seen);
        Assert.Contains("The ship sank.", seen);
        Assert.Contains("A quiet place.", seen);
        // the paragraphs either side of a removed block do not run into one
        Assert.Contains("Overview.\n\nThe docks.", seen);
        Assert.True(SecretText.HasSecrets(text));
    }

    [Fact]
    public void Parse_NamesEachBlockAndItsAudience()
    {
        var parts = SecretText.Parse("a\n:::dm\nb\n:::\nc\n:::party\nd\n:::\n:::players\ne\n:::");
        Assert.Equal(new[] { SecretAudience.Everyone, SecretAudience.Dm, SecretAudience.Everyone, SecretAudience.Party, SecretAudience.Players }, parts.Select(p => p.Audience));
        Assert.Equal(new[] { -1, 0, -1, 1, 2 }, parts.Select(p => p.Block));
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, parts.Select(p => p.Text));
    }

    [Theory]
    [InlineData("Open.\n:::dm\nNever closed, so the rest is the DM's.\nStill secret.")]
    [InlineData("Open.\n:::dmm\nA typo opens a DM block, not a public one.\n:::")]
    [InlineData("Open.\n:::for @Aria\nReserved: a name the campaign does not know reveals it to no one.\n:::")]
    [InlineData("Open.\n:::for [[actor:aria]]\nReserved for Aria, so no one else reads it.\n:::")]
    [InlineData("Open.\n:::dm\nOuter.\n:::party\nA reveal pasted inside a secret stays secret.\n:::\nStill the outer secret.\n:::")]
    [InlineData("Open.\n```\n:::dm\nMarkers count inside a code fence too.\n:::\n```")]
    [InlineData("Open.\n   :::DM   \nIndented and shouting.\n  :::  ")]
    public void EverythingFailsClosed(string text)
    {
        var seen = SecretText.ForPlayers(text);
        Assert.Contains("Open.", seen);
        Assert.DoesNotContain("secret", seen, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("typo", seen);
        Assert.DoesNotContain("Reserved", seen);
        Assert.DoesNotContain("Markers count", seen);
        Assert.DoesNotContain("shouting", seen);
    }

    [Fact]
    public void AStrayCloser_IsDroppedAndHidesNothing()
    {
        Assert.Equal("a\nb", SecretText.ForPlayers("a\n:::\nb"));
    }

    [Fact]
    public void LinesThatOnlyLookLikeMarkers_AreText()
    {
        const string text = "Ratio 3:::1 is fine.\n::::: a rule of colons\nkey: value";
        Assert.Equal(text, SecretText.ForPlayers(text));
    }

    [Fact]
    public void WithoutMarkers_KeepsEveryWordForTheDm()
    {
        Assert.Equal("a\n\nb\n\nc", SecretText.WithoutMarkers("a\n:::dm\nb\n:::\nc"));
    }

    [Fact]
    public void KeepSecrets_AppendsTheStoredDmBlocks_AndIsIdempotent()
    {
        const string stored = "Born at sea.\n:::dm\nHer father leads the cult.\n:::\nLoves boats.";
        var once = SecretText.KeepSecrets(stored, "Born at sea. Loves boats and storms.");
        Assert.Equal("Born at sea. Loves boats and storms.\n\n:::dm\nHer father leads the cult.\n:::", once);
        Assert.Equal(once, SecretText.KeepSecrets(stored, once));
        Assert.Equal(once, SecretText.KeepSecrets(once, SecretText.ForPlayers(once)));
        Assert.Equal("Edited.", SecretText.KeepSecrets("No secrets.", "Edited."));
        // A player cannot add a DM block of their own: it would hide their own text from themselves.
        Assert.Equal("Mine.", SecretText.KeepSecrets(null, "Mine.\n:::dm\nhidden\n:::"));
    }

    [Fact]
    public void Blocks_NumberEachOpeningLine_AndKnowWhatIsInsideADmBlock()
    {
        var blocks = SecretText.Blocks("a\n:::dm\nb\n:::party\nc\n:::\n:::\n:::for [[actor:x]] [[actor:y]]\nd\n:::\n:::dm\nnever closed\nstill");
        Assert.Equal(new[] { 0, 1, 2, 3 }, blocks.Select(b => b.Block));
        Assert.Equal(new[] { SecretAudience.Dm, SecretAudience.Party, SecretAudience.Some, SecretAudience.Dm }, blocks.Select(b => b.Marked));
        Assert.Equal(new[] { false, true, false, false }, blocks.Select(b => b.UnderDm));
        Assert.Equal("b\n:::party\nc\n:::", blocks[0].Inner);
        Assert.Equal("c", blocks[1].Inner);
        Assert.Equal(new[] { "x", "y" }, blocks[2].Knowers);
        Assert.Equal("never closed\nstill", blocks[3].Inner);
        Assert.Equal(new[] { 1, 3, 7, 10 }, blocks.Select(b => b.Line));
    }

    [Fact]
    public void Reveal_ChangesOnlyTheOpeningLine_StampsIt_AndCanBeUndone()
    {
        const string text = "Intro.\n  :::dm\nThe keeper is a cultist.\n:::\nOutro.";
        var at = new DateTime(2026, 10, 29, 14, 5, 0, DateTimeKind.Utc);
        var revealed = SecretText.SetAudience(text, 0, "The keeper is a cultist.", SecretAudience.Party, null, at);
        Assert.Equal("Intro.\n  :::party 2026-10-29T14:05:00Z\nThe keeper is a cultist.\n:::\nOutro.", revealed);
        Assert.Contains("cultist", SecretText.ForPlayers(revealed));
        var segment = SecretText.Parse(revealed).Single(s => s.Audience == SecretAudience.Party);
        Assert.Equal(at, segment.RevealedAt);

        var hidden = SecretText.SetAudience(revealed, 0, "The keeper is a cultist.", SecretAudience.Dm, null, at);
        Assert.Equal("Intro.\n  :::dm\nThe keeper is a cultist.\n:::\nOutro.", hidden);
        Assert.DoesNotContain("cultist", SecretText.ForPlayers(hidden));
    }

    [Fact]
    public void Reveal_RefusesWhatTheDmDidNotSee_AndWhatWouldShowNothing()
    {
        const string text = ":::dm\nouter\n:::party\ninner\n:::\n:::";
        var at = DateTime.UtcNow;
        Assert.Null(SecretText.SetAudience(text, 0, "something else", SecretAudience.Party, null, at)); // edited since
        Assert.Null(SecretText.SetAudience(text, 1, "inner", SecretAudience.Dm, null, at)); // inside a DM block
        Assert.Null(SecretText.SetAudience(text, 7, "x", SecretAudience.Party, null, at)); // no such block
        Assert.Null(SecretText.SetAudience(text, 0, "outer\n:::party\ninner\n:::", SecretAudience.Some, Array.Empty<string>(), at)); // to no one
        Assert.NotNull(SecretText.SetAudience(text, 0, "outer\n:::party\ninner\n:::", SecretAudience.Party, null, at));
    }

    [Fact]
    public void ABlockForSomeCharacters_IsReadOnlyByWhoeverPlaysOneOfThem()
    {
        const string text = "All.\n:::for [[actor:aria]] [[actor:bram]]\nAria and Bram know.\n:::for [[actor:bram]]\nOnly Bram.\n:::\n:::";
        Assert.Equal("All.", SecretText.ForPlayers(text));
        Assert.Equal("All.\n\nAria and Bram know.", SecretText.ForPlayers(text, new HashSet<string> { "aria" }));
        Assert.Equal("All.\n\nAria and Bram know.\n\nOnly Bram.", SecretText.ForPlayers(text, new HashSet<string> { "bram" }));
        Assert.Equal("All.", SecretText.ForPlayers(text, new HashSet<string> { "cara" }));

        var revealed = SecretText.SetAudience("x\n:::dm\nsecret\n:::", 0, "secret", SecretAudience.Some, new[] { "aria" }, new DateTime(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc));
        Assert.Equal("x\n:::for [[actor:aria]] 2026-01-02T03:04:00Z\nsecret\n:::", revealed);
    }

    [Fact]
    public void ForPlayersMarked_KeepsOnlyTheStampsOfRevealedBlocks()
    {
        // A stamp without seconds (written by hand) reads the same.
        const string text = "a\n:::party 2026-10-29T14:05:30Z\nnews\n:::\n:::party\nold news\n:::\n:::dm\nsecret\n:::\n:::for [[actor:aria]] 2026-10-30T09:00Z\nfor aria\n:::";
        Assert.Equal("a\n\n:::party 2026-10-29T14:05:30Z\nnews\n:::\n\nold news", SecretText.ForPlayersMarked(text, null));
        var forAria = SecretText.ForPlayersMarked(text, new HashSet<string> { "aria" });
        Assert.EndsWith(":::party 2026-10-30T09:00:00Z\nfor aria\n:::", forAria);
        Assert.Equal(SecretText.ForPlayers(text, new HashSet<string> { "aria" }), SecretText.ForPlayers(forAria));
    }

    [Fact]
    public void KeepSecrets_KeepsBlocksForSomeCharactersToo()
    {
        const string stored = "Mine.\n:::for [[actor:aria]]\nAria's secret.\n:::";
        Assert.Equal("Edited.\n\n:::for [[actor:aria]]\nAria's secret.\n:::", SecretText.KeepSecrets(stored, "Edited."));
    }

    [Fact]
    public void Blocks_KnowWhereTheyCloseAndHowDeepTheyAre()
    {
        var blocks = SecretText.Blocks("a\n:::dm\nb\n:::party\nc\n:::\n:::\n:::for [[actor:x]]\nd");

        Assert.Equal(new int?[] { 6, 5, null }, blocks.Select(b => b.ClosingLine));
        Assert.Equal(new[] { 0, 1, 0 }, blocks.Select(b => b.Depth));
    }

    [Fact]
    public void RevealedOnlyTo_IsWhatABlockForTheirCharacterAddsForThatPlayer()
    {
        const string text = "Born.\n:::for [[actor:a]]\nAdopted.\n:::\n:::for [[actor:b]]\nB's.\n:::\n:::dm\nCult.\n:::\n:::party\nOut.\n:::";

        Assert.Equal("Adopted.", SecretText.RevealedOnlyTo(text, new HashSet<string> { "a" }));
        Assert.Equal("Adopted.\n\nB's.", SecretText.RevealedOnlyTo(text, new HashSet<string> { "a", "b" }));
        Assert.Equal(string.Empty, SecretText.RevealedOnlyTo(text, null));
        Assert.Equal(string.Empty, SecretText.RevealedOnlyTo(text, new HashSet<string> { "c" }));
    }

    [Fact]
    public void PlayersVersion_GoesRightAfterTheSecret_AndReplacesTheOneThere()
    {
        const string text = "Intro.\n:::dm\nThe keeper runs the smugglers.\n:::\nAfter.";

        var once = SecretText.SetPlayersVersion(text, 0, "The keeper runs the smugglers.", "Lights at night.");
        Assert.Equal("Intro.\n:::dm\nThe keeper runs the smugglers.\n:::\n\n:::players\nLights at night.\n:::\n\nAfter.", once);
        Assert.Equal("Intro.\n\nLights at night.\n\nAfter.", SecretText.ForPlayers(once));

        var twice = SecretText.SetPlayersVersion(once, 0, "The keeper runs the smugglers.", "Boats come and go\n:::\n:::dm\nby night.");
        Assert.Equal("Intro.\n:::dm\nThe keeper runs the smugglers.\n:::\n\n:::players\nBoats come and go\nby night.\n:::\n\nAfter.", twice);
    }

    [Theory]
    [InlineData("Intro.\n:::dm\nSecret.\n:::", 0, "Changed.")] // edited since the DM looked
    [InlineData("Intro.\n:::dm\nSecret.", 0, "Secret.")] // never closed: no "after"
    [InlineData(":::party\n:::dm\nSecret.\n:::\n:::", 1, "Secret.")] // inside another block
    [InlineData("Intro.\n:::dm\nSecret.\n:::", 3, "Secret.")] // no such block
    public void PlayersVersion_IsRefused_WhereItWouldNotBeBesideTheSecretTheDmSaw(string text, int block, string expected) =>
        Assert.Null(SecretText.SetPlayersVersion(text, block, expected, "Told."));

    [Fact]
    public void PlayersVersion_ThatIsOnlyMarkers_IsRefused() =>
        Assert.Null(SecretText.SetPlayersVersion(":::dm\nS.\n:::", 0, "S.", ":::\n\n:::dm"));

    /// <summary>Putting a players' version beside a secret never shows a player a word they could not read before, and
    /// every player reads the version; random texts as in <see cref="NoWordAPlayerMayNotRead_EverReachesThem"/>.</summary>
    [Fact]
    public void PlayersVersion_RevealsNothingButItself()
    {
        var random = new Random(4321);
        string[] openers = { ":::dm", ":::party", ":::players", ":::for [[actor:a]]", ":::xyz" };
        IReadOnlySet<string>?[] viewers = { null, new HashSet<string> { "a" } };
        var tried = 0;
        for (var round = 0; round < 2000; round++)
        {
            var lines = new List<string>();
            var depth = 0;
            var n = random.Next(1, 25);
            for (var i = 0; i < n; i++)
            {
                var roll = random.Next(10);
                if (roll < 3) { lines.Add(openers[random.Next(openers.Length)]); depth++; }
                else if (roll < 6 && depth > 0) { lines.Add(":::"); depth--; }
                else lines.Add(random.Next(4) == 0 ? string.Empty : $"w{round}x{i}");
            }

            var text = string.Join("\n", lines);
            foreach (var block in SecretText.Blocks(text).Where(b => b is { Marked: SecretAudience.Dm, Depth: 0, ClosingLine: not null }))
            {
                tried++;
                var told = $"told{round}b{block.Block}";
                var after = SecretText.SetPlayersVersion(text, block.Block, block.Inner, told + "\n:::\n:::party");
                Assert.NotNull(after);
                Assert.Equal(block.Inner, SecretText.Blocks(after).Single(b => b.Line == block.Line).Inner);
                foreach (var viewer in viewers)
                {
                    var before = Words(SecretText.ForPlayers(text, viewer));
                    var now = Words(SecretText.ForPlayers(after, viewer));
                    // Nothing new but the version (a players' version it replaces is gone, which is the point).
                    Assert.True(now.IsSubsetOf(before.Append(told)) && now.Contains(told), $"viewer {(viewer == null ? "none" : "a")} on:\n{text}\n---\n{after}");
                }
            }
        }

        Assert.True(tried > 500, $"only {tried} blocks tried");

        static HashSet<string> Words(string text) => text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    /// <summary>The property that matters: whatever the DM writes, no word a player may not read reaches them, whether they own
    /// no character, the one a block names, or another. Random texts of plain lines, markers of every kind, nesting, unclosed
    /// blocks and noise, read as <see cref="SecretText.ForPlayers(string?, IReadOnlySet{string}?)"/> and as the marked copy a page hands them.</summary>
    [Fact]
    public void NoWordAPlayerMayNotRead_EverReachesThem()
    {
        var random = new Random(1234);
        string[] openers = { ":::dm", ":::party", ":::players", ":::for @Aria", ":::xyz", "  :::Dm  ", ":::revealed", ":::for [[actor:a]]", ":::for [[actor:b]] 2026-01-01T10:00Z", ":::for [[actor:a]] [[actor:b]]", ":::party 2026-10-29T14:05Z" };
        IReadOnlySet<string>?[] viewers = { null, new HashSet<string> { "a" }, new HashSet<string> { "b" } };
        for (var round = 0; round < 3000; round++)
        {
            var lines = new List<string>();
            // Each open block: who may read inside it (null: everyone the text is shown to; empty: no player).
            var stack = new List<HashSet<string>?>();
            var hiddenFrom = viewers.Select(_ => new HashSet<string>()).ToArray();
            var n = random.Next(1, 25);
            for (var i = 0; i < n; i++)
            {
                var roll = random.Next(10);
                if (roll < 2)
                {
                    var opener = openers[random.Next(openers.Length)];
                    var word = opener.Trim().TrimStart(':').Split(' ')[0].ToLowerInvariant();
                    stack.Add(word switch
                    {
                        "party" or "players" or "revealed" => null,
                        "for" => System.Text.RegularExpressions.Regex.Matches(opener, @"actor:(\w)").Select(m => m.Groups[1].Value).ToHashSet(),
                        _ => new HashSet<string>()
                    });
                    lines.Add(opener);
                }
                else if (roll < 4 && stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                    lines.Add(random.Next(2) == 0 ? ":::" : "  :::  ");
                }
                else
                {
                    var word = $"w{round}x{i}";
                    for (var v = 0; v < viewers.Length; v++)
                    {
                        var owned = viewers[v];
                        if (stack.Any(allowed => allowed != null && (owned == null || !allowed.Overlaps(owned)))) hiddenFrom[v].Add(word);
                    }

                    lines.Add(random.Next(5) == 0 ? $"- {word} and more" : word);
                }
            }

            var text = string.Join(random.Next(3) switch { 0 => "\n", 1 => "\r\n", _ => "\r" }, lines);
            for (var v = 0; v < viewers.Length; v++)
            {
                var plain = SecretText.ForPlayers(text, viewers[v]);
                var marked = SecretText.ForPlayersMarked(text, viewers[v]);
                foreach (var seen in new[] { plain, marked, SecretText.ForPlayers(marked) })
                {
                    var words = seen.Split(new[] { ' ', '\n', '\r', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var word in hiddenFrom[v])
                    {
                        Assert.True(!words.Contains(word), $"'{word}' leaked to viewer {v} from:\n{text}\n---\n{seen}");
                    }
                }

                Assert.DoesNotContain(":::", plain);
                // The marked copy holds no marker but a revealed block's: nothing in it can be read as a DM block's.
                Assert.All(marked.Split('\n').Where(l => l.Contains(":::")), l => Assert.Matches(@"^:::(party \S+)?$", l));
            }
        }
    }

    [Fact]
    public void LoneCarriageReturns_EndLines_SoAMarkerAfterOneStillHides()
    {
        var text = "Open.\r:::dm\rThe keeper is the cultist.\r:::\rAfter.";

        var seen = SecretText.ForPlayers(text);

        Assert.DoesNotContain("keeper", seen);
        Assert.Contains("Open.", seen);
        Assert.Contains("After.", seen);
    }

    private const string Hidden = "Village.\n:::dm\nThe keeper is a cultist.\n:::\n:::for [[actor:bram]]\nYou were born here.\n:::";

    [Theory]
    [InlineData("Village, now bigger.\n:::dm\nThe keeper is a cultist.\n:::\n:::for [[actor:bram]]\nYou were born here.\n:::")]
    [InlineData(":::for [[actor:bram]]\nYou were born here.  \n:::\n\nNew text.\n\n:::dm\n\nThe keeper is a cultist.\n:::\n:::dm\nA new secret.\n:::")]
    public void LostSecrets_IsEmpty_WhenEveryHiddenBlockIsKeptWhole(string after) =>
        Assert.Empty(SecretText.LostSecrets(Hidden, after));

    [Theory]
    [InlineData("Village.\n:::for [[actor:bram]]\nYou were born here.\n:::")]
    [InlineData("Village.\nThe keeper is a cultist.\n:::for [[actor:bram]]\nYou were born here.\n:::")]
    [InlineData("Village.\n:::party\nThe keeper is a cultist.\n:::\n:::for [[actor:bram]]\nYou were born here.\n:::")]
    [InlineData("Village.\n:::dm\nThe keeper is a smuggler.\n:::\n:::for [[actor:bram]]\nYou were born here.\n:::")]
    public void LostSecrets_NamesADmBlockDroppedMovedOutRevealedOrRewritten(string after) =>
        Assert.Equal(new[] { "The keeper is a cultist." }, SecretText.LostSecrets(Hidden, after));

    [Fact]
    public void LostSecrets_NamesABlockForOneCharacterGivenToAnother()
    {
        var after = Hidden.Replace("[[actor:bram]]", "[[actor:aria]]");

        Assert.Equal(new[] { "You were born here." }, SecretText.LostSecrets(Hidden, after));
    }
}
