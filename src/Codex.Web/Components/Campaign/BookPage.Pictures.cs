using System.Globalization;
using Codex.Core.AI;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Book;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Codex.Web.Components.Campaign;

/// <summary>
/// Pictures in the text (docs/BOOK_EXPORT.md 53): the chips the editor shows under the entries' headings. The page reads the entries' picture
/// places the way a drawing reads them (<see cref="BookArtDrawing.ChipsAsync"/>) and gives them to the script; a chip's buttons come back here.
/// What a chip posts is only a slot key and the name of an action: the upload, the art director and the drawing each read the edition's source
/// again and check the key against it (invariants 9 to 11), and each paid run takes the GM's one gate.
/// </summary>
public partial class BookPage
{
    [Inject] private BookArtService Pictures { get; set; } = null!;
    [Inject] private BookArtDirection ArtDirector { get; set; } = null!;
    [Inject] private BookArtDrawing ArtDrawing { get; set; } = null!;

    /// <summary>Changes when a chip put a picture in, started a run or dismissed a suggestion: the dock's tray reads its slots again.</summary>
    internal int artStamp;

    internal string? chipNote;
    internal bool chipNoteError;

    private BookChips? chips;

    // The run the chips show as under way: one a chip started, or one the tray did. "draw" or "suggest".
    private BookRun? chipRun;
    private string? chipRunKind;
    private readonly HashSet<string> chipPlaced = new(StringComparer.Ordinal);

    // The place the next file picked in the page's file field is for (the chip said so just before opening the field).
    private string? chipSlot;

    private void ChipNote(string? text, bool error = false) => (chipNote, chipNoteError) = (text, error);

    internal Dictionary<string, string> ChipWords() => new()
    {
        ["group"] = L["Campaign.Book.Chip.Group"],
        ["add"] = L["Campaign.Book.Chip.Add"],
        ["upload"] = L["Campaign.Book.Chip.Upload"],
        ["suggest"] = L["Campaign.Book.Chip.Suggest"],
        ["suggestAgain"] = L["Campaign.Book.Chip.SuggestAgain"],
        ["suggestYes"] = L["Campaign.Book.Chip.SuggestYes"],
        ["asking"] = L["Campaign.Book.Chip.Asking"],
        ["picture"] = L["Campaign.Book.Chip.Picture"],
        ["draw"] = L["Campaign.Book.Chip.Draw"],
        ["drawYes"] = L["Campaign.Book.Chip.DrawYes"],
        ["dismiss"] = L["Campaign.Book.Chip.Dismiss"],
        ["cancel"] = L["Common.Cancel"],
        ["stop"] = L["Campaign.Book.Ai.Stop"],
        ["noModel"] = L["Campaign.Book.Chip.NoModel"],
        ["setUp"] = L["Campaign.Book.Ai.SetUp"],
        ["stale"] = L["Campaign.Book.Chip.Stale"],
        ["drawing"] = L["Campaign.Book.Chip.Drawing"],
        ["suggesting"] = L["Campaign.Book.Chip.Suggesting"],
        ["prompt"] = L["Campaign.Book.Chip.Prompt"],
        ["problem"] = L["Campaign.Book.Chip.Problem"],
        ["ready"] = L["Campaign.Book.Chip.Ready"],
        ["place"] = L["Campaign.Book.Chip.Place"]
    };

    /// <summary>The entries' picture places read again and given to the editor: after it opens, and whenever a picture or a suggestion changed.</summary>
    internal async Task ChipsAsync()
    {
        if (module == null || editorId == null) return;
        chips = await ArtDrawing.ChipsAsync(Access, edition, language);
        ChipWatch();
        await ChipPushAsync();
    }

    // A run under way for this edition (the tray may have started it) is shown in the chips, and its pictures go into the text as they are made.
    private void ChipWatch()
    {
        if (chipRun is { Running: true }) return;
        var (run, kind) = ArtDrawing.Find(Access, edition) is { Running: true } draws ? (draws, "draw")
            : ArtDirector.Find(Access, edition) is { Running: true } asks ? (asks, "suggest")
            : (null, null);
        if (run != null) ChipAttach(run, kind!);
    }

    private void ChipAttach(BookRun run, string kind)
    {
        if (ReferenceEquals(run, chipRun)) return;
        if (chipRun != null) chipRun.Changed -= OnChipRun;
        (chipRun, chipRunKind) = (run, kind);
        chipPlaced.Clear();
        run.Changed += OnChipRun;
        // It may have ended between starting and listening.
        if (!run.Running) OnChipRun();
    }

    /// <summary>The places read before the editor opens, for it to open with: chips that arrive later push the lines down under the pointer.</summary>
    internal async Task<object?> ChipsForOpenAsync()
    {
        chips = await ArtDrawing.ChipsAsync(Access, edition, language);
        return ChipData();
    }

    private async Task ChipPushAsync()
    {
        if (module == null || editorId == null || ChipData() is not { } data) return;
        try
        {
            await module.InvokeVoidAsync("setChipData", editorId, data);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException)
        {
        }
    }

    private object? ChipData()
    {
        if (chips == null) return null;
        var model = chips.Model;
        var price = model?.Price;
        var each = price == null ? null : AiPrice.Format(price.PerImage, price.Label, CultureInfo.CurrentCulture);
        object? busy = null;
        if (chipRun is { Running: true } run)
        {
            var rows = run.Snapshot().Rows;
            busy = chipRunKind == "draw"
                ? new { kind = "draw", keys = rows.Where(r => r.Status == BookRowStatus.Queued).Select(r => r.Id) }
                // A director's row is a chapter, or a part of a long one ("cast#1").
                : new { kind = "suggest", sections = rows.Where(r => r.Status == BookRowStatus.Queued).Select(r => r.Id[..r.Id.LastIndexOf('#')]).Distinct() };
        }

        return new
        {
            chips = chips.Chips.Select(c => new
            {
                key = c.Slot,
                section = c.Section,
                label = c.Label,
                block = c.Block,
                pictured = c.Pictured,
                idea = c.Idea == null ? null : (object)new { depicts = c.Idea.Depicts, stale = c.Idea.Stale, prompt = c.Idea.Prompt, problems = c.Idea.Problems }
            }),
            model = model != null,
            price = model == null ? null : each != null ? L["Campaign.Book.Chip.Price", each, model.Label].ToString() : L["Campaign.Book.Chip.NoPrice", model.Label].ToString(),
            drawAsk = each != null ? L["Campaign.Book.Chip.DrawAsk", each].ToString() : L["Campaign.Book.Chip.DrawAskNoPrice"].ToString(),
            busy
        };
    }

    private void OnChipRun() => _ = InvokeAsync(async () =>
    {
        if (chipRun is not { } run) return;
        var snapshot = run.Snapshot();
        try
        {
            if (chipRunKind == "draw")
            {
                // Each picture goes under its heading as it is made, not when the whole run ends.
                foreach (var row in snapshot.Rows.Where(r => r.Status == BookRowStatus.Refined))
                {
                    if (chipPlaced.Add(row.Id)) await PicturedAsync(row.Id);
                }
            }

            if (run.Running)
            {
                await ChipPushAsync();
                return;
            }

            if (!ReferenceEquals(run, chipRun)) return;
            run.Changed -= OnChipRun;
            var kind = chipRunKind;
            (chipRun, chipRunKind) = (null, null);
            var failed = snapshot.Rows.FirstOrDefault(r => r.Reason != null && r.Status != BookRowStatus.Refined);
            if (kind == "draw")
            {
                var text = snapshot.Status switch
                {
                    BookRunStatus.Done => L["Campaign.Book.Art.Draw.Done", snapshot.Refined, snapshot.Rows.Count],
                    BookRunStatus.Stopped => L["Campaign.Book.Art.Draw.Stopped", snapshot.Refined, snapshot.Rows.Count],
                    _ => L["Campaign.Book.Ai.Status.Failed", snapshot.Failure ?? string.Empty]
                };
                ChipNote(failed == null ? text : $"{text} {failed.Title}: {L["Campaign.Book.Art.Draw.Reason." + failed.Reason]}", failed != null || snapshot.Status == BookRunStatus.Failed);
            }
            else
            {
                var text = snapshot.Status switch
                {
                    BookRunStatus.Done => L["Campaign.Book.Art.Suggest.Done", snapshot.Refined, snapshot.Rows.Count],
                    BookRunStatus.Stopped => L["Campaign.Book.Art.Suggest.Stopped", snapshot.Refined, snapshot.Rows.Count],
                    _ => L["Campaign.Book.Ai.Status.Failed", snapshot.Failure ?? string.Empty]
                };
                ChipNote(failed == null ? text : $"{text} {failed.Title}: {DirectorReason(failed.Reason!)}", failed != null || snapshot.Status == BookRunStatus.Failed);
            }

            artStamp++;
            await ChipsAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException)
        {
        }

        StateHasChanged();
    });

    private string DirectorReason(string reason) => reason switch
    {
        "budget" or "error" or "auth" or "ratelimit" or "allowance" or "storage" or "cancelled" => L["Campaign.Book.Ai.Reason." + reason],
        "cutoff" or "unreadable" or "some" => L["Campaign.Book.Art.Suggest.Reason." + reason],
        _ => L["Campaign.Book.Art.Suggest.Reason.checks"]
    };

    /// <summary>A picture is now in a place: its block goes under the entry's heading (one edit, undone like typing), and the preview and the
    /// chips read the pictures again. A chapter that is locked or under review keeps its text; the chip there then offers to put it in.</summary>
    private async Task PicturedAsync(string slot)
    {
        chips = await ArtDrawing.ChipsAsync(Access, edition, language);
        var how = "present";
        if (chips?.Chips.FirstOrDefault(c => c.Slot == slot) is { } chip && module != null && editorId != null)
        {
            try
            {
                how = await module.InvokeAsync<string>("placeArt", editorId, chip.Section, chip.Slot, chip.Block);
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException)
            {
            }
        }

        artStamp++;
        await ArtChangedAsync(read: false);
        await ChipPushAsync();
        ChipNote(how == "placed" ? L["Campaign.Book.Chip.Placed"] : how == "busy" ? L["Campaign.Book.Chip.Kept"] : L["Campaign.Book.Art.Stored"]);
    }

    /// <summary>The chip's Upload: the place the file about to be picked is for. Only a key of the right shape is kept; the upload checks it
    /// against the edition's own slots.</summary>
    [JSInvokable]
    public void ChipUploadFor(string slot) => chipSlot = BookSlots.IsKey(slot) ? slot : null;

    internal async Task ChipUploadedAsync(InputFileChangeEventArgs e)
    {
        var slot = chipSlot;
        chipSlot = null;
        if (slot == null) return;
        var file = e.File;
        if (ArtService.Problem(file.ContentType, file.Size) is { } problem)
        {
            ChipNote(problem, true);
            return;
        }

        ChipNote(L["Campaign.Book.Art.Uploading"]);
        StateHasChanged();
        try
        {
            using var buffer = new MemoryStream();
            await using (var stream = file.OpenReadStream(RavenMediaStore.MaxBytes))
            {
                await stream.CopyToAsync(buffer);
            }

            var result = await Pictures.UploadAsync(Access, edition, slot, buffer.ToArray(), file.ContentType, language);
            if (result == BookArtUpload.Stored)
            {
                await PicturedAsync(slot);
                return;
            }

            ChipNote(result switch
            {
                BookArtUpload.BadImage => L["Campaign.Book.Art.BadImage"],
                BookArtUpload.NoSuchSlot => L["Campaign.Book.Art.NoSlot"],
                BookArtUpload.Full => L["Campaign.Book.Art.Full"],
                BookArtUpload.StorageFull => L["Campaign.Book.Art.StorageFull"],
                _ => L["Campaign.Book.Art.NotAllowed"]
            }, true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Book picture upload from the text failed");
            ChipNote(L["Campaign.Book.Art.Failed", L.Describe(ex)], true);
        }
    }

    /// <summary>What the chip says before the art director is asked. <paramref name="Ok"/> false: nothing can be asked (no model, nothing to ask).</summary>
    public sealed record ChipCost(string Text, bool Ok);

    /// <summary>The cost of asking the art director about the chapter a place is in, shown in the chip before anything is sent.</summary>
    [JSInvokable]
    public async Task<ChipCost?> ChipQuote(string slot)
    {
        if (chips?.Chips.FirstOrDefault(c => c.Slot == slot) is not { } chip) return null;
        if (await ArtDirector.PreviewAsync(Access, edition, language, false, chip.Section) is not { } p) return null;
        if (p.Model == null) return new ChipCost(L["Campaign.Book.Ai.NoModel"], false);
        if (p.Preflight is not { } flight) return new ChipCost(L["Campaign.Book.Chip.SuggestNothing"], false);
        var cost = flight.CostLow is { } low && flight.CostHigh is { } high
            ? L["Campaign.Book.Chip.SuggestCost", Money(low, p.Model.Price), Money(high, p.Model.Price)]
            : L["Campaign.Book.Ai.Preflight.NoPrice"];
        return new ChipCost(L["Campaign.Book.Chip.SuggestAsk", p.Slots, TitleOf(chip.Section), p.Model.Label] + " " + cost, true);
    }

    /// <summary>A chip's button: "draw" (asked twice in the chip, the cost first), "suggest" (after its cost), "dismiss", "stop".</summary>
    [JSInvokable]
    public async Task Chip(string kind, string slot)
    {
        if (kind == "stop")
        {
            chipRun?.Cancel();
            return;
        }

        if (!BookSlots.IsKey(slot) || AiBusy || working) return;
        try
        {
            switch (kind)
            {
                case "draw":
                    await ChipDrawAsync(slot);
                    break;
                case "suggest":
                    await ChipSuggestAsync(slot);
                    break;
                case "dismiss":
                    if (await ArtDrawing.DismissAsync(Access, edition, slot)) ChipNote(L["Campaign.Book.Chip.Dismissed"]);
                    artStamp++;
                    await ChipsAsync();
                    break;
            }
        }
        catch (BookBuildBusyException)
        {
            ChipNote(L["Campaign.Book.Ai.Busy"], true);
        }
        catch (AiImageNotConfiguredException)
        {
            ChipNote(L["Campaign.Book.Art.Draw.NoModel"], true);
        }
        catch (AiNotConfiguredException)
        {
            ChipNote(L["Campaign.Book.Ai.NoModel"], true);
        }
        catch (LocalizedException ex)
        {
            ChipNote(L.Describe(ex), true);
        }

        StateHasChanged();
    }

    // One picture, from the place's own suggestion as the server holds it: the page sends the key and nothing else.
    private async Task ChipDrawAsync(string slot)
    {
        BookDrawPick[] picks = [new(slot)];
        var run = await ArtDrawing.StartAsync(Access, edition, picks, language);
        if (run == null)
        {
            // Refused before anything was drawn: the reason the tray would give.
            var item = (await ArtDrawing.PreviewAsync(Access, edition, picks, language))?.Items.FirstOrDefault();
            ChipNote(item?.Problem switch
            {
                null => L["Campaign.Book.Art.Draw.None"],
                "visual" => L["Campaign.Book.Art.Draw.Problem.visual", string.Join(" ", item.VisualProblems)],
                "many" => L["Campaign.Book.Art.Draw.Problem.many", BookArtDrawing.MaxPerRun],
                var why => L["Campaign.Book.Art.Draw.Problem." + why]
            }, true);
            await ChipsAsync();
            return;
        }

        ChipNote(null);
        ChipAttach(run, "draw");
        artStamp++;
        await ChipPushAsync();
    }

    private async Task ChipSuggestAsync(string slot)
    {
        if (chips?.Chips.FirstOrDefault(c => c.Slot == slot) is not { } chip) return;
        var run = await ArtDirector.StartAsync(Access, edition, language, false, chip.Section);
        if (run == null)
        {
            ChipNote(L["Campaign.Book.Chip.SuggestNothing"], true);
            return;
        }

        ChipNote(null);
        ChipAttach(run, "suggest");
        artStamp++;
        await ChipPushAsync();
    }

    private void ChipDispose()
    {
        if (chipRun != null) chipRun.Changed -= OnChipRun;
        chipRun = null;
    }
}
