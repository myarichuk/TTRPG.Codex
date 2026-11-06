using System.Collections.Concurrent;
using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>What a fetch of a draft's web addresses did: how many copies it made, what the draft keeps now, and what could not be fetched.</summary>
/// <param name="Changed">Copies were added or let go: the preview needs restyling. A fetch that changed nothing leaves it as it is.</param>
public sealed record BookWebReport(int Fetched, int Kept, long KeptBytes, IReadOnlyList<BookWebFailure> Failed, bool Changed);

/// <summary>
/// Keeps a draft's copies of the web addresses its stylesheet names in step with the stylesheet (docs/BOOK_EXPORT.md 41.14): every address it
/// names that has no copy is fetched, the sheets those import are read for theirs, and a copy whose address the sheet no longer names is let go.
/// What to fetch is what <see cref="BookCss.Clean(string, out IReadOnlyList{string}, BookCssWeb?)"/> asked for and did not have, so the server
/// fetches exactly what the page would have written. Nothing is fetched while printing: the book prints from the copies.
/// <para>The stylesheet saves as the GM types, so a fetch for a draft gives way to the next one, and a failure is not remembered: the next save
/// tries that address again.</para>
/// </summary>
public sealed class BookDraftWebs(IBookDraftRepository store, BookWebFetcher fetcher, ILogger<BookDraftWebs> logger)
{
    private const int AtOnce = 4;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);

    /// <summary>Whether the stylesheet names any web address at all (a sheet that names none needs no fetch).</summary>
    public static bool NamesTheWeb(string css, BookCssWeb have)
    {
        BookCss.Clean(css, out _, have);
        return have.Used.Count > 0 || have.Files.Count > 0;
    }

    public async Task<BookWebReport?> RefreshAsync(CampaignAccess dm, string edition, string language, string css, CancellationToken cancel)
    {
        var key = $"{dm.CampaignId}/{edition}/{language}";
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        running.AddOrUpdate(key, mine, (_, old) =>
        {
            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            return mine;
        });
        try
        {
            var have = await store.WebAsync(dm, edition, language);
            var files = have.Files.ToDictionary(f => f.Url, StringComparer.Ordinal);
            var tried = new HashSet<string>(StringComparer.Ordinal);
            var failed = new List<BookWebFailure>();
            var added = new List<BookCssWebFile>();
            // Each round finds what the sheets fetched in the last one import.
            for (var round = 0; round <= BookCssWeb.MaxImportDepth; round++)
            {
                var web = new BookCssWeb(files.Values);
                BookCss.Clean(css, out _, web);
                var todo = web.Missing.Where(a => tried.Add(a.Url)).ToList();
                if (todo.Count == 0) break;
                foreach (var batch in todo.Chunk(AtOnce))
                {
                    mine.Token.ThrowIfCancellationRequested();
                    var room = BookCssWeb.MaxTotalBytes - files.Values.Sum(f => (long)f.Bytes.Length);
                    var results = await Task.WhenAll(batch.Select(a => files.Count + batch.Length > BookCssWeb.MaxFiles
                        ? Task.FromResult<(BookCssWebFile?, BookWebFailure?)>((null, new BookWebFailure(a.Url, BookWebProblem.Full)))
                        : fetcher.FetchAsync(a.Url, a.Kind, room, mine.Token)));
                    foreach (var (file, failure) in results)
                    {
                        if (file != null && files.Values.Sum(f => (long)f.Bytes.Length) + file.Bytes.Length > BookCssWeb.MaxTotalBytes)
                        {
                            failed.Add(new BookWebFailure(file.Url, BookWebProblem.Full));
                        }
                        else if (file != null)
                        {
                            files[file.Url] = file;
                            added.Add(file);
                        }
                        else if (failure != null)
                        {
                            failed.Add(failure);
                        }
                    }
                }
            }

            mine.Token.ThrowIfCancellationRequested();
            var final = new BookCssWeb(files.Values);
            BookCss.Clean(css, out _, final);
            var keep = final.Used.Select(a => a.Url).ToHashSet(StringComparer.Ordinal);
            var kept = files.Values.Where(f => keep.Contains(f.Url)).ToList();
            var changed = added.Count > 0 || have.Files.Any(f => !keep.Contains(f.Url));
            if (changed)
            {
                var saved = await store.SaveWebAsync(dm, edition, language, added.Where(f => keep.Contains(f.Url)).ToList(), keep);
                if (saved != BookDraftSave.Saved)
                {
                    logger.LogInformation("The book's web copies were not saved: {Result}", saved);
                    return null;
                }
            }

            return new BookWebReport(added.Count, kept.Count, kept.Sum(f => (long)f.Bytes.Length), failed, changed);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, mine));
        }
    }
}
