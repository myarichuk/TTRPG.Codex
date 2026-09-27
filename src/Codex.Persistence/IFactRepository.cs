namespace Codex.Persistence;

public interface IFactRepository
{
    /// <summary>
    /// Facts in <paramref name="access"/>'s campaign that <paramref name="access"/> may see: every
    /// fact for the DM; for a player, <see cref="FactVisibility.Public"/> facts plus
    /// <see cref="FactVisibility.KnowersOnly"/> facts where one of that player's own actors
    /// appears in <see cref="FactDocument.KnownBy"/>. <see cref="FactVisibility.DmOnly"/> never
    /// leaves this method for a non-DM caller.
    /// </summary>
    Task<IEnumerable<FactDocument>> GetVisibleForCampaignAsync(CampaignAccess access, IReadOnlySet<string> ownedActorIds);

    /// <summary>Creates or updates a fact. DM only - lore authoring is a DM prep-loop tool (2.1/2.5).</summary>
    Task<bool> SaveAsync(FactDocument fact, CampaignAccess access);

    /// <summary>
    /// Proposes a lore entry. Any campaign member may call this: the DM's entry is stored
    /// approved with the requested visibility, a player's entry is stored as
    /// <see cref="FactStatus.Proposed"/> + <see cref="FactVisibility.DmOnly"/> regardless of
    /// what was requested, so it never leaks to other players before DM approval.
    /// </summary>
    Task<bool> ProposeAsync(FactDocument fact, CampaignAccess access);

    /// <summary>Approves a proposed entry with the given visibility. DM only.</summary>
    Task<bool> ApproveAsync(string factId, FactVisibility visibility, CampaignAccess access);

    /// <summary>Deletes a single fact (e.g. rejecting a proposal). DM only.</summary>
    Task<bool> DeleteAsync(string factId, CampaignAccess access);

    /// <summary>Sets a fact's visibility directly - backs the "Reveal to party" action (2.5). DM only.</summary>
    Task<bool> SetVisibilityAsync(string factId, FactVisibility visibility, CampaignAccess access);

    /// <summary>
    /// Records that an actor learned a fact - the per-actor reveal flow (4.2). Adds <paramref name="knower"/>
    /// to the fact's <see cref="FactDocument.KnownBy"/>, replacing any existing entry for the same actor
    /// so re-revealing upgrades the <see cref="KnowledgeLevel"/>. DM only.
    /// </summary>
    Task<bool> AddKnowerAsync(string factId, KnowerEntry knower, CampaignAccess access);

    /// <summary>Deletes every fact belonging to a campaign (cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
