namespace Codex.Persistence;

public interface INoteRepository
{
    Task CreateNoteAsync(NoteDocument note);
    Task DeleteNoteAsync(string id);

    /// <summary>
    /// Notes on a target, scoped by <paramref name="access"/>: a DM sees everything; a Player sees
    /// Public notes plus their own Private ones, filtered in the Raven query itself so another
    /// player's private note is never loaded into a session that isn't theirs (1.4).
    /// </summary>
    Task<IEnumerable<NoteDocument>> GetNotesForTargetAsync(string targetId, CampaignAccess access);

    Task<IEnumerable<NoteDocument>> GetNotesByAuthorAsync(string campaignId, string authorId);

    /// <summary>
    /// Every note in a campaign, regardless of target or visibility. Export/backup only -
    /// never call this for a player-facing read; use <see cref="GetNotesForTargetAsync"/> there.
    /// </summary>
    Task<IEnumerable<NoteDocument>> GetAllForCampaignAsync(string campaignId);

    /// <summary>Deletes every note belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
