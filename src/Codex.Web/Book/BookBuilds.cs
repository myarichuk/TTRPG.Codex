using System.Collections.Concurrent;
using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>What the page may ask a build to do.</summary>
/// <param name="Audience">"dm" or "party".</param>
/// <param name="Cap">The most tokens (input and output together) the run may use. The budget refuses every call that could pass it.</param>
/// <param name="Redo">Chapters to rebuild even though they are current: the DM's explicit re-run, which costs what a build costs.</param>
public sealed record BookBuildRequest(string Audience, string Language, string Style, long Cap, IReadOnlySet<string>? Redo = null)
{
    public const int MaxStyleChars = 800;
}

/// <summary>One chapter as the pre-flight and the progress list show it.</summary>
public sealed record BookChapterInfo(string Id, string Title, BookSectionState State, bool TooShort, string? LastReason, string? Model);

/// <summary>What a build would do and cost, before anything is sent. Every figure is an estimate and the page says so.</summary>
public sealed record BookBuildPreview(
    IReadOnlyList<BookChapterInfo> Chapters, int ToBuild, int Current, int TooShort, BookPreflight? Preflight, long SuggestedCap, long MinimumCap,
    BookModelInfo? Model, BookBuildTotals? Totals, IReadOnlyList<string> StyleProblems);

/// <summary>What the stored build has used over all its runs.</summary>
public sealed record BookBuildTotals(long InputTokens, long OutputTokens, int Calls, int Runs, bool Estimated, decimal Cost, int UnpricedRuns, string PriceLabel, DateTime UpdatedAt);

public enum BookRunStatus { Running, Done, Stopped, Failed }

public enum BookRowStatus { Queued, Cached, Refined, Kept, Skipped }

public sealed record BookRunRow(string Id, string Title, BookRowStatus Status, string? Reason);

/// <summary>A consistent picture of a run for the page: never a half-updated one.</summary>
public sealed record BookRunSnapshot(
    BookRunStatus Status, IReadOnlyList<BookRunRow> Rows, BookBudgetSnapshot Budget, AiPriceQuote? Price, string Model, string? Failure, bool SaveFailed)
{
    public int Finished => Rows.Count(r => r.Status != BookRowStatus.Queued);
    public int Refined => Rows.Count(r => r.Status == BookRowStatus.Refined);
}

public sealed class BookBuildBusyException() : Exception("A build of this book is already running.");

/// <summary>The style notes of a party build name or count something the party's book does not hold.</summary>
public sealed class BookStyleRefusedException(IReadOnlyList<string> problems) : Exception("The style notes carry facts the party edition may not have: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>One running (or finished) build, as the page watches it. The work does not belong to the page: closing the tab does not
/// throw away what the calls have already cost, and the page finds the run again when it is opened.</summary>
public sealed class BookRun
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private BookRunRow[] _rows;
    private BookRunStatus _status = BookRunStatus.Running;
    private string? _failure;
    private bool _saveFailed;
    private readonly BookBudget _budget;
    private BookBudgetSnapshot? _final;

    internal BookRun(string key, string userId, IEnumerable<BookRunRow> rows, BookBudget budget, AiPriceQuote? price, string model)
    {
        Key = key;
        UserId = userId;
        _rows = rows.ToArray();
        _budget = budget;
        Price = price;
        Model = model;
        Started = DateTime.UtcNow;
    }

    public string Key { get; }
    public string UserId { get; }
    public AiPriceQuote? Price { get; }
    public string Model { get; }
    public DateTime Started { get; }
    internal CancellationToken Token => _cancel.Token;
    /// <summary>Completes when the run has ended and everything it kept has been stored.</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    /// <summary>Raised after every change. The page re-reads <see cref="Snapshot"/> on its own thread.</summary>
    public event Action? Changed;

    public bool Running { get { lock (_gate) return _status == BookRunStatus.Running; } }

    public BookRunSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new BookRunSnapshot(_status, _rows.ToArray(), _final ?? _budget.Snapshot(), Price, Model, _failure, _saveFailed);
        }
    }

    /// <summary>Stops the run: calls in flight are cancelled (and charged as the sent calls they may be), and nothing else is sent.</summary>
    public void Cancel() => _cancel.Cancel();

    internal void Row(string id, BookRowStatus status, string? reason)
    {
        lock (_gate)
        {
            var i = Array.FindIndex(_rows, r => r.Id == id);
            if (i >= 0) _rows[i] = _rows[i] with { Status = status, Reason = reason };
        }

        Changed?.Invoke();
    }

    internal void SaveFailed()
    {
        lock (_gate) _saveFailed = true;
        Changed?.Invoke();
    }

    internal void Finish(BookRunStatus status, string? failure)
    {
        lock (_gate)
        {
            _status = status;
            _failure = failure;
            _final = _budget.Snapshot();
        }

        Changed?.Invoke();
    }
}

/// <summary>
/// The AI polish of a book, for the DM: what it would cost (<see cref="PreviewAsync"/>), the run itself, in the background, with live
/// progress (<see cref="StartAsync"/>), and the stored result that the page and the PDF use. One run per book and edition at a time, and
/// one per DM. A chapter is sent to the model only if it is not already stored from exactly what it is made from (<see cref="BookBuildKey"/>);
/// everything that comes back passes <see cref="BookChecks"/> or the deterministic chapter is kept and the row says why. What each call
/// cost is counted against the cap before it is made (<see cref="BookBudget"/>), and the run stops at the cap, at the first refusal that
/// every later call would meet, or when the DM cancels, keeping what exists.
/// </summary>
public sealed class BookBuilds(BookSourceReader reader, IBookBuildRepository store, IBookModels models, Func<string, string, Task<CampaignAccess?>> resolve, TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, BookRun> _runs = new();
    // One build (or discard) per DM, taken before the first await: a check made after the awaits would let two clicks both pass it.
    private readonly ConcurrentDictionary<string, bool> _busy = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static string KeyOf(string campaignId, string userId, string audience) => campaignId + "|" + userId + "|" + audience;

    /// <summary>The DM's run of this edition, running or finished since the page last asked, or null.</summary>
    public BookRun? Find(CampaignAccess dm, string audience) => _runs.GetValueOrDefault(KeyOf(dm.CampaignId, dm.UserId, audience));

    private sealed record Prepared(CampaignAccess Dm, BookSource Source, BookDocument Document, Dictionary<string, BookWorkItem> Items, string Edition, string Style, BookBuildDocument? Stored, IReadOnlyList<string> StyleProblems);

    private async Task<Prepared?> PrepareAsync(CampaignAccess asked, string audience, string language, string style)
    {
        // The role is read again here: a DM demoted after their page loaded gets nothing.
        if (await resolve(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return null;
        BookAudience? edition = audience switch { "dm" => BookAudience.Dm, "party" => BookAudience.Party, _ => null };
        if (edition is not { } which || await reader.ReadAsync(dm, which) is not { } source) return null;
        style = Clip(style);
        var document = BookComposer.Compose(source, language);
        var items = BookComposer.Items(document, source, style);
        // The DM's own words go into every request. For the party's book they may not carry a fact the party's chapters do not hold.
        var problems = which == BookAudience.Party && style.Length > 0
            ? BookChecks.StyleProblems(style, string.Join("\n", items.Values.Select(i => i.Title + "\n" + i.Work.Text)))
            : [];
        return new Prepared(dm, source, document, items, audience, style, await store.GetAsync(dm, audience), problems);
    }

    private static string Clip(string? style)
    {
        var text = (style ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return text.Length > BookBuildRequest.MaxStyleChars ? text[..BookBuildRequest.MaxStyleChars] : text;
    }

    /// <summary>The key each chapter has now, and the stored chapters that are still good for it.</summary>
    private static (List<BookChapterInfo> Chapters, Dictionary<string, string> Keys) Classify(Prepared p, IReadOnlySet<string>? redo)
    {
        var keys = p.Document.Sections.ToDictionary(s => s.Id, s => BookBuildKey.For(p.Items[s.Id], p.Edition), StringComparer.Ordinal);
        // Only a chapter that was rewritten counts as built: one that fell back to the deterministic text is tried again by the next run.
        var storedKeys = (p.Stored?.Sections ?? []).Where(s => s.Refined).ToDictionary(s => s.Id, s => s.Key, StringComparer.Ordinal);
        var states = BookStaleness.Classify(p.Document.Sections.Select(s => (s.Id, keys[s.Id])).ToList(), storedKeys).ToDictionary(x => x.Id, x => x.State);
        var chapters = p.Document.Sections.Select(s =>
        {
            var item = p.Items[s.Id];
            var kept = p.Stored?.Sections.FirstOrDefault(b => b.Id == s.Id);
            var state = states[s.Id];
            if (redo?.Contains(s.Id) == true && state == BookSectionState.Current) state = BookSectionState.Stale;
            return new BookChapterInfo(s.Id, item.Title, state, BookComposer.TooShort(item), kept is { Refined: false } ? kept.Reason : null, kept?.Model);
        }).ToList();
        return (chapters, keys);
    }

    /// <summary>What a build of this book would do and cost now. Reads the book as a build would; sends nothing.</summary>
    public async Task<BookBuildPreview?> PreviewAsync(CampaignAccess asked, BookBuildRequest request)
    {
        if (await PrepareAsync(asked, request.Audience, request.Language, request.Style) is not { } p) return null;
        var (chapters, _) = Classify(p, request.Redo);
        var model = await models.InfoAsync(p.Dm.UserId);
        var todo = chapters.Where(c => c.State != BookSectionState.Current && !c.TooShort).ToList();
        var loads = todo.Select(c => BookSectionWorker.LoadFor(p.Items[c.Id])).ToList();
        var preflight = loads.Count == 0 ? null : BookPreflight.Estimate(loads, 0, model?.Price);
        // A call reserves its worst case before it goes out (BookBudget.WorstCase), so a cap under the biggest chapter's own worst case sends
        // nothing at all. The suggestion covers every chapter once at the high estimate and room for two calls in flight at the biggest.
        var biggest = loads.Count == 0 ? 0 : loads.Max(l => BookBudget.WorstCase(l.InputTokens, l.MaxOutputTokens));
        var suggested = preflight == null ? 0 : RoundUp(Math.Max(preflight.InputHigh + preflight.OutputHigh, biggest * 2));
        var totals = p.Stored is { } b ? new BookBuildTotals(b.InputTokens, b.OutputTokens, b.Calls, b.Runs, b.Estimated, b.Cost, b.UnpricedRuns, b.PriceLabel, b.UpdatedAt) : null;
        return new BookBuildPreview(chapters, todo.Count, chapters.Count(c => c.State == BookSectionState.Current), chapters.Count(c => c.TooShort), preflight, suggested, biggest, model, totals, p.StyleProblems);
    }

    private static long RoundUp(long tokens) => Math.Max(1000, (tokens + 999) / 1000 * 1000);

    /// <summary>Starts the run in the background. Everything that can refuse it refuses it here, before a request leaves: no model, the
    /// server allowance not covering the calls, another run under way. Null when the book is not the caller's to read.</summary>
    public async Task<BookRun?> StartAsync(CampaignAccess asked, BookBuildRequest request)
    {
        if (!_busy.TryAdd(asked.UserId, true)) throw new BookBuildBusyException();
        var launched = false;
        try
        {
            var run = await StartCoreAsync(asked, request);
            launched = run != null;
            return run;
        }
        finally
        {
            if (!launched) _busy.TryRemove(asked.UserId, out _);
        }
    }

    private async Task<BookRun?> StartCoreAsync(CampaignAccess asked, BookBuildRequest request)
    {
        if (await PrepareAsync(asked, request.Audience, request.Language, request.Style) is not { } p) return null;
        if (p.StyleProblems.Count > 0) throw new BookStyleRefusedException(p.StyleProblems);
        var (chapters, keys) = Classify(p, request.Redo);
        var todo = chapters.Where(c => c.State != BookSectionState.Current && !c.TooShort).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        var key = KeyOf(p.Dm.CampaignId, p.Dm.UserId, request.Audience);

        var budget = new BookBudget(Math.Max(0, request.Cap));
        BookModelHandle? model = null;
        if (todo.Count > 0)
        {
            model = await models.OpenAsync(p.Dm.UserId, todo.Count * BookSectionWorker.MaxAttempts);
        }

        var rows = chapters.Select(c => new BookRunRow(c.Id, c.Title,
            c.State == BookSectionState.Current ? BookRowStatus.Cached : c.TooShort ? BookRowStatus.Skipped : BookRowStatus.Queued, null)).ToList();
        var run = new BookRun(key, p.Dm.UserId, rows, budget, model?.Price, model?.Model ?? string.Empty);
        // A finished run of an hour ago is only kept so the page can show how it ended.
        foreach (var old in _runs.Where(r => !r.Value.Running && _clock.GetUtcNow().UtcDateTime - r.Value.Started > TimeSpan.FromHours(1)).Select(r => r.Key)) _runs.TryRemove(old, out _);
        _runs[key] = run;
        run.Completion = Task.Run(() => ExecuteAsync(run, p, request, keys, todo, budget, model));
        return run;
    }

    private async Task ExecuteAsync(BookRun run, Prepared p, BookBuildRequest request, Dictionary<string, string> keys, HashSet<string> todo, BookBudget budget, BookModelHandle? model)
    {
        var build = p.Stored ?? new BookBuildDocument { Audience = p.Edition };
        var chain = Task.CompletedTask;
        var chainGate = new object();
        var totalsDone = 0;
        void Persist(Action mutate)
        {
            // One writer at a time, in order: results arrive from several workers, and the document is saved whole.
            lock (chainGate)
            {
                chain = chain.ContinueWith(async _ =>
                {
                    try
                    {
                        mutate();
                        if (!await store.SaveAsync(p.Dm, build))
                        {
                            // Not the campaign's DM any more: nothing more can be kept, so nothing more is bought.
                            run.SaveFailed();
                            run.Cancel();
                        }
                    }
                    catch
                    {
                        run.SaveFailed();
                    }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        try
        {
            // Inside the try: whatever breaks here still finishes the run, returns the model and frees the DM's build slot.
            // A chapter the book no longer has (deleted, or hidden from this edition) is dropped from the store: nothing may print it again.
            var present = p.Document.Sections.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            build.Sections.RemoveAll(s => !present.Contains(s.Id));
            build.Language = BookThemes.Primary(request.Language);
            build.Style = p.Style;

            if (model == null)
            {
                // Nothing to ask: every chapter is built or too short. The store is tidied (dropped chapters) and the run is over.
                Persist(() => { });
                await chain;
                run.Finish(BookRunStatus.Done, null);
                return;
            }

            var runner = new BookBuildRunner(model.Chat, budget, model.Concurrency, model.RefusedBeforeSending);
            var progress = new Inline<BookSectionResult>(result =>
            {
                if (result.Attempts == 0 && result.Reason == null)
                {
                    run.Row(result.Id, BookRowStatus.Cached, null);
                    return;
                }

                if (result.Reason == "skipped")
                {
                    run.Row(result.Id, BookRowStatus.Skipped, null);
                    return;
                }

                run.Row(result.Id, result.Refined ? BookRowStatus.Refined : BookRowStatus.Kept, result.Reason);
                Persist(() =>
                {
                    // A rewrite that was already paid for is not replaced by a failure (a stopped run, a spent cap, a refused retry): the
                    // chapter keeps it, and it is used again whenever its key is current again.
                    if (!result.Refined && build.Sections.Any(s => s.Id == result.Id && s.Refined)) return;
                    build.Sections.RemoveAll(s => s.Id == result.Id);
                    build.Sections.Add(new BookBuildSection
                    {
                        Id = result.Id,
                        Key = keys[result.Id],
                        Refined = result.Refined,
                        Reason = result.Reason,
                        Attempts = result.Attempts,
                        Markdown = result.Refined ? result.Section.Markdown : string.Empty,
                        Model = model.Model,
                        BuiltAt = DateTime.UtcNow
                    });
                });
            });

            // The chapters stored when the run began, looked up here and not in the list the writer changes while the workers read.
            var kept = build.Sections.Where(s => s.Refined).ToDictionary(s => s.Id, StringComparer.Ordinal);
            await runner.RunAsync(p.Document,
                section => todo.Contains(section.Id) ? p.Items[section.Id] : null,
                progress, run.Token,
                cached: section => todo.Contains(section.Id) ? null : Cached(p, kept, keys, section));

            PersistTotals();
            await chain;
            run.Finish(run.Token.IsCancellationRequested ? BookRunStatus.Stopped : BookRunStatus.Done, null);
        }
        catch (Exception ex)
        {
            // What the calls cost is still counted when the run itself broke.
            try { PersistTotals(); await chain; } catch { }
            run.Finish(BookRunStatus.Failed, AiFailure.From(ex).Detail);
        }
        finally
        {
            // The server requests that were set aside and not used go back, and the DM may start another build.
            model?.Dispose();
            _busy.TryRemove(run.UserId, out _);
        }

        void PersistTotals()
        {
            if (model == null || Interlocked.Exchange(ref totalsDone, 1) != 0) return;
            var spent = budget.Snapshot();
            Persist(() =>
            {
                build.Runs++;
                build.InputTokens += spent.InputTokens;
                build.OutputTokens += spent.OutputTokens;
                build.Calls += spent.Calls;
                build.Estimated |= spent.Estimated;
                if (spent.Calls > 0)
                {
                    if (spent.Cost(model.Price) is { } cost) { build.Cost += cost; build.PriceLabel = model.Price?.Label ?? build.PriceLabel; }
                    else build.UnpricedRuns++;
                }
            });
        }
    }

    // A chapter built from what it is made from now: the stored rewrite, at no cost.
    private static BookSectionResult? Cached(Prepared p, Dictionary<string, BookBuildSection> stored, Dictionary<string, string> keys, BookSection section) =>
        stored.TryGetValue(section.Id, out var kept) && kept.Key == keys[section.Id]
            ? new BookSectionResult(section.Id, BookComposer.Stored(kept, p.Items[section.Id]), true, null, 0, [])
            : null;

    /// <summary>Forgets the DM's stored build of an edition (the chapters and the totals). The next build starts from nothing.</summary>
    public async Task<bool> DiscardAsync(CampaignAccess asked, string audience)
    {
        if (await resolve(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return false;
        // Takes the DM's one slot, so a build cannot start between the check and the delete and bring the chapters back.
        if (!_busy.TryAdd(dm.UserId, true)) return false;
        try
        {
            return await store.DeleteAsync(dm, audience);
        }
        finally
        {
            _busy.TryRemove(dm.UserId, out _);
        }
    }

    private sealed class Inline<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
