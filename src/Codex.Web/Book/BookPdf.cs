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

    /// <summary>Run the browser without its own sandbox. Null decides by who runs: a root process (a container) cannot use it.</summary>
    public bool? NoSandbox { get; set; }
}

/// <summary>What printing a book produced. <paramref name="BlockedRequests"/> counts what the page tried to fetch and was refused: a
/// self-contained page asks for nothing, so anything above zero is worth a look.</summary>
public sealed record BookPdfResult(byte[] Pdf, string Browser, int BlockedRequests, TimeSpan Elapsed);

/// <summary>No usable browser, or it could not print. The message is for the person who runs the app.</summary>
public sealed class BookPdfUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The printer is busy and the line is full; try again shortly.</summary>
public sealed class BookPdfBusyException() : Exception("A book is being printed and others are waiting. Try again in a moment.");

public sealed class BookPdfTimeoutException(TimeSpan limit) : Exception($"Printing the book took longer than {limit.TotalSeconds:0} seconds and was stopped.");

/// <summary>
/// Prints a <see cref="Codex.Core.Book.BookHtml"/> page to PDF with the browser already on the machine. One job at a time with a short line
/// behind it, a fresh browser per job (nothing idle holds memory, nothing survives a stuck page), a hard timeout that kills the
/// process, no script and no network: every request except an inline <c>data:</c> one is refused, and the resolver is switched off as
/// well, so the page prints the same with the network cable out.
/// </summary>
public sealed class BookPdf(IOptions<BookPdfOptions> options, ILogger<BookPdf> log)
{
    private readonly BookPdfOptions _options = options.Value;
    private readonly SemaphoreSlim _one = new(1, 1);
    private int _waiting;

    public async Task<BookPdfResult> PrintAsync(string html, CancellationToken cancel)
    {
        if (Interlocked.Increment(ref _waiting) > _options.MaxWaiting + 1)
        {
            Interlocked.Decrement(ref _waiting);
            throw new BookPdfBusyException();
        }

        try
        {
            await _one.WaitAsync(cancel);
        }
        catch
        {
            Interlocked.Decrement(ref _waiting);
            throw;
        }

        try
        {
            return await PrintOneAsync(html, cancel);
        }
        finally
        {
            _one.Release();
            Interlocked.Decrement(ref _waiting);
        }
    }

    private async Task<BookPdfResult> PrintOneAsync(string html, CancellationToken cancel)
    {
        var path = BookBrowser.Find(_options.Browser, out var looked);
        if (path == null)
        {
            throw new BookPdfUnavailableException(string.IsNullOrWhiteSpace(_options.Browser) && Environment.GetEnvironmentVariable(BookBrowser.EnvironmentVariable) is not { Length: > 0 }
                ? $"No Chrome, Edge or Chromium was found to print with. Install one, or set {BookBrowser.EnvironmentVariable} (or {BookPdfOptions.Section}:Browser) to its executable."
                : $"The configured browser was not found: {looked.FirstOrDefault()}.");
        }

        var limit = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 600));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(limit);
        var clock = Stopwatch.StartNew();
        IBrowser? browser = null;
        try
        {
            var args = new List<string>
            {
                "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync", "--disable-background-networking",
                "--disable-dev-shm-usage", "--disable-gpu", "--mute-audio",
                // Defence in depth: the page cannot fetch (interception below), and the browser could not resolve a name if it tried.
                // The value holds spaces and the launcher joins arguments into one string, so it is quoted.
                "--host-resolver-rules=\"MAP * ~NOTFOUND\""
            };
            if (_options.NoSandbox ?? Environment.IsPrivilegedProcess)
            {
                args.Add("--no-sandbox");
            }

            browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = path, Args = [.. args], Timeout = (int)limit.TotalMilliseconds, DefaultViewport = null })
                .WaitAsync(deadline.Token);
            var product = await browser.GetVersionAsync().WaitAsync(deadline.Token);
            if (BookBrowser.MajorVersion(product) is var major && major < BookBrowser.MinimumMajorVersion)
            {
                throw new BookPdfUnavailableException($"{product} at {path} is too old to print a book: version {BookBrowser.MinimumMajorVersion} or newer is needed.");
            }

            await using var page = await browser.NewPageAsync().WaitAsync(deadline.Token);
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
                        // Counted once the browser has accepted the refusal: without interception the abort throws, and a request
                        // that was only seen is not a request that was stopped.
                        await e.Request.AbortAsync();
                        Interlocked.Increment(ref blocked);
                    }
                }
                catch (PuppeteerException)
                {
                    // The request was already settled by the browser (a cancelled load): nothing to do.
                }
            };

            await page.SetContentAsync(html, new SetContentOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = (int)limit.TotalMilliseconds }).WaitAsync(deadline.Token);
            var pdf = await page.PdfDataAsync(new PdfOptions
            {
                PreferCSSPageSize = true,
                PrintBackground = true,
                Tagged = true,
                Outline = true,
                WaitForFonts = true,
                DisplayHeaderFooter = false,
                Timeout = (int)limit.TotalMilliseconds
            }).WaitAsync(deadline.Token);

            if (blocked > 0)
            {
                log.LogWarning("The book page tried to fetch {Count} resources and was refused", blocked);
            }

            return new BookPdfResult(pdf, product, blocked, clock.Elapsed);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new BookPdfTimeoutException(limit);
        }
        catch (Exception ex) when (ex is PuppeteerException or System.ComponentModel.Win32Exception or IOException)
        {
            log.LogError(ex, "Printing a book with {Browser} failed", path);
            throw new BookPdfUnavailableException($"The browser at {path} could not print the book: {ex.Message}", ex);
        }
        finally
        {
            if (browser != null)
            {
                await Close(browser);
            }
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
}
