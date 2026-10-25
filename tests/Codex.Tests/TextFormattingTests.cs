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
}
