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
        var ex = await Assert.ThrowsAsync<BookPdfUnavailableException>(() => pdf.PrintAsync("<p>x</p>", CancellationToken.None));
        Assert.Contains("/nonexistent/chrome", ex.Message);
    }
}

public class BookPdfTests
{
    private static BookPdf Printer(int timeoutSeconds = 90, int maxWaiting = 2) =>
        new(Options.Create(new BookPdfOptions { TimeoutSeconds = timeoutSeconds, MaxWaiting = maxWaiting }), NullLogger<BookPdf>.Instance);

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
        var result = await Printer().PrintAsync(Page("# Coast\n\n" + chapters), CancellationToken.None);

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf, 0, 5));
        Assert.Equal(0, result.BlockedRequests);
        using var pdf = PdfDocument.Open(result.Pdf);
        Assert.True(pdf.NumberOfPages >= 3, $"pages: {pdf.NumberOfPages}");

        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        Assert.Contains("Saltmere", text);
        Assert.Contains("The gulls of place 40 are loud", text);

        // Only the theme's own faces are embedded: nothing fell back to a system font. Cinzel is a variable font and Chromium
        // embeds those as Type 3 (§15 of docs/BOOK_EXPORT.md), so "Type3" is Cinzel here; any other name would be a fallback.
        var fonts = pdf.GetPages().SelectMany(p => p.Letters).Select(l => l.FontName ?? "").Where(n => n.Length > 0).Select(n => n[(n.IndexOf('+') + 1)..]).Distinct().ToList();
        Assert.All(fonts, f => Assert.Matches("^(AlegreyaSans|Cinzel|Type3)", f.Replace(" ", "")));

        // The page size is the one the theme asked for (A4), not the browser's default.
        Assert.InRange(pdf.GetPage(1).Width, 594, 596);
        Assert.InRange(pdf.GetPage(1).Height, 841, 843);
    }

    [ChromiumFact]
    public async Task APageThatReachesForTheNetworkIsRefusedAndStillPrints()
    {
        // Not something the builder can produce; it proves the guard holds even if one day a hole lets a URL through.
        var html = "<!doctype html><html><body><p>Hello</p><img src=\"https://example.invalid/x.png\"><link rel=\"stylesheet\" href=\"http://example.invalid/a.css\"></body></html>";
        var result = await Printer().PrintAsync(html, CancellationToken.None);

        Assert.True(result.BlockedRequests >= 1, $"blocked: {result.BlockedRequests}");
        using var pdf = PdfDocument.Open(result.Pdf);
        Assert.Contains("Hello", pdf.GetPage(1).Text);
    }

    [ChromiumFact]
    public async Task ScriptInThePageNeverRuns()
    {
        var html = "<!doctype html><html><body><p id=\"p\">before</p><script>document.getElementById('p').textContent='RAN'</script></body></html>";
        var result = await Printer().PrintAsync(html, CancellationToken.None);

        using var pdf = PdfDocument.Open(result.Pdf);
        var text = pdf.GetPage(1).Text;
        Assert.Contains("before", text);
        Assert.DoesNotContain("RAN", text, StringComparison.Ordinal);
    }

    [ChromiumFact]
    public async Task ATenSecondLimitStopsAJobThatCannotFinish_AndTheNextOneStillPrints()
    {
        // A page that is far too big to lay out in the time allowed.
        var huge = "<!doctype html><html><body>" + string.Concat(Enumerable.Repeat("<table><tr><td>" + string.Concat(Enumerable.Repeat("<div>word </div>", 200)) + "</td></tr></table>", 4000)) + "</body></html>";
        var printer = Printer(timeoutSeconds: 5);
        await Assert.ThrowsAsync<BookPdfTimeoutException>(() => printer.PrintAsync(huge, CancellationToken.None));

        var after = await Printer().PrintAsync("<!doctype html><p>still fine</p>", CancellationToken.None);
        using var pdf = PdfDocument.Open(after.Pdf);
        Assert.Contains("still fine", pdf.GetPage(1).Text);
    }

    [ChromiumFact]
    public async Task OnlyOneBookPrintsAtATime_AndTheLineBehindItIsShort()
    {
        var printer = Printer(maxWaiting: 1);
        var html = Page("# One\n\ntext");
        var first = printer.PrintAsync(html, CancellationToken.None);
        var second = printer.PrintAsync(html, CancellationToken.None);
        // The line is one running + one waiting: a third is turned away at once.
        await Assert.ThrowsAsync<BookPdfBusyException>(() => printer.PrintAsync(html, CancellationToken.None));
        await Task.WhenAll(first, second);
    }
}
