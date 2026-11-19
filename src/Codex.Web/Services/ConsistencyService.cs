using Codex.Core.Consistency;
using Codex.Core.Rules;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>The checker's verdict on a campaign: the issues still open, and the ones the DM set aside.</summary>
public sealed record ConsistencyReport(IReadOnlyList<Issue> Open, IReadOnlyList<Issue> Dismissed);

/// <summary>
/// Runs the consistency checks over a campaign (docs/WORLD_BUILDER.md). Loads the documents with the DM's access, takes one snapshot, and
/// hands it to the pure checker (<see cref="ConsistencyChecker"/>): no model is asked whether anything is broken. DM only. The results are
/// derived each time and never stored; the only state is the DM's dismissals.
/// </summary>
public sealed class ConsistencyService(ISystemCatalog catalog, CampaignLibrary library, IWikiRepository wiki, IActorRepository actors,
    IRegionRepository regions, IFactRepository facts, IConsistencyDismissalRepository dismissals)
{
    /// <summary>Every issue in the campaign, <see cref="Issue.Order"/>ed, split by whether the DM dismissed it.</summary>
    public async Task<ConsistencyReport> CheckAsync(CampaignDocument campaign, CampaignAccess access)
    {
        DmOnly(access);
        var all = await RunAsync(campaign, access);
        var dismissed = (await dismissals.ListAsync(access)).Select(d => d.IssueId).ToHashSet(StringComparer.Ordinal);
        var (open, set) = IssueDismissals.Split(all, dismissed);
        return new ConsistencyReport(open, set);
    }

    /// <summary>The open issues <paramref name="filter"/> asks for: what the world builder's <c>list_issues</c> tool reads.</summary>
    public async Task<IReadOnlyList<Issue>> ListAsync(CampaignDocument campaign, CampaignAccess access, IssueFilter filter) =>
        filter.Apply((await CheckAsync(campaign, access)).Open);

    /// <summary>Sets an issue aside. Only an issue that is open right now can be: an id made up, or one already fixed, is refused.
    /// Only the DM does this; no model call ever reaches it.</summary>
    public async Task<bool> DismissAsync(CampaignDocument campaign, CampaignAccess access, string issueId, string? note)
    {
        DmOnly(access);
        var report = await CheckAsync(campaign, access);
        return report.Open.Any(i => i.Id == issueId) && await dismissals.DismissAsync(access, issueId, note);
    }

    public Task<bool> RestoreAsync(CampaignAccess access, string issueId)
    {
        DmOnly(access);
        return dismissals.RestoreAsync(access, issueId);
    }

    private async Task<IReadOnlyList<Issue>> RunAsync(CampaignDocument campaign, CampaignAccess access)
    {
        var system = catalog.GetDefinition(campaign.SystemId);
        // A system that is not installed has no Library to read: the checks that need one are left out, the rest still run.
        IEntryResolver resolver = system == null
            ? NullEntryResolver.Instance
            : await CachedEntryResolver.LoadAsync(library, campaign, access, system);

        var snapshot = ConsistencySnapshots.From(
            await wiki.GetVisibleForCampaignAsync(access),
            await actors.GetVisibleForCampaignAsync(access),
            await regions.GetVisibleForCampaignAsync(access),
            await facts.GetVisibleForCampaignAsync(access, new HashSet<string>()),
            campaign, system, resolver);
        return ConsistencyChecker.Run(snapshot);
    }

    private static void DmOnly(CampaignAccess access)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
    }
}
