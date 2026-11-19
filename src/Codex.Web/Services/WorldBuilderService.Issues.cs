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

    /// <summary>The one line the chat adds to the DM's message, counts only (never the list): null when nothing is open, when no checker
    /// is available, when the check fails, or when it takes longer than <see cref="IssueSummaryBudget"/> (a message is never held up by it). Deterministic: the same issues give the same words.</summary>
    public async Task<string?> IssueSummaryAsync(CampaignDocument campaign, CampaignAccess access)
    {
        using var budget = new CancellationTokenSource(IssueSummaryBudget);
        try
        {
            return IssueSummaryOf(await OpenIssuesAsync(campaign, access, budget.Token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || budget.IsCancellationRequested)
        {
            // Over the budget or failed: no note (silently), the message goes on.
            return null;
        }
    }

    internal static string? IssueSummaryOf(IReadOnlyList<Issue>? open)
    {
        if (open is not { Count: > 0 }) return null;
        int Count(IssueSeverity s) => open.Count(i => i.Severity == s);
        return $"(A note from the app, not from the game master: {open.Count} open issues ({Count(IssueSeverity.Error)} errors, {Count(IssueSeverity.Warning)} warnings, " +
               $"{Count(IssueSeverity.Info)} info); call list_issues for details.)";
    }
}
