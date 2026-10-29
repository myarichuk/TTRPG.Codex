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
    [InlineData("Open.\n:::for @Aria\nReserved for single characters: the DM's for now.\n:::")]
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

    /// <summary>The property that matters: whatever the DM writes, no word written only inside a DM block reaches a player.
    /// Random texts of plain lines, markers of every kind, nesting, unclosed blocks and noise.</summary>
    [Fact]
    public void NoWordFromADmBlock_EverReachesAPlayer()
    {
        var random = new Random(1234);
        string[] openers = { ":::dm", ":::party", ":::players", ":::for @Aria", ":::xyz", "  :::Dm  ", ":::revealed" };
        for (var round = 0; round < 3000; round++)
        {
            var lines = new List<string>();
            var stack = new List<bool>(); // true for a block a player may not read (it, or a block around it, is the DM's)
            var secretWords = new HashSet<string>();
            var n = random.Next(1, 25);
            for (var i = 0; i < n; i++)
            {
                var roll = random.Next(10);
                if (roll < 2)
                {
                    var opener = openers[random.Next(openers.Length)];
                    var word = opener.Trim().TrimStart(':').Split(' ')[0].ToLowerInvariant();
                    var secret = stack.Any(s => s) || word is not ("party" or "players" or "revealed");
                    stack.Add(secret);
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
                    if (stack.Any(s => s)) secretWords.Add(word);
                    lines.Add(random.Next(5) == 0 ? $"- {word} and more" : word);
                }
            }

            var text = string.Join(random.Next(2) == 0 ? "\n" : "\r\n", lines);
            var seen = SecretText.ForPlayers(text);
            foreach (var word in secretWords)
            {
                Assert.True(!seen.Split(new[] { ' ', '\n', '\r', '-' }, StringSplitOptions.RemoveEmptyEntries).Contains(word), $"'{word}' leaked from:\n{text}\n---\n{seen}");
            }

            Assert.DoesNotContain(":::", seen);
        }
    }
}
