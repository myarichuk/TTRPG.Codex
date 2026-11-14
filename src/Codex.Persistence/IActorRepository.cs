using Codex.Plugin.Abstractions;

namespace Codex.Persistence;

/// <summary>Some of one item handed from one character to another; no giver means the DM gives it from the Library.</summary>
public sealed record ItemMove(string? FromActorId, string ToActorId, string ItemId, int Quantity);

public enum ItemMoveResult
{
    Moved,
    /// <summary>The caller may not make this move (a player giving what is not theirs, or to someone who is not a player's character).</summary>
    NotAllowed,
    /// <summary>The giver carries fewer than that, perhaps since the screen was drawn.</summary>
    NotEnough,
    /// <summary>A character is not in the campaign any more, or was taken out of play.</summary>
    Gone
}

public interface IActorRepository
{
    /// <summary>
    /// Loads an actor by id, scoped to what <paramref name="access"/> is allowed to see. Returns
    /// null both when the actor doesn't exist and when it exists but is Hidden/not-owned from a
    /// non-DM - the two cases are indistinguishable to a player on purpose (1.4). An Unknown actor
    /// a player does not own comes back as a stranger: its id, kind, public name and public
    /// description, nothing else.
    /// </summary>
    Task<ActorDocument?> GetVisibleAsync(string actorId, CampaignAccess access);

    /// <summary>
    /// The actors of <paramref name="access"/>'s campaign with these ids, whole and whoever is asking: for the rules to work out
    /// what a link brings (docs/CREW_LINKS_DESIGN.md), which never depends on the viewer. Only their sheets may be read from the
    /// result for that, never shown; what a viewer sees of an actor comes from <see cref="GetVisibleAsync"/>. An id that is not an actor
    /// of the campaign (deleted, another campaign's, a vault character) is left out.
    /// </summary>
    Task<IReadOnlyList<ActorDocument>> GetForRulesAsync(CampaignAccess access, IEnumerable<string> actorIds);

    Task SaveAsync(ActorDocument actor);

    /// <summary>
    /// Reload-then-mutate: loads the stored actor, applies <paramref name="mutate"/> to it and saves
    /// under optimistic concurrency (retrying on a collision), so the caller changes only the fields
    /// it names instead of writing back a copy loaded earlier. Trusted server-side callers only.
    /// Returns the stored document, or null when the actor does not exist.
    /// </summary>
    Task<ActorDocument?> UpdateAsync(string actorId, Action<ActorDocument> mutate);

    /// <summary>
    /// Saves an actor only if <paramref name="access"/> is allowed to edit it: the DM, or the
    /// owning player of an existing actor (4.3). Players creating a new actor must own it and
    /// place it in their own campaign; a player's save can never move an actor to another
    /// campaign or change its owner - those fields are taken from the stored document. Returns
    /// false without saving anything otherwise. The unguarded <see cref="SaveAsync"/> stays for
    /// trusted server-side callers (runtime write-through, seeding).
    /// </summary>
    /// <remarks>An actor this repository handed out is saved only over the version it was handed out at: when someone saved
    /// it since, nothing is written and a <see cref="Codex.Localization.LocalizedException"/> ("Actor.Sheet.ChangedMeanwhile")
    /// is thrown, so the editor can keep its text. A copy handed out and deleted since returns false.</remarks>
    Task<bool> TrySaveAsync(ActorDocument actor, CampaignAccess access);

    /// <summary>Records that <paramref name="actor"/>'s portrait was stored (or removed) and bumps its version, and nothing else
    /// (D5): the sheet's unsaved edits are not written, and its rules are not bypassed. The DM or the owning player only.
    /// <paramref name="actor"/> takes the new portrait fields, and stays saveable when it was the latest before.</summary>
    Task<bool> TrySetPortraitAsync(ActorDocument actor, bool hasPortrait, CampaignAccess access);

    /// <summary>
    /// Actors in <paramref name="access"/>'s campaign that <paramref name="access"/> is allowed to
    /// see: everything for a DM; for a Player, every actor that is not <see cref="ActorVisibility.Hidden"/>,
    /// and the ones they own. An <see cref="ActorVisibility.Unknown"/> actor they do not own comes as a
    /// stranger (see <see cref="GetVisibleAsync"/>). The Hidden filter runs in the Raven query, not after
    /// loading, so a hidden actor's data never leaves the database for a player's session.
    /// </summary>
    Task<IEnumerable<ActorDocument>> GetVisibleForCampaignAsync(CampaignAccess access);

    /// <summary>Deletes every actor belonging to a campaign (B13 cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);

    /// <summary>
    /// Deletes a single actor if <paramref name="access"/> is the campaign's DM, or is the owning
    /// player of a <see cref="ActorKind.PlayerCharacter"/> (retiring their own character). Returns
    /// false without deleting anything for anyone else or a cross-campaign id.
    /// </summary>
    Task<bool> TryDeleteAsync(string actorId, CampaignAccess access);

    /// <summary>
    /// Takes an actor out of play without deleting it (D17): <see cref="ActorDocument.Retired"/> is set, so lists and pickers
    /// leave it out while mentions, reveals and links still name it. The same people may as for <see cref="TryDeleteAsync"/>.
    /// Returns the stored actor, or null when <paramref name="access"/> may not.
    /// </summary>
    Task<ActorDocument?> TryRetireAsync(string actorId, CampaignAccess access);

    /// <summary>Brings a retired actor back into play. The DM only; returns the stored actor, or null.</summary>
    Task<ActorDocument?> TryRestoreAsync(string actorId, CampaignAccess access);

    /// <summary>Changes who among the players may see an actor (R1). The DM only; returns the stored actor, or null.</summary>
    Task<ActorDocument?> TrySetVisibilityAsync(string actorId, ActorVisibility visibility, CampaignAccess access);

    /// <summary>
    /// Moves items between two sheets' inventories (<paramref name="rules"/> says where a sheet keeps them) in one save, so an item
    /// is never on both or on neither. The DM may give from the Library (no giver) or move between any of the campaign's characters;
    /// a player may only hand what their own character carries to another player's character the party knows of. The receiver gets
    /// it unequipped. A sheet open in an editor meanwhile is told it changed when it is saved, as for any other change.
    /// </summary>
    Task<ItemMoveResult> TryMoveItemAsync(ItemMove move, InventoryRules rules, CampaignAccess access);

    /// <summary>
    /// The dashboard's "recent archives" list: a user's own actors across every campaign they're
    /// in, most-recent first. Scoped to <paramref name="ownerUserId"/> rather than every actor in
    /// the database - the previous, pre-Phase-1 implementation had no such scoping at all.
    /// </summary>
    Task<IEnumerable<ActorDocument>> GetRecentForOwnerAsync(string ownerUserId, int limit, bool sortByCreated);
}
