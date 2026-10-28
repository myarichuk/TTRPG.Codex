using Codex.Core;

namespace Codex.Tests;

public class TextFormattingTests
{
    [Fact]
    public void ToHtml_EncodesMarkup_SoContentCannotInjectScripts()
    {
        var html = TextFormatting.ToHtml("<script>alert(1)</script> & **bold**");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.Contains("<strong>bold</strong>", html);
    }

    [Fact]
    public void ToHtml_RendersParagraphsBulletsTablesAndHeadings()
    {
        var html = TextFormatting.ToHtml("""
            *Level 3 Evocation*

            First line
            second line

            - one
            - two

            ### Actions
            | STR | DEX |
            | --- | --- |
            | 8 (-1) | 15 (+2) |
            """);

        Assert.Contains("<p><em>Level 3 Evocation</em></p>", html);
        Assert.Contains("<p>First line<br>second line</p>", html);
        Assert.Contains("<ul><li>one</li><li>two</li></ul>", html);
        Assert.Contains("<h5>Actions</h5>", html);
        Assert.Contains("<th scope=\"col\">STR</th>", html);
        Assert.Contains("<td>15 (+2)</td>", html);
        Assert.DoesNotContain("---", html);
    }

    [Fact]
    public void Summary_StripsMarkersAndTruncatesOnAWordBoundary()
    {
        var summary = TextFormatting.Summary("**Casting Time:** Action\nA bright streak flashes from you to a point you choose within range and then blossoms with a low roar.", 60);

        Assert.DoesNotContain("**", summary);
        Assert.True(summary.Length <= 62);
        Assert.EndsWith("…", summary);
    }

    [Theory]
    [InlineData("## Sights\nStilts creak over [[Varr|the port]].\n\n## What it hides\nA *trapdoor*.", "Stilts creak over the port. A trapdoor.")]
    [InlineData("### The city\nA port ruled by the [[Harbour Council]].\n- Population: about 9,000", "A port ruled by the Harbour Council. Population: about 9,000")]
    [InlineData("## Only a heading", "Only a heading")]
    [InlineData("**Bold** and _quiet_ text", "Bold and quiet text")]
    public void Summary_ReadsAsPlainText(string text, string expected)
    {
        Assert.Equal(expected, TextFormatting.Summary(text, 200));
    }

    [Fact]
    public void ToHtml_EmptyInput_IsEmpty()
    {
        Assert.Equal(string.Empty, TextFormatting.ToHtml(null));
        Assert.Equal(string.Empty, TextFormatting.ToHtml("   "));
    }

    [Theory]
    [InlineData("_Amphibious._ It can breathe air.", "<em>Amphibious.</em> It can breathe air.")]
    [InlineData("**_Инертен._** Вы роняете всё.", "<strong><em>Инертен.</em></strong> Вы роняете всё.")]
    [InlineData("_לחשים._ בכדור יש 7 מטענים.", "<em>לחשים.</em> בכדור יש 7 מטענים.")]
    [InlineData("Set max_hit_points and file_name.yaml", "Set max_hit_points and file_name.yaml")]
    [InlineData("A lone _ underscore _ stays", "A lone _ underscore _ stays")]
    public void Inline_ReadsUnderscoreItalics_ButNotInsideWords(string text, string expected)
    {
        Assert.Equal(expected, TextFormatting.Inline(text));
    }

    [Fact]
    public void ToHtml_RendersQuotesNumberedListsRulesCodeAndLinks()
    {
        var html = TextFormatting.ToHtml("""
            > Never trust a ledger.
            > - Mira

            1. first
            2. second

            ---

            See [the guild](https://example.com/a?x=1&y=2) and `a_b *c*`.

            ```
            raw <b>
            ```
            """);

        Assert.Contains("<blockquote><p>Never trust a ledger.<br>- Mira</p></blockquote>", html);
        Assert.Contains("<ol><li>first</li><li>second</li></ol>", html);
        Assert.Contains("<hr>", html);
        Assert.Contains("<a href=\"https://example.com/a?x=1&amp;y=2\" target=\"_blank\" rel=\"noopener noreferrer\">the guild</a>", html);
        Assert.Contains("<code>a_b *c*</code>", html); // what is inside code is left as written
        Assert.Contains("<pre><code>raw &lt;b&gt;</code></pre>", html);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](data:text/html;base64,AAAA)")]
    [InlineData("[x](https://a.com\" onmouseover=\"alert(1))")]
    public void ToHtml_LinksOnlyToWebAddresses_AndCannotBreakOutOfTheAttribute(string text)
    {
        var html = TextFormatting.ToHtml(text);

        Assert.DoesNotContain("href=\"javascript", html);
        Assert.DoesNotContain("href=\"data:", html);
        Assert.DoesNotContain("onmouseover=\"", html);
    }

    [Fact]
    public void Summary_CanKeepLinksForALaterResolve_AndNeverLeavesHalfOfOne()
    {
        const string text = "Walled farm town east of [[p1]]. Who is here: horse breeders, farmers, [[p1]] grain buyers, [[n5]]. Plenty more follows after this point in the text.";

        Assert.Contains("east of p1", TextFormatting.Summary(text, 400));           // the reading summary flattens a link to its text
        Assert.Contains("east of [[p1]]", TextFormatting.Summary(text, 400, keepLinks: true));

        var cut = TextFormatting.Summary("A long road runs to the north of the great city of [[Port Varr]] and on to the sea.", 55, keepLinks: true);
        Assert.DoesNotContain("[[", cut); // the link would have been cut in half, so it is dropped whole
    }

    [Theory]
    [InlineData("`a` and \u00035\u0004")]
    [InlineData("`a` \u000399999999999\u0004")]
    [InlineData("[x](https://a.com) \u00055\u0006")]
    public void ToHtml_TextHoldingThePlaceholderCharacters_DoesNotThrow(string text)
    {
        var ex = Record.Exception(() => TextFormatting.ToHtml(text));

        Assert.Null(ex);
    }

    [Fact]
    public void ToHtml_EmphasisDoesNotReachInsideALinksAddress()
    {
        var html = TextFormatting.ToHtml("[x](https://a.com/*b*c) and **[y](https://e.com/**)");

        Assert.Contains("href=\"https://a.com/*b*c\"", html);
        Assert.DoesNotContain("<em>b</em>c\"", html);
        Assert.Equal(html.Split("<a ").Length, html.Split("</a>").Length); // every link is closed
    }
}
