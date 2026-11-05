using System.Collections.Concurrent;
using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Persistence;
using Microsoft.Extensions.Logging;

namespace Codex.Web.Book;

/// <summary>What a director run would ask and cost now.</summary>
/// <param name="Slots">Slots the run would ask about.</param>
/// <param name="Current">Slots whose suggestion was made from what they hold now.</param>
/// <param name="Cap">The token cap the run is held to: every call twice at the high estimate, with room for the calls in flight.</param>
public sealed record BookDirectionPreview(int Slots, int Current, int Calls, BookPreflight? Preflight, long Cap, BookModelInfo? Model, BookArtSpend? Totals);

/// <summary>
/// The art director's runs (docs/BOOK_EXPORT.md 28.3, step 5): for each chapter of an edition, a model is asked what each picture slot should show,
/// and what passes the checks is stored as that edition's suggestion. Nothing is drawn. The model is shown only the outline of the edition
/// (<see cref="BookArtOutline"/>) and replies under numbers local to each call; the suggestions are tagged with the edition whose source was read.
/// A run takes the same per-GM gate as a chapter build (<see cref="BookGate"/>), reserves the server's requests up front, and is held to a cap
/// worked out from the estimate before anything is sent.
/// </summary>
public sealed class BookArtDirection(BookSourceReader reader, IBookArtRepository store, IBookModels models, Func<string, string, Task<CampaignAccess?>> resolve, BookGate gate, TimeProvider? clock = null, Microsoft.Extensions.Logging.ILogger? log = null)
{
    private readonly ConcurrentDictionary<string, BookRun> _runs = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static string KeyOf(string campaignId, string userId, string edition) => campaignId + "|" + userId + "|" + edition + "|art";

    public BookRun? Find(CampaignAccess dm, string edition) => _runs.GetValueOrDefault(KeyOf(dm.CampaignId, dm.UserId, edition));

    /// <summary>One call to make: a chapter (or a part of one) and how its local refs map back to slots.</summary>
    private sealed record Planned(string RowId, string Title, BookDirectorCall Call, IReadOnlyDictionary<int, BookOutlineSlot> Refs);

    private sealed record Prepared(CampaignAccess Dm, BookSource Source, string Tag, IReadOnlyList<BookOutlineChapter> Outline, BookArtDocument? Stored, IReadOnlyList<Planned> Calls, int Current);

    private async Task<Prepared?> PrepareAsync(CampaignAccess asked, string edition, string language, bool all)
    {
        BookAudience? audience = edition switch { "dm" => BookAudience.Dm, "party" => BookAudience.Party, _ => null };
        if (audience is not { } which) return null;
        if (await resolve(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return null;
        if (await reader.ReadAsync(dm, which) is not { } source) return null;
        var compiled = BookComposer.Compose(source, language, out var entries);
        var slots = BookSlots.For(source, compiled, entries);
        var outline = BookArtOutline.For(source, compiled, entries, slots);
        var stored = await store.GetAsync(dm);
        var tag = BookSlots.Tag(which);
        var current = (stored?.Suggestions ?? []).Where(s => s.BuiltFrom == tag).ToDictionary(s => s.Slot, s => s.Inputs, StringComparer.Ordinal);
        var names = BookArtOutline.Names(source, entries);
        var calls = new List<Planned>();
        var upToDate = 0;
        foreach (var chapter in outline)
        {
            var todo = chapter.Slots.Where(s => all || current.GetValueOrDefault(s.Slot.Key) != s.Inputs).ToList();
            upToDate += chapter.Slots.Count - todo.Count;
            for (var part = 0; part * BookArtDirector.MaxSlotsPerCall < todo.Count; part++)
            {
                var chunk = todo.Skip(part * BookArtDirector.MaxSlotsPerCall).Take(BookArtDirector.MaxSlotsPerCall).ToList();
                var refs = chunk.Select((s, i) => (Ref: i + 1, Slot: s)).ToDictionary(x => x.Ref, x => x.Slot);
                var call = new BookDirectorCall(chapter.Title, language, refs.Select(r => new BookDirectorSlot(r.Key, r.Value.Kind, r.Value.Slot.Label, r.Value.Text)).ToList(), names);
                calls.Add(new Planned(chapter.Id + "#" + part, part == 0 ? chapter.Title : chapter.Title + " (" + (part + 1) + ")", call, refs));
            }
        }

        return new Prepared(dm, source, tag, outline, stored, calls, upToDate);
    }

    private static (BookPreflight? Preflight, long Cap) Estimate(IReadOnlyList<Planned> calls, AiPriceQuote? price)
    {
        if (calls.Count == 0) return (null, 0);
        var loads = calls.Select(c => BookArtDirector.LoadFor(c.Call)).ToList();
        var preflight = BookPreflight.Estimate(loads, 0, price);
        var biggest = loads.Max(l => BookBudget.WorstCase(l.InputTokens, l.MaxOutputTokens));
        // Every call may be asked twice; a reservation needs its own worst case free, for up to two calls in flight.
        var cap = (preflight.InputHigh + preflight.OutputHigh) * BookArtDirector.MaxAttempts + biggest * 2;
        return (preflight, (cap + 999) / 1000 * 1000);
    }

    /// <summary>What a run would do and cost now; sends nothing. <paramref name="all"/> asks again about slots whose suggestion is current.</summary>
    public async Task<BookDirectionPreview?> PreviewAsync(CampaignAccess asked, string edition, string language, bool all = false)
    {
        if (await PrepareAsync(asked, edition, language, all) is not { } p) return null;
        var model = await models.InfoAsync(p.Dm.UserId);
        var (preflight, cap) = Estimate(p.Calls, model?.Price);
        return new BookDirectionPreview(p.Calls.Sum(c => c.Refs.Count), p.Current, p.Calls.Count, preflight, cap, model, Totals(p.Stored));
    }

    private static BookArtSpend? Totals(BookArtDocument? art) => art is not { DirectorRuns: > 0 } a ? null
        : new BookArtSpend(a.DirectorInputTokens, a.DirectorOutputTokens, a.DirectorCalls, a.DirectorEstimated, a.DirectorUnpricedRuns == 0 ? a.DirectorCost : null, a.DirectorPriceLabel);

    /// <summary>Starts a run in the background. Refused here, before anything is sent: another paid run of this GM's under way
    /// (<see cref="BookBuildBusyException"/>), no model, the server's allowance. Null when the book is not the caller's to read or there is nothing to ask.</summary>
    public async Task<BookRun?> StartAsync(CampaignAccess asked, string edition, string language, bool all = false)
    {
        if (!gate.TryEnter(asked.UserId)) throw new BookBuildBusyException();
        var launched = false;
        try
        {
            var run = await StartCoreAsync(asked, edition, language, all);
            launched = run != null;
            return run;
        }
        finally
        {
            if (!launched) gate.Exit(asked.UserId);
        }
    }

    private async Task<BookRun?> StartCoreAsync(CampaignAccess asked, string edition, string language, bool all)
    {
        if (await PrepareAsync(asked, edition, language, all) is not { } p || p.Calls.Count == 0) return null;
        var model = await models.OpenAsync(p.Dm.UserId, p.Calls.Count * BookArtDirector.MaxAttempts);
        try
        {
            var (_, cap) = Estimate(p.Calls, model.Price);
            var budget = new BookBudget(cap);
            var key = KeyOf(p.Dm.CampaignId, p.Dm.UserId, edition);
            var run = new BookRun(key, p.Dm.UserId, p.Calls.Select(c => new BookRunRow(c.RowId, c.Title, BookRowStatus.Queued, null)).ToList(), budget, model.Price, model.Model);
            foreach (var old in _runs.Where(r => !r.Value.Running && _clock.GetUtcNow().UtcDateTime - r.Value.Started > TimeSpan.FromHours(1)).Select(r => r.Key)) _runs.TryRemove(old, out _);
            _runs[key] = run;
            run.Completion = Task.Run(() => ExecuteAsync(run, p, budget, model));
            return run;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    private async Task ExecuteAsync(BookRun run, Prepared p, BookBudget budget, BookModelHandle model)
    {
        var spentOnce = 0;
        try
        {
            var director = new BookArtDirector(model.Chat, budget, refusedBeforeSending: model.RefusedBeforeSending);
            using var slots = new SemaphoreSlim(Math.Max(1, model.Concurrency));
            string? stop = null;
            var saving = new SemaphoreSlim(1);
            var present = p.Outline.SelectMany(c => c.Slots).Select(o => o.Slot.Key).ToHashSet(StringComparer.Ordinal);

            async Task One(Planned planned)
            {
                await slots.WaitAsync();
                try
                {
                    if (stop != null || run.Token.IsCancellationRequested || budget.Exhausted)
                    {
                        run.Row(planned.RowId, BookRowStatus.Skipped, stop ?? (budget.Exhausted ? "budget" : "cancelled"));
                        return;
                    }

                    var result = await director.RunAsync(planned.Call, run.Token);
                    // The first failure every later call would share stops the rest from being sent.
                    if (result.Reason is "auth" or "ratelimit" or "allowance") stop ??= result.Reason;
                    // What the checks refused, so a chapter that gave nothing can be explained. Only the checks' own words are logged:
                    // the reply itself is never written down.
                    if (result.Refused.Count > 0 && log != null)
                    {
                        log.LogInformation("Art director, {Chapter}: {Kept} kept after {Attempts} attempt(s), reason {Reason}; refused: {Refused}",
                            planned.Call.Chapter, result.Directions.Count, result.Attempts, result.Reason ?? "none",
                            string.Join(" | ", result.Refused.Select(r => r.Key + ": " + string.Join(", ", r.Value))));
                    }

                    if (result.Directions.Count == 0)
                    {
                        run.Row(planned.RowId, BookRowStatus.Kept, result.Reason ?? "checks");
                        return;
                    }

                    var suggestions = result.Directions.Select(d => new BookArtSuggestion
                    {
                        Slot = planned.Refs[d.Ref].Slot.Key, Inputs = planned.Refs[d.Ref].Inputs,
                        Depicts = d.Depicts, Visual = d.Visual, Mood = d.Mood, Model = model.Model
                    }).ToList();
                    await saving.WaitAsync();
                    try
                    {
                        // Tagged with the edition whose source was read: the repository is told nothing else.
                        if (!await store.SaveSuggestionsAsync(p.Dm, p.Tag, suggestions, present))
                        {
                            run.SaveFailed();
                            run.Cancel();
                        }
                    }
                    finally
                    {
                        saving.Release();
                    }

                    run.Row(planned.RowId, BookRowStatus.Refined, result.Refused.Count > 0 ? "some" : null);
                }
                finally
                {
                    slots.Release();
                }
            }

            await Task.WhenAll(p.Calls.Select(One));
            await SpendAsync();
            run.Finish(run.Token.IsCancellationRequested ? BookRunStatus.Stopped : BookRunStatus.Done, null);
        }
        catch (Exception ex)
        {
            try { await SpendAsync(); } catch { }
            run.Finish(BookRunStatus.Failed, AiFailure.From(ex).Detail);
        }
        finally
        {
            model.Dispose();
            gate.Exit(run.UserId);
        }

        async Task SpendAsync()
        {
            if (Interlocked.Exchange(ref spentOnce, 1) != 0) return;
            var spent = budget.Snapshot();
            if (spent.Calls == 0) return;
            if (!await store.AddDirectorSpendAsync(p.Dm, new BookArtSpend(spent.InputTokens, spent.OutputTokens, spent.Calls, spent.Estimated, spent.Cost(model.Price), model.Price?.Label)))
            {
                run.SaveFailed();
            }
        }
    }
}
