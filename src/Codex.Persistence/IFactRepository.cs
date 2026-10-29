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

    /// <summary>Creates or updates a fact. DM only - lore authoring is a DM prep-loop tool (2.1/2.5). The fact goes into the
    /// DM's campaign whatever its <see cref="FactDocument.CampaignId"/> says, an id taken by another campaign's fact is refused,
    /// and <see cref="FactDocument.RelatedEntityIds"/> is rebuilt from what its text mentions and links to
    /// (<see cref="FactLinks.Of"/>).</summary>
    Task<bool> SaveAsync(FactDocument fact, CampaignAccess access);

    /// <summary>Deletes a fact. DM only.</summary>
    Task<bool> DeleteAsync(string factId, CampaignAccess access);

    /// <summary>Takes a character off the fact's knowers (a reveal made by mistake). DM only.</summary>
    Task<bool> RemoveKnowerAsync(string factId, string entityId, CampaignAccess access);

    /// <summary>Sets a fact's visibility directly - backs the "Reveal to party" action (2.5). DM only.</summary>
    Task<bool> SetVisibilityAsync(string factId, FactVisibility visibility, CampaignAccess access);

    /// <summary>
    /// Records that an actor learned a fact - the per-actor reveal flow (4.2). Adds <paramref name="knower"/>
    /// to the fact's <see cref="FactDocument.KnownBy"/>, replacing any existing entry for the same actor
    /// so re-revealing upgrades the <see cref="KnowledgeLevel"/>. A fact still <see cref="FactVisibility.DmOnly"/> becomes
    /// <see cref="FactVisibility.KnowersOnly"/>, or the character would be told nothing. DM only.
    /// </summary>
    Task<bool> AddKnowerAsync(string factId, KnowerEntry knower, CampaignAccess access);

    /// <summary>Deletes every fact belonging to a campaign (cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
