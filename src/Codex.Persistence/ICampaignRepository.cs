namespace Codex.Persistence;

public interface ICampaignRepository
{
    Task<CampaignDocument?> GetAsync(string campaignId);
    Task SaveAsync(CampaignDocument campaign);
    Task<IEnumerable<CampaignDocument>> GetAllAsync();
}
