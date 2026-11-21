using Codex.Core;
using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>
/// <see cref="SecretBlock.OpenerWord"/> is the word a block was opened with, kept beside the audience it was read as. The parser's fail-closed reading is
/// unchanged (the SecretText tests are); the unknown-audience check now reads the word from the parser instead of re-reading the opening line.
/// </summary>
public class SecretOpenerTests
{
    [Theory]
    [InlineData(":::dm\nx\n:::", "dm", SecretAudience.Dm)]
    [InlineData(":::DM\nx\n:::", "DM", SecretAudience.Dm)]
    [InlineData(":::party\nx\n:::", "party", SecretAudience.Party)]
    [InlineData(":::revealed\nx\n:::", "revealed", SecretAudience.Party)]
    [InlineData(":::players\nx\n:::", "players", SecretAudience.Players)]
    [InlineData(":::for [[actor:a1]]\nx\n:::", "for", SecretAudience.Some)]
    [InlineData(":::partyy\nx\n:::", "partyy", SecretAudience.Dm)] // a typo: read as the DM's, and the word says it was not "dm"
    [InlineData("  :::Everyone\nx\n:::", "Everyone", SecretAudience.Dm)]
    [InlineData(":::dm:extra\nx\n:::", "dm:extra", SecretAudience.Dm)]
    public void TheWordABlockWasOpenedWith_IsKept_AsWritten_BesideTheAudienceItWasReadAs(string text, string word, SecretAudience audience)
    {
        var block = Assert.Single(SecretText.Blocks(text));
        Assert.Equal(word, block.OpenerWord);
        Assert.Equal(audience, block.Marked);
    }

    [Fact]
    public void EveryBlockOfANestedText_HasItsOwnWord()
    {
        var blocks = SecretText.Blocks(":::dm\n:::party\nx\n:::\n:::\n:::oops\ny\n:::");
        Assert.Equal(new[] { "dm", "party", "oops" }, blocks.Select(b => b.OpenerWord));
    }

    [Fact]
    public void TheParsing_IsAsItWas_ForAnUnknownWordAndAnUnclosedBlock()
    {
        // An unknown word and a block never closed are still the DM's, to the end of the text.
        var segments = SecretText.Parse("open\n:::partyy\nsecret\nmore");
        Assert.Equal(new[] { SecretAudience.Everyone, SecretAudience.Dm }, segments.Select(s => s.Audience));
        Assert.DoesNotContain("secret", SecretText.ForPlayers("open\n:::partyy\nsecret\nmore"));
    }

    private static List<Issue> Unknown(string body) =>
        ConsistencyChecker.Run(Fx.Snap(pages: new[] { Fx.Page("wikipages/1-A", "Page", body) }), new IConsistencyCheck[] { new SecretUnknownAudienceCheck() }).ToList();

    [Fact]
    public void TheCheck_ReportsOnlyAWordThatIsNotKnown_AndNamesIt()
    {
        var issue = Assert.Single(Unknown("a\n:::partyy\nx\n:::\nb\n:::dm\ny\n:::\n:::party\nz\n:::\n:::DM\nq\n:::"));

        Assert.Equal("Consistency.SecretUnknownAudience", issue.Code);
        Assert.Equal(new[] { "partyy", ":::partyy", "2" }, issue.Args);
        Assert.Contains(":::partyy", issue.Message);
        Assert.Contains("line 2", issue.Message);
        Assert.Empty(Unknown(":::dm\nx\n:::\n:::party\ny\n:::\n:::players\nz\n:::\n:::revealed\nw\n:::\n:::for [[actor:a]]\nv\n:::"));
    }

    [Fact]
    public void TheCheck_FindsTheWord_EvenWhenTheOpeningLineIsIndentedOrTheTextHasWindowsLineEndings()
    {
        Assert.Equal("typo", Assert.Single(Unknown("a\r\n  :::typo\r\nx\r\n  :::\r\n")).Args[0]);
        // A block inside a DM block that opens with an unknown word is still an unknown word.
        Assert.Equal("oops", Assert.Single(Unknown(":::dm\n:::oops\nx\n:::\n:::")).Args[0]);
    }
}
