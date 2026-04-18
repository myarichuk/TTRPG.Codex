using Raven.Client.Documents;

namespace Codex.Persistence;

public class RavenCampaignRepository : ICampaignRepository
{
    private readonly RavenDbService _dbService;

    public RavenCampaignRepository(RavenDbService dbService)
    {
        _dbService = dbService;
    }

    public async Task<IEnumerable<CampaignDocument>> GetAllAsync()
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return await session.Query<CampaignDocument>()
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<CampaignDocument?> GetAsync(string campaignId)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<CampaignDocument>(campaignId);
    }

    public async Task SaveAsync(CampaignDocument campaign)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        
        campaign.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(campaign.Id))
        {
            campaign.CreatedAt = DateTime.UtcNow;
        }

        await session.StoreAsync(campaign);
        await session.SaveChangesAsync();
    }
}
