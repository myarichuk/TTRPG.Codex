namespace Codex.Persistence;

/// <summary>
/// A resolved (campaign, user, role) triple. This is the only thing repository methods that
/// return campaign-scoped data should trust to decide what a caller can see - never a bare
/// userId, and never a check performed in Razor after the fact (1.4). Construct one only via
/// <see cref="ICampaignAccessResolver"/>.
/// </summary>
public sealed class CampaignAccess
{
    public string CampaignId { get; }
    public string UserId { get; }
    public CampaignRole Role { get; }
    public bool IsDm => Role == CampaignRole.DM;

    internal CampaignAccess(string campaignId, string userId, CampaignRole role)
    {
        CampaignId = campaignId;
        UserId = userId;
        Role = role;
    }
}

public interface ICampaignAccessResolver
{
    /// <summary>
    /// Resolves a user's role in a campaign from its <see cref="CampaignDocument.Members"/> list.
    /// The campaign owner is always at least DM, even for campaigns saved before the Members list
    /// existed or for an owner who was never explicitly added as a member. Returns null if the
    /// campaign doesn't exist or the user has no membership and isn't the owner - callers must
    /// treat null as "no access," not "DM by default."
    /// </summary>
    Task<CampaignAccess?> ResolveAsync(string campaignId, string userId);
}
