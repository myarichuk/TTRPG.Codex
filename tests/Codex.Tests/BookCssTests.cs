using Codex.Core.Book;

namespace Codex.Tests;

/// <summary>The GM's stylesheet (docs/BOOK_EXPORT.md 41): read as a browser reads it, so no escape or comment gets a fetch or a way out of the
/// style element past the checks, and everything harmless (escapes in strings, data fonts) survives as written.</summary>
public class BookCssTests
{
    private static string Clean(string css) => BookCss.Clean(css, out _);

    [Theory]
    // Escapes are decoded before a name is checked.
    [InlineData(@"p{background:\75rl(https://evil.example/x.png)}")]
    [InlineData(@"p{background:u\72l(https://evil.example/x.png)}")]
    [InlineData(@"p{background:\55\52\4c(https://evil.example/x.png)}")]
    [InlineData(@"@\69mport 'https://evil.example/x.css';")]
    [InlineData(@"@\69 mport url(https://evil.example/x.css);")]
    [InlineData(@"p{background:url('\68ttps://evil.example/x.png')}")]
    // Comments, whitespace and quotes inside url().
    [InlineData("p{background:url(/**/https://evil.example/x.png)}")]
    [InlineData("p{background:url(  https://evil.example/x.png  )}")]
    [InlineData("p{background:url( \"https://evil.example/x.png\" )}")]
    [InlineData("p{background:url(https://evil.example/x.png")]
    // What else fetches.
    [InlineData("@media print { @import url(https://evil.example/x.css); p{color:red} }")]
    [InlineData("@font-face{font-family:x;src:url(https://evil.example/f.woff2)}")]
    [InlineData("@font-face{font-family:x;src:url(data:font/woff2;base64,AA) format('woff2'), url(https://evil.example/f.woff2)}")]
    [InlineData("p{background:image-set('https://evil.example/x.png' 1x)}")]
    [InlineData("p{background:-webkit-image-set(url(https://evil.example/x.png) 1x)}")]
    [InlineData("p{background:image('https://evil.example/x.png')}")]
    [InlineData("p{background:src('https://evil.example/x.png')}")]
    [InlineData("p{cursor:url(https://evil.example/c.cur),auto}")]
    [InlineData("p{background:url(data:text/html,https://evil.example)}")]
    public void NothingIsFetched(string css)
    {
        var cleaned = Clean(css);

        Assert.DoesNotContain("evil.example", cleaned, StringComparison.OrdinalIgnoreCase);
        // And what is left reads back as nothing that fetches either: the output is its own input.
        Assert.Equal(cleaned, Clean(cleaned));
    }

    [Theory]
    // Dropping a token must not join its neighbours into a call that was never written: "ur" and "l(" stay two words.
    [InlineData("p{background:ur<l(https://evil.example/x.png)}")]
    [InlineData("p{background:ur\\\nl(https://evil.example/x.png)}")]
    [InlineData("p{background:ur<!--l(https://evil.example/x.png)}")]
    [InlineData("p{background:ur/**/l(https://evil.example/x.png)}")]
    [InlineData("p{background:ur\"x\nl(https://evil.example/x.png)}")]
    public void WhatIsDroppedJoinsNothing(string css)
    {
        var cleaned = Clean(css);

        Assert.DoesNotContain(CssTokenizer.Tokenize(cleaned), t => t.Type == CssTokenType.Url || t.Type == CssTokenType.Function && t.Lower == "url");
        Assert.Equal(cleaned, Clean(cleaned));
    }

    [Theory]
    [InlineData("p{color:red}</style><script>alert(1)</script>")]
    [InlineData("p{content:'</style><script>alert(1)</script>'}")]
    [InlineData(@"p{content:'\3c/style>'}")]
    [InlineData(@"a\3c/style\3e{color:red}")]
    [InlineData(@"#x\3c/style\3e{color:red}")]
    [InlineData(@"p{font-family:\3c/style>}")]
    [InlineData("p{content:'unclosed\n</style>")]
    [InlineData("<!--</style>-->")]
    public void NothingClosesTheStyleElement(string css)
    {
        Assert.DoesNotContain("<", Clean(css));
    }

    [Theory]
    [InlineData("p{width:expression(alert(1))}", "expression")]
    [InlineData(@"p{width:e\78pression(alert(1))}", "alert")]
    [InlineData("p{behavior:url(x.htc)}", "htc")]
    [InlineData(@"p{\62ehavior:url(x.htc)}", "htc")]
    [InlineData("p{-moz-binding:url(x.xml#b)}", "binding")]
    public void NothingRuns(string css, string forbidden)
    {
        Assert.DoesNotContain(forbidden, Clean(css), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Homebrewery's own sheets write these.
    [InlineData(@".page .toc li::after{content:'\2014'}")]
    [InlineData(".page .toc li a::after{content:'.......................................................'}")]
    [InlineData(@".x::before{content:'\201C' attr(data-x) '\201D'}")]
    [InlineData(@".icon\:fire{color:red}")]
    [InlineData(@".\31 0col{columns:10}")]
    [InlineData("@font-face{font-family:'X';src:url(data:font/woff2;base64,d09GMgAB) format('woff2')}")]
    [InlineData("@font-face{font-family:'X';src:local('X'),url('data:font/woff2;base64,d09GMgAB') format('woff2')}")]
    [InlineData(".x{background:url(data:image/png;base64,AAAA) 0 0/100% no-repeat}")]
    [InlineData(".x{filter:url(#shadow)}")]
    [InlineData(".page:has(.frontCover) h1{font-size:1.2e1px;margin:-0.5cm 0 .2cm}")]
    [InlineData("@media print{.page{break-after:page}}@supports (display:grid){.a{display:grid}}")]
    [InlineData(".a{.b &{color:red}}")]
    [InlineData(":root{--HB_Color_Accent:#E0E5C1;--x:{a:b}}")]
    [InlineData("@page :first{margin:0}")]
    [InlineData(".a{unicode-range:U+0000-00FF,U+0131}")]
    public void WhatIsHarmlessIsKept(string css)
    {
        var cleaned = Clean(css);

        Assert.Equal(Tokens(css), Tokens(cleaned));
        BookCss.Clean(css, out var warnings);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AnEscapeIsKeptAsWhatItMeans()
    {
        // The dash a contents line draws is still the dash, however it was written.
        Assert.Equal(".a::after{content:\"\u2014\"}", Clean(@".a::after{content:'\2014'}"));
        Assert.Equal(".a::after{content:\"\u2014\"}", Clean(".a::after{content:'\\2014 '}"));
    }

    [Fact]
    public void AFaceThatWouldFetchIsDroppedWhole()
    {
        var cleaned = BookCss.Clean("@font-face{font-family:'X';src:url(data:font/woff2;base64,AA) format('woff2'),url('https://evil.example/f.woff2')}p{color:red}", out var warnings);

        Assert.Equal("p{color:red}", cleaned);
        Assert.Contains("css-font-dropped", warnings);
    }

    [Fact]
    public void ATokenWithoutAnEscapeIsWrittenAsItWas()
    {
        const string css = ".page .note h3 + p { margin-top: 0.325cm; color: #58180D } /* note */ .x{width:calc(100% - 2px)}";

        Assert.Equal(".page .note h3 + p { margin-top: 0.325cm; color: #58180D } .x{width:calc(100% - 2px)}", Clean(css));
    }

    [Fact]
    public void ASheetPastTheLimitIsCut_AndSaysSo()
    {
        var css = new string('a', BookCss.MaxLength + 10);

        var cleaned = BookCss.Clean(css, out var warnings);

        Assert.True(cleaned.Length <= BookCss.MaxLength);
        Assert.Contains("css-truncated", warnings);
    }

    [Fact]
    public void AWholeThemeFitsUnderTheLimit()
    {
        // Homebrewery's compiled 5e theme is about 120 KB; a community design on top of it is more.
        Assert.True(BookCss.MaxLength >= 400_000);
    }

    [Theory]
    [InlineData("p{content:'abc\\")]
    [InlineData("p{content:'abc")]
    [InlineData("\\")]
    [InlineData("p{a:\0b}")]
    [InlineData("p{a:'\\\nb'}")]
    [InlineData("@")]
    [InlineData("url(")]
    [InlineData("u\\")]
    [InlineData("#\\")]
    [InlineData("1\\65 3px")]
    [InlineData("\ud800")]
    public void ABrokenSheetIsStillCleaned(string css)
    {
        var cleaned = Clean(css);

        Assert.DoesNotContain("<", cleaned);
        Assert.Equal(cleaned, Clean(cleaned));
    }

    // The meaning of a sheet: its tokens, without whitespace runs or comments, escapes decoded.
    private static List<string> Tokens(string css) =>
        CssTokenizer.Tokenize(css).Where(t => t.Type != CssTokenType.Whitespace).Select(t => $"{t.Type}:{t.NumberRaw}:{t.Value}").ToList();
}
