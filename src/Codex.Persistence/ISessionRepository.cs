namespace Codex.Persistence;

public interface ISessionRepository
{
    Task<SessionDocument?> GetAsync(string sessionId);

    /// <summary>
    /// The session as <paramref name="access"/> may read it, or null when it is not this campaign's. The DM gets it whole. A
    /// player gets no DM summary, no secret notes, the recap and notes without the blocks they may not read, the log through
    /// <see cref="SessionEventRedactor"/>, and no secret rolls or rolls of actors they can't see. The copy can't be saved.
    /// </summary>
    Task<SessionDocument?> GetVisibleAsync(string sessionId, CampaignAccess access);
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

    /// <summary>Deletes one session with its notes, log and rolls, and the encounters fought in it (U7). DM only; false for a
    /// session outside the campaign. The caller makes sure the table is not running it. Journal entries that named it keep
    /// their text and lose the link.</summary>
    Task<bool> DeleteAsync(string sessionId, CampaignAccess access);

    /// <summary>Deletes every session belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
