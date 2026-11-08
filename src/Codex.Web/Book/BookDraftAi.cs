using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;

namespace Codex.Web.Book;

/// <summary>How an AI edit request went, before or after the model.</summary>
public enum BookEditOutcome
{
    /// <summary>The pre-flight: ready to send. After sending: a replacement to review.</summary>
    Ready,
    /// <summary>Not the caller's draft, or no such chapter.</summary>
    NotAllowed,
    /// <summary>The chapter changed since the editor read it (another tab, a save not yet made): nothing was sent.</summary>
    Stale,
    /// <summary>No request, or a passage with nothing the model may change.</summary>
    Empty,
    /// <summary>The passage or the request is longer than one edit takes.</summary>
    TooLong,
    /// <summary>Another paid book run of this GM's is under way.</summary>
    Busy,
    /// <summary>The GM has no model set up.</summary>
    NoModel,
    /// <summary>Sent, and no reply passed the checks, or the call failed: see the reason. Nothing changes.</summary>
    Failed
}

/// <summary>What an edit would send and cost. <paramref name="From"/> and <paramref name="To"/> are the selection widened to whole pieces.</summary>
public sealed record BookEditPreview(BookEditOutcome Outcome, int From = 0, int To = 0, int Pieces = 0, BookPreflight? Preflight = null, BookModelInfo? Model = null);

/// <summary>A replacement for the chapter's text between <paramref name="From"/> and <paramref name="To"/>, which was <paramref name="Original"/>
/// when the model was asked. Nothing is saved: the GM reviews it in the editor.</summary>
public sealed record BookEditProposal(BookEditOutcome Outcome, int From = 0, int To = 0, string Original = "", string? Replacement = null, string? Reason = null,
    long Tokens = 0, decimal? Cost = null);

/// <summary>
/// AI edits on a passage of a draft (docs/BOOK_EXPORT.md 41.4, phase 4). The passage is read from the draft as stored, at the version the editor
/// had, and must be the text the editor shows there; it is cut on the server (<see cref="BookDraftEdits.Cut"/>), so no secret, picture, marker
/// or break reaches the model whatever the page sends. The prompt takes the book's shape from the campaign's game system
/// (<see cref="AiVocabulary.BookShape"/>). A request takes the per-GM <see cref="BookGate"/> before anything is awaited, sets aside the
/// server's requests up front, and is held to a cap worked out before it is sent.
/// </summary>
public sealed class BookDraftAi(BookDrafts drafts, IBookModels models, BookGate gate, ISystemCatalog catalog, ILogger<BookDraftAi>? log = null)
{
    private sealed record Prepared(CampaignAccess Dm, BookEditRequest Request);

    /// <summary>The words a request is sent as: a preset's, or the GM's own.</summary>
    public static string? AskText(string? ask) =>
        ask == null ? null : BookEditPrompts.Presets.TryGetValue(ask, out var preset) ? preset : ask.Trim();

    private async Task<(BookEditOutcome Outcome, Prepared? Prepared)> PrepareAsync(CampaignAccess asked, string edition, string language, string section, int version,
        int from, int to, string selected, string? ask)
    {
        var words = AskText(ask);
        if (string.IsNullOrWhiteSpace(words)) return (BookEditOutcome.Empty, null);
        if (words.Length > BookDraftEdits.MaxAskChars) return (BookEditOutcome.TooLong, null);
        if (await drafts.ChapterAsync(asked, edition, language, section) is not { } found) return (BookEditOutcome.NotAllowed, null);
        var text = found.Chapter.Markdown;
        // What the editor showed must be what is stored: the page's offsets are trusted only as far as the text they point at matches.
        if (found.Chapter.Version != version || from < 0 || to > text.Length || from > to || selected.Length != to - from
            || string.CompareOrdinal(text, from, selected, 0, to - from) != 0)
        {
            return (BookEditOutcome.Stale, null);
        }

        // Nothing selected: the whole chapter.
        if (from == to) (from, to) = (0, text.Length);
        if (BookDraftEdits.Cut(text, from, to) is not { } passage) return (BookEditOutcome.Stale, null);
        if (System.Text.RegularExpressions.Regex.Replace(passage.Text, @"\{\{block:\d+\}\}", string.Empty).Trim().Length == 0) return (BookEditOutcome.Empty, null);
        if (BookTokens.Estimate(passage.Text) > BookDraftEdits.MaxPassageTokens) return (BookEditOutcome.TooLong, null);
        var system = catalog.GetLocalizedDefinition(found.SystemId)?.Canonical;
        var name = system == null ? null : catalog.GetDisplayName(found.SystemId);
        return (BookEditOutcome.Ready, new Prepared(found.Dm, new BookEditRequest(found.Chapter.Title, language, name, system?.Ai.BookShape, words, passage)));
    }

    /// <summary>The most one request may spend: the first attempt and a retry that carries the refused reply back, each at its worst case.</summary>
    public static long CapFor(BookEditRequest request)
    {
        var load = BookEditWorker.LoadFor(request);
        var retry = load.InputTokens + BookTokens.Estimate(new string('x', 4000)) + 400;
        return BookBudget.WorstCase(load.InputTokens, load.MaxOutputTokens) + BookBudget.WorstCase(retry, load.MaxOutputTokens);
    }

    /// <summary>What the request would send and cost now; sends nothing.</summary>
    public async Task<BookEditPreview> PreviewAsync(CampaignAccess asked, string edition, string language, string section, int version, int from, int to, string selected, string? ask)
    {
        var (outcome, p) = await PrepareAsync(asked, edition, language, section, version, from, to, selected, ask);
        if (p == null) return new BookEditPreview(outcome);
        var model = await models.InfoAsync(p.Dm.UserId);
        if (model == null) return new BookEditPreview(BookEditOutcome.NoModel, p.Request.Passage.From, p.Request.Passage.To, p.Request.Passage.Pieces.Count);
        var preflight = BookPreflight.Estimate([BookEditWorker.LoadFor(p.Request)], 0, model.Price);
        return new BookEditPreview(BookEditOutcome.Ready, p.Request.Passage.From, p.Request.Passage.To, p.Request.Passage.Pieces.Count, preflight, model);
    }

    /// <summary>Asks the model for the edit and waits for it. Refused before anything is sent: another paid run of this GM's, no model, the
    /// server's allowance (its own message, as a <see cref="LocalizedException"/>).</summary>
    public async Task<BookEditProposal> AskAsync(CampaignAccess asked, string edition, string language, string section, int version, int from, int to, string selected, string? ask,
        CancellationToken cancel = default)
    {
        if (!gate.TryEnter(asked.UserId)) return new BookEditProposal(BookEditOutcome.Busy);
        try
        {
            var (outcome, p) = await PrepareAsync(asked, edition, language, section, version, from, to, selected, ask);
            if (p == null) return new BookEditProposal(outcome);
            var passage = p.Request.Passage;
            BookModelHandle model;
            try
            {
                model = await models.OpenAsync(p.Dm.UserId, BookEditWorker.MaxAttempts);
            }
            catch (AiNotConfiguredException)
            {
                return new BookEditProposal(BookEditOutcome.NoModel);
            }

            using (model)
            {
                var budget = new BookBudget(CapFor(p.Request));
                var result = await new BookEditWorker(model.Chat, budget, refusedBeforeSending: model.RefusedBeforeSending).RunAsync(p.Request, cancel);
                var spent = budget.Snapshot();
                if (result.Replacement == null && log != null)
                {
                    // The checks' words, not the reply: they may quote a marker number, never the campaign's text.
                    log.LogInformation("AI book edit: no replacement after {Attempts} attempt(s), reason {Reason}; problems: {Problems}",
                        result.Attempts, result.Reason, string.Join(" | ", result.Problems));
                }

                return new BookEditProposal(result.Replacement == null ? BookEditOutcome.Failed : BookEditOutcome.Ready, passage.From, passage.To, passage.Original,
                    result.Replacement, result.Reason, spent.InputTokens + spent.OutputTokens, spent.Cost(model.Price));
            }
        }
        finally
        {
            gate.Exit(asked.UserId);
        }
    }
}
