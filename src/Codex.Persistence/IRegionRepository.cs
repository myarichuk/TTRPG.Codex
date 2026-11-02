namespace Codex.Persistence;

/// <summary>A place taken off the map, and the places inside it that moved up to its parent.</summary>
public sealed record RemovedLocation(LocationDocument Location, IReadOnlyList<string> MovedIds);

public interface IRegionRepository
{
    /// <summary>
    /// Regions for <paramref name="access"/>'s campaign. For a non-DM, every returned region's
    /// <see cref="RegionDocument.Locations"/> is filtered down to what that player is allowed to
    /// see (2.5) - a Hidden location that player doesn't own is never handed to the caller, so
    /// there is nothing for a Razor page to accidentally render.
    /// </summary>
    Task<IEnumerable<RegionDocument>> GetVisibleForCampaignAsync(CampaignAccess access);

    Task<RegionDocument?> GetAsync(string regionId, CampaignAccess access);

    /// <summary>Creates or updates a region. DM only (2.1/2.5 are DM prep-loop tools).</summary>
    Task<bool> SaveAsync(RegionDocument region, CampaignAccess access);

    /// <summary>Renames a region, changing nothing else, so a stale copy can't undo place edits made since. DM only;
    /// false for a blank name or a region outside the campaign.</summary>
    Task<bool> RenameAsync(string regionId, string name, CampaignAccess access);

    /// <summary>Says what a region is (a dungeon, a ship), changing nothing else. DM only; false for a region outside the campaign.</summary>
    Task<bool> SetKindAsync(string regionId, RegionKind kind, CampaignAccess access);

    /// <summary>Adds or replaces (by <see cref="LocationDocument.Id"/>) a location within a region. DM only.</summary>
    Task<bool> UpsertLocationAsync(string regionId, LocationDocument location, CampaignAccess access);

    /// <summary>Sets a location's visibility - the "Reveal to party" action (2.5). DM only.</summary>
    Task<bool> SetLocationVisibilityAsync(string regionId, string locationId, ActorVisibility visibility, CampaignAccess access);

    /// <summary>Takes a place off the map (U7). The places inside it move up to its parent. Returns what
    /// was removed and the places that moved, for <see cref="RestoreLocationAsync"/>; null when there is no such place or the
    /// caller is not the DM. Its art stays on the region, so an undo gets it back. DM only.</summary>
    Task<RemovedLocation?> RemoveLocationAsync(string regionId, string locationId, CampaignAccess access);

    /// <summary>Moves a place and every place inside it from one region to another, the place itself going inside
    /// <paramref name="newParentId"/> (a place of the destination; null: the top of it), in one stored change. DM only. False when
    /// either region is outside the campaign, the place or the new parent is not where it should be, or a place being moved has art
    /// (art is stored on its region and does not travel).</summary>
    Task<bool> MoveLocationAsync(string fromRegionId, string toRegionId, string locationId, string? newParentId, CampaignAccess access);

    /// <summary>Puts a removed place back, and the places that moved out of it back inside it (those still where the removal put
    /// them). DM only.</summary>
    Task<bool> RestoreLocationAsync(string regionId, RemovedLocation removed, CampaignAccess access);

    /// <summary>Deletes a region with every place in it and their art (U7). DM only; false for a region outside the campaign.</summary>
    Task<bool> DeleteAsync(string regionId, CampaignAccess access);

    /// <summary>Deletes every region belonging to a campaign (cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
