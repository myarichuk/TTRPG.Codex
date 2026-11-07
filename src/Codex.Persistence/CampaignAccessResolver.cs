namespace Codex.Persistence;

public class CampaignAccessResolver(ICampaignRepository campaignRepository) : ICampaignAccessResolver
{
    public async Task<CampaignAccess?> ResolveAsync(string campaignId, string userId)
    {
        var campaign = await campaignRepository.GetAsync(campaignId);
        return campaign == null || RoleOf(campaign, userId) is not { } role ? null : new CampaignAccess(campaignId, userId, role);
    }

    /// <summary>The caller with their role as the campaign stores it now, or null when they are no longer in it. Every repository
    /// write starts with this, so a co-DM demoted after their page resolved its access writes as a player from then on, and
    /// someone removed from the campaign writes nothing (F13). A preview's access (no user) comes back null too.</summary>
    public static async Task<CampaignAccess?> NowAsync(Raven.Client.Documents.IDocumentStore store, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(access.CampaignId)) return null;
        using var session = store.OpenAsyncSession();
        var campaign = await session.LoadAsync<CampaignDocument>(access.CampaignId);
        return campaign == null || RoleOf(campaign, access.UserId) is not { } role ? null
            : role == access.Role ? access : new CampaignAccess(access.CampaignId, access.UserId, role);
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
