using System.Text;
using Codex.Core.Book;
using Codex.Web.Book;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace Codex.Tests;

/// <summary>The editable book printed through the real print browser: the bundled renderer lays it out in a page with no network, and the
/// result is printed with script off (docs/BOOK_EXPORT.md 41.6).</summary>
public class BookDraftPdfTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "lib", "book", "book-render.js"));

    private static byte[]? Font(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "fonts", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static BookPdf Printer() =>
        new(Options.Create(new BookPdfOptions { TimeoutSeconds = 90, NoSandbox = BookPdfTests.NoSandbox }), NullLogger<BookPdf>.Instance);

    private static async Task<BookPdfResult> PrintAsync(string markdown, IReadOnlyDictionary<string, string>? art = null, string? css = null)
    {
        var shell = BookDraftHtml.Shell("Saltmere", "en", BookDraftThemes.Plain, "Letter", css, Font);
        Assert.NotNull(shell.Html);
        var printer = Printer();
        using var slot = printer.TryReserve("dm")!;
        return await printer.PrintAsync(slot, new BookPrintJob(shell.Html!, [], _ => shell.Html!) { Layout = new BookPrintLayout(Script, markdown, art ?? new Dictionary<string, string>()) },
            CancellationToken.None);
    }

    /// <summary>Every theme sits on Homebrewery's Blank layout, takes the book's paper size over Blank's Letter, and its page loads nothing.</summary>
    [Fact]
    public void EveryTheme_SitsOnBlank_AtTheBooksPaperSize_AndLoadsNothing()
    {
        Assert.Contains("counter-reset: page-numbers", BookDraftThemes.Blank, StringComparison.Ordinal);
        Assert.Contains("naturalcrit/homebrewery", BookDraftThemes.Blank, StringComparison.Ordinal);
        foreach (var theme in BookDraftThemes.All)
        {
            var html = BookDraftHtml.Shell("Saltmere", "en", theme, "A4", null, Font).Html!;
            var css = html[html.IndexOf("<style id=\"theme\">", StringComparison.Ordinal)..];
            var blank = css.IndexOf(".columnWrapper {", StringComparison.Ordinal);
            var size = css.IndexOf("width: 210mm; height: 297mm;", StringComparison.Ordinal);
            Assert.True(blank > 0 && size > blank, $"{theme.Id}: Blank first, then the book's page size");
            Assert.DoesNotMatch(@"@import\s*(url|['""])", css);
            Assert.Empty(System.Text.RegularExpressions.Regex.Matches(css, @"url\(\s*['""]?(?!data:)").Select(m => m.Value));
        }
    }

    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))).Normalize(NormalizationForm.FormKC);
    }

    private static int PageCount(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    [ChromiumFact]
    public async Task ADraftIsLaidOutByTheRenderer_AndPrintedWithNothingFetched()
    {
        var printed = await PrintAsync("{{chapter,#ch-cast}}\n# Cast\n\n{{entry,#e-s0123456789abcdef01234567}}\n## Vera Holloway\n\nPUBLIC_VERA keeps the lamp.\n");
        var text = Text(printed.Pdf);
        Assert.Contains("Vera Holloway", text);
        Assert.Contains("PUBLIC_VERA", text);
        Assert.Equal(0, printed.BlockedRequests);
        Assert.Equal(1, printed.Pages);
        Assert.Empty(printed.Overflow);
    }

    [ChromiumFact]
    public async Task HostileMarkupInADraft_NeitherRunsNorFetches()
    {
        var md = "# Title\n\n<script>document.body.innerText='CANARY_SCRIPT'</script>\n\n<img src=\"https://example.invalid/CANARY_IMG.png\" onerror=\"document.body.innerText='CANARY_ONERROR'\">\n\n"
            + "<div style=\"background:url(https://example.invalid/CANARY_STYLE)\">PUBLIC_STYLED</div>\n\n![x](https://example.invalid/CANARY_MD.png)\n\n"
            + "<iframe src=\"https://example.invalid/CANARY_FRAME\"></iframe>\n\nPUBLIC_END\n";
        var printed = await PrintAsync(md, css: "body { background: url(https://example.invalid/CANARY_CSS); }");
        var text = Text(printed.Pdf);
        Assert.Contains("PUBLIC_STYLED", text);
        Assert.Contains("PUBLIC_END", text);
        Assert.DoesNotContain("CANARY_SCRIPT", text);
        Assert.DoesNotContain("CANARY_ONERROR", text);
        // Nothing was even asked for: the cleaner took the addresses out before the page could try them.
        Assert.Equal(0, printed.BlockedRequests);
    }

    [ChromiumFact]
    public async Task TextLongerThanAPage_FlowsOntoPagesAfterIt_AndTheContentsSayWhere()
    {
        var sb = new StringBuilder("{{toc\n# Contents\n\n- [Lore](#ch-lore)\n}}\n\n\\page\n\n{{chapter,#ch-lore}}\n# Lore\n\n");
        for (var i = 0; i < 160; i++) sb.Append("Paragraph ").Append(i).Append(" of the long chapter, which runs on past one page and must not be clipped by it.\n\n");
        sb.Append("PUBLIC_LAST_PARAGRAPH\n");
        var printed = await PrintAsync(sb.ToString());
        Assert.True(printed.Pages >= 3, $"{printed.Pages} pages");
        Assert.Equal(printed.Pages, PageCount(printed.Pdf));
        Assert.Empty(printed.Overflow);
        var text = Text(printed.Pdf);
        Assert.Contains("PUBLIC_LAST_PARAGRAPH", text);
        Assert.Contains("Paragraph 159", text);
        // The chapter begins on the second page, and the contents line says so.
        Assert.Matches(@"Lore\s*\.*\s*2", text.Split('\n')[0]);
    }

    [ChromiumFact]
    public async Task ABlockTallerThanAPage_IsReported_NotSilentlyLost()
    {
        var sb = new StringBuilder("# Big\n\n{{note\n");
        for (var i = 0; i < 260; i++) sb.Append("Line ").Append(i).Append(" of a note that can never fit on one page.  \n");
        sb.Append("}}\n");
        var printed = await PrintAsync(sb.ToString());
        Assert.NotEmpty(printed.Overflow);
    }

    [ChromiumFact]
    public async Task AnEditionsPicture_PrintsInItsSlot_AndAnUnknownSlotPrintsNothing()
    {
        const string key = "s0123456789abcdef01234567";
        // A 1x1 PNG.
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        var printed = await PrintAsync($"# Cast\n\n{{{{portrait\n![PUBLIC_ALT](art:{key})\n}}}}\n\n{{{{portrait\n![MISSING_ALT](art:s{new string('f', 24)})\n}}}}\n\nText.\n",
            new Dictionary<string, string> { [key] = png });
        using var doc = PdfDocument.Open(printed.Pdf);
        Assert.Single(doc.GetPages().SelectMany(p => p.GetImages()));
        Assert.Equal(0, printed.BlockedRequests);
    }
}
