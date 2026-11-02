namespace Codex.Persistence;

public interface INoteRepository
{
    Task CreateNoteAsync(NoteDocument note);

    /// <summary>Deletes a note in <paramref name="access"/>'s campaign for its author or a DM, by their role in the campaign as
    /// stored now (D15). False when it is gone or the caller may not; nothing is deleted then.</summary>
    Task<bool> DeleteNoteAsync(string id, CampaignAccess access);

    /// <summary>
    /// Notes on a target, scoped by <paramref name="access"/>: a DM sees everything; a Player sees
    /// Public notes plus their own Private ones, filtered in the Raven query itself so another
    /// player's private note is never loaded into a session that isn't theirs (1.4).
    /// </summary>
    Task<IEnumerable<NoteDocument>> GetNotesForTargetAsync(string targetId, CampaignAccess access);

    Task<IEnumerable<NoteDocument>> GetNotesByAuthorAsync(string campaignId, string authorId);

    /// <summary>Every note in a campaign this viewer may read, on any target, scoped like <see cref="GetNotesForTargetAsync"/>.</summary>
    Task<IEnumerable<NoteDocument>> GetVisibleForCampaignAsync(CampaignAccess access);

    /// <summary>
    /// Every note in a campaign, regardless of target or visibility. Export/backup only -
    /// never call this for a player-facing read; use <see cref="GetNotesForTargetAsync"/> there.
    /// </summary>
    Task<IEnumerable<NoteDocument>> GetAllForCampaignAsync(string campaignId);

    /// <summary>Deletes every note belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
