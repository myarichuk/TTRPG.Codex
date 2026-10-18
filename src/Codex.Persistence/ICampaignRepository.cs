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
