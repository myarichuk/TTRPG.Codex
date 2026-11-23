using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>A region's maps, its own and its places', by <see cref="MapDocument.RegionId"/> (MAPS_DESIGN.md 9.2); and a campaign's, for the
/// Places page and for deleting.</summary>
public class MapsByRegionIndex : AbstractIndexCreationTask<MapDocument>
{
    public MapsByRegionIndex()
    {
        Map = maps => from map in maps
                      select new
                      {
                          map.CampaignId,
                          map.RegionId,
                          map.LocationId
                      };
    }
}

public class RavenMapRepository(RavenDbService dbService, StorageQuota? quota = null) : IMapRepository
{
    private const int MaxAttempts = 8;

    public async Task<IReadOnlyList<MapDocument>> ListForCampaignAsync(CampaignAccess access)
    {
        if (string.IsNullOrEmpty(access.CampaignId)) return [];
        using var session = dbService.Store.OpenAsyncSession();
        var maps = await session.Query<MapDocument, MapsByRegionIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(m => m.CampaignId == access.CampaignId)
            .ToListAsync();
        if (maps.Count == 0) return [];
        var regions = await session.LoadAsync<RegionDocument>(maps.Select(m => m.RegionId).Distinct());
        return Ordered(maps.Select(m => ForReader(m, regions.GetValueOrDefault(m.RegionId), access)).OfType<MapDocument>()).ToList();
    }

    public async Task<IReadOnlyList<MapDocument>> ListForRegionAsync(string regionId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var region = await session.LoadAsync<RegionDocument>(regionId);
        if (region == null || region.CampaignId != access.CampaignId) return [];
        var maps = await InRegionAsync(session, regionId);
        return Ordered(maps.Select(m => ForReader(m, region, access)).OfType<MapDocument>())
            .OrderBy(m => m.LocationId == null ? 0 : 1).ThenBy(m => m.LocationId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<MapDocument>> ListVersionsAsync(string regionId, string? locationId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        var region = await session.LoadAsync<RegionDocument>(regionId);
        if (region == null || region.CampaignId != access.CampaignId) return [];
        var maps = await OfOwnerAsync(session, regionId, locationId);
        return Ordered(maps.Select(m => ForReader(m, region, access)).OfType<MapDocument>()).ToList();
    }

    public async Task<MapDocument?> GetCurrentAsync(string regionId, string? locationId, CampaignAccess access) =>
        (await ListVersionsAsync(regionId, locationId, access)).FirstOrDefault();

    public async Task<MapDocument?> GetAsync(string mapId, CampaignAccess access)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await ReadAsync(session, mapId, access);
    }

    public async Task<MapDocument?> CreateAsync(string regionId, string? locationId, string? name, string? label, CampaignAccess access)
    {
        if (await GmAsync(access) is not { } gm) return null;
        for (var attempt = 1; ; attempt++)
        {
            using var session = OpenOptimistic();
            var region = await session.LoadAsync<RegionDocument>(regionId);
            if (region == null || region.CampaignId != gm.CampaignId) return null;
            var place = locationId == null ? null : region.Locations.FirstOrDefault(l => l.Id == locationId);
            if (locationId != null && place == null) return null;

            // Two creates at once both save the owner's guard; the second save fails, and its retry finds the first one's map. The guard
            // is loaded before the maps are looked up: a create that saves after our lookup then always fails our save. Loaded after the
            // lookup, it could already carry the other create's save, and a second map would be stored.
            var guardId = MapOwnerGuard.IdFor(gm.CampaignId, regionId, locationId);
            var guard = await session.LoadAsync<MapOwnerGuard>(guardId) ?? new MapOwnerGuard { Id = guardId, CampaignId = gm.CampaignId };

            var siblings = await OfOwnerAsync(session, regionId, locationId);
            // A map with no picture yet is what a second Add map would make again: that one is opened instead.
            if (siblings.Where(m => m.Image == null).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault() is { } empty)
            {
                return empty;
            }

            guard.Creates++;
            await session.StoreAsync(guard);

            var map = new MapDocument
            {
                Id = Guid.NewGuid().ToString(),
                CampaignId = gm.CampaignId,
                RegionId = regionId,
                LocationId = locationId,
                Name = Name(name) ?? Name(place?.Name ?? region.Name) ?? string.Empty,
                Label = Label(label),
                Current = siblings.Count == 0,
                ShownToPlayers = false,
                Version = 1
            };
            await session.StoreAsync(map);
            if (siblings.Count > 0) KeepOneCurrent(siblings);
            try
            {
                await session.SaveChangesAsync();
                return map;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    public Task<bool> RenameAsync(string mapId, string name, string? label, CampaignAccess access) =>
        WriteAsync(mapId, access, (_, map, _) =>
        {
            if (Name(name) is not { } tidyName) return Task.FromResult(false);
            map.Name = tidyName;
            map.Label = Label(label);
            return Task.FromResult(true);
        });

    public Task<bool> SetGridAsync(string mapId, MapGrid grid, CampaignAccess access) =>
        WriteAsync(mapId, access, (_, map, _) =>
        {
            map.Grid = MapGrid.Tidy(grid);
            return Task.FromResult(true);
        });

    public Task<bool> SetCurrentAsync(string mapId, CampaignAccess access) =>
        WriteAsync(mapId, access, async (session, map, _) =>
        {
            foreach (var other in await OfOwnerAsync(session, map.RegionId, map.LocationId))
            {
                if (other.Id == map.Id) continue;
                if (!other.Current) continue;
                other.Current = false;
                other.Version++;
                other.UpdatedAt = DateTime.UtcNow;
            }

            map.Current = true;
            return true;
        });

    public Task<bool> SetShownToPlayersAsync(string mapId, bool shown, CampaignAccess access) =>
        WriteAsync(mapId, access, (_, map, _) =>
        {
            map.ShownToPlayers = shown;
            return Task.FromResult(true);
        });

    public async Task<MapDocument?> CopyAsNewVersionAsync(string mapId, string? label, CampaignAccess access)
    {
        if (await GmAsync(access) is not { } gm) return null;
        using var session = dbService.Store.OpenAsyncSession();
        var source = await session.LoadAsync<MapDocument>(mapId);
        if (source == null || source.CampaignId != gm.CampaignId) return null;
        var region = await session.LoadAsync<RegionDocument>(source.RegionId);
        if (region == null || region.CampaignId != gm.CampaignId) return null;
        if (source.LocationId != null && region.Locations.All(l => l.Id != source.LocationId)) return null;

        var places = region.Locations.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var copy = new MapDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = source.CampaignId,
            RegionId = source.RegionId,
            LocationId = source.LocationId,
            Name = source.Name,
            Label = Label(label),
            Current = false,
            ShownToPlayers = false,
            Grid = MapGrid.Tidy(source.Grid),
            Markers = source.Markers.Where(m => places.Contains(m.LocationId)).Select(m => new MapMarker
            {
                Id = Guid.NewGuid().ToString(),
                LocationId = m.LocationId,
                Kind = m.Kind,
                Points = m.Points.ToList(),
                LabelAt = m.LabelAt
            }).ToList(),
            Version = 1
        };

        await session.StoreAsync(copy);
        var imageBytes = source.Image == null ? 0 : StorageQuota.SizeOf(session, source, MapDocument.ImageAttachment);
        if (imageBytes > 0)
        {
            if (quota != null) await quota.EnsureRoomAsync(gm.CampaignId, imageBytes);
            // The picture is copied in the same save as the copy itself, so a copy never names a picture it doesn't have.
            copy.Image = new MapImage { Width = source.Image!.Width, Height = source.Image.Height, ContentType = source.Image.ContentType, Version = 1 };
            session.Advanced.Attachments.Copy(source, MapDocument.ImageAttachment, copy, MapDocument.ImageAttachment);
        }

        await session.SaveChangesAsync();
        return copy;
    }

    public Task<MapMarker?> PlaceMarkerAsync(string mapId, MapMarker marker, CampaignAccess access) =>
        WriteAsync<MapMarker>(mapId, access, (_, map, region) =>
        {
            if (region.Locations.All(l => l.Id != marker.LocationId) || TidyShape(marker, map.Image) is not { } points) return Task.FromResult<MapMarker?>(null);
            var index = map.Markers.FindIndex(m => !string.IsNullOrEmpty(marker.Id) && m.Id == marker.Id);
            if (index < 0) index = map.Markers.FindIndex(m => m.LocationId == marker.LocationId);
            if (index < 0 && map.Markers.Count >= MapDocument.MaxMarkers) return Task.FromResult<MapMarker?>(null);

            var stored = new MapMarker
            {
                Id = index >= 0 ? map.Markers[index].Id : Guid.NewGuid().ToString(),
                LocationId = marker.LocationId,
                Kind = marker.Kind,
                Points = points,
                LabelAt = marker.LabelAt is { } at ? Clamp(at, map.Image) : null
            };
            if (index >= 0) map.Markers[index] = stored;
            else map.Markers.Add(stored);
            // One marker per place on a map: any other one for the same place goes.
            map.Markers.RemoveAll(m => m.LocationId == stored.LocationId && m.Id != stored.Id);
            return Task.FromResult<MapMarker?>(stored);
        });

    public Task<bool> RemoveMarkerAsync(string mapId, string markerId, CampaignAccess access) =>
        WriteAsync(mapId, access, (_, map, _) => Task.FromResult(map.Markers.RemoveAll(m => m.Id == markerId) > 0));

    public async Task<bool> DeleteAsync(string mapId, CampaignAccess access)
    {
        if (await GmAsync(access) is not { } gm) return false;
        using var session = dbService.Store.OpenAsyncSession();
        var map = await session.LoadAsync<MapDocument>(mapId);
        if (map == null || map.CampaignId != gm.CampaignId) return false;

        // The picture is an attachment of the map and goes with it.
        var rest = (await OfOwnerAsync(session, map.RegionId, map.LocationId)).Where(m => m.Id != map.Id).ToList();
        session.Delete(map);
        KeepOneCurrent(rest);
        await session.SaveChangesAsync();
        return true;
    }

    public async Task<MapImageProblem> PutImageAsync(string mapId, Stream content, CampaignAccess access)
    {
        if (await GmAsync(access) is not { } gm) return MapImageProblem.NotAllowed;

        // Read no further than the limit: a bigger upload is refused without being held whole.
        var bytes = await ReadAtMostAsync(content, MapImages.MaxBytes + 1);
        if (bytes == null) return MapImageProblem.TooManyBytes;
        var problem = MapImages.Check(bytes, out var image);
        if (problem != MapImageProblem.None || image == null) return problem == MapImageProblem.None ? MapImageProblem.Malformed : problem;

        for (var attempt = 1; ; attempt++)
        {
            using var session = OpenOptimistic();
            var map = await session.LoadAsync<MapDocument>(mapId);
            if (map == null || map.CampaignId != gm.CampaignId) return MapImageProblem.NotAllowed;
            if (quota != null) await quota.EnsureRoomAsync(gm.CampaignId, image.Bytes.LongLength, StorageQuota.SizeOf(session, map, MapDocument.ImageAttachment));

            // One save: the new picture replaces the old one under the same name, together with its size, so the map never names a
            // picture that isn't the one stored.
            map.Image = new MapImage
            {
                Width = image.Width,
                Height = image.Height,
                ContentType = image.ContentType,
                Version = (map.Image?.Version ?? 0) + 1
            };
            map.Version++;
            map.UpdatedAt = DateTime.UtcNow;
            using var picture = new MemoryStream(image.Bytes, writable: false);
            session.Advanced.Attachments.Store(map, MapDocument.ImageAttachment, picture, image.ContentType);
            try
            {
                await session.SaveChangesAsync();
                return MapImageProblem.None;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    public async Task<MapImageFile?> OpenImageAsync(string mapId, CampaignAccess access)
    {
        // The stream lives as long as its session, so the session is handed over with it.
        var session = dbService.Store.OpenAsyncSession();
        try
        {
            if (await ReadAsync(session, mapId, access) is not { Image: not null })
            {
                session.Dispose();
                return null;
            }

            var attachment = await session.Advanced.Attachments.GetAsync(mapId, MapDocument.ImageAttachment);
            if (attachment == null)
            {
                session.Dispose();
                return null;
            }

            return new MapImageFile(attachment.Stream, attachment.Details.ContentType, attachment.Details.Size, attachment, session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>A map's whole picture in memory, as <see cref="OpenImageAsync"/> would stream it.</summary>
    public async Task<MediaFile?> GetImageAsync(string mapId, CampaignAccess access)
    {
        using var file = await OpenImageAsync(mapId, access);
        if (file == null) return null;
        using var buffer = new MemoryStream();
        await file.Content.CopyToAsync(buffer);
        return new MediaFile(buffer.ToArray(), file.ContentType);
    }

    // ---- Used by RegionRepository, in its own session, so a region's change and its maps' are one save. ----

    /// <summary>Deletes a region's maps (and their pictures) in <paramref name="session"/>; the caller saves.</summary>
    internal static async Task DeleteForRegionAsync(IAsyncDocumentSession session, string regionId)
    {
        foreach (var map in await InRegionAsync(session, regionId)) session.Delete(map);
        if (await session.LoadAsync<RegionDocument>(regionId) is { } region)
        {
            session.Delete(MapOwnerGuard.IdFor(region.CampaignId, regionId, null));
            foreach (var place in region.Locations) session.Delete(MapOwnerGuard.IdFor(region.CampaignId, regionId, place.Id));
        }
    }

    /// <summary>Deletes a campaign's maps in <paramref name="session"/>; the caller saves.</summary>
    internal static async Task DeleteForCampaignAsync(IAsyncDocumentSession session, string campaignId)
    {
        var maps = await session.Query<MapDocument, MapsByRegionIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(m => m.CampaignId == campaignId)
            .ToListAsync();
        foreach (var map in maps) session.Delete(map);

        const int page = 256;
        for (var start = 0; ; start += page)
        {
            var guards = (await session.Advanced.LoadStartingWithAsync<MapOwnerGuard>(MapOwnerGuard.Prefix(campaignId), start: start, pageSize: page)).ToList();
            foreach (var guard in guards) session.Delete(guard);
            if (guards.Count < page) break;
        }
    }

    /// <summary>Places (<paramref name="moving"/>, a place and everything inside it) moved to another region (MAPS_DESIGN.md 9.2): their own maps
    /// go with them, keeping the markers of places that moved too; the maps left behind lose their markers. The GM places them again on
    /// the new region's maps. In <paramref name="session"/>; the caller saves.</summary>
    internal static async Task MovePlacesAsync(IAsyncDocumentSession session, string fromRegionId, string toRegionId, IReadOnlySet<string> moving)
    {
        foreach (var map in await InRegionAsync(session, fromRegionId))
        {
            var travels = map.LocationId != null && moving.Contains(map.LocationId);
            var dropped = map.Markers.RemoveAll(m => travels ? !moving.Contains(m.LocationId) : moving.Contains(m.LocationId));
            if (travels) map.RegionId = toRegionId;
            if (!travels && dropped == 0) continue;
            map.Version++;
            map.UpdatedAt = DateTime.UtcNow;
        }
    }

    // ---- Reading ----

    private static async Task<MapDocument?> ReadAsync(IAsyncDocumentSession session, string mapId, CampaignAccess access)
    {
        if (string.IsNullOrEmpty(access.CampaignId)) return null;
        var map = await session.LoadAsync<MapDocument>(mapId);
        if (map == null || map.CampaignId != access.CampaignId) return null;
        return ForReader(map, await session.LoadAsync<RegionDocument>(map.RegionId), access);
    }

    /// <summary>The map as <paramref name="access"/> may see it, or null (MAPS_DESIGN.md 9.4). Places a player knows are those
    /// <see cref="RegionRepository.KnownToPlayers"/> lets through, the same rule the places list uses.</summary>
    private static MapDocument? ForReader(MapDocument map, RegionDocument? region, CampaignAccess access)
    {
        if (region == null || region.Id != map.RegionId || region.CampaignId != map.CampaignId || map.CampaignId != access.CampaignId) return null;

        // A deleted place's maps wait, unseen, for an undo (its sweep is MAPS_DESIGN.md 9.10 step 8).
        if (map.LocationId != null && region.Locations.All(l => l.Id != map.LocationId)) return null;

        var seen = (access.IsDm ? region.Locations : region.Locations.Where(RegionRepository.KnownToPlayers))
            .Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (!access.IsDm)
        {
            // A map with no picture is nothing to look at: players aren't offered it, by its pin, its Map button or the version list.
            if (map.Image == null || !map.ShownToPlayers) return null;
            if (map.LocationId == null ? seen.Count == 0 : !seen.Contains(map.LocationId)) return null;
        }

        map.Markers = map.Markers.Where(m => seen.Contains(m.LocationId)).ToList();
        return map;
    }

    // Current first, then oldest first: one owner's versions as the switcher lists them.
    private static IEnumerable<MapDocument> Ordered(IEnumerable<MapDocument> maps) =>
        maps.OrderBy(m => m.RegionId, StringComparer.Ordinal).ThenBy(m => m.LocationId ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(m => m.Current).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal);

    private static async Task<List<MapDocument>> InRegionAsync(IAsyncDocumentSession session, string regionId) =>
        await session.Query<MapDocument, MapsByRegionIndex>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(m => m.RegionId == regionId)
            .ToListAsync();

    private static async Task<List<MapDocument>> OfOwnerAsync(IAsyncDocumentSession session, string regionId, string? locationId) =>
        (await InRegionAsync(session, regionId)).Where(m => m.LocationId == locationId).ToList();

    // ---- Writing ----

    private IAsyncDocumentSession OpenOptimistic()
    {
        var session = dbService.Store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        return session;
    }

    private async Task<CampaignAccess?> GmAsync(CampaignAccess access) =>
        await CampaignAccessResolver.NowAsync(dbService.Store, access) is { IsDm: true } current ? current : null;

    private async Task<bool> WriteAsync(string mapId, CampaignAccess access, Func<IAsyncDocumentSession, MapDocument, RegionDocument, Task<bool>> change) =>
        await WriteAsync<object>(mapId, access, async (session, map, region) => await change(session, map, region) ? new object() : null) != null;

    // One write under optimistic concurrency, as RavenUpdate does (its loop loads one document and takes a plain Action, where this needs
    // the region and the session too): the change is applied to a fresh load, and a save that lands between our load and our save makes
    // us load and apply it again. Markers of places that are gone are dropped on every save.
    private async Task<T?> WriteAsync<T>(string mapId, CampaignAccess access, Func<IAsyncDocumentSession, MapDocument, RegionDocument, Task<T?>> change) where T : class
    {
        if (await GmAsync(access) is not { } gm) return null;
        for (var attempt = 1; ; attempt++)
        {
            using var session = OpenOptimistic();
            var map = await session.LoadAsync<MapDocument>(mapId);
            if (map == null || map.CampaignId != gm.CampaignId) return null;
            var region = await session.LoadAsync<RegionDocument>(map.RegionId);
            if (region == null || region.CampaignId != gm.CampaignId) return null;

            if (await change(session, map, region) is not { } result) return null;
            var places = region.Locations.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            map.Markers.RemoveAll(m => !places.Contains(m.LocationId));
            map.Version++;
            map.UpdatedAt = DateTime.UtcNow;
            try
            {
                await session.SaveChangesAsync();
                return result;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    // Exactly one current version per owner: the current one that was made first, or else the oldest.
    private static void KeepOneCurrent(List<MapDocument> maps)
    {
        if (maps.Count == 0) return;
        var keep = maps.Where(m => m.Current).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault()
                   ?? maps.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal).First();
        foreach (var map in maps)
        {
            var current = map.Id == keep.Id;
            if (map.Current == current) continue;
            map.Current = current;
            map.Version++;
            map.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static List<MapPoint>? TidyShape(MapMarker marker, MapImage? image)
    {
        if (!Enum.IsDefined(marker.Kind) || marker.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return null;
        var points = marker.Points.Select(p => Clamp(p, image)).ToList();
        return marker.Kind switch
        {
            MapMarkerKind.Pin when points.Count == 1 => points,
            MapMarkerKind.Area when points.Count is >= 3 and <= MapMarker.MaxCorners => points,
            _ => null
        };
    }

    private static MapPoint Clamp(MapPoint p, MapImage? image) => image == null
        ? new MapPoint(Math.Max(0, p.X), Math.Max(0, p.Y))
        : new MapPoint(Math.Clamp(p.X, 0, image.Width), Math.Clamp(p.Y, 0, image.Height));

    private static string? Name(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim() is var t && t.Length > MapDocument.MaxName ? t[..MapDocument.MaxName] : name.Trim();

    private static string? Label(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim() is var t && t.Length > MapDocument.MaxLabel ? t[..MapDocument.MaxLabel] : label.Trim();

    private static async Task<byte[]?> ReadAtMostAsync(Stream content, long limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read >= limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
