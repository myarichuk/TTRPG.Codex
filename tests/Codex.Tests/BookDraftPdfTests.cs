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
        string pageSize = "Letter")
    {
        var shell = BookDraftHtml.Shell("Saltmere", "en", theme ?? BookDraftThemes.Plain, pageSize, css, Font);
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

    public static TheoryData<string, string> Themes()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in BookDraftThemes.All)
        {
            data.Add(theme.Id, "Letter");
            data.Add(theme.Id, "A4");
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
    public async Task EveryBlock_UnderEveryTheme_KeepsPicturesOffTheText(string themeId, string pageSize)
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
        var printed = await PrintAsync(md.ToString(), art, theme: BookDraftThemes.Find(themeId), pageSize: pageSize);
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
                    $"{themeId} {pageSize}, page {page.Number}: words on a picture ({box.Left:0},{box.Bottom:0} {box.Width:0}x{box.Height:0} of a {page.Width:0} page): {string.Join(" ", on.Take(12))}");
            }
        }

        Assert.True(portraits >= 1 && places >= 1, $"{themeId} {pageSize}: the portrait ({portraits}) and the place picture ({places}) were printed");
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
    public async Task ALongBook_UnderEveryTheme_StaysSmall_AndItsTextStaysText(string themeId, string pageSize)
    {
        var md = new StringBuilder("{{frontCover}}\n\n# Saltmere\n\n*A coast of wrecks*\n\n\\page\n\n{{chapter,#ch-lore\n# Lore\n}}\n\n");
        for (var i = 0; i < 1400; i++)
        {
            md.Append("PUBLIC_P").Append(i).Append(" The tide came in grey over the reef, and the keepers of the light wrote down every sail they saw.\n\n");
            if (i % 60 == 59) md.Append("{{note\n##### A note\nThe ledger has a page torn out.\n}}\n\n");
        }

        var printed = await PrintAsync(md.ToString(), theme: BookDraftThemes.Find(themeId), pageSize: pageSize);
        Assert.True(printed.Pages >= 30, $"{printed.Pages} pages");
        var perPage = printed.Pdf.Length / printed.Pages;
        if (Environment.GetEnvironmentVariable("CODEX_BOOK_DUMP") is { } dump) File.WriteAllBytes(Path.Combine(dump, $"{themeId}-{pageSize}.pdf"), printed.Pdf);
        Console.WriteLine($"{themeId} {pageSize}: {printed.Pages} pages, {printed.Pdf.Length / 1024} KB, {perPage / 1024} KB a page, {printed.Elapsed.TotalSeconds:0.0} s");
        Assert.True(perPage < 60 * 1024, $"{themeId} {pageSize}: {perPage / 1024} KB a page over {printed.Pages} pages");
        var text = Text(printed.Pdf);
        // Each word once: a shadow or an under-stroke is another copy of the text in the PDF ("SSSaaalll..." when searched or read aloud).
        // (Spaces aside: a letter-spaced line is read back as letters with gaps between them, which is the reader's guess, not a copy.)
        Assert.StartsWith("SaltmereAcoastofwrecks", text.Replace(" ", string.Empty));
        Assert.Contains("\nLore ", text);
        // From the second paragraph: the first one's initial is a drop cap, set apart from its word.
        for (var i = 1; i < 1400; i += 37) Assert.Contains("PUBLIC_P" + i + " ", text);
        Assert.Contains("PUBLIC_P1399", text);
    }
}
