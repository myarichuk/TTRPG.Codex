using System.Collections.Concurrent;
using Codex.Core.Consistency;
using Codex.Persistence;

namespace Codex.Web.Services;

// What the chat's list_issues tool reads: the consistency checker's open issues (docs/WORLD_BUILDER.md, section 9).
public sealed partial class WorldBuilderService
{
    /// <summary>
    /// The open issues <paramref name="scope"/> and <paramref name="severity"/> ask for, as <see cref="IssueListing"/> writes them. The
    /// checks run in code; nothing here asks a model whether something is broken. Issues the DM dismissed are not listed. A scope or a
    /// severity that is not one of the known ones is refused in words ("Nothing was listed"), never answered with an empty list that
    /// would read as all clear. DM only.
    /// </summary>
    public async Task<string> IssuesAsync(CampaignDocument campaign, CampaignAccess access, string? scope, string? severity)
    {
        DmOnly(access);
        if (!IssueFilter.IsKnownScope(scope))
        {
            var shown = scope!.Length > 80 ? scope[..80] + "…" : scope;
            var noise = string.Join(", ", CheckNames.Noise);
            return $"Nothing was listed: \"{shown}\" is not a scope. Use \"all\" (or leave it out), the name of a check ({string.Join(", ", CheckNames.All)}), " +
                   $"or one thing as page:<id>, actor:<id>, place:<id> or fact:<id>, with an issue's entity_id; \"noise\" lists the checks that are off by default ({noise}).";
        }

        if (!IssueListing.TryParseSeverity(severity, out var least))
        {
            var shown = severity!.Length > 40 ? severity[..40] + "…" : severity;
            return $"Nothing was listed: \"{shown}\" is not a severity. Use {string.Join(", ", IssueListing.SeverityNames)} (the least severe level to list), or leave it out for all.";
        }

        if (consistency == null) return "Nothing was listed: the consistency checker is not available here.";
        return IssueListing.Render(await consistency.ListAsync(campaign, access, new IssueFilter(scope, least)));
    }

    /// <summary>
    /// The open issues right now, one full snapshot, for the apply path to compare before and after a save; null when no checker is
    /// available here. Dismissed issues are not in it. DM only. The caller treats a throw as "could not re-check", never as a failed apply.
    /// </summary>
    public async Task<IReadOnlyList<Issue>?> OpenIssuesAsync(CampaignDocument campaign, CampaignAccess access, CancellationToken cancel = default)
    {
        DmOnly(access);
        return consistency == null ? null : (await consistency.CheckAsync(campaign, access, cancel: cancel)).Open;
    }

    /// <summary>How long the per-message count note may take before it is left out. The checks are pure code over one snapshot and are quick
    /// for any real campaign; this only means a pathological one can never hold up a chat message.</summary>
    public TimeSpan IssueSummaryBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a campaign whose count note went over <see cref="IssueSummaryBudget"/> goes without one: its next messages skip the
    /// check at once instead of paying the budget again each time. Then the note is tried again.</summary>
    public TimeSpan IssueSummaryBackoff { get; set; } = TimeSpan.FromMinutes(10);

    // The campaigns whose note went over the budget, and until when it is skipped. Per service (a singleton in the app, one per test), never static.
    private readonly ConcurrentDictionary<string, DateTimeOffset> summarySkippedUntil = new(StringComparer.Ordinal);

    /// <summary>The clock the budget and <see cref="IssueSummaryBackoff"/> are read on (the system's, unless a test sets its own).</summary>
    public TimeProvider Clock { get; set; } = clock ?? TimeProvider.System;

    /// <summary>The one line the chat adds to the DM's message, counts only (never the list): null when nothing is open, when no checker
    /// is available, when the check fails, or when it takes longer than <see cref="IssueSummaryBudget"/> (a message is never held up by it). A campaign
    /// that went over the budget is skipped, silently and at once, for <see cref="IssueSummaryBackoff"/>, so a pathological one costs the budget once
    /// in that while, not on every message. Only the note is skipped: <c>list_issues</c>, the Issues panel and the apply's re-check run in full, with no
    /// budget. Deterministic: the same issues give the same words.</summary>
    public async Task<string?> IssueSummaryAsync(CampaignDocument campaign, CampaignAccess access)
    {
        if (summarySkippedUntil.TryGetValue(campaign.Id, out var until))
        {
            if (Clock.GetUtcNow() < until) return null;
            summarySkippedUntil.TryRemove(campaign.Id, out _);
        }

        using var budget = new CancellationTokenSource(IssueSummaryBudget, Clock);
        try
        {
            return IssueSummaryOf(await OpenIssuesAsync(campaign, access, budget.Token));
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            // Over the budget: no note (silently), the message goes on, and the next ones do not wait for it either.
            if (summarySkippedUntil.Count >= MaxSkipped) summarySkippedUntil.Clear();
            summarySkippedUntil[campaign.Id] = Clock.GetUtcNow() + IssueSummaryBackoff;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failed: no note, the message goes on.
            return null;
        }
    }

    // A bound on the campaigns remembered as over the budget; past it the list starts again (each then pays the budget once more).
    private const int MaxSkipped = 1000;

    internal static string? IssueSummaryOf(IReadOnlyList<Issue>? open)
    {
        if (open is not { Count: > 0 }) return null;
        int Count(IssueSeverity s) => open.Count(i => i.Severity == s);
        return $"(A note from the app, not from the game master: {open.Count} open issues ({Count(IssueSeverity.Error)} errors, {Count(IssueSeverity.Warning)} warnings, " +
               $"{Count(IssueSeverity.Info)} info); call list_issues for details.)";
    }
}
