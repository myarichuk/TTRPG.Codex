using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;

namespace Codex.Web.Book;

/// <summary>How a "Bring up to date" went, before or after the model.</summary>
public enum BookMergeOutcome
{
    /// <summary>The pre-flight: ready. After: a merged chapter to review.</summary>
    Ready,
    /// <summary>Not the caller's draft, or no such chapter, or the campaign no longer has it.</summary>
    NotAllowed,
    /// <summary>The campaign has not changed since the chapter's base: nothing to bring in.</summary>
    UpToDate,
    /// <summary>The chapter changed since the editor read it: nothing was done.</summary>
    Stale,
    /// <summary>An entry is in the chapter twice (copied with its marker): the merge cannot tell which is which.</summary>
    Duplicate,
    /// <summary>Another paid book run of this GM's is under way.</summary>
    Busy
}

/// <summary>What the merge would do, before anything is sent: how many entries come from the campaign, stay the GM's, are new, are offered for
/// removal or back, and conflict (both changed). <paramref name="Sendable"/> of the conflicts can go to the model (their pieces line up); the rest
/// are offered as the campaign's text. <paramref name="Preflight"/> and <paramref name="Model"/> only when something would be sent.</summary>
public sealed record BookMergePreview(BookMergeOutcome Outcome, int Theirs = 0, int Mine = 0, int Added = 0, int Removed = 0, int Restored = 0, int Conflicts = 0,
    int Sendable = 0, BookPreflight? Preflight = null, BookModelInfo? Model = null);

/// <summary>A merged chapter to review against <paramref name="Original"/> (the GM's text, as stored). <paramref name="Key"/> is the campaign's
/// version it was merged with, for the save. <paramref name="Merged"/> conflicts came back from the model; <paramref name="Fallback"/> are shown as
/// the campaign's text (no model, pieces that do not line up, or a reply that failed its checks: <paramref name="Reason"/> says the last).</summary>
public sealed record BookMergeProposal(BookMergeOutcome Outcome, string Original = "", string? Replacement = null, string Key = "", int Conflicts = 0,
    int Merged = 0, int Fallback = 0, string? Reason = null, long Tokens = 0, decimal? Cost = null);

/// <summary>How keeping a merge went.</summary>
public enum BookMergeSave { Saved, NotAllowed, Conflict, Moved, TooLarge }

/// <summary>
/// "Bring up to date" for an edited chapter the campaign moved on from (docs/BOOK_EXPORT.md 41.3, 41.18). The code merges entry by entry
/// (<see cref="BookDraftMerge.Plan"/>); a model is asked only about the entries both the GM and the campaign changed, one call each, in turn,
/// under the per-GM <see cref="BookGate"/> and one <see cref="BookBudget"/> capped at every call's worst case, with the server's requests set aside
/// up front. With no conflicts, no model, or when the GM says so, nothing is sent: a conflict is then offered as the campaign's text, which the GM
/// may reject in the review to keep their own. Nothing is saved until the GM keeps the review (<see cref="SaveAsync"/>).
/// </summary>
public sealed class BookDraftMerges(BookDrafts drafts, IBookModels models, BookGate gate, ISystemCatalog catalog, ILogger<BookDraftMerges>? log = null)
{
    private sealed record Prepared(BookDraftMergeInput Input, BookMergePlan Plan, IReadOnlyList<BookMergeRequest> Requests);

    private async Task<(BookMergeOutcome Outcome, Prepared? Prepared)> PrepareAsync(CampaignAccess asked, string edition, string language, string section, int version)
    {
        if (await drafts.MergeInputAsync(asked, edition, language, section) is not { } input || input.Now == null) return (BookMergeOutcome.NotAllowed, null);
        if (input.Version != version) return (BookMergeOutcome.Stale, null);
        if (!input.Moved) return (BookMergeOutcome.UpToDate, null);
        if (BookDraftMerge.Plan(input.Base, input.Markdown, input.Now.Markdown) is not { } plan) return (BookMergeOutcome.Duplicate, null);
        var name = catalog.GetLocalizedDefinition(input.SystemId) == null ? null : catalog.GetDisplayName(input.SystemId);
        var requests = plan.Units.Select(BookDraftMerge.Conflict).OfType<BookMergeConflict>()
            .Where(c => BookTokens.Estimate(c.Mine.Text) <= BookDraftEdits.MaxPassageTokens)
            .Select(c => new BookMergeRequest(input.Now.Title, language, name, c.Mine, c.Base, c.Theirs) { Id = c.Id })
            .ToList();
        return (BookMergeOutcome.Ready, new Prepared(input, plan, requests));
    }

    /// <summary>What the merge would do and, for its conflicts, send and cost; sends nothing.</summary>
    public async Task<BookMergePreview> PreviewAsync(CampaignAccess asked, string edition, string language, string section, int version)
    {
        var (outcome, p) = await PrepareAsync(asked, edition, language, section, version);
        if (p == null) return new BookMergePreview(outcome);
        var plan = p.Plan;
        var model = p.Requests.Count == 0 ? null : await models.InfoAsync(p.Input.Dm.UserId);
        var preflight = model == null ? null : BookPreflight.Estimate([.. p.Requests.Select(BookMergePrompts.LoadFor)], 0, model.Price);
        return new BookMergePreview(BookMergeOutcome.Ready, plan.Count(BookMergeKind.Theirs), plan.Count(BookMergeKind.Mine), plan.Count(BookMergeKind.Added),
            plan.Count(BookMergeKind.Removed), plan.Count(BookMergeKind.Restored), plan.Count(BookMergeKind.Conflict), p.Requests.Count, preflight, model);
    }

    /// <summary>The merged chapter, for review. <paramref name="useAi"/>: send the conflicts to the GM's model; otherwise (or with none) each
    /// conflict is the campaign's text. Refused before anything is sent: another paid run of this GM's, the server's allowance (its own message,
    /// as a <see cref="LocalizedException"/>).</summary>
    public async Task<BookMergeProposal> MergeAsync(CampaignAccess asked, string edition, string language, string section, int version, bool useAi,
        CancellationToken cancel = default)
    {
        var entered = useAi && gate.TryEnter(asked.UserId);
        if (useAi && !entered) return new BookMergeProposal(BookMergeOutcome.Busy);
        try
        {
            var (outcome, p) = await PrepareAsync(asked, edition, language, section, version);
            if (p == null) return new BookMergeProposal(outcome);
            var conflicts = p.Plan.Count(BookMergeKind.Conflict);
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            string? reason = null;
            long tokens = 0;
            decimal? cost = null;
            if (useAi && p.Requests.Count > 0)
            {
                BookModelHandle? model = null;
                try
                {
                    model = await models.OpenAsync(p.Input.Dm.UserId, BookEditWorker.MaxAttempts * p.Requests.Count);
                }
                catch (AiNotConfiguredException)
                {
                    reason = "nomodel";
                }

                if (model != null)
                {
                    using (model)
                    {
                        var budget = new BookBudget(p.Requests.Sum(BookMergePrompts.CapFor));
                        var worker = new BookEditWorker(model.Chat, budget, temperature: 0.3f, refusedBeforeSending: model.RefusedBeforeSending);
                        foreach (var request in p.Requests)
                        {
                            if (cancel.IsCancellationRequested) break;
                            var result = await BookMergePrompts.RunAsync(worker, request, cancel);
                            if (result.Replacement != null)
                            {
                                resolved[request.Id] = result.Replacement;
                                continue;
                            }

                            reason = result.Reason;
                            log?.LogInformation("Book merge: a conflict kept the campaign's text after {Attempts} attempt(s), reason {Reason}; problems: {Problems}",
                                result.Attempts, result.Reason, string.Join(" | ", result.Problems));
                            // The provider refused the key, or the money ran out: the rest would fail the same way.
                            if (result.Reason is "auth" or "ratelimit" or "allowance" or "budget" or "cancelled") break;
                        }

                        var spent = budget.Snapshot();
                        tokens = spent.InputTokens + spent.OutputTokens;
                        cost = spent.Cost(model.Price);
                    }
                }
            }

            return new BookMergeProposal(BookMergeOutcome.Ready, p.Input.Markdown, p.Plan.Text(resolved), p.Input.Now!.Key, conflicts, resolved.Count,
                conflicts - resolved.Count, reason, tokens, cost);
        }
        finally
        {
            if (entered) gate.Exit(asked.UserId);
        }
    }

    /// <summary>Keeps what the GM accepted of a merge: <paramref name="markdown"/> becomes the chapter, the campaign's chapter now its base. Refused
    /// (Moved, nothing saved) when the campaign changed again since the merge was made; the GM brings it up to date again.</summary>
    public async Task<(BookMergeSave Result, int Version)> SaveAsync(CampaignAccess asked, string edition, string language, string section, int version,
        string markdown, string key)
    {
        if (await drafts.SaveMergeAsync(asked, edition, language, section, version, markdown, key) is not { } saved) return (BookMergeSave.Moved, version);
        return (saved.Result switch
        {
            BookDraftSave.Saved => BookMergeSave.Saved,
            BookDraftSave.Conflict => BookMergeSave.Conflict,
            BookDraftSave.TooLarge => BookMergeSave.TooLarge,
            _ => BookMergeSave.NotAllowed
        }, saved.Version);
    }
}
