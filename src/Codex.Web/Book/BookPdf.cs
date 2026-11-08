using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Codex.Core.Book;
using PuppeteerSharp;

namespace Codex.Web.Book;

public sealed class BookPdfOptions
{
    public const string Section = "Codex:Book:Pdf";

    /// <summary>A browser to print with. Empty means look for one (<see cref="BookBrowser"/>).</summary>
    public string? Browser { get; set; }

    /// <summary>How long one book may take, browser start included, before the browser is killed.</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Jobs allowed to wait behind the one printing. A book is a heavy job and a self-hosted box is small, so the line is short.</summary>
    public int MaxWaiting { get; set; } = 2;

    /// <summary>Run the browser without its own sandbox. Null decides by who runs: a root process on Linux (a container) cannot use it.</summary>
    public bool? NoSandbox { get; set; }

    /// <summary>Where each job's throwaway browser profile is made. Empty is the system temp directory.</summary>
    public string? ProfileRoot { get; set; }
}

/// <summary>What printing a book produced. <paramref name="BlockedRequests"/> counts what the page tried to fetch and was refused: a
/// self-contained page asks for nothing, so anything above zero is worth a look.</summary>
public sealed record BookPdfResult(byte[] Pdf, string Browser, int BlockedRequests, TimeSpan Elapsed, int Passes = 1)
{
    /// <summary>For a book laid out by script (<see cref="BookPrintLayout"/>): how many pages it made, and the pages whose content still did not
    /// fit (a block taller than a page of its own), which the PDF shows clipped.</summary>
    public int Pages { get; init; }

    public IReadOnlyList<BookOverflow> Overflow { get; init; } = [];

    /// <summary>For a Fit pages job (<see cref="BookPrintLayout.Fit"/>): the chapters with their breaks, and what is left. Nothing is printed.</summary>
    public BookFitted? Fitted { get; init; }
}

/// <summary>A page of a laid-out book whose content does not fit (a block taller than a page of its own): its place among the pages (1 is the
/// first, the cover included), the chapter it is in (null when the book was not given as chapters), and the first words of what does not fit.
/// For a break Fit pages could not write, <paramref name="Page"/> is 0.</summary>
public sealed record BookOverflow(int Page, string? Section, string? Words);

/// <summary>A chapter's text as the renderer hands it in and back.</summary>
public sealed record BookLayoutChapter(string Section, string Markdown);

/// <summary>What Fit pages made of a draft (docs/BOOK_EXPORT.md 41.15), laid out in the print browser.</summary>
public sealed class BookFitted
{
    public List<BookLayoutChapter> Chapters { get; set; } = [];

    /// <summary>Blocks that would start a new page, but whose place in the Markdown could not be told, so no break was written before them.</summary>
    public List<BookOverflow> Unplaced { get; set; } = [];

    /// <summary>Pages that still do not fit with the breaks written: a block taller than a page.</summary>
    public List<BookOverflow> Overflow { get; set; } = [];

    /// <summary>Pages the drawing still continues by itself with the breaks written (0 when Fit pages placed every break).</summary>
    public int RunOn { get; set; }

    public int Pages { get; set; }
}

/// <summary>
/// A book whose pages are laid out by a script before it is printed: the editable book (docs/BOOK_EXPORT.md 41.6). The print browser opens
/// <see cref="BookPrintJob.Html"/> (the page shell) in a page of its own with script on and the network closed, runs <paramref name="Script"/>
/// (the bundled renderer, never anything from the page), lays out <paramref name="Markdown"/> with <paramref name="Art"/> (slot key to data image),
/// and the page as it then is, is printed in another page with script off, as every book is.
/// </summary>
public sealed record BookPrintLayout(string Script, string Markdown, IReadOnlyDictionary<string, string> Art)
{
    /// <summary>The draft as chapters, in place of <see cref="Markdown"/>: the renderer joins them as the editor does, and each page knows its
    /// chapter, so a page that does not fit is reported with it.</summary>
    public IReadOnlyList<BookLayoutChapter>? Chapters { get; init; }

    /// <summary>Fit pages: lay the chapters out, write the breaks into their text and return it; nothing is printed.</summary>
    public bool Fit { get; init; }
}

/// <summary>A book to print. When <paramref name="Headings"/> is not empty the page is printed, the pages the headings landed on are
/// read back from the PDF, and <paramref name="WithPageNumbers"/> builds the page again with them in its table of contents; that
/// repeats until the numbers stop moving (three prints at most).</summary>
public sealed record BookPrintJob(string Html, IReadOnlyList<BookHeading> Headings, Func<IReadOnlyDictionary<string, int>, string> WithPageNumbers)
{
    public BookPrintLayout? Layout { get; init; }

    public static BookPrintJob Single(string html) => new(html, [], _ => html);
}

/// <summary>Why a book could not be printed, as a code the page can answer in the reader's language. Details (paths, browser output)
/// go to the log, not to the person who asked.</summary>
public enum BookPdfFailure
{
    NoBrowser,
    BrowserTooOld,
    LaunchFailed,
    PrintFailed
}

/// <summary>No usable browser, or it could not print. <see cref="Exception.Message"/> is safe to show; the cause is in the log.</summary>
public sealed class BookPdfUnavailableException(BookPdfFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public BookPdfFailure Failure { get; } = failure;

    public string Code => Failure switch
    {
        BookPdfFailure.NoBrowser => "no-browser",
        BookPdfFailure.BrowserTooOld => "browser-too-old",
        BookPdfFailure.LaunchFailed => "launch-failed",
        _ => "print-failed"
    };
}

/// <summary>The printer is busy and the line is full, or this person already has a book in it; try again shortly.</summary>
public sealed class BookPdfBusyException() : Exception("A book is being printed and others are waiting. Try again in a moment.");

public sealed class BookPdfTimeoutException(TimeSpan limit) : Exception($"Printing the book took longer than {limit.TotalSeconds:0} seconds and was stopped.");

/// <summary>A place in the printing line, taken before the expensive work of reading a campaign and building its page, so a request that
/// would be turned away costs nothing. Dispose it when the job is over, whatever happened.</summary>
public sealed class BookPdfSlot : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal BookPdfSlot(Action release) => _release = release;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release();
        }
    }
}

/// <summary>
/// Prints a <see cref="Codex.Core.Book.BookHtml"/> page to PDF with the browser already on the machine. One job at a time with a short
/// line behind it (and one place per person in it), a fresh browser and profile per job (nothing idle holds memory, nothing survives
/// a stuck page), a hard timeout that ends the browser whatever state it is in, no script and no network: every request except an
/// inline <c>data:</c> one is refused. The resolver is also switched off, which stops host names only; the refusal is what stops an
/// address such as <c>http://169.254.169.254</c>.
/// </summary>
public sealed class BookPdf(IOptions<BookPdfOptions> options, ILogger<BookPdf> log)
{
    private readonly BookPdfOptions _options = options.Value;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _owners = new(StringComparer.Ordinal);
    private int _held;
    private int _running;
    private int _peakRunning;

    /// <summary>The most jobs that ever printed at once; a test reads it to prove the line is one wide.</summary>
    public int PeakRunning => Volatile.Read(ref _peakRunning);

    /// <summary>A place in the line for <paramref name="owner"/>, or null when the line is full or the owner already has a book in it.</summary>
    public BookPdfSlot? TryReserve(string owner)
    {
        var capacity = Math.Max(0, _options.MaxWaiting) + 1;
        if (Interlocked.Increment(ref _held) > capacity)
        {
            Interlocked.Decrement(ref _held);
            return null;
        }

        if (!_owners.TryAdd(owner, 0))
        {
            Interlocked.Decrement(ref _held);
            return null;
        }

        return new BookPdfSlot(() =>
        {
            _owners.TryRemove(owner, out _);
            Interlocked.Decrement(ref _held);
        });
    }

    public Task<BookPdfResult> PrintAsync(BookPdfSlot slot, string html, CancellationToken cancel) => PrintAsync(slot, BookPrintJob.Single(html), cancel);

    /// <summary>Fit pages (docs/BOOK_EXPORT.md 41.15): the draft's chapters laid out in the print browser, as the PDF is, with a break written
    /// wherever a block would otherwise be moved onto a page of its own. Takes a place in the same line as printing.</summary>
    public async Task<BookFitted> FitAsync(BookPdfSlot slot, string shell, BookPrintLayout layout, CancellationToken cancel) =>
        (await PrintAsync(slot, new BookPrintJob(shell, [], _ => shell) { Layout = layout with { Fit = true } }, cancel)).Fitted
        ?? throw new InvalidOperationException("Fit pages returned nothing.");

    public async Task<BookPdfResult> PrintAsync(BookPdfSlot slot, BookPrintJob job, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(slot);
        await _one.WaitAsync(cancel);
        try
        {
            var now = Interlocked.Increment(ref _running);
            int peak;
            while ((peak = Volatile.Read(ref _peakRunning)) < now && Interlocked.CompareExchange(ref _peakRunning, now, peak) != peak)
            {
            }

            try
            {
                return await PrintOneAsync(job, cancel);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<BookPdfResult> PrintOneAsync(BookPrintJob job, CancellationToken cancel)
    {
        var candidates = BookBrowser.Existing(_options.Browser, out var looked);
        if (candidates.Count == 0)
        {
            log.LogWarning("No browser to print with. Looked at: {Looked}", string.Join(", ", looked));
            throw new BookPdfUnavailableException(BookPdfFailure.NoBrowser,
                $"No Chrome, Edge or Chromium was found to print with. Install one, or set {BookBrowser.EnvironmentVariable} (or {BookPdfOptions.Section}:Browser) to its executable.");
        }

        var limit = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 600));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(limit);
        var clock = Stopwatch.StartNew();
        string? tooOld = null;
        try
        {
            foreach (var path in candidates)
            {
                var result = await TryPrintAsync(path, job, limit, deadline.Token, clock);
                if (result.Pdf != null)
                {
                    return result.Pdf;
                }

                tooOld = result.TooOld;
            }

            throw new BookPdfUnavailableException(BookPdfFailure.BrowserTooOld,
                $"The browser found is too old to print a book: version {BookBrowser.MinimumMajorVersion} or newer is needed ({tooOld}).");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new BookPdfTimeoutException(limit);
        }
    }

    private async Task<(BookPdfResult? Pdf, string? TooOld)> TryPrintAsync(string path, BookPrintJob job, TimeSpan limit, CancellationToken token, Stopwatch clock)
    {
        var profile = Path.Combine(string.IsNullOrWhiteSpace(_options.ProfileRoot) ? Path.GetTempPath() : _options.ProfileRoot, "codex-book-" + Guid.NewGuid().ToString("N"));
        IBrowser? browser = null;
        Task<IBrowser>? launching = null;
        try
        {
            var args = new List<string>
            {
                "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync", "--disable-background-networking",
                "--disable-dev-shm-usage", "--disable-gpu", "--mute-audio",
                // The page cannot fetch (interception below) and a host name could not be resolved if it tried. Addresses are
                // not names: only the interception stops those. The value holds spaces and the launcher joins arguments into one
                // string, so it is quoted.
                "--host-resolver-rules=\"MAP * ~NOTFOUND\""
            };
            if (_options.NoSandbox ?? (OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess))
            {
                args.Add("--no-sandbox");
            }

            Directory.CreateDirectory(profile);
            launching = Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true, ExecutablePath = path, Args = [.. args], UserDataDir = profile, Timeout = (int)limit.TotalMilliseconds, DefaultViewport = null
            });
            browser = await launching.WaitAsync(token);
            var product = await browser.GetVersionAsync().WaitAsync(token);
            if (BookBrowser.MajorVersion(product) < BookBrowser.MinimumMajorVersion)
            {
                log.LogWarning("{Product} at {Path} is older than the {Minimum} a book needs", product, path, BookBrowser.MinimumMajorVersion);
                return (null, product);
            }

            var blocked = 0;
            // Every request but an inline data: one is refused, in every page of the job.
            async Task Seal(IPage tab)
            {
                await tab.SetRequestInterceptionAsync(true);
                tab.Request += async (_, e) =>
                {
                    try
                    {
                        if (e.Request.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || e.Request.Url == "about:blank")
                        {
                            await e.Request.ContinueAsync();
                        }
                        else
                        {
                            // Counted once the browser has accepted the abort: without interception the abort throws, and a request
                            // that was only seen is not a request that was stopped.
                            await e.Request.AbortAsync();
                            Interlocked.Increment(ref blocked);
                        }
                    }
                    catch (Exception)
                    {
                        // An async void handler must never throw: the request was already settled, or the page is gone.
                    }
                };
            }

            var toPrint = job.Html;
            var laidOut = (Pages: 0, Overflow: (IReadOnlyList<BookOverflow>)[]);
            if (job.Layout is { } layout)
            {
                // The one page of a job that runs script: ours only. The shell's policy forbids every script element; the renderer is put in
                // through the browser's own evaluation, which the policy does not govern, and the page's text is data to it.
                await using var scratch = await browser.NewPageAsync().WaitAsync(token);
                await Seal(scratch);
                await scratch.SetContentAsync(job.Html, new SetContentOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = (int)limit.TotalMilliseconds }).WaitAsync(token);
                await scratch.EvaluateExpressionAsync(layout.Script).WaitAsync(token);
                object input = layout.Chapters is { } chapters ? chapters.Select(c => new { section = c.Section, markdown = c.Markdown }).ToArray() : layout.Markdown;
                if (layout.Fit)
                {
                    var fitted = await scratch.EvaluateFunctionAsync<BookFitted>("(chapters, art) => CodexBook.fitPages(chapters, art)", input, layout.Art).WaitAsync(token)
                        ?? throw new InvalidOperationException("The book's renderer returned no fitted chapters.");
                    return (new BookPdfResult([], product, blocked, clock.Elapsed, 0) { Pages = fitted.Pages, Overflow = fitted.Overflow, Fitted = fitted }, null);
                }

                var result = await scratch.EvaluateFunctionAsync<BookLaidOut>("(input, art) => CodexBook.print(input, art)", input, layout.Art).WaitAsync(token);
                if (result?.Html is not { Length: > 0 } drawn)
                {
                    throw new InvalidOperationException("The book's renderer returned no page.");
                }

                toPrint = drawn;
                laidOut = (result.Pages, result.Overflow ?? []);
            }

            await using var page = await browser.NewPageAsync().WaitAsync(token);
            await page.SetJavaScriptEnabledAsync(false);
            await Seal(page);

            async Task<byte[]> Pass(string html)
            {
                await page.SetContentAsync(html, new SetContentOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = (int)limit.TotalMilliseconds }).WaitAsync(token);
                return await page.PdfDataAsync(new PdfOptions
                {
                    PreferCSSPageSize = true,
                    PrintBackground = true,
                    Tagged = true,
                    Outline = true,
                    WaitForFonts = true,
                    DisplayHeaderFooter = false,
                    Timeout = (int)limit.TotalMilliseconds
                }).WaitAsync(token);
            }

            var pdf = await Pass(toPrint);
            var passes = 1;
            if (job.Headings.Count > 0)
            {
                // The first print reserved the numbers' column and left it empty. Read where the headings landed and print again with
                // them in; the column has a fixed width, so the numbers rarely move the pages, and a second look proves it.
                var numbers = BookPages.Map(pdf, job.Headings);
                while (numbers.Count > 0 && passes < 3)
                {
                    pdf = await Pass(job.WithPageNumbers(numbers));
                    passes++;
                    var landed = BookPages.Map(pdf, job.Headings);
                    var stable = landed.Count == numbers.Count && landed.All(kv => numbers.TryGetValue(kv.Key, out var n) && n == kv.Value);
                    numbers = landed;
                    if (stable)
                    {
                        break;
                    }
                }

                if (numbers.Count == 0)
                {
                    log.LogWarning("The printed book's outline could not be read, so its contents carry no page numbers");
                }
            }

            if (blocked > 0)
            {
                log.LogWarning("The book page tried to fetch {Count} resources and was refused", blocked);
            }

            return (new BookPdfResult(pdf, product, blocked, clock.Elapsed, passes) { Pages = laidOut.Pages, Overflow = laidOut.Overflow }, null);
        }
        catch (BookPdfUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Printing a book with {Browser} failed", path);
            var started = browser != null;
            throw new BookPdfUnavailableException(started ? BookPdfFailure.PrintFailed : BookPdfFailure.LaunchFailed,
                started
                    ? "The browser could not print the book. The server log says why."
                    : $"The browser could not start. The server log says why; in a container without a browser sandbox, set {BookPdfOptions.Section}:NoSandbox to true.", ex);
        }
        finally
        {
            // A launch we stopped waiting for still finishes, and its browser must not outlive the job: wait for it (it is bounded by
            // the same limit) and end it. The line is not released until this is done.
            if (browser == null && launching != null)
            {
                browser = await Late(launching);
            }

            if (browser != null)
            {
                await Close(browser);
            }

            Remove(profile);
        }
    }

    private static async Task<IBrowser?> Late(Task<IBrowser> launching)
    {
        try
        {
            return await launching.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A browser that does not close politely within a few seconds is killed: a stuck one must not outlive its job.</summary>
    private static async Task Close(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            try
            {
                browser.Process?.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
        }
        finally
        {
            browser.Dispose();
        }
    }

    private void Remove(string profile)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(profile))
                {
                    Directory.Delete(profile, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The browser can still be letting go of its files.
                Thread.Sleep(200);
            }
        }

        log.LogWarning("Could not remove the browser profile {Profile}", profile);
    }
}

/// <summary>What the renderer's <c>print</c> returns.</summary>
internal sealed class BookLaidOut
{
    public string? Html { get; set; }
    public int Pages { get; set; }
    public BookOverflow[]? Overflow { get; set; }
}
