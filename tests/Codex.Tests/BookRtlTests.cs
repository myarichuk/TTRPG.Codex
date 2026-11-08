using Codex.Core.Book;

namespace Codex.Tests;

/// <summary>A theme's sheets mirrored for a right-to-left book (docs/BOOK_EXPORT.md 41.21, <see cref="BookRtl"/>).</summary>
public class BookRtlTests
{
    [Theory]
    [InlineData(".a{padding-left:1.4em}", ".a{padding-right:1.4em}")]
    [InlineData(".a { margin-right : -40px; float : left; }", ".a { margin-left : -40px; float : right; }")]
    [InlineData(".p:nth-child(even) .n{left:30px}", ".p:nth-child(even) .n{right:30px}")]
    [InlineData(".a{text-align:right!important}", ".a{text-align:left!important}")]
    [InlineData(".a{margin:1px 2px 3px 4px}", ".a{margin:1px 4px 3px 2px}")]
    [InlineData(".a{padding:0 1em}", ".a{padding:0 1em}")]
    [InlineData(".a{padding:1px 2px 3px}", ".a{padding:1px 2px 3px}")]
    [InlineData(".a{margin:0 1em 0 2em}", ".a{margin:0 2em 0 1em}")]
    [InlineData(".a{border-radius:1px 2px 3px 4px}", ".a{border-radius:2px 1px 4px 3px}")]
    [InlineData(".a{border-top-left-radius:3px;border-left:2px solid red}", ".a{border-top-right-radius:3px;border-right:2px solid red}")]
    [InlineData(".a{background-position:left top}", ".a{background-position:right top}")]
    [InlineData("@media print{.a{right:0}}", "@media print{.a{left:0}}")]
    // Pictures, strings, comments, logical properties and selectors are kept as written.
    [InlineData(".a{background:url(\"data:image/svg+xml,left;right:0\") left}", ".a{background:url(\"data:image/svg+xml,left;right:0\") right}")]
    [InlineData(".a{content:'left'}/* float:left */", ".a{content:'left'}/* float:left */")]
    [InlineData(".a{float:inline-start;padding-inline-start:1em}", ".a{float:inline-start;padding-inline-start:1em}")]
    [InlineData(".left{color:red}", ".left{color:red}")]
    [InlineData("a:hover{color:red}", "a:hover{color:red}")]
    public void LeftAndRight_AreExchanged_AndNothingElse(string css, string mirrored)
    {
        Assert.Equal(mirrored, BookRtl.Mirror(css));
    }

    [Fact]
    public void MirroredTwice_EveryThemeSheet_ComesBackAsItWas()
    {
        // Each exchange is its own inverse, so a sheet mirrored twice is the sheet (shorthands written with four values already).
        foreach (var theme in BookDraftThemes.All)
        {
            var css = theme.Css(BookLayout.For("en", "A4", 2));
            var twice = BookRtl.Mirror(BookRtl.Mirror(css));
            Assert.Equal(Normalized(css), Normalized(twice));
        }

        static string Normalized(string css) => System.Text.RegularExpressions.Regex.Replace(css, @"\b(margin|padding|inset|border-(?:width|style|color)|border-radius)(\s*:\s*)([^;{}]*)",
            m => m.Groups[1].Value + m.Groups[2].Value + string.Join(' ', Four(m.Groups[3].Value.Trim())));

        static string[] Four(string value)
        {
            var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return value.Contains('(') || value.Contains('/') || p.Length is < 2 or > 4 ? p : [p[0], p[1], p.Length > 2 ? p[2] : p[0], p.Length > 3 ? p[3] : p[1]];
        }
    }

    [Fact]
    public void OnlyARightToLeftBook_IsMirrored_AndTheGmsStylesheetNever()
    {
        const string gm = ".page ul { padding-left: 3em; }";
        var hebrew = BookDraftHtml.Shell("S", "he", BookDraftThemes.Plain, "A4", gm, true, _ => null).Html!;
        var english = BookDraftHtml.Shell("S", "en", BookDraftThemes.Plain, "A4", gm, true, _ => null).Html!;

        // Homebrewery's layer writes .page ul{padding-left:1.4em}: on the right in Hebrew, as written in English.
        Assert.Contains(".page ul{padding-right:1.4em", hebrew);
        Assert.Contains(".page ul{padding-left:1.4em", english);
        Assert.Contains("dir=\"rtl\"", hebrew);
        var gmSheet = hebrew[hebrew.IndexOf("<style id=\"gm-css\">", StringComparison.Ordinal)..];
        Assert.Contains("padding-left: 3em", gmSheet);
    }
}
