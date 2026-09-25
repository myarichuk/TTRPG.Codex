namespace Codex.Persistence;

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

    /// <summary>Adds or replaces (by <see cref="LocationDocument.Id"/>) a location within a region. DM only.</summary>
    Task<bool> UpsertLocationAsync(string regionId, LocationDocument location, CampaignAccess access);

    /// <summary>Sets a location's visibility - the "Reveal to party" action (2.5). DM only.</summary>
    Task<bool> SetLocationVisibilityAsync(string regionId, string locationId, ActorVisibility visibility, CampaignAccess access);

    /// <summary>Deletes every region belonging to a campaign (cascade delete).</summary>
    Task DeleteAllForCampaignAsync(string campaignId);
}
