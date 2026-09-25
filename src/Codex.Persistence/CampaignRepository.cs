using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Codex.Persistence;

public class CampaignRepository(
    RavenDbService dbService,
    ICharacterRepository characterRepository,
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
        using IAsyncDocumentSession session = dbService.Store.OpenAsyncSession();

        campaign.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(campaign.Id))
        {
            campaign.CreatedAt = DateTime.UtcNow;
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
        await characterRepository.DeleteAllForCampaignAsync(campaignId);
        await sessionRepository.DeleteAllForCampaignAsync(campaignId);
        await noteRepository.DeleteAllForCampaignAsync(campaignId);

        session.Delete(campaignId);
        await session.SaveChangesAsync();
        return CampaignDeleteResult.Deleted;
    }
}
