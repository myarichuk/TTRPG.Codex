using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Web.Book;

/// <summary>One slot the GM ticked to draw, and the visual description they want it drawn from (null: the suggestion's own).</summary>
public sealed record BookDrawPick(string Slot, string? Visual = null);

/// <summary>A ticked slot as the run would treat it. <paramref name="Problem"/> is why it would not be drawn: "slot" (not one of this edition's),
/// "suggestion" (none made for this edition), "stale" (the text changed since it was made), "visual" (the description fails the checks),
/// "repeat" (ticked twice), "many" (past the most one run draws); null when it would be drawn. <paramref name="Replaces"/>: the slot already
/// holds this edition's picture, which drawing puts back among the kept versions. <paramref name="Drops"/>: the slot already keeps
/// <see cref="BookArtDocument.MaxVersions"/> versions, so drawing drops the oldest kept one.</summary>
public sealed record BookDrawItem(string Slot, string Label, bool Replaces, string? Problem, IReadOnlyList<string> VisualProblems, bool Drops = false);

/// <summary>What drawing the ticked slots would do and cost now; nothing is drawn. <paramref name="Cost"/> is pictures × the price per picture,
/// null when no price is set.</summary>
public sealed record BookDrawPreview(IReadOnlyList<BookDrawItem> Items, int Pictures, BookImageInfo? Model, decimal? Cost, BookArtStyle Style, BookDrawTotals? Totals);

/// <summary>What drawing has cost this book over all runs.</summary>
public sealed record BookDrawTotals(int Pictures, decimal Cost, int Unpriced, string PriceLabel);

/// <summary>
/// Drawing a book's pictures (docs/BOOK_EXPORT.md 28.3, step 6). The GM ticks slots that have a suggestion; nothing is drawn without that
/// click, and a run draws at most <see cref="MaxPerRun"/>. When a run starts every pick is checked again here, from the edition's source as
/// it is read now, and the client gives only slot keys and the visual descriptions it may have edited: the slot must be one this edition
/// makes, its suggestion must be this edition's and made from the slot's current text, and the visual must pass
/// <see cref="BookImagePrompt.Check"/> against the edition's names. The prompt is assembled by code from the slot's kind, that visual, the
/// suggestion's mood and the book's style. The picture is stored under the edition's tag, run through the sanitizer like an upload, and
/// is put in use, the one it puts back kept as an earlier version (at most <see cref="BookArtDocument.MaxVersions"/>). Pictures are drawn one at a time, set aside up front on the server's key, never retried, and a run takes
/// the same per-GM gate as chapter builds and the art director (<see cref="BookGate"/>).
/// </summary>
public sealed class BookArtDrawing(BookSourceReader reader, IBookArtRepository store, IBookImages images, Func<string, string, Task<CampaignAccess?>> resolve, BookGate gate, TimeProvider? clock = null,
    StorageQuota? quota = null)
{
    public const int MaxPerRun = 12;

    private const string InputsVersion = "book-draw-1";

    private readonly ConcurrentDictionary<string, BookRun> _runs = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static string KeyOf(string campaignId, string userId, string edition) => campaignId + "|" + userId + "|" + edition + "|draw";

    public BookRun? Find(CampaignAccess dm, string edition) => _runs.GetValueOrDefault(KeyOf(dm.CampaignId, dm.UserId, edition));

    /// <summary>A pick that will be drawn: everything the picture is made from, read on the server.</summary>
    private sealed record Ready(string Slot, string Label, string Kind, string Visual, string Mood, string Prompt, ArtShape Shape, string Inputs);

    private sealed record Prepared(CampaignAccess Dm, string Tag, BookArtStyle Style, BookArtDocument? Stored, IReadOnlyList<BookDrawItem> Items, IReadOnlyList<Ready> Ready);

    private async Task<Prepared?> PrepareAsync(CampaignAccess asked, string edition, IReadOnlyList<BookDrawPick> picks, string language)
    {
        BookAudience? audience = edition switch { "dm" => BookAudience.Dm, "party" => BookAudience.Party, _ => null };
        if (audience is not { } which) return null;
        if (await resolve(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return null;
        if (await reader.ReadAsync(dm, which) is not { } source) return null;
        var compiled = BookComposer.Compose(source, language, out var entries);
        var slots = BookSlots.For(source, compiled, entries);
        var outline = BookArtOutline.For(source, compiled, entries, slots).SelectMany(c => c.Slots).ToDictionary(o => o.Slot.Key, StringComparer.Ordinal);
        var names = BookArtOutline.Names(source, entries);
        var stored = await store.GetAsync(dm);
        var tag = BookSlots.Tag(which);
        var style = BookArtStyles.Normalize(stored?.Style);
        // The GM's own style guide, when they wrote one, in place of the book's choices (41.23).
        var guide = await images.StyleAsync(dm.UserId);

        var items = new List<BookDrawItem>();
        var ready = new List<Ready>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pick in picks)
        {
            var key = pick.Slot ?? string.Empty;
            var label = outline.TryGetValue(key, out var known) ? known.Slot.Label : string.Empty;
            var replaces = stored?.Pictures.Any(p => p.Slot == key && p.BuiltFrom == tag) == true;
            var drops = stored?.DroppedByNew(key, tag) != null;
            BookDrawItem Refuse(string why, IReadOnlyList<string>? visual = null) => new(key, label, replaces, why, visual ?? [], drops);

            if (!seen.Add(key)) { items.Add(Refuse("repeat")); continue; }
            if (known == null) { items.Add(Refuse("slot")); continue; }
            // Only this edition's suggestion, and only one made from what the slot holds now: an older one may hold words since taken out.
            var suggestion = stored?.Suggestions.FirstOrDefault(s => s.Slot == key && s.BuiltFrom == tag);
            if (suggestion == null) { items.Add(Refuse("suggestion")); continue; }
            if (suggestion.Inputs != known.Inputs) { items.Add(Refuse("stale")); continue; }
            var visual = string.IsNullOrWhiteSpace(pick.Visual) ? suggestion.Visual : pick.Visual.Trim();
            var prompt = BookImagePrompt.Assemble(known.Kind, visual, style, suggestion.Mood, names, guide);
            if (prompt.Prompt == null) { items.Add(Refuse("visual", prompt.Problems)); continue; }
            if (ready.Count >= MaxPerRun) { items.Add(Refuse("many")); continue; }

            items.Add(new BookDrawItem(key, label, replaces, null, [], drops));
            ready.Add(new Ready(key, label, known.Kind, visual, suggestion.Mood, prompt.Prompt, ShapeFor(known.Kind), InputsOf(known.Kind, visual, suggestion.Mood, style, guide)));
        }

        return new Prepared(dm, tag, style, stored, items, ready);
    }

    /// <summary>The frame a slot kind is drawn in. The header's box is far wider than any shape a model draws, so its picture is cropped to a band.</summary>
    public static ArtShape ShapeFor(string kind) => kind switch
    {
        "cover" or "portrait" or "homebrew" => ArtShape.Tall,
        "spot" => ArtShape.Square,
        _ => ArtShape.Wide
    };

    /// <summary>A hash of what a picture is drawn from, kept beside it so the tray can say it was drawn from an older description.</summary>
    public static string InputsOf(string kind, string visual, string mood, BookArtStyle style, string? guide = null)
    {
        var s = BookArtStyles.Normalize(style);
        // With a guide its words are the style; without one the hash is as it always was, so no picture drawn before turns "older".
        var text = Codex.Core.AI.ImageStyleGuide.Clean(guide) is { } own
            ? string.Join('\n', InputsVersion, kind, visual.Trim(), mood, "guide", own)
            : string.Join('\n', InputsVersion, kind, visual.Trim(), mood, s.Medium, s.Palette, s.Line, s.Light);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 12).ToLowerInvariant();
    }

    /// <summary>What drawing the ticked slots would do and cost now; draws nothing.</summary>
    public async Task<BookDrawPreview?> PreviewAsync(CampaignAccess asked, string edition, IReadOnlyList<BookDrawPick> picks, string language = "en")
    {
        if (await PrepareAsync(asked, edition, picks, language) is not { } p) return null;
        var model = await images.InfoAsync(p.Dm.UserId);
        return new BookDrawPreview(p.Items, p.Ready.Count, model, model?.Price is { } price ? price.PerImage * p.Ready.Count : null, p.Style, Totals(p.Stored));
    }

    private static BookDrawTotals? Totals(BookArtDocument? art) => art is not { DrawnPictures: > 0 } a ? null
        : new BookDrawTotals(a.DrawnPictures, a.DrawCost, a.DrawUnpricedPictures, a.DrawPriceLabel);

    /// <summary>Sets the book's style for every picture drawn from now on. Pictures already drawn are kept.</summary>
    public async Task<bool> SetStyleAsync(CampaignAccess asked, BookArtStyle style)
    {
        if (await resolve(asked.CampaignId, asked.UserId) is not { IsDm: true } dm) return false;
        return await store.SetStyleAsync(dm, style);
    }

    /// <summary>Starts drawing in the background. Refused before anything is drawn: another paid run of this GM's under way
    /// (<see cref="BookBuildBusyException"/>), no picture model, the server's allowance. Null when the book is not the caller's or no pick
    /// can be drawn.</summary>
    public async Task<BookRun?> StartAsync(CampaignAccess asked, string edition, IReadOnlyList<BookDrawPick> picks, string language = "en")
    {
        if (!gate.TryEnter(asked.UserId)) throw new BookBuildBusyException();
        var launched = false;
        try
        {
            var run = await StartCoreAsync(asked, edition, picks, language);
            launched = run != null;
            return run;
        }
        finally
        {
            if (!launched) gate.Exit(asked.UserId);
        }
    }

    private async Task<BookRun?> StartCoreAsync(CampaignAccess asked, string edition, IReadOnlyList<BookDrawPick> picks, string language)
    {
        if (await PrepareAsync(asked, edition, picks, language) is not { } p || p.Ready.Count == 0) return null;
        // No room left for the pictures: refused before any is paid for (docs/SERVER_ACCESS_AND_VAULT.md 15).
        if (quota != null) await quota.EnsureRoomBeforeDrawingAsync(p.Dm.CampaignId);

        var model = await images.OpenAsync(p.Dm.UserId, p.Ready.Count);
        try
        {
            var key = KeyOf(p.Dm.CampaignId, p.Dm.UserId, edition);
            var run = new BookRun(key, p.Dm.UserId, p.Ready.Select(r => new BookRunRow(r.Slot, r.Label, BookRowStatus.Queued, null)).ToList(), new BookBudget(1), null, model.Model);
            foreach (var old in _runs.Where(r => !r.Value.Running && _clock.GetUtcNow().UtcDateTime - r.Value.Started > TimeSpan.FromHours(1)).Select(r => r.Key)) _runs.TryRemove(old, out _);
            _runs[key] = run;
            run.Completion = Task.Run(() => ExecuteAsync(run, p, model));
            return run;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    private async Task ExecuteAsync(BookRun run, Prepared p, BookImageHandle model)
    {
        var drawn = 0;
        try
        {
            string? stop = null;
            foreach (var item in p.Ready)
            {
                if (stop != null || run.Token.IsCancellationRequested)
                {
                    run.Row(item.Slot, BookRowStatus.Skipped, stop ?? "cancelled");
                    continue;
                }

                // The limit reached by the pictures already drawn: the rest are not drawn, so none is paid for and then thrown away.
                if (quota != null && !await quota.HasRoomAsync(p.Dm.CampaignId))
                {
                    stop = "storage";
                    run.Row(item.Slot, BookRowStatus.Skipped, stop);
                    continue;
                }

                GeneratedImage picture;
                try
                {
                    picture = await model.DrawAsync(item.Prompt, item.Shape, run.Token);
                }
                catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
                {
                    run.Row(item.Slot, BookRowStatus.Skipped, "cancelled");
                    continue;
                }
                catch (LocalizedException)
                {
                    // The server's allowance, refused before anything was sent: every later picture would meet it too.
                    stop = "allowance";
                    run.Row(item.Slot, BookRowStatus.Skipped, stop);
                    continue;
                }
                catch (Exception ex)
                {
                    // Never drawn again by itself: a picture may have been paid for even when the reply was lost.
                    var failure = AiFailure.From(ex);
                    if (failure.Kind is AiFailureKind.Auth or AiFailureKind.NotFound) stop = "auth";
                    else if (failure.Kind is AiFailureKind.RateLimit) stop = "ratelimit";
                    run.Row(item.Slot, BookRowStatus.Kept, stop ?? "failed");
                    continue;
                }

                drawn++;
                // A drawn picture is untrusted bytes like an upload: re-encoded, or refused.
                if (ArtService.Problem(picture.ContentType, picture.Bytes.Length) != null
                    || BookImageSanitizer.TrySanitize(picture.Bytes, out var image) != BookImageProblem.None || image == null
                    || !RavenMediaStore.AllowedContentTypes.Contains(image.ContentType))
                {
                    run.Row(item.Slot, BookRowStatus.Kept, "image");
                    continue;
                }

                // The edition's tag, the one the suggestion was checked against: the request names no tag.
                bool stored;
                try
                {
                    stored = await store.PutAsync(p.Dm, item.Slot, p.Tag, item.Label, image, "drawn", new BookArtDrawn(item.Inputs, item.Visual));
                }
                catch (LocalizedException ex) when (ex.Key == "Service.Storage.Full")
                {
                    stop = "storage";
                    run.Row(item.Slot, BookRowStatus.Kept, stop);
                    continue;
                }

                if (!stored)
                {
                    run.SaveFailed();
                    run.Cancel();
                    run.Row(item.Slot, BookRowStatus.Kept, "save");
                    continue;
                }

                run.Row(item.Slot, BookRowStatus.Refined, null);
            }

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
            var count = Interlocked.Exchange(ref drawn, 0);
            if (count == 0) return;
            if (!await store.AddDrawSpendAsync(p.Dm, count, model.Price is { } price ? price.PerImage * count : null, model.Price?.Label)) run.SaveFailed();
        }
    }
}
