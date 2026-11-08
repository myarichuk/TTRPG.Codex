using System.Text;
using Codex.Core.Book;
using Codex.Web.Book;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Codex.Persistence;
using UglyToad.PdfPig;

namespace Codex.Tests;

/// <summary>
/// Russian and Hebrew books (docs/BOOK_EXPORT.md 41.21): every element of a draft in each theme that declares the language is drawn in a bundled
/// face, asked of the browser itself (a screenshot would not show it: the Mac's system fonts look fine, and Linux has others); a Hebrew page reads
/// from the right, the theme's sheets mirrored; and both languages print to a PDF whose words are the draft's.
/// </summary>
public class BookDraftScriptsTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "lib", "book", "book-render.js"));

    private static byte[]? Font(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "fonts", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // The sample pages the theme wizard previews on (cover, contents, every block the themes style), in each language.
    private static IReadOnlyDictionary<string, BookThemeSample.Words> Text => BookThemeSample.Text;

    private static string Draft(string language) => BookThemeSample.Markdown(language);

    public static TheoryData<string, string> ThemesAndLanguages()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in BookDraftThemes.All.Where(t => t.Fonts.Count > 0))
        {
            foreach (var language in new[] { "en", "ru", "he" }.Where(theme.Supports)) data.Add(theme.Id, language);
        }

        return data;
    }

    private static Task<(IPlaywright Playwright, IBrowser Browser, IPage Page)> DrawAsync(string themeId, string language) =>
        DrawAsync(BookDraftThemes.Find(themeId)!, language, true);

    private static async Task<(IPlaywright Playwright, IBrowser Browser, IPage Page)> DrawAsync(BookTheme theme, string language, bool homebrewery)
    {
        var shell = BookDraftHtml.Shell("Saltmere", language, theme, "A4", null, homebrewery, Font);
        Assert.NotNull(shell.Html);
        Assert.Empty(shell.MissingFonts);
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync();
        var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 900, Height = 1300 } });
        var page = await context.NewPageAsync();
        // The shell's policy refuses script; the renderer is put in by the browser's own evaluate, as the print browser does.
        await page.SetContentAsync(shell.Html!);
        await page.EvaluateAsync(Script);
        await page.EvaluateAsync("async md => { await CodexBook.layout(document, document.getElementById('brew'), md, {}); }", Draft(language));
        return (playwright, browser, page);
    }

    [ChromiumTheory]
    [MemberData(nameof(ThemesAndLanguages))]
    public async Task EveryElement_IsDrawnInABundledFace_NeverASystemOne(string themeId, string language)
    {
        var (playwright, browser, page) = await DrawAsync(themeId, language);
        try
        {
            var system = await SystemFontsAsync(page);
            Assert.True(system.Count == 0, $"{themeId} ({language}) draws in system fonts:\n" + string.Join("\n", system.Distinct()));
        }
        finally
        {
            await browser.DisposeAsync();
            playwright.Dispose();
        }
    }

    /// <summary>Which elements of the drawn sample a system font draws, as the browser itself reports (every kind of element must be measured).</summary>
    private static async Task<List<string>> SystemFontsAsync(IPage page)
    {
        {
            string[] selectors = [".page:has(.frontCover) h1", ".page:has(.frontCover) p", ".toc h1", ".toc li", ".page h1", ".page h2", ".page h3", ".page h5",
                ".page p", ".page p strong", ".page p em", ".page .note p", ".page .descriptive p", ".page .secret p", ".page .monster", ".page td", ".page th", ".page li", ".pageNumber"];
            var marked = await page.EvaluateAsync<int[]>(@"selectors => selectors.map((selector, k) => {
                let n = 0;
                for (const el of [...document.querySelectorAll(selector)].slice(0, 3))
                    for (const e of [el, ...el.querySelectorAll('*')])
                        // The element itself always (a page number is drawn by its ::after), and every element in it that holds text of its own.
                        if (e === el || [...e.childNodes].some(c => c.nodeType === 3 && c.textContent.trim())) { e.setAttribute('data-face-check', ((e.getAttribute('data-face-check') || '') + ' s' + k).trim()); n++; }
                return n; })", selectors);
            var cdp = await page.Context.NewCDPSessionAsync(page);
            await cdp.SendAsync("DOM.enable");
            await cdp.SendAsync("CSS.enable");
            var root = (await cdp.SendAsync("DOM.getDocument", new Dictionary<string, object> { ["depth"] = 0 }))!.Value.GetProperty("root").GetProperty("nodeId").GetInt32();
            var system = new List<string>();
            var unmeasured = new List<string>();
            var seen = 0;
            // The browser reports the faces of a node's own text only: each selector's first three matches are measured through the elements
            // in them that hold text themselves (a contents entry's words are in a link's spans, not in the li).
            for (var k = 0; k < selectors.Length; k++)
            {
                var found = await cdp.SendAsync("DOM.querySelectorAll", new Dictionary<string, object> { ["nodeId"] = root, ["selector"] = $"[data-face-check~='s{k}']" });
                var faces = 0;
                foreach (var node in found!.Value.GetProperty("nodeIds").EnumerateArray())
                {
                    var fonts = await cdp.SendAsync("CSS.getPlatformFontsForNode", new Dictionary<string, object> { ["nodeId"] = node.GetInt32() });
                    foreach (var font in fonts!.Value.GetProperty("fonts").EnumerateArray())
                    {
                        seen++;
                        faces++;
                        if (!font.GetProperty("isCustomFont").GetBoolean()) system.Add($"{selectors[k]}: {font.GetProperty("familyName").GetString()}");
                    }
                }

                if (faces == 0) unmeasured.Add($"{selectors[k]} ({marked[k]} elements with text)");
            }

            Assert.True(unmeasured.Count == 0, "nothing measured for: " + string.Join(", ", unmeasured));
            return system;
        }
    }

    /// <summary>
    /// A theme the GM brought in (41.22) whose faces have Latin letters only: with the stand-ins the wizard proposes, a Hebrew book draws every
    /// element in a bundled face (the stand-ins are declared after the theme's own faces, which have no range, and still win for Hebrew); with
    /// none chosen, the Hebrew is left to a system font, which is what the check would then say.
    /// </summary>
    [ChromiumTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APastedThemeWithLatinFacesOnly_PrintsHebrewInItsStandIns(bool standIns)
    {
        string Face(string family, string file, string style = "normal") =>
            $"@font-face {{ font-family: '{family}'; font-style: {style}; font-weight: 400; src: url(data:font/woff2;base64,{Convert.ToBase64String(Font(file)!)}) format('woff2'); }}\n";
        var css = Face("Saltmere Text", "im-fell-english/im-fell-english-latin-400-normal.woff2") + Face("Saltmere Text", "im-fell-english/im-fell-english-latin-400-italic.woff2", "italic")
            + Face("Saltmere Titles", "pirata-one/pirata-one-latin-400-normal.woff2")
            + ".page { font-family: 'Saltmere Text', serif; } .page :is(h1, h2, h3, h4, h5, h6) { font-family: 'Saltmere Titles', serif; }";
        var faces = BookThemeCatalog.Measure(css, false, new BookCssWeb(), Font);
        var he = faces.Languages.Single(l => l.Language == "he");
        Assert.Equal(["Saltmere Text", "Saltmere Titles"], he.Lacking.Order());
        var chosen = new Dictionary<string, Dictionary<string, string>>
        {
            ["he"] = standIns ? he.Lacking.ToDictionary(f => f, f => BookStandIns.Propose(f, "he")!.Id) : []
        };
        var theme = BookThemeCatalog.Build("custom:test", "Saltmere", css, false, ["en", "he"], chosen, new BookCssWeb(), "he", Font).Theme;

        var (playwright, browser, page) = await DrawAsync(theme, "he", false);
        try
        {
            var system = await SystemFontsAsync(page);
            if (standIns) Assert.True(system.Count == 0, "drawn in system fonts:\n" + string.Join("\n", system.Distinct()));
            else Assert.NotEmpty(system);
        }
        finally
        {
            await browser.DisposeAsync();
            playwright.Dispose();
        }
    }

    [ChromiumTheory]
    [InlineData("plain")]
    [InlineData("ashen-vellum")]
    public async Task AHebrewPage_ReadsFromTheRight(string themeId)
    {
        var (playwright, browser, page) = await DrawAsync(themeId, "he");
        try
        {
            var layout = await page.EvaluateAsync<double[]>(@"() => {
                const pages = [...document.querySelectorAll('.page')], chapter = pages.find(p => p.querySelector('.block.chapter'));
                const box = p => p.getBoundingClientRect(), middle = r => r.left + r.width / 2;
                const firstText = chapter.querySelector('.columnWrapper > p');
                const li = chapter.querySelector('li'), list = li.parentElement.getBoundingClientRect(), item = li.getBoundingClientRect();
                const odd = pages[2].querySelector('.pageNumber'), even = pages[1].querySelector('.pageNumber');
                const cap = getComputedStyle(firstText, '::first-letter').float;
                const tracked = [...document.querySelectorAll('.page *')].filter(e => !['normal', '0px'].includes(getComputedStyle(e).letterSpacing)).length;
                return [tracked, middle(box(firstText)) - middle(box(chapter)), (list.right - item.right) - (item.left - list.left),
                    odd ? middle(box(odd)) - middle(box(pages[2])) : 0, even ? middle(box(even)) - middle(box(pages[1])) : 0, cap === 'right' ? 1 : cap === 'none' ? 0 : -1];
            }");
            // The chapter's first paragraph is in the right-hand column, a list's bullets have their room on its right (outside markers stand in
            // the list's padding, beside the item's box), an odd page's number is on
            // the left (the outer edge of a right-bound book) and an even page's on the right, and a drop cap, where the theme has one, floats right.
            // And no Hebrew is letter-spaced: Ashen Vellum's tracked cover and small-caps lines pulled its letters apart into broken-looking words.
            Assert.True(layout[0] == 0, layout[0] + " elements are letter-spaced");
            layout = layout[1..];
            Assert.True(layout[0] > 0, "first paragraph not in the right column: " + layout[0]);
            Assert.True(layout[1] > 4, "the list's bullet room is not on its right: " + layout[1]);
            Assert.True(layout[2] < 0, "an odd page's number is not on the left: " + layout[2]);
            Assert.True(layout[3] > 0, "an even page's number is not on the right: " + layout[3]);
            Assert.True(layout[4] >= 0, "a drop cap floats left");
        }
        finally
        {
            await browser.DisposeAsync();
            playwright.Dispose();
        }
    }

    private static string Bare(string word) => word.Trim('.', ',', '«', '»', '"', '“', '”');

    [ChromiumTheory]
    [InlineData("plain", "ru")]
    [InlineData("plain", "he")]
    [InlineData("ashen-vellum", "ru")]
    [InlineData("ashen-vellum", "he")]
    public async Task ARussianOrHebrewDraft_Prints_WithItsWordsInThePdf(string themeId, string language)
    {
        var shell = BookDraftHtml.Shell("Saltmere", language, BookDraftThemes.Find(themeId)!, "A4", null, true, Font);
        var printer = new BookPdf(Options.Create(new BookPdfOptions { TimeoutSeconds = 90, NoSandbox = BookPdfTests.NoSandbox }), NullLogger<BookPdf>.Instance);
        using var slot = printer.TryReserve("dm")!;
        var printed = await printer.PrintAsync(slot, new BookPrintJob(shell.Html!, [], _ => shell.Html!)
            { Layout = new BookPrintLayout(Script, Draft(language), new Dictionary<string, string>()) }, CancellationToken.None);

        Assert.Empty(printed.Overflow);
        Assert.Equal(0, printed.BlockedRequests);
        using var doc = PdfDocument.Open(printed.Pdf);
        // Words as a reader extracts them; a Hebrew word may come out in drawing order (reversed), which is the same word for this check.
        var words = doc.GetPages().SelectMany(p => p.GetWords()).Select(x => Bare(x.Text.Normalize(NormalizationForm.FormKC))).ToHashSet(StringComparer.Ordinal);
        var w = Text[language];
        foreach (var expected in new[] { w.Title, w.Chapter, w.Name, w.Note, w.Secret, w.A, w.Item2 }.SelectMany(s => s.Split(' ')).Select(Bare))
        {
            Assert.True(words.Contains(expected) || words.Contains(new string(expected.Reverse().ToArray())), $"{themeId} ({language}): \"{expected}\" is not in the PDF");
        }
    }

    /// <summary>Fit pages on Hebrew and Russian: the print browser measures right-to-left columns as it does left-to-right ones, so the fitted text
    /// prints with nothing run on and every word on the pages the fitting counted.</summary>
    [ChromiumTheory]
    [InlineData("plain", "he")]
    [InlineData("ashen-vellum", "he")]
    [InlineData("ashen-vellum", "ru")]
    public async Task FitPages_InARightToLeftOrCyrillicBook_LeavesNothingRunOn(string themeId, string language)
    {
        var w = Text[language];
        var chapter = new StringBuilder($"{{{{chapter,#ch-lore\n# {w.Chapter}\n}}}}\n\n");
        for (var i = 0; i < 30; i++)
        {
            chapter.Append($"## {w.NoteTitle} {i}\n\n");
            for (var j = 0; j < 1 + i % 4; j++) chapter.Append(w.Paragraph).Append(' ').Append(i).Append('.').Append(j).Append("\n\n");
        }

        var shell = BookDraftHtml.Shell("Saltmere", language, BookDraftThemes.Find(themeId)!, "A4", null, true, Font);
        var printer = new BookPdf(Options.Create(new BookPdfOptions { TimeoutSeconds = 90, NoSandbox = BookPdfTests.NoSandbox }), NullLogger<BookPdf>.Instance);
        List<BookLayoutChapter> chapters = [new("front", $"{{{{frontCover}}}}\n\n# {w.Title}\n"), new("lore", chapter.ToString())];
        BookFitted fitted;
        using (var slot = printer.TryReserve("dm")!)
        {
            fitted = await printer.FitAsync(slot, shell.Html!, new BookPrintLayout(Script, string.Empty, new Dictionary<string, string>()) { Chapters = chapters }, CancellationToken.None);
        }

        Assert.Equal(0, fitted.RunOn);
        Assert.Empty(fitted.Overflow);
        Assert.True(fitted.Chapters.Single(c => c.Section == "lore").Markdown.Split('\n').Count(l => l == @"\page {fitted}") >= 3, "too few breaks written");
        using var again = printer.TryReserve("dm")!;
        var printed = await printer.PrintAsync(again, new BookPrintJob(shell.Html!, [], _ => shell.Html!)
            { Layout = new BookPrintLayout(Script, string.Empty, new Dictionary<string, string>()) { Chapters = fitted.Chapters } }, CancellationToken.None);
        Assert.Empty(printed.Overflow);
        Assert.Equal(fitted.Pages, printed.Pages);
    }
}
