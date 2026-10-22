namespace Codex.Persistence;

public enum CampaignDeleteResult
{
    Deleted,
    NotFound,
    Forbidden
}

public enum CampaignJoinResult
{
    Joined,
    AlreadyMember,
    InvalidCode
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
    /// <summary>Every campaign on the server. Administrative/export use only - never for a user-facing list.</summary>
    Task<IEnumerable<CampaignDocument>> GetAllAsync();

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
}
