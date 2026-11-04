using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Tests;

/// <summary>The page a book prints from: the language gate, the closed network, and the theme held to the fonts it ships.</summary>
public class BookHtmlTests
{
    private static string? FontsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Codex.Web", "wwwroot", "fonts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static byte[]? Font(string file) => FontsDirectory() is { } dir && File.Exists(Path.Combine(dir, file)) ? File.ReadAllBytes(Path.Combine(dir, file)) : null;

    private static BookDocument Book(string markdown = "# Coast\n\nText.", string title = "Saltmere", string language = "en", string tagline = "") =>
        new(title, tagline, language, [new BookSection("a", markdown)], new BookLabels());

    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("EN", true)]
    [InlineData("ru", false)]
    [InlineData("he", false)]
    [InlineData("he-IL", false)]
    [InlineData("", false)]
    public void ThePlainThemeCoversEnglishOnly_AndRefusesTheRestInsteadOfPrintingThemInTheWrongFonts(string language, bool supported)
    {
        Assert.Equal(supported, BookThemes.Plain.Supports(language));

        var built = BookHtml.Build(Book(language: language), BookThemes.Plain, new BookHtmlOptions { Font = Font });

        Assert.Equal(supported, built.Html != null);
        if (!supported)
        {
            Assert.Contains("does not support", built.Refused);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(0, false)]
    public void TwoColumnsAreOnlyEverTwo_AndTheChapterTitleSpansBoth(int columns, bool two)
    {
        foreach (var theme in BookThemes.All)
        {
            var html = BookHtml.Build(Book(), theme, new BookHtmlOptions { Columns = columns, Font = Font }).Html!;

            Assert.Equal(two, html.Contains("column-count: 2", StringComparison.Ordinal));
            Assert.Equal(two, html.Contains(".chapter > h1 { column-span: all; }", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheDropCapBelongsToParchmentAndFollowsTheWritingDirection()
    {
        var plain = BookHtml.Build(Book(), BookThemes.Plain, new BookHtmlOptions { Font = Font }).Html!;
        var parchment = BookHtml.Build(Book(), BookThemes.Parchment, new BookHtmlOptions { Font = Font }).Html!;

        Assert.DoesNotContain("::first-letter", plain, StringComparison.Ordinal);
        Assert.Contains("::first-letter", parchment, StringComparison.Ordinal);
        Assert.Contains("float: left", parchment, StringComparison.Ordinal);
        Assert.Contains("float: right", BookThemes.Parchment.Css(new BookLayout("A4", true)), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFontAThemeNamesIsShipped()
    {
        Assert.NotNull(FontsDirectory());
        foreach (var theme in BookThemes.All)
        {
            var built = BookHtml.Build(Book(), theme, new BookHtmlOptions { Font = Font });

            Assert.NotNull(built.Html);
            Assert.Empty(built.MissingFonts);
        }
    }

    [Fact]
    public void AThemeIsHeldToTheLanguagesItsFontsCover()
    {
        // The plain theme declares English, so it must name a Latin face for each weight a book uses; a theme that declares
        // Russian or Hebrew must name faces for those scripts. This is the check that makes adding a language to a theme a decision.
        foreach (var theme in BookThemes.All)
        {
            foreach (var language in theme.Languages)
            {
                var needed = language switch { "ru" => "cyrillic", "he" => "hebrew", _ => "latin" };
                Assert.Contains(theme.Fonts, f => f.File.Contains(needed, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void ThePageFetchesNothing()
    {
        var built = BookHtml.Build(Book("# A\n\n![x](https://evil.example/a.png) [l](https://evil.example)\n\n![](slot:s)"), BookThemes.Plain, new BookHtmlOptions { Font = Font });

        Assert.NotNull(built.Html);
        Assert.DoesNotContain("evil.example", built.Html);
        Assert.DoesNotContain("<script", built.Html, StringComparison.OrdinalIgnoreCase);
        // The only address in the page is the policy's own text and the XML namespace-free inline data.
        var withoutData = Regex.Replace(built.Html, """data:[^)"']*""", string.Empty);
        Assert.Empty(Regex.Matches(withoutData, @"(?i)\b(?:https?:|//)[a-z0-9]").Select(m => m.Value));
        Assert.Contains("Content-Security-Policy", built.Html);
        Assert.Contains("default-src 'none'", built.Html);
        Assert.DoesNotMatch(@"url\((?!data:)", built.Html);
    }

    [Fact]
    public void TheTitleIsEscaped()
    {
        var built = BookHtml.Build(Book(title: "</title><script>alert(1)</script>", tagline: "<img src=x onerror=1>"), BookThemes.Plain, new BookHtmlOptions());

        Assert.DoesNotContain("<script", built.Html!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", built.Html!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;/title&gt;", built.Html!);
    }

    [Fact]
    public void ThePaperAndDirectionAreWrittenIntoTheCss_NotLeftToCustomProperties()
    {
        var a4 = BookHtml.Build(Book(), BookThemes.Plain, new BookHtmlOptions { PageSize = "A4" }).Html!;
        var letter = BookHtml.Build(Book(), BookThemes.Plain, new BookHtmlOptions { PageSize = "Letter" }).Html!;

        Assert.Contains("size: A4;", a4);
        Assert.Contains("size: Letter;", letter);
        Assert.DoesNotMatch(@"@page[^{]*\{[^}]*size:\s*var\(", a4);
        Assert.Contains("<html lang=\"en\" dir=\"ltr\">", a4);
        Assert.True(BookLayout.For("he").Rtl);
        Assert.False(BookLayout.For("ru").Rtl);
    }

    [Fact]
    public void ChaptersShareOneHeadingNamespace_AndTheContentsLinkToRealIds()
    {
        var book = new BookDocument("T", "", "en", [new BookSection("a", "# Coast\n\n## Docks"), new BookSection("b", "# Coast\n\n## Docks")], new BookLabels());

        var built = BookHtml.Build(book, BookThemes.Plain, new BookHtmlOptions());

        Assert.Equal(["coast", "docks", "coast-2", "docks-2"], built.Headings.Select(h => h.Id));
        foreach (var heading in built.Headings)
        {
            Assert.Contains($"<a href=\"#{heading.Id}\">", built.Html!);
            Assert.Contains($"id=\"{heading.Id}\"", built.Html!);
        }
    }

    [Theory]
    [InlineData("@import url(https://evil.example/x.css);p{color:red}", "evil.example")]
    [InlineData("p{background:url(https://evil.example/x.png)}", "evil.example")]
    [InlineData("@font-face{font-family:x;src:url(https://evil.example/f.woff2)}", "evil.example")]
    [InlineData("p{color:red}</style><script>alert(1)</script>", "<script")]
    [InlineData("p{behavior:url(x.htc)}", "htc")]
    [InlineData("p{width:expression(alert(1))}", "expression")]
    public void CustomCssCannotFetchOrBreakOut(string css, string forbidden)
    {
        var built = BookHtml.Build(Book(), BookThemes.Plain, new BookHtmlOptions { CustomCss = css });

        Assert.DoesNotContain(forbidden, built.Html!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomCssKeepsWhatIsHarmless()
    {
        var built = BookHtml.Build(Book(), BookThemes.Plain, new BookHtmlOptions { CustomCss = ".chapter > h1 { color: #336; } .x { background: url(data:image/png;base64,AAAA); }" });

        Assert.Contains(".chapter > h1 { color: #336; }", built.Html!);
        Assert.Contains("url(data:image/png;base64,AAAA)", built.Html!);
    }

    [Fact]
    public void ACampaignNameThatLooksLikeMarkupIsJustAName()
    {
        var actor = new ActorDocument { Id = "a1", Name = "**Bob** [[actor:a1|Leak]] :::dm" };
        var source = new BookSource(BookAudience.Party, new BookCampaign("c", "C", "srd5e", "", "", []), [actor], [], [], [], [], [], [], []);

        var built = BookHtml.Build(BookCompiler.Compile(source, "en"), BookThemes.Plain, new BookHtmlOptions { Mention = BookCompiler.Mentions(source) });

        Assert.NotNull(built.Html);
        // The name is printed as it was typed: not bold, not a mention that names itself, not a secret block.
        Assert.Contains("**Bob** [[actor:a1|Leak]] :::dm", built.Html);
        Assert.DoesNotContain("<strong>", built.Html);
        Assert.DoesNotContain("class=\"mention", built.Html);
        Assert.DoesNotContain("class=\"secret", built.Html);
    }

    [Fact]
    public void TheDmSummaryIsInTheDmBookOnlyAndIsBoxed()
    {
        var session = new SessionDocument { Id = "s", Title = "One", Recap = "Public recap.", DmSummary = "Secret plan.", Status = SessionStatus.Completed };
        BookSource Source(BookAudience audience) => new(audience, new BookCampaign("c", "C", "srd5e", "", "", []), [], [session], [], [], [], [], [], []);

        var dm = BookHtml.Build(BookCompiler.Compile(Source(BookAudience.Dm), "en"), BookThemes.Plain, new BookHtmlOptions { BoxSecrets = true }).Html!;
        var party = BookHtml.Build(BookCompiler.Compile(Source(BookAudience.Party), "en"), BookThemes.Plain, new BookHtmlOptions()).Html!;

        Assert.Contains("Secret plan.", dm);
        Assert.Contains("class=\"secret secret-dm\"", dm);
        Assert.DoesNotContain("Secret plan.", party);
        Assert.Contains("Public recap.", party);
    }

    [Fact]
    public void TheContentsHaveNoNumbersForAPreview_AReservedColumnForTheFirstPrint_AndNumbersOnlyWhereKnown()
    {
        var book = new BookDocument("Saltmere", "", "en", [new BookSection("a", "# Coast\n\n## Harbour\n\n## Cliffs")], new BookLabels());
        var ids = BookHtml.Build(book, BookThemes.Plain, new BookHtmlOptions { Font = Font }).Headings;
        var harbour = ids.Single(h => h.Text == "Harbour").Id;

        var preview = BookHtml.Build(book, BookThemes.Plain, new BookHtmlOptions { Font = Font }).Html!;
        Assert.False(preview.Contains("<span class=\"toc-page\"", StringComparison.Ordinal));

        var reserved = BookHtml.Build(book, BookThemes.Plain, new BookHtmlOptions { Font = Font, PageNumbers = new Dictionary<string, int>() }).Html!;
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(reserved, "<span class=\"toc-page\"></span>").Count);

        var filled = BookHtml.Build(book, BookThemes.Plain, new BookHtmlOptions { Font = Font, PageNumbers = new Dictionary<string, int> { [harbour] = 7 } }).Html!;
        Assert.Contains("<span class=\"toc-page\">7</span>", filled, StringComparison.Ordinal);
        // The heading with no known page shows none, not a guess.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(filled, "<span class=\"toc-page\"></span>").Count);
    }

    [Theory]
    [InlineData("Contents")]
    [InlineData("Overview")]
    [InlineData("Cast")]
    [InlineData("Gazetteer")]
    [InlineData("Chronicle")]
    [InlineData("Lore")]
    [InlineData("Pages")]
    [InlineData("Homebrew")]
    public void EveryChapterTitleIsTranslated(string label)
    {
        var key = "Campaign.Book.Label." + label;
        var english = Codex.Localization.Localizer.For("en")[key];
        Assert.Equal(label, english);
        foreach (var language in new[] { "ru", "he" })
        {
            var text = Codex.Localization.Localizer.For(language)[key];
            Assert.NotEmpty(text);
            Assert.NotEqual(english, text);
        }
    }
}
