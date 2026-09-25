using Codex.Plugin.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Codex.Persistence;

public class CampaignRepository(
    RavenDbService dbService,
    ISystemCatalog systemCatalog,
    IActorRepository actorRepository,
    ISessionRepository sessionRepository,
    INoteRepository noteRepository) : ICampaignRepository
{
    public async Task<IEnumerable<CampaignDocument>> GetAllAsync()
    {
        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();
        return await session.Query<CampaignDocument>()
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<CampaignDocument?> GetAsync(string campaignId)
    {
        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<CampaignDocument>(campaignId);
    }

    public async Task SaveAsync(CampaignDocument campaign)
    {
        // 1.1: reject a SystemId nothing implements. Skipped until the catalog has actually run
        // once - refusing every save during the plugin-loading window at startup would be worse
        // than the gap it closes.
        if (systemCatalog.IsLoaded
            && !string.IsNullOrEmpty(campaign.SystemId)
            && !systemCatalog.LoadedSystemIds.Contains(campaign.SystemId))
        {
            throw new InvalidOperationException($"SystemId '{campaign.SystemId}' is not a loaded system plugin.");
        }

        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();

        campaign.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(campaign.Id))
        {
            campaign.CreatedAt = DateTime.UtcNow;
        }

        // A campaign always has its owner as at least a DM member - membership, not OwnerId, is
        // what the access layer (1.4) actually checks.
        if (!campaign.Members.Any(m => string.Equals(m.UserId, campaign.OwnerId, StringComparison.Ordinal)))
        {
            campaign.Members.Add(new CampaignMember { UserId = campaign.OwnerId, Role = CampaignRole.DM });
        }

        if (string.IsNullOrEmpty(campaign.InviteCode))
        {
            campaign.InviteCode = Guid.NewGuid().ToString("N")[..8];
        }

        await session.StoreAsync(campaign);
        await session.SaveChangesAsync();
    }

    public async Task<CampaignDeleteResult> DeleteAsync(string campaignId, string requestingUserId)
    {
        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();
        var campaign = await session.LoadAsync<CampaignDocument>(campaignId);
        if (campaign == null)
        {
            return CampaignDeleteResult.NotFound;
        }

        if (!string.Equals(campaign.OwnerId, requestingUserId, StringComparison.Ordinal))
        {
            return CampaignDeleteResult.Forbidden;
        }

        // B13: cascade delete everything scoped to this campaign before the campaign itself.
        await actorRepository.DeleteAllForCampaignAsync(campaignId);
        await sessionRepository.DeleteAllForCampaignAsync(campaignId);
        await noteRepository.DeleteAllForCampaignAsync(campaignId);

        session.Delete(campaignId);
        await session.SaveChangesAsync();
        return CampaignDeleteResult.Deleted;
    }
}
