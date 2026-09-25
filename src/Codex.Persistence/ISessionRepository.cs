namespace Codex.Persistence;

public interface ISessionRepository
{
    Task<SessionDocument?> GetAsync(string sessionId);
    Task SaveAsync(SessionDocument session);
    Task<IEnumerable<SessionDocument>> GetAllForCampaignAsync(string campaignId);

    /// <summary>Deletes every session belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
