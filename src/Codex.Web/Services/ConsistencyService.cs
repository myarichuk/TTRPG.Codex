using Codex.Core;
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
    IRegionRepository regions, IFactRepository facts, IConsistencyDismissalRepository dismissals, IHomebrewRepository? homebrew = null)
{
    /// <summary>Every issue in the campaign, <see cref="Issue.Order"/>ed, split by whether the DM dismissed it. The noisy checks (orphan pages and
    /// the like) run only with <paramref name="noise"/>, so by default they are never in the list, the count or the DM's issue panel. The one load the
    /// v2 checks add is the campaign's homebrew list (one query, beside the several the Library's resolver already makes for the sheet's types); the
    /// rest (members, packs, the system's changes and load problems) is in memory. It is always read, so an issue is the same issue whatever scope asked for it.</summary>
    public async Task<ConsistencyReport> CheckAsync(CampaignDocument campaign, CampaignAccess access, bool noise = false)
    {
        DmOnly(access);
        var all = await RunAsync(campaign, access, noise);
        var dismissed = (await dismissals.ListAsync(access)).Select(d => d.IssueId).ToHashSet(StringComparer.Ordinal);
        var (open, set) = IssueDismissals.Split(all, dismissed);
        return new ConsistencyReport(open, set);
    }

    /// <summary>The open issues <paramref name="filter"/> asks for: what the world builder's <c>list_issues</c> tool reads.</summary>
    public async Task<IReadOnlyList<Issue>> ListAsync(CampaignDocument campaign, CampaignAccess access, IssueFilter filter) =>
        filter.Apply((await CheckAsync(campaign, access, filter.WantsNoise)).Open);

    /// <summary>Sets an issue aside. Only an issue that is open right now can be: an id made up, or one already fixed, is refused.
    /// Only the DM does this; no model call ever reaches it.</summary>
    public async Task<bool> DismissAsync(CampaignDocument campaign, CampaignAccess access, string issueId, string? note)
    {
        DmOnly(access);
        var report = await CheckAsync(campaign, access);
        return report.Open.Any(i => i.Id == issueId) && await dismissals.DismissAsync(access, issueId, note);
    }

    /// <summary>What the DM set aside, in the order they did it, including dismissals whose problem is gone (they stay, and apply again if it comes back).
    /// DM only.</summary>
    public Task<IReadOnlyList<IssueDismissal>> DismissalsAsync(CampaignAccess access)
    {
        DmOnly(access);
        return dismissals.ListAsync(access);
    }

    public Task<bool> RestoreAsync(CampaignAccess access, string issueId)
    {
        DmOnly(access);
        return dismissals.RestoreAsync(access, issueId);
    }

    private async Task<IReadOnlyList<Issue>> RunAsync(CampaignDocument campaign, CampaignAccess access, bool noise)
    {
        var system = catalog.GetDefinition(campaign.SystemId);
        var brews = homebrew != null ? (await homebrew.GetVisibleForCampaignAsync(access)).ToList() : null;
        // A system that is not installed has no Library to read: the checks that need one are left out, the rest still run.
        // The sheet's own types are loaded as the sheet needs them; the other entries a check names (a hazard, a creature) are looked up one by one.
        IEntryResolver resolver = system == null
            ? NullEntryResolver.Instance
            : new LookupResolver(await CachedEntryResolver.LoadAsync(library, campaign, access, system), campaign.SystemId, library, brews);
        var context = new SnapshotContext(system != null,
            system != null && campaign.SystemStamp != null ? SnapshotContext.Describe(campaign.SystemStamp.ChangesTo(campaign.SystemId, catalog.GetFingerprint(campaign.SystemId))) : Array.Empty<string>(),
            (catalog as PluginLoader)?.LoadProblems.Where(p => string.Equals(p.SystemId, campaign.SystemId, StringComparison.OrdinalIgnoreCase)).Select(p => p.Text).ToList() ?? new List<string>(),
            system == null ? Array.Empty<string>() : library.PackIds(campaign));

        var snapshot = ConsistencySnapshots.From(
            await wiki.GetVisibleForCampaignAsync(access),
            await actors.GetVisibleForCampaignAsync(access),
            await regions.GetVisibleForCampaignAsync(access),
            await facts.GetVisibleForCampaignAsync(access, new HashSet<string>()),
            campaign, system, resolver, brews, context);
        return ConsistencyChecker.Run(snapshot, noise ? ConsistencyChecker.Default.Concat(ConsistencyChecker.Noisy).ToList() : null);
    }

    /// <summary>The sheet's resolver, and for any other entry a check names (a creature, a hazard, a field of a homebrew entry) the pack's entry or the
    /// campaign's homebrew one: found by id, never by loading a whole type.</summary>
    private sealed class LookupResolver(IEntryResolver inner, string systemId, CampaignLibrary library, IReadOnlyList<HomebrewEntryDocument>? brews) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) =>
            inner.Resolve(type, id) ?? library.PackEntry(systemId, type, id)
            ?? brews?.FirstOrDefault(h => string.Equals(h.Type, type, StringComparison.OrdinalIgnoreCase) && string.Equals(h.EntryId, id, StringComparison.Ordinal))?.ToEntry();

        public IEnumerable<ContentEntry> Candidates(string type) => inner.Candidates(type);
    }

    private static void DmOnly(CampaignAccess access)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
    }
}
