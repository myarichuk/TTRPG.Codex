namespace Codex.Persistence;

public interface IActorRepository
{
    /// <summary>
    /// Loads an actor by id, scoped to what <paramref name="access"/> is allowed to see. Returns
    /// null both when the actor doesn't exist and when it exists but is Hidden/not-owned from a
    /// non-DM - the two cases are indistinguishable to a player on purpose (1.4).
    /// </summary>
    Task<ActorDocument?> GetVisibleAsync(string actorId, CampaignAccess access);

    Task SaveAsync(ActorDocument actor);

    /// <summary>
    /// Saves an actor only if <paramref name="access"/> is allowed to edit it: the DM, or the
    /// owning player of an existing actor (4.3). Players creating a new actor must own it and
    /// place it in their own campaign; a player's save can never move an actor to another
    /// campaign or change its owner - those fields are taken from the stored document. Returns
    /// false without saving anything otherwise. The unguarded <see cref="SaveAsync"/> stays for
    /// trusted server-side callers (runtime write-through, seeding).
    /// </summary>
    Task<bool> TrySaveAsync(ActorDocument actor, CampaignAccess access);

    /// <summary>
    /// Actors in <paramref name="access"/>'s campaign that <paramref name="access"/> is allowed to
    /// see: everything for a DM; for a Player, actors that are <see cref="ActorVisibility.Known"/>
    /// or that they own, with Unknown-but-owned actors excluded because ownership without
    /// knowledge doesn't happen in practice and the plan calls for a strict allow-list. The
    /// filter runs in the Raven query, not after loading, so a hidden actor's data never leaves
    /// the database for a player's session.
    /// </summary>
    Task<IEnumerable<ActorDocument>> GetVisibleForCampaignAsync(CampaignAccess access);

    /// <summary>Deletes every actor belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);

    /// <summary>
    /// Deletes a single actor if, and only if, <paramref name="access"/> is the campaign's DM.
    /// Returns false without deleting anything for a Player/Observer or a cross-campaign id -
    /// there is no player-facing "delete an actor" operation in this domain (1.4).
    /// </summary>
    Task<bool> TryDeleteAsync(string actorId, CampaignAccess access);

    /// <summary>
    /// The dashboard's "recent archives" list: a user's own actors across every campaign they're
    /// in, most-recent first. Scoped to <paramref name="ownerUserId"/> rather than every actor in
    /// the database - the previous, pre-Phase-1 implementation had no such scoping at all.
    /// </summary>
    Task<IEnumerable<ActorDocument>> GetRecentForOwnerAsync(string ownerUserId, int limit, bool sortByCreated);
}
