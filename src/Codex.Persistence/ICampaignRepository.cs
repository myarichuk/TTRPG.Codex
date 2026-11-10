namespace Codex.Persistence;

public enum CampaignDeleteResult
{
    Deleted,
    NotFound,
    Forbidden
}

public enum CampaignLeaveResult
{
    Left,

    /// <summary>The owner cannot leave: they pass the campaign on first, or delete it.</summary>
    Owner,

    /// <summary>Not a member (already left, removed, or no such campaign).</summary>
    NotMember
}

public enum CampaignJoinResult
{
    Joined,
    AlreadyMember,
    InvalidCode,

    /// <summary>This user tried too many wrong codes lately (D14); nothing was looked up.</summary>
    TooManyAttempts
}

public interface ICampaignRepository
{
    Task<CampaignDocument?> GetAsync(string campaignId);
    Task SaveAsync(CampaignDocument campaign);

    /// <summary>
    /// Reload-then-mutate: loads the stored campaign, applies <paramref name="mutate"/> and saves under
    /// optimistic concurrency (retrying on a collision). Use this instead of <see cref="SaveAsync"/>
    /// for any edit made from a page that loaded the campaign earlier: invites add members and the
    /// live table sets <c>CurrentSessionId</c> in the meantime, and a whole-document save of the
    /// stale copy would silently drop both. Returns the stored document, or null when it does not exist.
    /// </summary>
    Task<CampaignDocument?> UpdateAsync(string campaignId, Action<CampaignDocument> mutate);

    /// <summary>
    /// <see cref="UpdateAsync"/> for a change only a DM may make, from a page. The role is read from the campaign as it is
    /// stored when the change is written, not from <paramref name="access"/>: a DM demoted or removed after the page loaded
    /// is refused with <c>Campaign.AccessChanged</c> (D15). Returns the stored document, or null when it does not exist.
    /// </summary>
    Task<CampaignDocument?> UpdateAsDmAsync(CampaignAccess access, Action<CampaignDocument> mutate);

    /// <summary>
    /// <paramref name="userId"/> leaves the campaign: their membership goes, whatever their role, and their characters stay in the
    /// cast as a removal leaves them. Coming back takes a new invite. The owner cannot leave (docs/SERVER_ACCESS_AND_VAULT.md 4).
    /// </summary>
    Task<CampaignLeaveResult> LeaveAsync(string campaignId, string userId);

    /// <summary>Every campaign on the server. Administrative/export use only - never for a user-facing list.</summary>
    Task<IEnumerable<CampaignDocument>> GetAllAsync();

    /// <summary>Records the campaign's system as it is loaded now, once the GM has seen what changed (overhaul step 10).</summary>
    Task<CampaignDocument?> AcceptSystemAsync(CampaignAccess access);

    /// <summary>The campaigns <paramref name="userId"/> owns or is a member of - the only list a user should ever see.</summary>
    Task<IEnumerable<CampaignDocument>> GetForUserAsync(string userId);

    /// <summary>
    /// Deletes a campaign and everything scoped to it (characters, sessions, notes) only if
    /// <paramref name="requestingUserId"/> is the campaign's owner (B2). There is no membership
    /// model yet (that's Phase 1), so ownership is the only access check available today.
    /// </summary>
    Task<CampaignDeleteResult> DeleteAsync(string campaignId, string requestingUserId);

    /// <summary>
    /// Redeems an invite code, adding <paramref name="userId"/> as a <see cref="CampaignRole.Player"/>
    /// member if the code resolves to a campaign and that user isn't already a member (2.4).
    /// </summary>
    Task<(CampaignJoinResult Result, string? CampaignId)> JoinByInviteCodeAsync(string inviteCode, string userId);

    /// <summary>Whether some campaign currently uses this invite code. Lets a server open to the internet
    /// accept a new account only from someone holding a real invite.</summary>
    Task<bool> InviteCodeExistsAsync(string inviteCode);
}
