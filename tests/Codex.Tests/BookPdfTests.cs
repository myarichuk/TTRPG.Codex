using Codex.Core.Book;
using Codex.Web.Book;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace Codex.Tests;

/// <summary>Runs only where a Chromium-family browser exists. On CI one must (the e2e suite needs it as well), so a missing browser
/// there is a failure, not a quiet skip.</summary>
public sealed class ChromiumFactAttribute : FactAttribute
{
    public ChromiumFactAttribute()
    {
        if (BookBrowser.Find(null, out _) == null && Environment.GetEnvironmentVariable("CI") == null)
        {
            Skip = "No Chrome, Edge or Chromium on this machine.";
        }
    }
}

public class BookBrowserTests
{
    private static IReadOnlyList<string> Linux(string? configured = null, Dictionary<string, string>? env = null) =>
        BookBrowser.Candidates(configured, k => env != null && env.TryGetValue(k, out var v) ? v : null, _ => [], windows: false, mac: false);

    [Fact]
    public void AConfiguredBrowserIsTheOnlyCandidate_SoATypoIsReportedInsteadOfPrintingWithSomethingElse()
    {
        Assert.Equal(["/opt/x/chrome"], Linux("/opt/x/chrome", new() { ["PATH"] = "/usr/bin" }));
        Assert.Equal(["/opt/y/chrome"], Linux(null, new() { [BookBrowser.EnvironmentVariable] = "/opt/y/chrome", ["PATH"] = "/usr/bin" }));
        Assert.Equal(["/opt/x/chrome"], Linux("/opt/x/chrome", new() { [BookBrowser.EnvironmentVariable] = "/opt/y/chrome" }));
    }

    [Fact]
    public void WithNothingConfigured_LinuxLooksAtThePathForTheUsualNames()
    {
        var found = Linux(null, new() { ["PATH"] = "/usr/bin:/usr/local/bin" });
        Assert.Contains("/usr/bin/google-chrome", found);
        Assert.Contains("/usr/local/bin/chromium", found);
        Assert.Contains("/usr/bin/microsoft-edge", found);
    }

    [Fact]
    public void WindowsAndMacLookWhereTheBrowsersInstall()
    {
        var windows = BookBrowser.Candidates(null, k => k == "ProgramFiles" ? @"C:\Program Files" : null, _ => [], windows: true, mac: false);
        Assert.Contains(windows, p => p.EndsWith("chrome.exe", StringComparison.Ordinal));
        Assert.Contains(windows, p => p.EndsWith("msedge.exe", StringComparison.Ordinal));

        var mac = BookBrowser.Candidates(null, _ => null, _ => [], windows: false, mac: true);
        Assert.Contains("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", mac);
    }

    [Fact]
    public void ThePlaywrightBuildsAreOrderedByNumber_SoANewOneBeatsAnOldOne()
    {
        var found = BookBrowser.Candidates(null, k => k == "PLAYWRIGHT_BROWSERS_PATH" ? "/cache" : null,
            _ => ["/cache/chromium-999/chrome-linux/chrome", "/cache/chromium-1243/chrome-linux64/chrome", "/cache/chromium-80/chrome-linux/chrome"],
            windows: false, mac: false);
        Assert.Equal(["/cache/chromium-1243/chrome-linux64/chrome", "/cache/chromium-999/chrome-linux/chrome", "/cache/chromium-80/chrome-linux/chrome"], found.Take(3));
    }

    [Theory]
    [InlineData("a b: c*?.pdf", "a b c.pdf")]
    [InlineData("../../etc/passwd", "....etcpasswd")]
    [InlineData("ev\u202Eil\u200Bname", "evilname")]
    [InlineData("line\r\nbreak\";x%", "linebreakx")]
    public void TheDownloadNameHoldsNothingAFileSystemOrAnEyeWouldBeFooledBy(string campaign, string expected) =>
        Assert.Equal(expected + " (dm).pdf", Codex.Web.BookEndpoints.FileName(campaign, "dm"));

    [Fact]
    public void ALongDownloadNameIsCutBetweenCharactersNeverInsideOne()
    {
        var name = Codex.Web.BookEndpoints.FileName(new string('a', 79) + "\U0001F409\U0001F409", "party");
        Assert.DoesNotContain('\uFFFD', name);
        Assert.Equal(new string('a', 79) + "\U0001F409 (party).pdf", name);
        Assert.Equal("campaign (dm).pdf", Codex.Web.BookEndpoints.FileName("???", "dm"));
    }

    [Theory]
    [InlineData("HeadlessChrome/131.0.6778.33", 131)]
    [InlineData("Chrome/153.0.1.2", 153)]
    [InlineData("Edg/140.0.1", 140)]
    [InlineData("HeadlessChrome", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void TheMajorVersionIsReadFromTheProductString(string? product, int expected) =>
        Assert.Equal(expected, BookBrowser.MajorVersion(product));

    [Fact]
    public async Task ABrowserThatDoesNotExistIsReportedWithItsPath()
    {
        var pdf = new BookPdf(Options.Create(new BookPdfOptions { Browser = "/nonexistent/chrome" }), NullLogger<BookPdf>.Instance);
        using var slot = pdf.TryReserve("dm")!;
        var ex = await Assert.ThrowsAsync<BookPdfUnavailableException>(() => pdf.PrintAsync(slot, "<p>x</p>", CancellationToken.None));
        Assert.Equal(BookPdfFailure.NoBrowser, ex.Failure);
        // The reason is safe to show: it names how to fix it, not where the server looked.
        Assert.DoesNotContain("/nonexistent", ex.Message, StringComparison.Ordinal);
    }
}

public class BookPdfTests
{
    private static BookPdf Printer(int timeoutSeconds = 90, int maxWaiting = 2, string? profileRoot = null) =>
        new(Options.Create(new BookPdfOptions { TimeoutSeconds = timeoutSeconds, MaxWaiting = maxWaiting, ProfileRoot = profileRoot }), NullLogger<BookPdf>.Instance);

    private static Task<BookPdfResult> Print(BookPdf printer, BookPrintJob job, CancellationToken cancel = default, string owner = "dm")
    {
        var slot = printer.TryReserve(owner) ?? throw new InvalidOperationException("no place in the line");
        return Run(printer, slot, job, cancel);
    }

    private static async Task<BookPdfResult> Run(BookPdf printer, BookPdfSlot slot, BookPrintJob job, CancellationToken cancel)
    {
        using (slot)
        {
            return await printer.PrintAsync(slot, job, cancel);
        }
    }

    private static Task<BookPdfResult> Print(BookPdf printer, string html, CancellationToken cancel = default, string owner = "dm")
    {
        var slot = printer.TryReserve(owner) ?? throw new InvalidOperationException("no place in the line");
        return Run(printer, slot, html, cancel);
    }

    private static async Task<BookPdfResult> Run(BookPdf printer, BookPdfSlot slot, string html, CancellationToken cancel)
    {
        using (slot)
        {
            return await printer.PrintAsync(slot, html, cancel);
        }
    }

    private static byte[]? Font(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Codex.Web", "wwwroot", "fonts", file);
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }
        }

        return null;
    }

    private static string Page(string markdown, string title = "Saltmere")
    {
        var built = BookHtml.Build(new BookDocument(title, "A coast", "en", [new BookSection("a", markdown)], new BookLabels()), BookThemes.Plain, new BookHtmlOptions { Font = Font });
        Assert.NotNull(built.Html);
        return built.Html!;
    }

    [ChromiumFact]
    public async Task APlainBookPrintsToAPdfWithItsTextItsOutlineAndNoRequests()
    {
        var chapters = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"## Place {i}\n\nThe gulls of place {i} are loud and the tide is late.\n"));
        var result = await Print(Printer(), Page("# Coast\n\n" + chapters));

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf, 0, 5));
        Assert.Equal(0, result.BlockedRequests);
        using var pdf = PdfDocument.Open(result.Pdf);
        Assert.True(pdf.NumberOfPages >= 3, $"pages: {pdf.NumberOfPages}");

        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        Assert.Contains("Saltmere", text);
        Assert.Contains("The gulls of place 40 are loud", text);

        // Only the theme's own faces are embedded: nothing fell back to a system font. Cinzel is a variable font and Chromium
        // embeds those as Type 3 (§15 of docs/BOOK_EXPORT.md), so "Type3" is Cinzel here; any other name would be a fallback.
        var letters = pdf.GetPages().SelectMany(p => p.Letters).ToList();
        var fonts = letters.Select(l => l.FontName ?? "").Where(n => n.Length > 0).Select(n => n[(n.IndexOf('+') + 1)..]).Distinct().ToList();
        Assert.NotEmpty(fonts);
        Assert.Contains(fonts, f => f.Replace(" ", "").StartsWith("AlegreyaSans", StringComparison.Ordinal));
        Assert.All(fonts, f => Assert.Matches("^(AlegreyaSans|Cinzel|Type3)", f.Replace(" ", "")));
        // Type 3 may only be the headings' face: its letters are all ones the headings spell, never a body paragraph's.
        var headingLetters = "SaltmereAcoastContentsCoastPlace0123456789 ".ToHashSet();
        Assert.All(letters.Where(l => l.FontName == "Type3" || (l.FontName ?? "").EndsWith("Type3", StringComparison.Ordinal)), l => Assert.Contains(l.Value[0], headingLetters));

        // The page size is the one the theme asked for (A4), not the browser's default.
        Assert.InRange(pdf.GetPage(1).Width, 594, 596);
        Assert.InRange(pdf.GetPage(1).Height, 841, 843);
    }

    /// <summary>The way the route prints: a page with the numbers' column reserved, then the same page with the numbers the headings landed on.</summary>
    private static BookPrintJob Job(string markdown, string language = "en")
    {
        var document = new BookDocument("Saltmere", "A coast", language, [new BookSection("a", markdown)], new BookLabels());
        BookHtmlResult Build(IReadOnlyDictionary<string, int>? numbers) => BookHtml.Build(document, BookThemes.Plain, new BookHtmlOptions { Font = Font, PageNumbers = numbers });
        var first = Build(new Dictionary<string, int>());
        Assert.NotNull(first.Html);
        return new BookPrintJob(first.Html!, first.Headings, n => Build(n).Html!);
    }

    [ChromiumFact]
    public async Task TheContentsCarryThePagesTheHeadingsLandedOn()
    {
        var sections = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"## Place {i}\n\n" + string.Join("\n\n", Enumerable.Repeat("The gulls are loud and the tide is late, as it always is on this coast.", 6))));
        var result = await Print(Printer(), BookPrintJobOf("# Coast\n\n" + sections));

        Assert.True(result.Passes >= 2, $"passes: {result.Passes}");
        using var pdf = PdfDocument.Open(result.Pdf);
        var contents = pdf.GetPage(2).Text;
        Assert.Contains("Contents", contents);

        // For every entry the number printed in the contents is the page its heading is really on.
        Assert.True(pdf.TryGetBookmarks(out var outline));
        var bookmarks = outline!.GetNodes().OfType<UglyToad.PdfPig.Outline.DocumentBookmarkNode>().ToList();
        var checkedEntries = 0;
        foreach (var i in new[] { 1, 7, 15, 30 })
        {
            var place = bookmarks.Last(b => b.Title == $"Place {i}");
            var line = System.Text.RegularExpressions.Regex.Match(contents, $@"Place {i}\s*(\d+)");
            Assert.True(line.Success, $"no number for Place {i} in: {contents}");
            Assert.Equal(place.PageNumber, int.Parse(line.Groups[1].Value));
            Assert.True(place.PageNumber >= 3);
            checkedEntries++;
        }

        Assert.Equal(4, checkedEntries);
    }

    private static BookPrintJob BookPrintJobOf(string markdown) => Job(markdown);

    [ChromiumFact]
    public async Task ABookWithNoHeadingsPrintsOnce()
    {
        var result = await Print(Printer(), BookPrintJob.Single("<!doctype html><p>plain</p>"));
        Assert.Equal(1, result.Passes);
    }

    [ChromiumFact]
    public async Task APageThatReachesForTheNetworkIsRefusedAndStillPrints()
    {
        // Not something the builder can produce; it proves the guard holds even if one day a hole lets a URL through.
        var html = "<!doctype html><html><body><p>Hello</p><img src=\"https://example.invalid/x.png\"><link rel=\"stylesheet\" href=\"http://example.invalid/a.css\"></body></html>";
        var result = await Print(Printer(), html);

        Assert.True(result.BlockedRequests >= 1, $"blocked: {result.BlockedRequests}");
        using var pdf = PdfDocument.Open(result.Pdf);
        Assert.Contains("Hello", pdf.GetPage(1).Text);
    }

    [ChromiumFact]
    public async Task ScriptInThePageNeverRuns()
    {
        var html = "<!doctype html><html><body><p id=\"p\">before</p><script>document.getElementById('p').textContent='RAN'</script></body></html>";
        var result = await Print(Printer(), html);

        using var pdf = PdfDocument.Open(result.Pdf);
        var text = pdf.GetPage(1).Text;
        Assert.Contains("before", text);
        Assert.DoesNotContain("RAN", text, StringComparison.Ordinal);
    }

    [ChromiumFact]
    public async Task AJobThatCannotFinishInTimeIsStopped_AndTheSamePrinterPrintsTheNext()
    {
        // A page far too big to lay out in the time allowed (the minimum limit is five seconds).
        var huge = "<!doctype html><html><body>" + string.Concat(Enumerable.Repeat("<table><tr><td>" + string.Concat(Enumerable.Repeat("<div>word </div>", 200)) + "</td></tr></table>", 4000)) + "</body></html>";
        var printer = Printer(timeoutSeconds: 5);
        await Assert.ThrowsAsync<BookPdfTimeoutException>(() => Print(printer, huge));

        // The same instance: the line was let go, so the next job runs.
        var after = await Print(printer, "<!doctype html><p>still fine</p>");
        using var pdf = PdfDocument.Open(after.Pdf);
        Assert.Contains("still fine", pdf.GetPage(1).Text);
    }

    [Fact]
    public void TheLineIsOneRunningPlusMaxWaiting_AndOnePlacePerPerson_AndAPlaceIsGivenBackOnDispose()
    {
        var printer = Printer(maxWaiting: 1);
        var first = printer.TryReserve("a");
        var second = printer.TryReserve("b");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(printer.TryReserve("c"));

        first!.Dispose();
        first.Dispose();
        Assert.Null(printer.TryReserve("b"));
        var third = printer.TryReserve("c");
        Assert.NotNull(third);
        Assert.Null(printer.TryReserve("d"));

        second!.Dispose();
        third!.Dispose();
        Assert.NotNull(printer.TryReserve("a"));
    }

    [Fact]
    public void ANegativeMaxWaitingStillAllowsOneJob()
    {
        var printer = Printer(maxWaiting: -5);
        using var one = printer.TryReserve("a");
        Assert.NotNull(one);
        Assert.Null(printer.TryReserve("b"));
    }

    [ChromiumFact]
    public async Task TwoBooksInTheLinePrintOneAfterTheOther()
    {
        var printer = Printer(maxWaiting: 1);
        var html = Page("# One\n\ntext");
        await Task.WhenAll(Print(printer, html, owner: "a"), Print(printer, html, owner: "b"));
        Assert.Equal(1, printer.PeakRunning);
    }

    [ChromiumFact]
    public async Task ARequestThatGivesUpWhileTheBrowserIsStarting_LeavesNoBrowserAndNoProfile()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "codex-book-leak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var printer = Printer(profileRoot: root);
            for (var i = 0; i < 3; i++)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Print(printer, "<p>x</p>", cancel.Token, owner: "a" + i));
            }

            // Nothing of ours is left running (the profile directory is on every one of its command lines) and nothing is left on disk.
            Assert.Empty(ProcessesUsing(root));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            foreach (var pid in ProcessesUsing(root))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(true); } catch { /* gone */ }
            }

            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    private static List<int> ProcessesUsing(string marker)
    {
        var found = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
            {
                continue;
            }

            try
            {
                if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains(marker, StringComparison.Ordinal))
                {
                    found.Add(pid);
                }
            }
            catch (IOException)
            {
                // Exited while we looked.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return found;
    }

    [ChromiumFact]
    public async Task TheParchmentEdge_IsVectorOnEveryPage_AndCostsAlmostNothing()
    {
        var chapters = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"## Place {i}\n\nThe gulls of place {i} are loud and the tide is late.\n"));
        BookHtmlResult Build(BookTheme theme) => BookHtml.Build(new BookDocument("Saltmere", "A coast", "en", [new BookSection("a", "# Coast\n\n" + chapters)], new BookLabels()), theme, new BookHtmlOptions { Font = Font });
        var plain = await Print(Printer(), Build(BookThemes.Plain).Html!);
        var parchment = await Print(Printer(), Build(BookThemes.Parchment).Html!);

        using var document = PdfDocument.Open(parchment.Pdf);
        Assert.True(document.NumberOfPages >= 3);
        // The edge is a path, not a picture: no page holds an image, and the file is barely larger than the plain book's.
        Assert.All(document.GetPages(), page => Assert.Empty(page.GetImages()));
        Assert.True(parchment.Pdf.Length < plain.Pdf.Length + 60_000, $"{parchment.Pdf.Length} vs {plain.Pdf.Length}");
        Assert.Equal(0, parchment.BlockedRequests);
        if (Environment.GetEnvironmentVariable("CODEX_DUMP_PDF") is { Length: > 0 } dump) await File.WriteAllBytesAsync(dump, parchment.Pdf);
    }
}
