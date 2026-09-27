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
    Task<IEnumerable<CampaignDocument>> GetAllAsync();

    /// <summary>
    /// Deletes a campaign and everything scoped to it (characters, sessions, notes) only if
    /// <paramref name="requestingUserId"/> is the campaign's owner (B2). Membership roles
    /// (DM/Player/Observer) govern everything else, but deletion stays owner-only.
    /// </summary>
    Task<CampaignDeleteResult> DeleteAsync(string campaignId, string requestingUserId);

    /// <summary>
    /// Redeems an invite code, adding <paramref name="userId"/> as a member if the code resolves
    /// to a campaign and that user isn't already a member (2.4). The invite only ever grants
    /// <see cref="CampaignRole.Player"/> or <see cref="CampaignRole.Observer"/> - a request for
    /// <see cref="CampaignRole.DM"/> is clamped to Player, since DM is granted by an existing
    /// DM via <see cref="SetMemberRoleAsync"/>, never self-served from an invite link.
    /// </summary>
    Task<(CampaignJoinResult Result, string? CampaignId)> JoinByInviteCodeAsync(string inviteCode, string userId, CampaignRole role = CampaignRole.Player);

    /// <summary>
    /// Changes <paramref name="targetUserId"/>'s role in a campaign. An existing DM can move
    /// anyone between DM/Player/Observer; anyone can move themselves between Player and
    /// Observer (picking their own seat). Self-promotion to DM is always denied, and the
    /// campaign owner's DM role is locked - the owner stays DM until the campaign is deleted.
    /// Returns false without changing anything otherwise.
    /// </summary>
    Task<bool> SetMemberRoleAsync(string campaignId, string targetUserId, CampaignRole newRole, string requestingUserId);

    /// <summary>
    /// Removes <paramref name="targetUserId"/> from a campaign: a DM can remove any non-owner
    /// member, and anyone can remove themselves (leave). The owner cannot be removed - deleting
    /// the campaign is the way out for an owner. Returns false without changing anything
    /// otherwise.
    /// </summary>
    Task<bool> RemoveMemberAsync(string campaignId, string targetUserId, string requestingUserId);
}
