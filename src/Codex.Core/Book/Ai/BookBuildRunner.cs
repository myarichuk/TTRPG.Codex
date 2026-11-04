using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Codex.Core.Book;

/// <summary>A finished run: the book with every chapter either rewritten or kept as the deterministic text, what happened to each, and
/// what it cost.</summary>
public sealed record BookBuildReport(BookDocument Document, IReadOnlyList<BookSectionResult> Sections, BookBudgetSnapshot Budget)
{
    public int Refined => Sections.Count(s => s.Refined);
}

/// <summary>
/// Runs the chapters of a book through <see cref="BookSectionWorker"/>, a few at a time. Concurrency is one for a model on this machine or
/// this network (Ollama queues requests until they time out) and two for a hosted one. The first failure that every later chapter would
/// share (a refused key, a spent credit, the server's allowance) stops the calls: the rest of the book keeps its deterministic text
/// instead of failing the same way N more times. The cap does the same, and a run can always be stopped with the token.
/// </summary>
public sealed class BookBuildRunner(IChatClient chat, BookBudget budget, int concurrency = 2, Func<Exception, bool>? refusedBeforeSending = null, float temperature = 0.4f)
{
    private static readonly Regex TitleLine = new(@"^#[ \t]+(?<t>[^\n]*)\n*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>1 for an address on this machine or a private network, 2 otherwise.</summary>
    public static int ConcurrencyFor(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return 2;
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') && !host.Contains(':'))
        {
            return 1;
        }

        if (!IPAddress.TryParse(host, out var ip)) return 2;
        if (IPAddress.IsLoopback(ip)) return 1;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC ? 1 : 2;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127 ? 1 : 2;
    }

    /// <summary>A chapter prepared for a model: its title taken from the first line, the rest made ready by <see cref="BookWork.Prepare"/>.</summary>
    public static BookWorkItem Item(BookSection section, string language, string style, Func<string, string, string?>? mention, BookMarkdownOptions render)
    {
        var m = TitleLine.Match(section.Markdown);
        var title = m.Success ? Regex.Replace(m.Groups["t"].Value, @"\\(.)", "$1", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Trim() : section.Id;
        var body = m.Success ? section.Markdown[m.Length..] : section.Markdown;
        return new BookWorkItem(section.Id, title, language, style, section.Markdown, BookWork.Prepare(body, mention, render));
    }

    public async Task<BookBuildReport> RunAsync(BookDocument deterministic, Func<BookSection, BookWorkItem?> prepare, IProgress<BookSectionResult>? progress = null, CancellationToken ct = default,
        Func<BookSection, BookSectionResult?>? cached = null)
    {
        var worker = new BookSectionWorker(chat, budget, temperature, refusedBeforeSending);
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        string? stopped = null;
        var results = new BookSectionResult[deterministic.Sections.Count];

        async Task RunOne(int index)
        {
            var section = deterministic.Sections[index];
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                BookSectionResult result;
                // A chapter already built from exactly what it is made from now costs nothing: no budget, no call.
                if (cached?.Invoke(section) is { } kept)
                {
                    result = kept;
                }
                else if (prepare(section) is not { } item)
                {
                    result = new BookSectionResult(section.Id, section, false, "skipped", 0, []);
                }
                else if (Volatile.Read(ref stopped) is { } why)
                {
                    result = new BookSectionResult(section.Id, section, false, why, 0, []);
                }
                else if (budget.Exhausted)
                {
                    result = new BookSectionResult(section.Id, section, false, "budget", 0, []);
                }
                else
                {
                    result = await worker.RunAsync(item, ct);
                    // What every later chapter would meet too.
                    if (result.Reason is "auth" or "ratelimit" or "allowance") Interlocked.CompareExchange(ref stopped, result.Reason, null);
                }

                results[index] = result;
                progress?.Report(result);
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, deterministic.Sections.Count).Select(RunOne));
        var document = deterministic with { Sections = results.Select(r => r.Section).ToList() };
        return new BookBuildReport(document, results, budget.Snapshot());
    }
}
