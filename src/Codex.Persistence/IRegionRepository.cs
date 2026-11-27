namespace Codex.Persistence;

/// <summary>A place taken off the map, and the places inside it that moved up to its parent.</summary>
public sealed record RemovedLocation(LocationDocument Location, IReadOnlyList<string> MovedIds);

/// <summary>What <see cref="IRegionRepository.PlaceHazardIfNoneAsync"/> did.</summary>
public enum HazardPlacing
{
    /// <summary>The place had no hazard; it holds this one now.</summary>
    Placed,

    /// <summary>The place already holds this very hazard (the same entry): nothing was written.</summary>
    AlreadyThere,

    /// <summary>The place holds another hazard: nothing was written.</summary>
    Taken,

    /// <summary>The region or the place is not there, or the caller may not write it.</summary>
    Refused
}

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

    /// <summary>Changes only <see cref="PlaceFeature.Hazard"/> of one place, for the table (HAZARDS_DESIGN.md 4.1, 11): <paramref name="change"/> gets the
    /// placement as stored now and returns what to store, or null to leave it. Nothing else on the place, the feature or the region is written, so a place
    /// edited meanwhile keeps its edits: one write under optimistic concurrency, so a save that lands between its load and its save makes it reload and
    /// call <paramref name="change"/> again (it must be a pure function of the stored hazard, and may run more than once). Campaign-scoped, not user-scoped: the table's runtime writes it, and only a DM's table runs. False when the region,
    /// the place or its hazard is not there, or <paramref name="change"/> left it.</summary>
    Task<bool> PatchPlacedHazardAsync(string campaignId, string regionId, string locationId, Func<PlacedHazard, PlacedHazard?> change);

    /// <summary>Places <paramref name="hazard"/> on one place only when it has none (a prepared encounter's editor adds a hazard this way): a patch of
    /// <see cref="PlaceFeature.Hazard"/> alone, under the same optimistic concurrency as <see cref="PatchPlacedHazardAsync"/>, so the rest of the place is never rewritten.
    /// Idempotent: a place that already holds the same entry (and homebrew flag) answers <see cref="HazardPlacing.AlreadyThere"/> and is left as it is. DM only.</summary>
    Task<HazardPlacing> PlaceHazardIfNoneAsync(string regionId, string locationId, PlacedHazard hazard, CampaignAccess access);

    /// <summary>Sets a location's visibility - the "Reveal to party" action (2.5). DM only.</summary>
    Task<bool> SetLocationVisibilityAsync(string regionId, string locationId, ActorVisibility visibility, CampaignAccess access);

    /// <summary>Takes a place off the map (U7). The places inside it move up to its parent. Returns what
    /// was removed and the places that moved, for <see cref="RestoreLocationAsync"/>; null when there is no such place or the
    /// caller is not the DM. Its art stays on the region, so an undo gets it back. DM only.</summary>
    Task<RemovedLocation?> RemoveLocationAsync(string regionId, string locationId, CampaignAccess access);

    /// <summary>Moves a place and every place inside it from one region to another, the place itself going inside
    /// <paramref name="newParentId"/> (a place of the destination; null: the top of it), in one stored change. DM only. False when
    /// either region is outside the campaign, the place or the new parent is not where it should be, or a place being moved has art
    /// (art is stored on its region and does not travel). The moving places' own maps go with them; their markers come off the maps
    /// left in the old region (MAPS_DESIGN.md 9.2).</summary>
    Task<bool> MoveLocationAsync(string fromRegionId, string toRegionId, string locationId, string? newParentId, CampaignAccess access);

    /// <summary>Puts a removed place back, and the places that moved out of it back inside it (those still where the removal put
    /// them). DM only.</summary>
    Task<bool> RestoreLocationAsync(string regionId, RemovedLocation removed, CampaignAccess access);

    /// <summary>Deletes a region with every place in it, their art and the region's maps (U7). DM only; false for a region outside the campaign.</summary>
    Task<bool> DeleteAsync(string regionId, CampaignAccess access);

    /// <summary>Deletes every region belonging to a campaign, and every map of the campaign (cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
