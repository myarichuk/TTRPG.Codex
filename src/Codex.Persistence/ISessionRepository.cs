namespace Codex.Persistence;

public interface ISessionRepository
{
    Task<SessionDocument?> GetAsync(string sessionId);
    Task SaveAsync(SessionDocument session);

    /// <summary>
    /// Reload-then-mutate: loads the stored session, applies <paramref name="mutate"/> and saves under
    /// optimistic concurrency (retrying on a collision). Edit single fields (Recap, DmSummary, Title,
    /// Status) or append to the event, roll and note lists through this; never store a copy loaded
    /// earlier, which would drop whatever the live table appended since. Returns the stored
    /// document, or null when the session does not exist.
    /// </summary>
    Task<SessionDocument?> UpdateAsync(string sessionId, Action<SessionDocument> mutate);
    Task<IEnumerable<SessionDocument>> GetAllForCampaignAsync(string campaignId);

    /// <summary>Deletes every session belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
