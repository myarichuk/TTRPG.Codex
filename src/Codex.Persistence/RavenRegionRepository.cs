using Codex.Core;
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

        var owned = await OwnedActors.OfAsync(session, access);
        foreach (var region in regions)
        {
            region.Locations = ForPlayers(region.Locations, owned);
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
            region.Locations = ForPlayers(region.Locations, await OwnedActors.OfAsync(session, access));
            if (region.Locations.Count == 0) return null; // nothing revealed yet, as in GetVisibleForCampaignAsync
        }

        return region;
    }

    /// <summary>The places a player may see, each description without the blocks they may not read (see <see cref="SecretText"/>)
    /// and without a feature's notes.</summary>
    private static List<LocationDocument> ForPlayers(IEnumerable<LocationDocument> locations, IReadOnlySet<string> ownedActorIds) =>
        locations.Where(l => l.Visibility == ActorVisibility.Known)
            .Select(l =>
            {
                l.Description = string.IsNullOrEmpty(l.Description) ? l.Description : SecretText.ForPlayers(l.Description, ownedActorIds);
                // The lock, the trap and what is inside are the DM's to tell (a revealed chest still hides its mimic).
                l.Feature = null;
                return l;
            })
            .ToList();

    public async Task<bool> SaveAsync(RegionDocument region, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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
        await ForgetRemovedArtAsync(session, region);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetKindAsync(string regionId, RegionKind kind, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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

        region.Kind = kind;
        region.UpdatedAt = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpsertLocationAsync(string regionId, LocationDocument location, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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
        await ForgetRemovedArtAsync(session, region);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetLocationVisibilityAsync(string regionId, string locationId, ActorVisibility visibility, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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

    public async Task<RemovedLocation?> RemoveLocationAsync(string regionId, string locationId, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return null;
        access = current;

        if (!access.IsDm)
        {
            return null;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var region = await session.LoadAsync<RegionDocument>(regionId);
        var location = region?.Locations.FirstOrDefault(l => l.Id == locationId);
        if (region == null || region.CampaignId != access.CampaignId || location == null)
        {
            return null;
        }

        var moved = new List<string>();
        foreach (var child in region.Locations.Where(l => l.ParentId == locationId))
        {
            child.ParentId = location.ParentId;
            moved.Add(child.Id);
        }

        region.Locations.Remove(location);
        region.UpdatedAt = DateTime.UtcNow;
        await ForgetRemovedArtAsync(session, region);
        if (location.HasArt) region.RemovedArt[location.Id] = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return new RemovedLocation(location, moved);
    }

    public async Task<bool> MoveLocationAsync(string fromRegionId, string toRegionId, string locationId, string? newParentId, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

        if (!access.IsDm || fromRegionId == toRegionId)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var from = await session.LoadAsync<RegionDocument>(fromRegionId);
        var to = await session.LoadAsync<RegionDocument>(toRegionId);
        var place = from?.Locations.FirstOrDefault(l => l.Id == locationId);
        if (from == null || to == null || place == null || from.CampaignId != access.CampaignId || to.CampaignId != access.CampaignId)
        {
            return false;
        }

        if (newParentId != null && to.Locations.All(l => l.Id != newParentId))
        {
            return false;
        }

        // The place and everything inside it, parents first.
        var moving = new List<LocationDocument> { place };
        for (var i = 0; i < moving.Count; i++)
        {
            moving.AddRange(from.Locations.Where(l => l.ParentId == moving[i].Id && moving.All(m => m.Id != l.Id)));
        }

        if (moving.Any(l => l.HasArt))
        {
            return false;
        }

        place.ParentId = newParentId;
        foreach (var l in moving)
        {
            from.Locations.Remove(l);
            to.Locations.Add(l);
        }

        from.UpdatedAt = to.UpdatedAt = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return true;
    }

    /// <summary>How long a deleted place's art is kept for Undo: well past the toast's twelve seconds.</summary>
    public static readonly TimeSpan RemovedArtKept = TimeSpan.FromHours(1);

    // Drops the art of places deleted longer ago than Undo can reach. Run on any change to the region, so nothing sweeps
    // on its own schedule.
    private static async Task ForgetRemovedArtAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, RegionDocument region)
    {
        foreach (var id in region.RemovedArt.Where(e => DateTime.UtcNow - e.Value > RemovedArtKept).Select(e => e.Key).ToList())
        {
            if (await session.Advanced.Attachments.ExistsAsync(region.Id, RavenMediaStore.LocationArtName(id)))
            {
                session.Advanced.Attachments.Delete(region, RavenMediaStore.LocationArtName(id));
            }

            region.RemovedArt.Remove(id);
        }
    }

    public async Task<bool> RestoreLocationAsync(string regionId, RemovedLocation removed, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

        if (!access.IsDm)
        {
            return false;
        }

        using var session = dbService.Store.OpenAsyncSession();
        var region = await session.LoadAsync<RegionDocument>(regionId);
        if (region == null || region.CampaignId != access.CampaignId || region.Locations.Any(l => l.Id == removed.Location.Id))
        {
            return false;
        }

        // Art dropped since (an undo can't come this late, but a stale copy of the toast's action could) is gone for good.
        if (!region.RemovedArt.Remove(removed.Location.Id) && removed.Location.HasArt)
        {
            removed.Location.HasArt = false;
        }

        region.Locations.Add(removed.Location);
        foreach (var child in region.Locations.Where(l => removed.MovedIds.Contains(l.Id) && l.ParentId == removed.Location.ParentId))
        {
            child.ParentId = removed.Location.Id;
        }


        region.UpdatedAt = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(string regionId, CampaignAccess access)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { } current) return false;
        access = current;

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

        // The art of the region's places is stored on the region document and goes with it.
        session.Delete(region);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var toDelete = await session.Query<RegionDocument, RegionsByCampaignIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(r => r.CampaignId == campaignId)
            .ToListAsync();

        foreach (var doc in toDelete)
        {
            session.Delete(doc.Id);
        }

        await session.SaveChangesAsync();
    }
}
