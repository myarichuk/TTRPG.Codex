using Codex.Plugin.Abstractions;

namespace Codex.Persistence;

public class CampaignAccessResolver(ICampaignRepository campaignRepository, ISystemCatalog? systemCatalog = null) : ICampaignAccessResolver
{
    public async Task<CampaignAccess?> ResolveAsync(string campaignId, string userId)
    {
        var campaign = await campaignRepository.GetAsync(campaignId);
        return campaign == null || RoleOf(campaign, userId) is not { } role ? null
            : new CampaignAccess(campaignId, userId, role, IsSystemMissing(campaign, systemCatalog));
    }

    /// <summary>True when the catalog has loaded and does not have the campaign's system. Before the first load nothing is
    /// known to be missing, so nothing is made read-only during startup.</summary>
    public static bool IsSystemMissing(CampaignDocument campaign, ISystemCatalog? catalog) =>
        catalog is { IsLoaded: true } && !string.IsNullOrEmpty(campaign.SystemId) && !catalog.LoadedSystemIds.Contains(campaign.SystemId);

    /// <summary>The caller with their role as the campaign stores it now, or null when they are no longer in it. Every repository
    /// write starts with this, so a co-DM demoted after their page resolved its access writes as a player from then on, and
    /// someone removed from the campaign writes nothing (F13). A preview's access (no user) comes back null too, and so does
    /// one for a campaign whose system is missing (<see cref="CampaignAccess.SystemMissing"/>) when it is for a write: pass
    /// <paramref name="write"/> false for the rare read that rechecks the role.</summary>
    public static async Task<CampaignAccess?> NowAsync(Raven.Client.Documents.IDocumentStore store, CampaignAccess access, bool write = true)
    {
        if (string.IsNullOrEmpty(access.CampaignId)) return null;
        if (write && access.SystemMissing) return null;

        using var session = store.OpenAsyncSession();
        var campaign = await session.LoadAsync<CampaignDocument>(access.CampaignId);
        return campaign == null || RoleOf(campaign, access.UserId) is not { } role ? null
            : role == access.Role ? access : new CampaignAccess(access.CampaignId, access.UserId, role, access.SystemMissing);
    }

    /// <summary>The user's role in the campaign as stored, or null when they are not in it. Writes call this on the copy they
    /// load, so a role taken away after a page resolved its access is not used again (D15).</summary>
    public static CampaignRole? RoleOf(CampaignDocument campaign, string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        if (string.Equals(campaign.OwnerId, userId, StringComparison.Ordinal))
        {
            return CampaignRole.DM;
        }

        return campaign.Members.FirstOrDefault(m => string.Equals(m.UserId, userId, StringComparison.Ordinal))?.Role;
    }

    /// <summary>Everyone who GMs the campaign: the owner and the members holding the GM role (co-GMs).</summary>
    public static HashSet<string> GameMasters(CampaignDocument campaign)
    {
        var gms = campaign.Members.Where(m => m.Role == CampaignRole.DM).Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(campaign.OwnerId)) gms.Add(campaign.OwnerId);
        return gms;
    }
}
