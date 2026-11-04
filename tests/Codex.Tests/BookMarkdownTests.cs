using System.Diagnostics;
using Codex.Core.Book;

namespace Codex.Tests;

/// <summary>The book renderer: the dialect, and above all what it refuses. A book is printed from text a model and several people
/// wrote, so "escaped, no URL, no name it may not give, no secret" are tested as properties, not as examples of output.</summary>
public class BookMarkdownTests
{
    private static string Html(string text, BookMarkdownOptions? options = null) => BookMarkdown.Render(text, options).Html;

    // ---- what it refuses -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("**<b onclick=1>**")]
    [InlineData("[<i>x</i>](javascript:alert(1))")]
    [InlineData("`<script>`")]
    [InlineData("| <script> |\n|---|\n| <script> |")]
    [InlineData("# <script>")]
    [InlineData("> <script>")]
    [InlineData("- <script>")]
    [InlineData("```\n<script>\n```")]
    [InlineData("{{<script> <script>}}")]
    public void RawHtmlNeverSurvives(string markdown)
    {
        var html = Html(markdown);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b ", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<i>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoUrlSurvivesInALinkOrAnImage()
    {
        var result = BookMarkdown.Render("See [the map](https://evil.example/x?secret=DM) and ![pic](https://evil.example/p.png?secret=DM) and ![a](file:///etc/passwd).");

        Assert.DoesNotContain("evil.example", result.Html);
        Assert.DoesNotContain("file:", result.Html);
        Assert.DoesNotContain("href", result.Html);
        Assert.DoesNotContain("src=", result.Html);
        Assert.Contains("the map", result.Html); // the words stay
        Assert.Contains("link-dropped", result.Warnings);
        Assert.Contains("image-dropped", result.Warnings);
    }

    [Fact]
    public void AnImageSlotIsAPlaceholderUntilItHasArt_AndOnlyDataImagesAreAccepted()
    {
        Assert.Contains("class=\"slot\" data-slot=\"geography-1\"", Html("![The coast](slot:geography-1)"));

        var art = new BookMarkdownOptions { Image = id => id == "geography-1" ? "data:image/png;base64,AAAA" : null };
        Assert.Contains("<img class=\"art\" src=\"data:image/png;base64,AAAA\" alt=\"The coast\">", Html("![The coast](slot:geography-1)", art));
        Assert.Contains("class=\"slot\"", Html("![x](slot:other)", art));

        foreach (var bad in new[] { "https://evil.example/a.png", "data:text/html,<script>", "data:image/png;base64,AA\" onerror=\"x" })
        {
            var html = Html("![x](asset:a)", new BookMarkdownOptions { Image = _ => bad });
            Assert.DoesNotContain("evil.example", html);
            Assert.DoesNotContain("onerror", html);
            Assert.DoesNotContain("<img", html);
        }
    }

    [Fact]
    public void AMentionIsNamedOnlyByTheResolver_AndItsOwnLabelIsNeverUsed()
    {
        const string text = "Ask [[actor:a1|CANARY_Real_Name]] about [[place:p9]] and [[item:weapon/sword|CANARY_Label]].";

        // No resolver: nothing is named, and the typed label does not leak instead.
        var closed = BookMarkdown.Render(text);
        Assert.DoesNotContain("CANARY", closed.Html);
        Assert.Contains("mention-hidden", closed.Warnings);

        var open = Html(text, new BookMarkdownOptions { Mention = (kind, id) => kind == "actor" && id == "a1" ? "Aria" : null });
        Assert.Contains("<span class=\"mention mention-actor\">Aria</span>", open);
        Assert.DoesNotContain("CANARY", open);
        Assert.DoesNotContain("p9", open);
    }

    [Fact]
    public void ThePartyEditionDropsWhatAStrangerMayNotRead_EvenFromASourceThatWasNotCleaned()
    {
        const string text = "Open.\n\n:::dm\nCANARY_DM\n:::\n\n:::for [[actor:a1]]\nCANARY_FOR\n:::\n\n:::party 2026-01-01T00:00:00Z\nPUBLIC_REVEALED\n:::\n\n:::players\nPUBLIC_TOLD\n:::\n\nClosed.";

        var html = BookMarkdown.Render(text);

        Assert.DoesNotContain("CANARY", html.Html);
        Assert.DoesNotContain(":::", html.Html);
        Assert.Contains("PUBLIC_REVEALED", html.Html);
        Assert.Contains("PUBLIC_TOLD", html.Html);
        Assert.Contains("Closed.", html.Html);
        Assert.Contains("secret-dropped", html.Warnings);
    }

    [Fact]
    public void AnUnclosedSecretBlockIsStillSecret()
    {
        var html = Html("Before.\n\n:::dm\nCANARY_RUNS_TO_THE_END\n\nmore CANARY text");

        Assert.Contains("Before.", html);
        Assert.DoesNotContain("CANARY", html);
    }

    [Fact]
    public void TheDmEditionBoxesSecretsAndNeverPrintsAMarker()
    {
        var html = Html("Open.\n\n:::dm\nThe keeper is a smuggler.\n:::\n\nAfter.", new BookMarkdownOptions { BoxSecrets = true });

        Assert.Contains("<div class=\"secret secret-dm\">", html);
        Assert.Contains("The keeper is a smuggler.", html);
        Assert.DoesNotContain(":::", html);
        Assert.Contains("After.", html);
    }

    [Fact]
    public void StyleIsAllowListed_AndNoValueCanFetchAnything()
    {
        var result = BookMarkdown.Render("{{note,color:red,width:50%,background:url(https://evil.example/x),position:fixed,behavior:url(x),background-color:expression(alert(1)),font-size:\"calc(1em + 2px)\",#box\ntext\n}}");

        Assert.DoesNotContain("evil.example", result.Html);
        Assert.DoesNotContain("url(", result.Html);
        Assert.DoesNotContain("expression", result.Html);
        Assert.DoesNotContain("position", result.Html);
        Assert.Contains("class=\"note\"", result.Html);
        Assert.Contains("id=\"box\"", result.Html);
        Assert.Contains("color:red", result.Html);
        Assert.Contains("width:50%", result.Html);
        Assert.Contains("font-size:calc(1em + 2px)", result.Html);
        Assert.Contains("attribute-dropped", result.Warnings);
    }

    [Theory]
    [InlineData("a\"onmouseover=\"x")]
    [InlineData("a b")]
    [InlineData("1abc")]
    [InlineData("a;b")]
    public void AClassNameThatIsNotPlainIsDropped(string name)
    {
        var html = Html($"{{{{{name} text}}}}");

        Assert.DoesNotContain("onmouseover", html);
        Assert.DoesNotContain("class=\"" + name, html);
    }

    [Fact]
    public void ABlockPlaceholderIsTheOnlyWayInForTrustedHtml()
    {
        var blocks = new Dictionary<int, string> { [3] = "<table class=\"stat\"><tr><td>AC 15</td></tr></table>" };

        Assert.Contains("<table class=\"stat\"><tr><td>AC 15</td></tr></table>", Html("{{block:3}}", new BookMarkdownOptions { Blocks = blocks }));

        var missing = BookMarkdown.Render("{{block:4}}", new BookMarkdownOptions { Blocks = blocks });
        Assert.Equal(string.Empty, missing.Html);
        Assert.Contains("block-missing", missing.Warnings);

        // Mid-sentence it is not a placeholder, so a model cannot smuggle HTML in by a different route.
        Assert.DoesNotContain("<table", Html("see {{block:3}} here", new BookMarkdownOptions { Blocks = blocks }));
    }

    // ---- the dialect -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Inline_BoldItalicStrikeCodeAndEscapes()
    {
        Assert.Equal("<p dir=\"auto\"><strong>b</strong> <em>i</em> <em>u</em> <s>s</s> <code>a*b</code> <strong><em>both</em></strong> *lit*</p>",
            Html("**b** *i* _u_ ~~s~~ `a*b` ***both*** \\*lit\\*"));
    }

    [Fact]
    public void AnUnderscoreInsideAWordIsNotEmphasis_InAnyScript()
    {
        Assert.Equal("<p dir=\"auto\">snake_case_name Привет_мир_всем שלום_עולם_כולם</p>", Html("snake_case_name Привет_мир_всем שלום_עולם_כולם"));
        Assert.Contains("<em>мир</em>", Html("Привет _мир_ всем"));
        Assert.Contains("<em>עולם</em>", Html("שלום _עולם_ כולם"));
    }

    [Fact]
    public void Headings_GetUniqueIds_InAnyScript_AndAreCollected()
    {
        var result = BookMarkdown.Render("# Coast\n\n## Coast\n\n# Побережье\n\n## **מגדלור** ##");

        Assert.Equal(["coast", "coast-2", "побережье", "מגדלור"], result.Headings.Select(h => h.Id));
        Assert.Equal([1, 2, 1, 2], result.Headings.Select(h => h.Level));
        Assert.Equal("מגדלור", result.Headings[3].Text);
        Assert.Contains("<h1 id=\"coast\" dir=\"auto\">Coast</h1>", result.Html);
    }

    [Fact]
    public void Paragraphs_JoinSoftBreaks_AndHonourHardBreaks()
    {
        Assert.Equal("<p dir=\"auto\">one\ntwo</p><p dir=\"auto\">three<br>four<br>five</p>", Html("one\ntwo\n\nthree  \nfour\\\nfive"));
    }

    [Fact]
    public void Lists_Nest_AndAWrappedNumberStaysProse()
    {
        Assert.Equal("<ul dir=\"auto\"><li dir=\"auto\">a<ul dir=\"auto\"><li dir=\"auto\">b</li><li dir=\"auto\">c</li></ul></li><li dir=\"auto\">d</li></ul>",
            Html("- a\n  - b\n  - c\n- d"));
        Assert.Equal("<ol dir=\"auto\" start=\"3\"><li dir=\"auto\">x</li><li dir=\"auto\">y</li></ol>", Html("3. x\n4. y"));
        Assert.Equal("<p dir=\"auto\">in the year\n1482. The tower fell</p>", Html("in the year\n1482. The tower fell"));
    }

    [Fact]
    public void ALooseListKeepsItsParagraphs()
    {
        var html = Html("- a\n\n- b");

        Assert.Contains("<li dir=\"auto\"><p dir=\"auto\">a</p></li>", html);
        Assert.Contains("<li dir=\"auto\"><p dir=\"auto\">b</p></li>", html);
    }

    [Fact]
    public void Tables_AlignAndEscapePipes()
    {
        var html = Html("| Name | AC | HP |\n|:--|:-:|--:|\n| Orc | 13 | a\\|b |\n| Elf |");

        Assert.Contains("<th dir=\"auto\" style=\"text-align:left\">Name</th><th dir=\"auto\" style=\"text-align:center\">AC</th><th dir=\"auto\" style=\"text-align:right\">HP</th>", html);
        Assert.Contains("<td dir=\"auto\" style=\"text-align:right\">a|b</td>", html);
        Assert.Contains("<td dir=\"auto\" style=\"text-align:left\">Elf</td><td dir=\"auto\" style=\"text-align:center\"></td>", html);
    }

    [Fact]
    public void QuotesRulesFencesAndBreaks()
    {
        Assert.Equal("<blockquote><p dir=\"auto\">q1\nq2</p></blockquote>", Html("> q1\n> q2"));
        Assert.Equal("<hr>", Html("---"));
        Assert.Equal("<pre><code>a &lt; b\n  *not bold*</code></pre>", Html("```\na < b\n  *not bold*\n```"));
        Assert.Equal("<div class=\"page-break\"></div><div class=\"column-break\"></div>", Html("\\page\n\n\\column"));
    }

    [Fact]
    public void ABlockDivNestsAndAnInlineSpanTakesText()
    {
        var html = Html("{{wide,monster\n{{note\ninner\n}}\n}}\n\nplain {{descriptive,color:red hot text}} end");

        Assert.Equal("<div class=\"wide monster\"><div class=\"note\"><p dir=\"auto\">inner</p></div></div><p dir=\"auto\">plain <span class=\"descriptive\" style=\"color:red\">hot text</span> end</p>", html);
    }

    [Fact]
    public void AnUnclosedBlockIsClosedAtTheEnd_WithAWarning()
    {
        var result = BookMarkdown.Render("{{note\ntext");

        Assert.Equal("<div class=\"note\"><p dir=\"auto\">text</p></div>", result.Html);
        Assert.Contains("block-unclosed", result.Warnings);
    }

    [Fact]
    public void WikiLinksKeepTheirWordsAndNothingElse()
    {
        Assert.Equal("<p dir=\"auto\"><span class=\"wikilink\">Lighthouse</span> and <span class=\"wikilink\">the keeper</span></p>", Html("[[Lighthouse]] and [[Light|the keeper]]"));
    }

    // ---- robustness ------------------------------------------------------------------------------------------------------

    [Fact]
    public void HostileInputIsBoundedInTimeAndDepth()
    {
        var stopwatch = Stopwatch.StartNew();
        var inputs = new[]
        {
            new string('*', 200_000),
            string.Concat(Enumerable.Repeat("*a ", 40_000)),
            string.Concat(Enumerable.Repeat("_a ", 40_000)),
            string.Concat(Enumerable.Repeat("[", 20_000)),
            string.Concat(Enumerable.Repeat("{{", 20_000)),
            string.Concat(Enumerable.Repeat("> ", 5_000)) + "x",
            string.Concat(Enumerable.Repeat("{{a\n", 5_000)),
            string.Concat(Enumerable.Repeat("- x\n  ", 3_000)) + "y",
            string.Concat(Enumerable.Repeat("`", 30_001))
        };

        foreach (var input in inputs)
        {
            _ = BookMarkdown.Render(input);
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ATextOverTheLimitIsCutWithAWarning()
    {
        var result = BookMarkdown.Render(new string('a', 100) + "\n\nTAIL", new BookMarkdownOptions { MaxLength = 50 });

        Assert.DoesNotContain("TAIL", result.Html);
        Assert.Contains("text-truncated", result.Warnings);
    }

    [Fact]
    public void ControlCharactersAreRemoved_AndLineEndingsNormalised()
    {
        Assert.Equal("<p dir=\"auto\">a\nb</p><p dir=\"auto\">c</p>", Html("a\r\nb\r\n\r\nc\u0000\u0001"));
    }

    [Fact]
    public void RussianAndHebrewRenderWithAutomaticDirection()
    {
        var html = Html("## Маяк\n\nСмотритель — **контрабандист**.\n\n- שומר המגדלור\n- מפה");

        Assert.Contains("dir=\"auto\"", html);
        Assert.Contains("<strong>контрабандист</strong>", html);
        Assert.Contains("<li dir=\"auto\">שומר המגדלור</li>", html);
    }
}
