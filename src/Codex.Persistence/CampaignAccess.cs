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

    /// <summary>The campaign's game system is not installed (removed, or it failed to load). Everything can still be read,
    /// but nothing is written through this access: repository writes refuse it, as they refuse a party preview. The way
    /// out is to install the system again, or for a GM to choose another one in Settings.</summary>
    public bool SystemMissing { get; }

    internal CampaignAccess(string campaignId, string userId, CampaignRole role, bool systemMissing = false)
    {
        CampaignId = campaignId;
        UserId = userId;
        Role = role;
        SystemMissing = systemMissing;
    }

    /// <summary>
    /// The DM's "view as players": the campaign as a player who owns no character reads it. The previews go through the
    /// same repository calls a player's do, so what the DM sees is exactly what the party gets, not a second rendering that
    /// could drift from it. Only a DM gets one (a player has nothing more to preview), and it can read but never write:
    /// it is a player's access, and its user id matches no one: it is made up for each preview, never empty, since an owner id stored
    /// as "" would otherwise read as the preview's own character and come unmasked.
    /// </summary>
    public CampaignAccess? PartyPreview() => IsDm ? Nobody(CampaignId, SystemMissing) : null;

    /// <summary>What a vault character is built against (docs/SERVER_ACCESS_AND_VAULT.md 8): no campaign, so the server's packs and
    /// nothing of any campaign. Every repository refuses it a campaign's documents, and it writes nothing.</summary>
    public static CampaignAccess Vault(string userId) => new(string.Empty, userId, CampaignRole.Player);

    /// <summary>The same person reading as a player: what of their own character is theirs to take to their vault, whatever
    /// their role (a co-GM's history holds the campaign's secrets).</summary>
    public CampaignAccess AsPlayer() => IsDm ? new CampaignAccess(CampaignId, UserId, CampaignRole.Player, SystemMissing) : this;

    /// <summary>A player of the campaign who is nobody: no account has this id and no character names it as owner. Read-only like
    /// the access it stands in for when that campaign's system is missing (<paramref name="systemMissing"/>).</summary>
    internal static CampaignAccess Nobody(string campaignId, bool systemMissing = false) =>
        new(campaignId, "nobody:" + Guid.NewGuid().ToString("N"), CampaignRole.Player, systemMissing);
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
