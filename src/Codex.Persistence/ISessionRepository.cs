namespace Codex.Persistence;

public interface ISessionRepository
{
    Task<SessionDocument?> GetAsync(string sessionId);
    Task SaveAsync(SessionDocument session);
    Task<IEnumerable<SessionDocument>> GetAllForCampaignAsync(string campaignId);
}
