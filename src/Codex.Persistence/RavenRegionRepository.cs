using Raven.Client.Documents;

namespace Codex.Persistence;

public class RegionRepository(RavenDbService dbService) : IRegionRepository
{
    public async Task<IEnumerable<RegionDocument>> GetVisibleForCampaignAsync(CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var regions = await session.Query<RegionDocument, RegionsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(r => r.CampaignId == access.CampaignId)
            .ToListAsync();

        if (access.IsDm)
        {
            return regions;
        }

        foreach (var region in regions)
        {
            region.Locations = region.Locations.Where(l => l.Visibility == ActorVisibility.Known).ToList();
        }

        // A region with nothing revealed in it is the DM's planning (its name can be a spoiler, such as a quest's title),
        // so players see a region once one of its places is revealed.
        return regions.Where(r => r.Locations.Count > 0).ToList();
    }

    public async Task<RegionDocument?> GetAsync(string regionId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var region = await session.LoadAsync<RegionDocument>(regionId);
        if (region == null || region.CampaignId != access.CampaignId)
        {
            return null;
        }

        if (!access.IsDm)
        {
            region.Locations = region.Locations.Where(l => l.Visibility == ActorVisibility.Known).ToList();
            if (region.Locations.Count == 0) return null; // nothing revealed yet, as in GetVisibleForCampaignAsync
        }

        return region;
    }

    public async Task<bool> SaveAsync(RegionDocument region, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        region.UpdatedAt = DateTime.UtcNow;
        await session.StoreAsync(region);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RenameAsync(string regionId, string name, CampaignAccess access)
    {
        if (!access.IsDm || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var region = await session.LoadAsync<RegionDocument>(regionId);
        if (region == null || region.CampaignId != access.CampaignId)
        {
            return false;
        }

        region.Name = name.Trim();
        region.UpdatedAt = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpsertLocationAsync(string regionId, LocationDocument location, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var region = await session.LoadAsync<RegionDocument>(regionId);
        if (region == null || region.CampaignId != access.CampaignId)
        {
            return false;
        }

        var index = region.Locations.FindIndex(l => l.Id == location.Id);
        if (index >= 0)
        {
            region.Locations[index] = location;
        }
        else
        {
            region.Locations.Add(location);
        }

        region.UpdatedAt = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetLocationVisibilityAsync(string regionId, string locationId, ActorVisibility visibility, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var region = await session.LoadAsync<RegionDocument>(regionId);
        var location = region?.Locations.FirstOrDefault(l => l.Id == locationId);
        if (region == null || region.CampaignId != access.CampaignId || location == null)
        {
            return false;
        }

        location.Visibility = visibility;
        region.UpdatedAt = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<RegionDocument, RegionsByCampaignIndex>()
            .Where(r => r.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
