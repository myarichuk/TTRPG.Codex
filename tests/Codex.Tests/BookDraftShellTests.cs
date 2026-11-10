using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>The editable book's page shell and generator on what the old HTML route was tested for (docs/BOOK_EXPORT.md 50 maps each one):
/// the language gate, a title that looks like markup, and the GM's summary in the GM's book only.</summary>
public class BookDraftShellTests
{
    private static byte[]? Font(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "fonts", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static string Draft(BookSource source) => string.Join("\n", BookDraftWriter.Generate(source, "en", null).Select(c => c.Markdown));

    private static string Plain(string markdown) => Regex.Replace(markdown, @"\\(.)", "$1");

    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("EN", true)]
    [InlineData("ru", true)]
    [InlineData("he", true)]
    [InlineData("he-IL", true)]
    [InlineData("de", false)]
    [InlineData("ar", false)]
    [InlineData("", false)]
    public void ThePlainThemePrintsEnglishRussianAndHebrew_AndRefusesTheRestInsteadOfPrintingThemInTheWrongFonts(string language, bool supported)
    {
        // Which faces cover which letters is BookFontCoverageTests'; this is the gate.
        Assert.Equal(supported, BookDraftThemes.Plain.Supports(language));

        var built = BookDraftHtml.Shell("Saltmere", language, BookDraftThemes.Plain, "A4", null, true, Font);

        Assert.Equal(supported, built.Html != null);
        if (!supported) Assert.Contains("does not support", built.Refused);
    }

    [Fact]
    public void TheTitleIsEscaped_InTheShell()
    {
        var built = BookDraftHtml.Shell("</title><script>alert(1)</script><img src=x onerror=1>", "en", BookDraftThemes.Plain, "A4", null, true, Font);

        Assert.DoesNotContain("<script", built.Html!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", built.Html!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;/title&gt;", built.Html!);
    }

    [Fact]
    public void ANameThatLooksLikeMarkup_IsJustAName_InTheDraft()
    {
        var actor = new ActorDocument { Id = "a1", Name = "**Bob** [[actor:a1|Leak]] :::dm {{secret" };
        var source = new BookSource(BookAudience.Party, new BookCampaign("c", "<b>C</b> {{note", "srd5e", "", "", []), [actor], [], [], [], [], [], [], []);

        var draft = Draft(source);

        // Printed as it was typed: every character that would make it bold, a mention, a block or a secret is escaped.
        Assert.Contains("**Bob** [[actor:a1|Leak]] :::dm {{secret", Plain(draft));
        Assert.DoesNotContain("**Bob**", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("[[actor:", draft, StringComparison.Ordinal);
        Assert.DoesNotContain(":::", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("{{secret", draft, StringComparison.Ordinal);
        Assert.Contains("<b>C</b> {{note", Plain(draft));
        Assert.DoesNotContain("<b>C</b>", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("{{note", draft, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGmSummary_IsInTheGmDraftOnly_AndIsASecret()
    {
        var session = new SessionDocument { Id = "s", Title = "One", Recap = "Public recap.", DmSummary = "Secret plan.", Status = SessionStatus.Completed };
        BookSource Source(BookAudience audience) => new(audience, new BookCampaign("c", "C", "srd5e", "", "", []), [], [session], [], [], [], [], [], []);

        var gm = Draft(Source(BookAudience.Dm));
        var party = Draft(Source(BookAudience.Party));

        Assert.Matches(@"\{\{secret\n[^}]*Secret plan\.", gm);
        Assert.DoesNotContain("Secret plan.", party);
        Assert.Contains("Public recap.", party);
    }

    [Theory]
    [InlineData("Contents", "Contents")]
    [InlineData("Overview", "Introduction")]
    [InlineData("Cast", "Cast")]
    [InlineData("Gazetteer", "Gazetteer")]
    [InlineData("Chronicle", "The story so far")]
    [InlineData("Lore", "Lore")]
    [InlineData("Pages", "Pages")]
    [InlineData("Homebrew", "Homebrew")]
    [InlineData("Appendix", "Appendix {0}: {1}")]
    [InlineData("AppendixLetters", "AB")]
    [InlineData("Quests", "Quests")]
    [InlineData("Factions", "Factions")]
    [InlineData("People", "People")]
    [InlineData("Places", "Places")]
    [InlineData("OtherPages", "Other pages")]
    public void EveryChapterTitleIsTranslated(string label, string words)
    {
        var key = "Campaign.Book.Label." + label;
        var english = Codex.Localization.Localizer.For("en")[key];
        Assert.Equal(words, english);
        foreach (var language in new[] { "ru", "he" })
        {
            var text = Codex.Localization.Localizer.For(language)[key];
            Assert.NotEmpty(text);
            Assert.NotEqual(english, text);
        }
    }
}
