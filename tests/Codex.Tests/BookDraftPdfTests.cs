using System.Text;
using Codex.Core.Book;
using Codex.Web.Book;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace Codex.Tests;

/// <summary>Like <see cref="ChromiumFactAttribute"/>, for a theory.</summary>
public sealed class ChromiumTheoryAttribute : TheoryAttribute
{
    public ChromiumTheoryAttribute()
    {
        if (BookBrowser.Find(null, out _) == null && Environment.GetEnvironmentVariable("CI") == null) Skip = "No Chrome, Edge or Chromium on this machine.";
    }
}

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

    private static async Task<BookPdfResult> PrintAsync(string markdown, IReadOnlyDictionary<string, string>? art = null, string? css = null, BookTheme? theme = null,
        string pageSize = "Letter", bool homebrewery = true, BookCssWeb? web = null)
    {
        var shell = BookDraftHtml.Shell("Saltmere", "en", theme ?? BookDraftThemes.Plain, pageSize, css, homebrewery, Font, web);
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
            var html = BookDraftHtml.Shell("Saltmere", "en", theme, "A4", null, true, Font).Html!;
            var css = html[html.IndexOf("<style id=\"theme\">", StringComparison.Ordinal)..];
            var blank = css.IndexOf(".columnWrapper {", StringComparison.Ordinal);
            var size = css.IndexOf("width: 210mm; height: 297mm;", StringComparison.Ordinal);
            Assert.True(blank > 0 && size > blank, $"{theme.Id}: Blank first, then the book's page size");
            Assert.DoesNotMatch(@"@import\s*(url|['""])", css);
            // Only data: pictures, and the references inside an embedded SVG to its own filters (url(#r), written %23r).
            Assert.Empty(System.Text.RegularExpressions.Regex.Matches(css, @"url\(\s*(?>['""]?)(?!data:|#|%23)").Select(m => css.Substring(m.Index, Math.Min(60, css.Length - m.Index))));
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
        // The chapter begins on the second page, and the contents line says so: a "2" on the line of "Lore", to its right. (Positions, not the
        // order words are stored in: Chrome writes a flex line's items in its own order.)
        using var doc = PdfDocument.Open(printed.Pdf);
        var words = doc.GetPage(1).GetWords().ToList();
        var lore = words.First(w => w.Text == "Lore");
        Assert.Contains(words, w => w.Text == "2" && Math.Abs(w.BoundingBox.Bottom - lore.BoundingBox.Bottom) < 3 && w.BoundingBox.Left > lore.BoundingBox.Right);
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

    private static async Task<BookFitted> FitAsync(IReadOnlyList<BookLayoutChapter> chapters, BookTheme? theme = null, string pageSize = "Letter")
    {
        var shell = BookDraftHtml.Shell("Saltmere", "en", theme ?? BookDraftThemes.Plain, pageSize, null, true, Font);
        var printer = Printer();
        using var slot = printer.TryReserve("dm")!;
        return await printer.FitAsync(slot, shell.Html!, new BookPrintLayout(Script, string.Empty, new Dictionary<string, string>()) { Chapters = chapters }, CancellationToken.None);
    }

    private static async Task<BookPdfResult> PrintChaptersAsync(IReadOnlyList<BookLayoutChapter> chapters)
    {
        var shell = BookDraftHtml.Shell("Saltmere", "en", BookDraftThemes.Plain, "Letter", null, true, Font);
        var printer = Printer();
        using var slot = printer.TryReserve("dm")!;
        return await printer.PrintAsync(slot, new BookPrintJob(shell.Html!, [], _ => shell.Html!)
        {
            Layout = new BookPrintLayout(Script, string.Empty, new Dictionary<string, string>()) { Chapters = chapters }
        }, CancellationToken.None);
    }

    // A long chapter of sections of uneven length, each under its own heading, so headings land near the foot of a page somewhere.
    private static string LongChapter()
    {
        var sb = new StringBuilder("{{chapter,#ch-lore\n# Lore\n}}\n\n");
        for (var i = 0; i < 40; i++)
        {
            sb.Append("## Section ").Append(i).Append("\n\n");
            for (var j = 0; j < 1 + i % 4; j++)
            {
                sb.Append("Paragraph ").Append(i).Append('.').Append(j).Append(" of the long chapter, ").Append(string.Concat(Enumerable.Repeat("which runs on and on ", 3 + (i * 7 + j) % 9))).Append("to its end.\n\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>Fit pages (41.15): the breaks are written into the text where the drawing would move blocks, so the stored text lays out with
    /// nothing moved; a heading never ends a page; running it again writes the same text; and the breaks are not an edit.</summary>
    [ChromiumFact]
    public async Task FitPages_WritesTheBreaks_SoTheStoredTextPrintsWithNothingMoved_AndAgainChangesNothing()
    {
        var chapters = new List<BookLayoutChapter> { new("front", "{{frontCover}}\n\n# Saltmere\n"), new("lore", LongChapter()) };
        var fitted = await FitAsync(chapters);
        var lore = fitted.Chapters.Single(c => c.Section == "lore").Markdown;
        var breaks = lore.Split('\n').Count(l => l == @"\page {fitted}");
        Assert.True(breaks >= 3, $"{breaks} breaks written");
        Assert.Equal(0, fitted.RunOn);
        Assert.Empty(fitted.Overflow);
        Assert.Empty(fitted.Unplaced);
        Assert.Equal(chapters[0].Markdown, fitted.Chapters.Single(c => c.Section == "front").Markdown);
        Assert.Equal(Codex.Persistence.BookDraftText.Content(chapters[1].Markdown), Codex.Persistence.BookDraftText.Content(lore));

        // No page ends with a heading: the line before each break (blank lines aside) is never one.
        var lines = lore.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] != @"\page {fitted}") continue;
            var before = lines.Take(i).Last(l => l.Trim().Length > 0);
            Assert.False(before.StartsWith('#'), $"a page ends with \"{before}\"");
        }

        // Every word is still printed, in order, and on as many pages as the drawing had made.
        var printed = await PrintChaptersAsync(fitted.Chapters);
        Assert.Empty(printed.Overflow);
        Assert.Equal(fitted.Pages, printed.Pages);
        Assert.Contains("Paragraph 39.", Text(printed.Pdf));

        var again = await FitAsync(fitted.Chapters);
        Assert.Equal(lore, again.Chapters.Single(c => c.Section == "lore").Markdown);

        // After an edit that shortens the chapter, the earlier breaks are moved, not added to: the GM's own plain break stays where it was.
        var edited = lore.Replace("## Section 3\n", "\\page\n\n## Section 3\n");
        edited = string.Join('\n', edited.Split('\n').Where(l => !l.StartsWith("Paragraph 1", StringComparison.Ordinal)));
        var refit = (await FitAsync([chapters[0], new("lore", edited)])).Chapters.Single(c => c.Section == "lore").Markdown;
        Assert.Contains("\\page\n\n## Section 3\n", refit);
        Assert.Equal(Codex.Persistence.BookDraftText.Content(edited), Codex.Persistence.BookDraftText.Content(refit));
        Assert.True(refit.Split('\n').Count(l => l == @"\page {fitted}") < breaks, "fewer pages, fewer breaks");
    }

    /// <summary>A heading never ends a page: over chapters whose heading lands lower and lower, wherever the text under a heading would start
    /// the next page, the heading goes with it (some break is written right before a heading, and none right after one).</summary>
    [ChromiumFact]
    public async Task FitPages_NeverLeavesAHeadingAtTheFootOfAPage()
    {
        var chapters = Enumerable.Range(0, 40).Select(k => new BookLayoutChapter("c" + k,
            string.Concat(Enumerable.Range(0, 70 + k).Select(i => $"Filler line {i}.\n\n")) + "## Target heading\n\nThe text under the heading, " +
            string.Concat(Enumerable.Repeat("which goes on for a while ", 12)) + "and ends.\n")).ToList();
        var fitted = await FitAsync(chapters);
        Assert.Equal(0, fitted.RunOn);
        var beforeHeading = 0;
        foreach (var chapter in fitted.Chapters)
        {
            var lines = chapter.Markdown.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i] != @"\page {fitted}") continue;
                Assert.False(lines.Take(i).Last(l => l.Trim().Length > 0).StartsWith('#'), $"{chapter.Section}: a page ends with its heading");
                if (lines.Skip(i + 1).First(l => l.Trim().Length > 0) == "## Target heading") beforeHeading++;
            }
        }

        Assert.True(beforeHeading > 0, "no heading ever fell at the foot of a page: the test proves nothing");
    }

    /// <summary>A break after a styled page carries its classes, as the drawing's own continued page does; a block too tall even for a page of
    /// its own is reported with its chapter and first words, by Fit pages and by printing.</summary>
    [ChromiumFact]
    public async Task FitPages_KeepsAStyledPagesClasses_AndReportsABlockTooTallWithItsChapter()
    {
        var styled = "Opening.\n\n\\page {reliquary}\n\n" + LongChapter();
        var big = new StringBuilder("# Big\n\nBefore the note.\n\n{{note\n");
        for (var i = 0; i < 260; i++) big.Append("Line ").Append(i).Append(" of a note that can never fit on one page.  \n");
        big.Append("}}\n");
        var fitted = await FitAsync([new("lore", styled), new("big", big.ToString())]);
        var lore = fitted.Chapters.Single(c => c.Section == "lore").Markdown;
        Assert.Contains(@"\page {reliquary,fitted}", lore);
        Assert.DoesNotContain(lore.Split('\n'), l => l == @"\page {fitted}");
        var tall = Assert.Single(fitted.Overflow);
        Assert.Equal("big", tall.Section);
        Assert.StartsWith("Line 0 of a note", tall.Words);
        // The note was moved to a page of its own first: a break before it, after the text above it.
        Assert.Contains("Before the note.\n\n\\page {fitted}\n\n{{note", fitted.Chapters.Single(c => c.Section == "big").Markdown);

        var printed = await PrintChaptersAsync(fitted.Chapters);
        var cut = Assert.Single(printed.Overflow);
        Assert.Equal(("big", tall.Page), (cut.Section, cut.Page));
        Assert.Equal(cut.Page + ":big", Codex.Web.BookEndpoints.OverflowHeader(printed.Overflow));
    }

    /// <summary>A brew that uses what Homebrewery's 5e layout styles: headings of every level, the first paragraph's drop cap, lists, a quote, a
    /// note, a descriptive box, a table, a stat block, a column break, a quote with its attribution, a footnote, a wide block and the contents. Every word to compare is a PARITY_ marker.</summary>
    private static string ParityBrew()
    {
        var md = new StringBuilder("{{toc,wide\n# Contents\n\n- [Lore](#ch-lore)\n}}\n\n\\page\n\n# PARITY_H1 Lore\n\n");
        md.Append("The PARITY_P0 tide came in grey over the reef, and the keepers of the light wrote down every sail they saw.\n\n");
        var n = 1;
        void Para(int count)
        {
            for (var i = 0; i < count; i++, n++) md.Append("A PARITY_P").Append(n).Append(" keeper wrote that the ships passed in the night, and that the lamp burned until the oil ran out at dawn.\n\n");
        }

        Para(4);
        md.Append("## PARITY_H2 The Reef\n\n");
        Para(2);
        md.Append("### PARITY_H3 Wrecks\n\n- PARITY_L1 the Gannet\n- PARITY_L2 the Tern\n\n1. PARITY_O1 first\n2. PARITY_O2 second\n\n#### PARITY_H4 Salvage\n\n");
        Para(2);
        md.Append("##### PARITY_H5 Rumours\n\n");
        md.Append("{{note\n##### PARITY_N0 A note\nPARITY_N1 The ledger has a page torn out, and the keeper will not say who took it.\n}}\n\n");
        Para(3);
        md.Append("{{descriptive\nPARITY_D1 The lamp room smells of oil and salt, and the glass is cracked in three places.\n}}\n\n");
        md.Append("##### PARITY_TC Ships lost\n| PARITY_TH Ship | Year |\n|:---|:---:|\n| PARITY_T1 Gannet | 1311 |\n| PARITY_T2 Tern | 1314 |\n| PARITY_T3 Skua | 1320 |\n\n");
        Para(3);
        md.Append("{{monster,frame\n## PARITY_M0 Reef Gull\n*Small beast, unaligned*\n___\n**Armor Class** PARITY_M1 12\n**Hit Points** PARITY_M2 4 (1d6)\n___\n");
        md.Append("|STR|DEX|CON|INT|WIS|CHA|\n|:---:|:---:|:---:|:---:|:---:|:---:|\n|6 (-2)|14 (+2)|10 (+0)|2 (-4)|12 (+1)|5 (-3)|\n___\n");
        md.Append("***Keen Sight.*** PARITY_M3 The gull has advantage on Wisdom checks that rely on sight.\n### PARITY_M4 Actions\n***Beak.*** PARITY_M5 Melee attack: +4 to hit.\n}}\n\n");
        md.Append("> PARITY_Q1 Every light on this coast was lit by a keeper who drowned.\n\n");
        Para(6);
        md.Append("\\column\n\nPARITY_C1 After the column break.\n\n");
        md.Append("{{quote\nPARITY_QQ1 The sea keeps what the lamp forgets, and gives it back at low tide.\n\n{{attribution PARITY_QA Keeper Holloway}}\n}}\n\n");
        md.Append("{{footnote PARITY_FN Part 1 | Lore}}\n\n{{wide\nPARITY_W1 A wide block runs across both columns of the page.\n}}\n\n");
        Para(30);
        return md.ToString();
    }

    private static Dictionary<string, (int Page, double X, double Y)> Markers(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var found = new Dictionary<string, (int, double, double)>();
        foreach (var page in doc.GetPages())
        {
            foreach (var w in page.GetWords().Where(w => w.Text.Contains("PARITY_", StringComparison.Ordinal)))
            {
                var key = w.Text[w.Text.IndexOf("PARITY_", StringComparison.Ordinal)..];
                found.TryAdd(key, (page.Number, w.BoundingBox.Left, w.BoundingBox.Bottom));
            }
        }

        return found;
    }

    /// <summary>
    /// The point of the Homebrewery layer (docs/BOOK_EXPORT.md 41.12): a brew lays out under it, word for word, where it does under Homebrewery's
    /// own 5e PHB theme. The reference is that theme compiled whole (<c>Fixtures/homebrewery-5e-reference.css</c>, its look and all; its pictures
    /// load nowhere, and move nothing) pasted as the GM's stylesheet over no layer, as a GM would paste it, with the same faces.
    /// </summary>
    /// And so with Homebrewery's 5e DMG theme, which sits on the PHB one: its CSS (<c>Fixtures/homebrewery-5e-dmg.css</c>) pasted on the layer lays
    /// the brew out as the DMG theme does in Homebrewery.
    [ChromiumTheory]
    [InlineData("Letter", null)]
    [InlineData("A4", null)]
    [InlineData("Letter", "homebrewery-5e-dmg.css")]
    public async Task UnderTheHomebreweryLayer_ABrewLaysOutAsUnderHomebrewerysOwnTheme(string pageSize, string? onTop)
    {
        var reference = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Fixtures", "homebrewery-5e-reference.css"));
        var extra = onTop == null ? string.Empty : "\n" + File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Fixtures", onTop));
        var faces = new StringBuilder();
        foreach (var face in BookDraftThemes.LayerFaces)
        {
            faces.Append("@font-face{font-family:'").Append(face.Family).Append("';font-weight:").Append(face.Weight).Append(";font-style:").Append(face.Style)
                .Append(";src:url(data:font/woff2;base64,").Append(Convert.ToBase64String(Font(face.File)!)).Append(") format('woff2')}\n");
        }

        var brew = ParityBrew();
        var theirs = Markers((await PrintAsync(brew, css: faces + reference + extra, theme: BookDraftThemes.HomebreweryLayout, pageSize: pageSize, homebrewery: false)).Pdf);
        var ours = Markers((await PrintAsync(brew, css: extra.Length == 0 ? null : extra, theme: BookDraftThemes.HomebreweryLayout, pageSize: pageSize)).Pdf);

        Assert.True(theirs.Count >= 60, $"{theirs.Count} markers found under Homebrewery's theme");
        Assert.Equal(theirs.Keys.Order(), ours.Keys.Order());
        var off = theirs.Where(t => t.Value.Page != ours[t.Key].Page || Math.Abs(t.Value.X - ours[t.Key].X) > 1 || Math.Abs(t.Value.Y - ours[t.Key].Y) > 1)
            .Select(t => $"{t.Key}: page {t.Value.Page} ({t.Value.X:0.0},{t.Value.Y:0.0}) there, page {ours[t.Key].Page} ({ours[t.Key].X:0.0},{ours[t.Key].Y:0.0}) here").ToList();
        Assert.True(off.Count == 0, $"{pageSize} {onTop}: {off.Count} of {theirs.Count} words moved:\n" + string.Join("\n", off.Take(20)));
    }

    /// <summary>A stylesheet that imports a theme from the web, as Tasha's Style and Google Fonts are imported (docs/BOOK_EXPORT.md 41.14),
    /// prints with that theme's face and pictures once the server has copies: the imported sheet's own addresses are relative to it. Without the
    /// copies it prints as it always did, in the book's own faces and with no picture.</summary>
    [ChromiumFact]
    public async Task AnImportedThemesFaceAndPictures_Print_FromTheServersCopies()
    {
        const string css = "@import url('https://themes.example/tasha-like/style.css');";
        var web = new BookCssWeb([
            new BookCssWebFile("https://themes.example/tasha-like/style.css", BookCssWebKind.Sheet, "text/css", Encoding.UTF8.GetBytes(
                "@import 'fonts.css';\n.page{background-image:url(img/tex.png);background-repeat:no-repeat;background-size:100% 100%}")),
            new BookCssWebFile("https://themes.example/tasha-like/fonts.css", BookCssWebKind.Sheet, "text/css", Encoding.UTF8.GetBytes(
                "@font-face{font-family:'Web Face';src:url('face.woff2') format('woff2')}\n.page h1{font-family:'Web Face'}")),
            new BookCssWebFile("https://themes.example/tasha-like/face.woff2", BookCssWebKind.Font, "font/woff2", Font("pirata-one/pirata-one-latin-400-normal.woff2")!),
            new BookCssWebFile("https://themes.example/tasha-like/img/tex.png", BookCssWebKind.Picture, "image/png",
                File.ReadAllBytes(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "favicon.png")))
        ]);
        const string brew = "# WEBFACE Heading\n\nBody text.";
        var theme = BookDraftThemes.Find("homebrewery")!;

        var with = (await PrintAsync(brew, css: css, theme: theme, web: web)).Pdf;
        var without = (await PrintAsync(brew, css: css, theme: theme, web: new BookCssWeb())).Pdf;

        // The face, as the GM could have written it into the sheet by hand.
        var byHand = (await PrintAsync(brew, theme: theme, css: "@font-face{font-family:'Web Face';src:url(data:font/woff2;base64,"
            + Convert.ToBase64String(Font("pirata-one/pirata-one-latin-400-normal.woff2")!) + ")}\n.page h1{font-family:'Web Face'}")).Pdf;

        // Chrome writes these faces as Type3, so a face is told by the width it sets the word at.
        static (double Width, int Pictures) Read(byte[] pdf)
        {
            using var doc = PdfDocument.Open(pdf);
            var page = doc.GetPage(1);
            return (page.GetWords().First(w => w.Text == "WEBFACE").BoundingBox.Width, page.GetImages().Count());
        }

        var (width, pictures) = Read(with);
        var (plainWidth, noPictures) = Read(without);
        Assert.Equal(Read(byHand).Width, width, 0.5);
        Assert.True(Math.Abs(width - plainWidth) > 5, $"{width:0.0} with the copies, {plainWidth:0.0} without");
        Assert.Equal((1, 0), (pictures, noPictures));
    }

    private static string Hex(UglyToad.PdfPig.Graphics.Colors.IColor? color)
    {
        if (color == null) return "";
        var (r, g, b) = color.ToRGBValues();
        return $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
    }

    private static (HashSet<string> Headings, HashSet<string> Fills) Colours(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var pages = doc.GetPages().ToList();
        // Above the page's foot: the running footer repeats the chapter's title there, in the footnote's colour (41.24).
        var headings = pages.SelectMany(p => p.GetWords().Where(w => w.BoundingBox.Bottom > p.Height * 0.08)).Where(w => w.Text is "PARITY_H1" or "PARITY_H2" or "PARITY_H3" or "PARITY_M0")
            .Select(w => Hex(w.Letters[0].Color)).ToHashSet();
        var fills = pages.SelectMany(p => p.Paths).Where(p => p.IsFilled).Select(p => Hex(p.FillColor)).ToHashSet();
        return (headings, fills);
    }

    /// <summary>Homebrewery's colour variables (<c>--HB_Color_*</c>), set in a brew's CSS as Homebrewery's users set them, colour the book as they
    /// colour Homebrewery's: under the layout-only theme and under Plain, on the layer or off it. (Ashen Vellum keeps its own palette.) And Plain,
    /// set nothing, keeps its own colours on the layer, not the layer's greys.</summary>
    [ChromiumTheory]
    [InlineData("homebrewery", true)]
    [InlineData("plain", true)]
    [InlineData("plain", false)]
    public async Task HomebrewerysColourVariables_SetInABrewsCss_ColourTheBook(string themeId, bool homebrewery)
    {
        const string css = ":root{--HB_Color_HeaderText:#C00000;--HB_Color_Accent:#00C000;--HB_Color_MonsterStatBackground:#0000C0;--HB_Color_Background:#FFF0C0}";
        var theme = BookDraftThemes.Find(themeId)!;

        var set = Colours((await PrintAsync(ParityBrew(), css: css, theme: theme, homebrewery: homebrewery)).Pdf);

        Assert.Equal(["#C00000"], set.Headings);
        Assert.Contains("#00C000", set.Fills);
        Assert.Contains("#0000C0", set.Fills);
        Assert.Contains("#FFF0C0", set.Fills);

        if (themeId != "plain") return;
        var plain = Colours((await PrintAsync(ParityBrew(), theme: theme, homebrewery: homebrewery)).Pdf);
        Assert.Equal(["#5A1414"], plain.Headings);
        Assert.Contains("#F3EEE4", plain.Fills);
        Assert.Contains("#FBF6EC", plain.Fills);
        Assert.DoesNotContain(plain.Fills, f => f.Length == 7 && f[1..3] == f[3..5] && f[3..5] == f[5..7] && f is not "#FFFFFF" and not "#000000" and not "#1F1F1F");
    }

    /// <summary>A theme with faces of its own prints in them alone: the layer's faces are for a brew's CSS and the layout-only theme, and an
    /// element the theme forgot would otherwise come out in Homebrewery's.</summary>
    private static void OwnFacesOnly(PdfDocument doc, string themeId, string what)
    {
        if (BookDraftThemes.Find(themeId)!.Fonts.Count == 0) return;
        var layerFaces = new[] { "Bookinsanity", "Scaly", "MrEaves", "Nodesto", "Solbera", "Overpass", "Walter" };
        var stray = doc.GetPages().SelectMany(p => p.Letters).Where(l => l.Value.Trim().Length > 0)
            .Select(l => l.FontName ?? string.Empty).Distinct().Where(f => layerFaces.Any(x => f.Contains(x, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(stray.Count == 0, $"{themeId} {what}: printed in the layer's faces: {string.Join(", ", stray)}");
    }

    /// <summary>Every theme at both paper sizes on the Homebrewery layer (the default), and at Letter without it.</summary>
    public static TheoryData<string, string, bool> Themes()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var theme in BookDraftThemes.All)
        {
            data.Add(theme.Id, "Letter", true);
            data.Add(theme.Id, "A4", true);
            data.Add(theme.Id, "Letter", false);
        }

        return data;
    }

    /// <summary>A PNG of the given size, one flat colour.</summary>
    private static string Png(int width, int height, byte shade)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width * 3; x++) raw[y * (1 + width * 3) + 1 + x] = shade;
        }

        using var zipped = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(zipped, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(raw);
        var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            o.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            o.Write(t);
            o.Write(data);
            var crc = BookArtTests.Crc(t.Concat(data).ToArray());
            o.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        }

        Chunk("IHDR", [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 2, 0, 0, 0]);
        Chunk("IDAT", zipped.ToArray());
        Chunk("IEND", []);
        return "data:image/png;base64," + Convert.ToBase64String(o.ToArray());
    }

    /// <summary>
    /// The block contract under every theme (section 40, finding 1): a chapter with its art, a portrait on the chapter page, text running onto
    /// the next page, a place picture there, and the boxed blocks. No printed word may sit on a portrait or a place picture. (The chapter art
    /// is a banner the title is printed over on purpose, so it is left out: it is the one picture as wide as the page's text.)
    /// </summary>
    [ChromiumTheory]
    [MemberData(nameof(Themes))]
    public async Task EveryBlock_UnderEveryTheme_KeepsPicturesOffTheText(string themeId, string pageSize, bool homebrewery)
    {
        const string banner = "s000000000000000000000001", face = "s000000000000000000000002", place = "s000000000000000000000003";
        var md = new StringBuilder($"{{{{chapter,#ch-cast\n# Cast\n}}}}\n\n{{{{chapter-art\n![The cast](art:{banner})\n}}}}\n\n");
        md.Append($"{{{{entry,#e-{face}}}}}\n## Vera Holloway\n\n{{{{portrait\n![Vera](art:{face})\n}}}}\n\n");
        for (var i = 0; i < 30; i++) md.Append("PUBLIC_VERA_").Append(i).Append(" keeps the Greywater lamp and a ledger of every ship that passes the reef at night.\n\n");
        md.Append("{{note\n##### A note\nThe keeper knows more than she says.\n}}\n\n{{descriptive\nThe lamp room smells of oil and salt.\n}}\n\n");
        md.Append("{{secret\nShe hid the torn page in the lamp room.\n}}\n\n| Ship | Lost |\n|---|---|\n| Gannet | 1311 |\n| Tern | 1314 |\n\n");
        md.Append($"{{{{entry,#e-{place}}}}}\n## The Greywater light\n\n{{{{place\n![The light](art:{place})\n}}}}\n\n");
        for (var i = 0; i < 30; i++) md.Append("PUBLIC_LIGHT_").Append(i).Append(" stands on the last rock of the reef, white by day and burning all night.\n\n");
        var art = new Dictionary<string, string> { [banner] = Png(80, 30, 90), [face] = Png(30, 40, 140), [place] = Png(80, 40, 190) };
        var printed = await PrintAsync(md.ToString(), art, theme: BookDraftThemes.Find(themeId), pageSize: pageSize, homebrewery: homebrewery);
        Assert.Empty(printed.Overflow);
        Assert.Equal(0, printed.BlockedRequests);

        using var doc = PdfDocument.Open(printed.Pdf);
        int portraits = 0, places = 0;
        foreach (var page in doc.GetPages())
        {
            // The chapter's title is printed over its banner and ribbon on purpose; every other word must keep off every picture.
            var words = page.GetWords().Where(w => w.Text.Trim().Length > 0 && w.Text != "Cast").ToList();
            foreach (var image in page.GetImages())
            {
                var box = image.Bounds;
                // The chapter's banner is as wide as the text or wider; the paper's textures are the page or a tile of it.
                if (box.Width > page.Width * 0.8 || box.Width < 20) continue;
                if (box.Height / box.Width is > 1.2 and < 1.45) portraits++;
                if (box.Width / box.Height is > 1.7 and < 2.3) places++;
                var on = words.Where(w => w.BoundingBox.Left < box.Right - 1 && w.BoundingBox.Right > box.Left + 1
                    && w.BoundingBox.Bottom < box.Top - 1 && w.BoundingBox.Top > box.Bottom + 1).Select(w => w.Text).ToList();
                Assert.True(on.Count == 0,
                    $"{themeId} {pageSize} {(homebrewery ? "layer" : "no layer")}, page {page.Number}: words on a picture ({box.Left:0},{box.Bottom:0} {box.Width:0}x{box.Height:0} of a {page.Width:0} page): {string.Join(" ", on.Take(12))}");
            }
        }

        // The running footer: every page names its chapter at its foot, below the text, and no word of the text runs into it.
        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().Where(w => w.Text.Trim().Length > 0).ToList();
            var foot = words.Where(w => string.Equals(w.Text, "Cast", StringComparison.OrdinalIgnoreCase) && w.BoundingBox.Top < page.Height * 0.08).ToList();
            var where = $"{themeId} {pageSize} {(homebrewery ? "layer" : "no layer")}, page {page.Number}";
            var shown = string.Join(" ", words.Where(w => w.BoundingBox.Top < page.Height * 0.08).Select(w => w.Text));
            Assert.True(foot.Count == 1, $"{where}: the foot of the page reads \"{shown}\"");
            var box = foot[0].BoundingBox;
            var lowest = words.Where(w => w != foot[0] && !w.Text.All(c => char.IsDigit(c) || c == '†')).Min(w => w.BoundingBox.Bottom);
            Assert.True(lowest > box.Top, $"{where}: the text comes down to {lowest:0}, onto the footer at {box.Bottom:0}-{box.Top:0} (\"{shown}\")");
        }

        OwnFacesOnly(doc, themeId, $"{pageSize} {(homebrewery ? "layer" : "no layer")}");
        Assert.True(portraits >= 1 && places >= 1, $"{themeId} {pageSize} {(homebrewery ? "layer" : "no layer")}: the portrait ({portraits}) and the place picture ({places}) were printed");
        var text = Text(printed.Pdf);
        Assert.Contains("PUBLIC_VERA_29", text);
        Assert.Contains("PUBLIC_LIGHT_29", text);
        Assert.Contains("Greywater", text);
    }

    /// <summary>
    /// A long book under every theme (section 13): the paper's textures are stored once, not once a page, and the text stays text (filters and
    /// blend modes near text can turn it into a picture, section 14). Prints the size, so a theme that grows it can be seen.
    /// </summary>
    [ChromiumTheory]
    [MemberData(nameof(Themes))]
    public async Task ALongBook_UnderEveryTheme_StaysSmall_AndItsTextStaysText(string themeId, string pageSize, bool homebrewery)
    {
        var md = new StringBuilder("{{frontCover}}\n\n# Saltmere\n\n*A coast of wrecks*\n\n\\page\n\n{{toc,wide\n# Contents\n\n- [Lore](#ch-lore)\n}}\n\n\\page\n\n{{chapter,#ch-lore\n# Lore\n}}\n\n");
        for (var i = 0; i < 1400; i++)
        {
            md.Append("PUBLIC_P").Append(i).Append(" The tide came in grey over the reef, and the keepers of the light wrote down every sail they saw.\n\n");
            if (i % 60 == 59) md.Append("{{note\n##### A note\nThe ledger has a page torn out.\n}}\n\n");
        }

        var printed = await PrintAsync(md.ToString(), theme: BookDraftThemes.Find(themeId), pageSize: pageSize, homebrewery: homebrewery);
        Assert.True(printed.Pages >= 30, $"{printed.Pages} pages");
        var perPage = printed.Pdf.Length / printed.Pages;
        if (Environment.GetEnvironmentVariable("CODEX_BOOK_DUMP") is { } dump) File.WriteAllBytes(Path.Combine(dump, $"{themeId}-{pageSize}-{homebrewery}.pdf"), printed.Pdf);
        Console.WriteLine($"{themeId} {pageSize} {(homebrewery ? "layer" : "no layer")}: {printed.Pages} pages, {printed.Pdf.Length / 1024} KB, {perPage / 1024} KB a page, {printed.Elapsed.TotalSeconds:0.0} s");
        Assert.True(perPage < 60 * 1024, $"{themeId} {pageSize} {(homebrewery ? "layer" : "no layer")}: {perPage / 1024} KB a page over {printed.Pages} pages");
        using (var printedDoc = PdfDocument.Open(printed.Pdf)) OwnFacesOnly(printedDoc, themeId, $"{pageSize} {(homebrewery ? "layer" : "no layer")}");
        var text = Text(printed.Pdf);
        // Each word once: a shadow or an under-stroke is another copy of the text in the PDF ("SSSaaalll..." when searched or read aloud).
        // (Spaces aside: a letter-spaced line is read back as letters with gaps between them, which is the reader's guess, not a copy.)
        // (Case aside too: Homebrewery's layout sets a cover's title in capitals, and a theme may.)
        Assert.StartsWith("SaltmereAcoastofwrecks", text.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\nLore ", text);
        // From the second paragraph: the first one's initial is a drop cap, set apart from its word.
        for (var i = 1; i < 1400; i += 37) Assert.Contains("PUBLIC_P" + i + " ", text);
        Assert.Contains("PUBLIC_P1399", text);
    }
}
