namespace Codex.Persistence;

/// <summary>
/// Characters kept outside every campaign, each its owner's alone (docs/SERVER_ACCESS_AND_VAULT.md 8), and the requests that
/// bring them into campaigns (7). Every call names the caller, and nothing here reads or writes a campaign's actors except
/// <see cref="AcceptAsync"/>, which only a GM of that campaign may run.
/// </summary>
public interface IVaultRepository
{
    /// <summary>The caller's vault characters, newest change first.</summary>
    Task<IReadOnlyList<ActorDocument>> GetAllAsync(string userId);

    /// <summary>One of the caller's vault characters, or null when there is none by that id or it is someone else's.</summary>
    Task<ActorDocument?> GetAsync(string actorId, string userId);

    /// <summary>A new, blank player character in the caller's vault, for <paramref name="systemId"/>.</summary>
    Task<ActorDocument> CreateAsync(string userId, string systemId, string name);

    /// <summary>Saves the name, sheet and history of one of the caller's vault characters; its owner, system and place
    /// outside every campaign never change. False when it is not theirs. Throws <c>Actor.Sheet.ChangedMeanwhile</c> when
    /// it was saved elsewhere after this copy was loaded.</summary>
    Task<bool> SaveAsync(ActorDocument actor, string userId);

    Task<bool> DeleteAsync(string actorId, string userId);

    /// <summary>Asks to bring <paramref name="request"/>'s vault character into the campaign (or a newer version of it over the
    /// campaign's copy). Refused (null) unless the caller is in the campaign, owns the vault character, it is built for the
    /// campaign's system, and, for an update, the copy is the caller's and came from that character. A second request for
    /// the same character replaces the first.</summary>
    Task<CharacterRequestDocument?> AskAsync(CampaignAccess access, CharacterRequestDocument request);

    /// <summary>A GM gets every request in the campaign; anyone else only their own.</summary>
    Task<IReadOnlyList<CharacterRequestDocument>> GetRequestsAsync(CampaignAccess access);

    /// <summary>Takes a request back (its player) or turns it down (a GM).</summary>
    Task<bool> WithdrawAsync(string requestId, CampaignAccess access);

    /// <summary>A GM accepts: a new request becomes a player character of the campaign, played by whoever asked. Null when the
    /// caller is not a GM, the request is gone, or the player has left. An update request is not applied here (the live table
    /// may hold the copy); its request is returned removed, for the caller to apply.</summary>
    Task<(CharacterRequestDocument Request, ActorDocument? Added)?> AcceptAsync(string requestId, CampaignAccess access);

    /// <summary>Updates the vault character a campaign's copy came from with the parts the player owns, as the caller
    /// prepared them. False unless the vault character is the caller's.</summary>
    Task<bool> UpdateFromCopyAsync(string vaultActorId, string userId, string name, Dictionary<string, object> sheet, string? biography);

    Task DeleteAllRequestsForCampaignAsync(string campaignId);

    /// <summary>Drops every request <paramref name="userId"/> has waiting in the campaign (they left it).</summary>
    Task WithdrawAllAsync(string campaignId, string userId);
}
