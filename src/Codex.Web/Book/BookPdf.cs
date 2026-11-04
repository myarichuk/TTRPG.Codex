using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
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
public sealed record BookPdfResult(byte[] Pdf, string Browser, int BlockedRequests, TimeSpan Elapsed);

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

    public async Task<BookPdfResult> PrintAsync(BookPdfSlot slot, string html, CancellationToken cancel)
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
                return await PrintOneAsync(html, cancel);
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

    private async Task<BookPdfResult> PrintOneAsync(string html, CancellationToken cancel)
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
                var result = await TryPrintAsync(path, html, limit, deadline.Token, clock);
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

    private async Task<(BookPdfResult? Pdf, string? TooOld)> TryPrintAsync(string path, string html, TimeSpan limit, CancellationToken token, Stopwatch clock)
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

            await using var page = await browser.NewPageAsync().WaitAsync(token);
            var blocked = 0;
            await page.SetJavaScriptEnabledAsync(false);
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (_, e) =>
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

            await page.SetContentAsync(html, new SetContentOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = (int)limit.TotalMilliseconds }).WaitAsync(token);
            var pdf = await page.PdfDataAsync(new PdfOptions
            {
                PreferCSSPageSize = true,
                PrintBackground = true,
                Tagged = true,
                Outline = true,
                WaitForFonts = true,
                DisplayHeaderFooter = false,
                Timeout = (int)limit.TotalMilliseconds
            }).WaitAsync(token);

            if (blocked > 0)
            {
                log.LogWarning("The book page tried to fetch {Count} resources and was refused", blocked);
            }

            return (new BookPdfResult(pdf, product, blocked, clock.Elapsed), null);
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
