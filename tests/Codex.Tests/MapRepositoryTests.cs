using Codex.Core;
using Codex.Persistence;

namespace Codex.Tests;

/// <summary>Map pages, step 1 (MAPS_DESIGN.md 9.9, 9.10): the map document, its versions, its markers, its picture, and the rule that
/// nothing hidden reaches a player.</summary>
[Collection("RavenDb")]
public class MapRepositoryTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _db;
    private readonly RegionRepository _regions;
    private readonly RavenMapRepository _maps;

    public MapRepositoryTests(RavenDbFixture fixture)
    {
        _db = fixture.Open(fixture.DbName);
        _regions = new RegionRepository(_db);
        _maps = new RavenMapRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private CampaignAccess Gm(string campaignId) => TestAccess.Member(_db, campaignId, "gm", CampaignRole.DM);
    private CampaignAccess Player(string campaignId) => TestAccess.Member(_db, campaignId, "player", CampaignRole.Player);

    private sealed record World(string CampaignId, RegionDocument Region, LocationDocument Town, LocationDocument Mill, LocationDocument Crypt);

    // A region with a known town, a known mill inside it and a hidden crypt inside it.
    private async Task<World> WorldAsync(bool anythingKnown = true)
    {
        var campaignId = "camp-maps-" + Guid.NewGuid();
        var gm = Gm(campaignId);
        Player(campaignId);
        var region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vale" };
        Assert.True(await _regions.SaveAsync(region, gm));
        var known = anythingKnown ? ActorVisibility.Known : ActorVisibility.Hidden;
        var town = new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Brindle", Kind = PlaceKind.Settlement, Visibility = known };
        var mill = new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Old Mill", ParentId = town.Id, Visibility = known };
        var crypt = new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Crypt", ParentId = town.Id, Visibility = ActorVisibility.Hidden };
        foreach (var place in new[] { town, mill, crypt }) Assert.True(await _regions.UpsertLocationAsync(region.Id, place, gm));
        return new World(campaignId, region, town, mill, crypt);
    }

    /// <summary>A map players can be given: made, with a picture, and shown to them (a new map starts hidden).</summary>
    private async Task<MapDocument> ShownMapAsync(World w, string? locationId, string? label = null, int width = 40, int height = 30)
    {
        var gm = Gm(w.CampaignId);
        var map = (await _maps.CreateAsync(w.Region.Id, locationId, null, label, gm))!;
        Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(map.Id, new MemoryStream(Png(width, height)), gm));
        Assert.True(await _maps.SetShownToPlayersAsync(map.Id, true, gm));
        return map;
    }

    private static MapMarker Pin(string locationId, double x = 10, double y = 20) =>
        new() { LocationId = locationId, Kind = MapMarkerKind.Pin, Points = [new MapPoint(x, y)] };

    private static MapMarker Area(string locationId) =>
        new() { LocationId = locationId, Kind = MapMarkerKind.Area, Points = [new MapPoint(0, 0), new MapPoint(30, 0), new MapPoint(30, 30)] };

    [Fact]
    public async Task APlayer_NeverGetsAMarkerForAHiddenPlace_FromAnyCall()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = await ShownMapAsync(w, w.Town.Id);
        Assert.NotNull(await _maps.PlaceMarkerAsync(map.Id, Pin(w.Mill.Id), gm));
        Assert.NotNull(await _maps.PlaceMarkerAsync(map.Id, Area(w.Crypt.Id), gm));

        var player = Player(w.CampaignId);
        var reads = new List<MapDocument?>
        {
            await _maps.GetAsync(map.Id, player),
            await _maps.GetCurrentAsync(w.Region.Id, w.Town.Id, player)
        };
        reads.AddRange(await _maps.ListVersionsAsync(w.Region.Id, w.Town.Id, player));
        reads.AddRange(await _maps.ListForRegionAsync(w.Region.Id, player));
        reads.AddRange(await _maps.ListForCampaignAsync(player));
        reads.Add(await _maps.GetAsync(map.Id, gm.PartyPreview()!));
        Assert.Equal(6, reads.Count);
        foreach (var read in reads)
        {
            Assert.NotNull(read);
            Assert.Equal([w.Mill.Id], read.Markers.Select(m => m.LocationId));
        }

        // The GM sees both, and revealing the crypt shows its area at once.
        Assert.Equal(2, (await _maps.GetAsync(map.Id, gm))!.Markers.Count);
        Assert.True(await _regions.SetLocationVisibilityAsync(w.Region.Id, w.Crypt.Id, ActorVisibility.Known, gm));
        Assert.Equal(2, (await _maps.GetAsync(map.Id, player))!.Markers.Count);
    }

    [Fact]
    public async Task APlayer_CantGetAMapOfAHiddenPlace_OrAVersionNotShown_AsAMapOrAPicture()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var player = Player(w.CampaignId);
        var crypt = await ShownMapAsync(w, w.Crypt.Id);
        var town = await ShownMapAsync(w, w.Town.Id);

        Assert.Null(await _maps.GetAsync(crypt.Id, player));
        Assert.Null(await _maps.GetImageAsync(crypt.Id, player));
        Assert.Empty(await _maps.ListVersionsAsync(w.Region.Id, w.Crypt.Id, player));
        Assert.DoesNotContain(await _maps.ListForRegionAsync(w.Region.Id, player), m => m.Id == crypt.Id);
        Assert.NotNull(await _maps.GetImageAsync(crypt.Id, gm));

        Assert.NotNull(await _maps.GetImageAsync(town.Id, player));
        Assert.True(await _maps.SetShownToPlayersAsync(town.Id, false, gm));
        Assert.Null(await _maps.GetAsync(town.Id, player));
        Assert.Null(await _maps.GetImageAsync(town.Id, player));
        Assert.Null(await _maps.GetCurrentAsync(w.Region.Id, w.Town.Id, player));
        Assert.Empty(await _maps.ListForRegionAsync(w.Region.Id, player));
        Assert.Equal(2, (await _maps.ListForRegionAsync(w.Region.Id, gm)).Count);

        // Another campaign's GM gets nothing either.
        var stranger = Gm("camp-other-" + Guid.NewGuid());
        Assert.Null(await _maps.GetAsync(town.Id, stranger));
        Assert.Null(await _maps.GetImageAsync(town.Id, stranger));
        Assert.Empty(await _maps.ListForRegionAsync(w.Region.Id, stranger));
    }

    [Fact]
    public async Task ARegionsOwnMap_ReachesPlayers_OnlyOnceOneOfItsPlacesIsKnown()
    {
        var w = await WorldAsync(anythingKnown: false);
        var gm = Gm(w.CampaignId);
        var player = Player(w.CampaignId);
        var map = await ShownMapAsync(w, null);
        Assert.Equal("Vale", map.Name);

        Assert.Null(await _maps.GetAsync(map.Id, player));
        Assert.Null(await _maps.GetCurrentAsync(w.Region.Id, null, player));

        Assert.True(await _regions.SetLocationVisibilityAsync(w.Region.Id, w.Mill.Id, ActorVisibility.Known, gm));
        Assert.Equal(map.Id, (await _maps.GetCurrentAsync(w.Region.Id, null, player))?.Id);
    }

    [Fact]
    public async Task APlace_HasExactlyOneCurrentVersion()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var player = Player(w.CampaignId);
        // Each gets its picture before the next is made: Add map on an owner with an empty map opens that one (see below).
        var summer = await ShownMapAsync(w, w.Town.Id, "Summer");
        var winter = await ShownMapAsync(w, w.Town.Id, "Winter");
        var fire = await ShownMapAsync(w, w.Town.Id, "After the fire");
        Assert.Equal("Brindle", summer.Name);

        async Task<string[]> CurrentAsync() => (await _maps.ListVersionsAsync(w.Region.Id, w.Town.Id, gm)).Where(m => m.Current).Select(m => m.Id).ToArray();
        Assert.Equal([summer.Id], await CurrentAsync());

        Assert.True(await _maps.SetCurrentAsync(winter.Id, gm));
        Assert.Equal([winter.Id], await CurrentAsync());
        Assert.Equal(winter.Id, (await _maps.ListVersionsAsync(w.Region.Id, w.Town.Id, gm))[0].Id);

        // Players get the shown versions, the current one first; a hidden current version leaves them the next one shown.
        Assert.True(await _maps.SetShownToPlayersAsync(winter.Id, false, gm));
        Assert.Equal([summer.Id, fire.Id], (await _maps.ListVersionsAsync(w.Region.Id, w.Town.Id, player)).Select(m => m.Id));

        Assert.True(await _maps.DeleteAsync(winter.Id, gm));
        Assert.Equal([summer.Id], await CurrentAsync());

        // Another place's maps are their own.
        var mill = (await _maps.CreateAsync(w.Region.Id, w.Mill.Id, null, null, gm))!;
        Assert.True(mill.Current);
        Assert.Equal([summer.Id], await CurrentAsync());
    }

    [Fact]
    public async Task CopyAsNewVersion_CopiesThePictureTheGridAndTheMarkers_AndHidesTheCopy()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = await ShownMapAsync(w, w.Town.Id, width: 64, height: 48);
        Assert.True(await _maps.RenameAsync(map.Id, "Brindle", "Before", gm));
        Assert.True(await _maps.SetGridAsync(map.Id, new MapGrid { Kind = MapGridKind.HexRows, CellSize = 32, OffsetX = 3, OffsetY = 4, Unit = "5 ft" }, gm));
        var pin = (await _maps.PlaceMarkerAsync(map.Id, Pin(w.Mill.Id), gm))!;
        Assert.NotNull(await _maps.PlaceMarkerAsync(map.Id, Area(w.Crypt.Id), gm));

        var copy = (await _maps.CopyAsNewVersionAsync(map.Id, "After the fire", gm))!;
        var stored = (await _maps.GetAsync(copy.Id, gm))!;
        Assert.Equal("After the fire", stored.Label);
        Assert.False(stored.Current);
        Assert.False(stored.ShownToPlayers);
        Assert.Equal(MapGridKind.HexRows, stored.Grid.Kind);
        Assert.Equal(32, stored.Grid.CellSize);
        Assert.Equal("5 ft", stored.Grid.Unit);
        Assert.Equal(new[] { w.Mill.Id, w.Crypt.Id }.Order(), stored.Markers.Select(m => m.LocationId).Order());
        Assert.DoesNotContain(stored.Markers, m => m.Id == pin.Id);
        Assert.Equal((64, 48), (stored.Image!.Width, stored.Image.Height));

        var original = (await _maps.GetImageAsync(map.Id, gm))!;
        var copied = (await _maps.GetImageAsync(copy.Id, gm))!;
        Assert.Equal(original.Bytes, copied.Bytes);
        Assert.Equal("image/png", copied.ContentType);

        // The copy is a version of its own: moving a marker on it leaves the original where it was.
        Assert.NotNull(await _maps.PlaceMarkerAsync(copy.Id, Pin(w.Mill.Id, 50, 40), gm));
        Assert.Equal(new MapPoint(10, 20), (await _maps.GetAsync(map.Id, gm))!.Markers.Single(m => m.LocationId == w.Mill.Id).Points[0]);
        Assert.Equal(2, (await _maps.ListVersionsAsync(w.Region.Id, w.Town.Id, gm)).Count);
        Assert.Single(await _maps.ListVersionsAsync(w.Region.Id, w.Town.Id, Player(w.CampaignId)));
    }

    [Fact]
    public async Task Markers_StayInsideThePicture_OnePerPlace_AndOnlyForPlacesOfTheRegion()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(map.Id, new MemoryStream(Png(100, 80)), gm));

        var first = (await _maps.PlaceMarkerAsync(map.Id, Pin(w.Mill.Id, 500, -5), gm))!;
        Assert.Equal(new MapPoint(100, 0), first.Points[0]);
        var moved = (await _maps.PlaceMarkerAsync(map.Id, Pin(w.Mill.Id, 7, 8), gm))!;
        Assert.Equal(first.Id, moved.Id);
        Assert.Single((await _maps.GetAsync(map.Id, gm))!.Markers);

        Assert.Null(await _maps.PlaceMarkerAsync(map.Id, Pin("no-such-place"), gm));
        Assert.Null(await _maps.PlaceMarkerAsync(map.Id, new MapMarker { LocationId = w.Crypt.Id, Kind = MapMarkerKind.Area, Points = [new MapPoint(1, 1), new MapPoint(2, 2)] }, gm));
        Assert.Null(await _maps.PlaceMarkerAsync(map.Id, new MapMarker { LocationId = w.Crypt.Id, Kind = MapMarkerKind.Pin, Points = [new MapPoint(double.NaN, 1)] }, gm));

        Assert.True(await _maps.RemoveMarkerAsync(map.Id, first.Id, gm));
        Assert.Empty((await _maps.GetAsync(map.Id, gm))!.Markers);
    }

    [Fact]
    public async Task ADeletedPlace_LosesItsMarkers_OnReadForEveryone_AndOnTheNextSave()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        var millMap = (await _maps.CreateAsync(w.Region.Id, w.Mill.Id, null, null, gm))!;
        Assert.NotNull(await _maps.PlaceMarkerAsync(map.Id, Pin(w.Mill.Id), gm));

        var removed = (await _regions.RemoveLocationAsync(w.Region.Id, w.Mill.Id, gm))!;
        Assert.Empty((await _maps.GetAsync(map.Id, gm))!.Markers);
        Assert.Null(await _maps.GetAsync(millMap.Id, gm));

        // Undo brings the place back with its marker and its map, since nothing was saved in between.
        Assert.True(await _regions.RestoreLocationAsync(w.Region.Id, removed, gm));
        Assert.Single((await _maps.GetAsync(map.Id, gm))!.Markers);
        Assert.NotNull(await _maps.GetAsync(millMap.Id, gm));

        Assert.NotNull(await _regions.RemoveLocationAsync(w.Region.Id, w.Mill.Id, gm));
        Assert.True(await _maps.SetShownToPlayersAsync(map.Id, true, gm));
        using var session = _db.Store.OpenAsyncSession();
        Assert.Empty((await session.LoadAsync<MapDocument>(map.Id)).Markers);
    }

    [Fact]
    public async Task MovingAPlace_TakesItsMarkersOffTheOldRegionsMaps_AndTakesItsOwnMapsAlong()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var other = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = w.CampaignId, Name = "Coast" };
        Assert.True(await _regions.SaveAsync(other, gm));
        var regionMap = (await _maps.CreateAsync(w.Region.Id, null, null, null, gm))!;
        var townMap = await ShownMapAsync(w, w.Town.Id);
        Assert.NotNull(await _maps.PlaceMarkerAsync(regionMap.Id, Pin(w.Town.Id), gm));
        Assert.NotNull(await _maps.PlaceMarkerAsync(townMap.Id, Pin(w.Mill.Id), gm));

        Assert.True(await _regions.MoveLocationAsync(w.Region.Id, other.Id, w.Town.Id, null, gm));

        Assert.Empty((await _maps.GetAsync(regionMap.Id, gm))!.Markers);
        using (var session = _db.Store.OpenAsyncSession())
        {
            Assert.Empty((await session.LoadAsync<MapDocument>(regionMap.Id)).Markers);
        }

        var moved = (await _maps.GetAsync(townMap.Id, gm))!;
        Assert.Equal(other.Id, moved.RegionId);
        Assert.Equal([w.Mill.Id], moved.Markers.Select(m => m.LocationId));
        Assert.Equal(townMap.Id, (await _maps.GetCurrentAsync(other.Id, w.Town.Id, Player(w.CampaignId)))?.Id);
    }

    [Fact]
    public async Task DeletingARegion_OrTheCampaign_DeletesItsMaps()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(map.Id, new MemoryStream(Png(10, 10)), gm));
        Assert.NotNull(await _maps.CreateAsync(w.Region.Id, null, null, null, gm));
        Assert.True(await _regions.DeleteAsync(w.Region.Id, gm));
        using (var session = _db.Store.OpenAsyncSession())
        {
            Assert.Null(await session.LoadAsync<MapDocument>(map.Id));
            // The owners' guards go with their maps.
            Assert.Null(await session.LoadAsync<MapOwnerGuard>(MapOwnerGuard.IdFor(w.CampaignId, w.Region.Id, w.Town.Id)));
            Assert.Null(await session.LoadAsync<MapOwnerGuard>(MapOwnerGuard.IdFor(w.CampaignId, w.Region.Id, null)));
        }

        var v = await WorldAsync();
        var second = (await _maps.CreateAsync(v.Region.Id, null, null, null, Gm(v.CampaignId)))!;
        Assert.NotNull(await _maps.CreateAsync(v.Region.Id, v.Mill.Id, null, null, Gm(v.CampaignId)));
        await _regions.DeleteAllForCampaignAsync(v.CampaignId);
        using (var session = _db.Store.OpenAsyncSession())
        {
            Assert.Null(await session.LoadAsync<MapDocument>(second.Id));
            Assert.Empty(await session.Advanced.LoadStartingWithAsync<MapOwnerGuard>(MapOwnerGuard.Prefix(v.CampaignId)));
        }
    }

    [Fact]
    public async Task OnlyTheGm_WritesMaps()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        foreach (var notGm in new[] { Player(w.CampaignId), gm.PartyPreview()!, Gm("camp-other-" + Guid.NewGuid()) })
        {
            Assert.Null(await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, notGm));
            Assert.False(await _maps.RenameAsync(map.Id, "Mine", null, notGm));
            Assert.False(await _maps.SetGridAsync(map.Id, new MapGrid { Kind = MapGridKind.Square }, notGm));
            Assert.False(await _maps.SetCurrentAsync(map.Id, notGm));
            Assert.False(await _maps.SetShownToPlayersAsync(map.Id, false, notGm));
            Assert.Null(await _maps.CopyAsNewVersionAsync(map.Id, null, notGm));
            Assert.Null(await _maps.PlaceMarkerAsync(map.Id, Pin(w.Mill.Id), notGm));
            Assert.False(await _maps.RemoveMarkerAsync(map.Id, "any", notGm));
            Assert.Equal(MapImageProblem.NotAllowed, await _maps.PutImageAsync(map.Id, new MemoryStream(Png(10, 10)), notGm));
            Assert.False(await _maps.DeleteAsync(map.Id, notGm));
        }

        var stored = (await _maps.GetAsync(map.Id, gm))!;
        Assert.Equal("Brindle", stored.Name);
        Assert.Null(stored.Image);
        Assert.Empty(stored.Markers);
        Assert.Single(await _maps.ListVersionsAsync(w.Region.Id, w.Town.Id, gm));

        // The media store's own check knows maps too: the GM may, a player may not.
        var media = new RavenMediaStore(_db);
        await media.PutAsync(gm, map.Id, "probe", new MemoryStream(Png(2, 2)), "image/png");
        await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() => media.PutAsync(Player(w.CampaignId), map.Id, "probe", new MemoryStream(Png(2, 2)), "image/png"));
    }

    [Fact]
    public async Task APicture_IsRefused_WhenTooBig_APixelBomb_OrAnSvgCalledPng()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;

        var huge = new byte[MapImages.MaxBytes + 1];
        Png(10, 10).CopyTo(huge, 0);
        Assert.Equal(MapImageProblem.TooManyBytes, await _maps.PutImageAsync(map.Id, new MemoryStream(huge), gm));

        // A few hundred bytes whose header claims 20000 x 20000 (400 megapixels): refused from the header, never decoded.
        var bomb = Png(20000, 20000);
        Assert.True(bomb.Length < 200);
        Assert.Equal(MapImageProblem.TooManyPixels, await _maps.PutImageAsync(map.Id, new MemoryStream(bomb), gm));
        Assert.Equal(MapImageProblem.TooManyPixels, MapImages.Check(Png(8200, 8200), out _));
        Assert.Equal(MapImageProblem.None, MapImages.Check(Png(8000, 8000), out _));

        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><rect width=\"10\" height=\"10\"/></svg>"u8.ToArray();
        Assert.Equal(MapImageProblem.Unsupported, await _maps.PutImageAsync(map.Id, new MemoryStream(svg), gm));
        Assert.Equal(MapImageProblem.Unsupported, await _maps.PutImageAsync(map.Id, new MemoryStream("GIF89a\u0001\0\u0001\0\0\0\0;"u8.ToArray()), gm));
        Assert.Equal(MapImageProblem.Malformed, await _maps.PutImageAsync(map.Id, new MemoryStream(Png(10, 10)[..20]), gm));

        Assert.Null((await _maps.GetAsync(map.Id, gm))!.Image);
        Assert.Null(await _maps.GetImageAsync(map.Id, gm));

        // A real one is kept, with its size, and a new one bumps the version the URL carries.
        Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(map.Id, new MemoryStream(Png(30, 20)), gm));
        Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(map.Id, new MemoryStream(Png(60, 40)), gm));
        var image = (await _maps.GetAsync(map.Id, gm))!.Image!;
        Assert.Equal((60, 40, 2, "image/png"), (image.Width, image.Height, image.Version, image.ContentType));
    }

    [Fact]
    public async Task APicture_CountsAgainstTheOwnersStorage()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        using (var session = _db.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new UserDocument { Id = "gm", StorageLimitMb = 1 });
            await session.SaveChangesAsync();
        }

        var settings = new RavenServerSettingsStore(_db);
        var maps = new RavenMapRepository(_db, new StorageQuota(_db, settings));
        var map = (await maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        var big = Png(10, 10, extraBytes: 2 * 1024 * 1024);
        await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() => maps.PutImageAsync(map.Id, new MemoryStream(big), gm));
        Assert.Null((await maps.GetAsync(map.Id, gm))!.Image);
    }

    [Fact]
    public async Task ANewMap_StartsHiddenFromPlayers_AndPlayersAreNeverOfferedOneWithNoPicture()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var player = Player(w.CampaignId);
        var townMap = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        var millMap = (await _maps.CreateAsync(w.Region.Id, w.Mill.Id, null, null, gm))!;
        Assert.False(townMap.ShownToPlayers);
        Assert.False((await _maps.GetAsync(townMap.Id, gm))!.ShownToPlayers);

        // Shown, but with no picture: no map for players by any read, so the town map's pin carries no "has a map" sign for them and
        // the mill has no Open map (both come from the region's list), and there is no version to switch to.
        Assert.True(await _maps.SetShownToPlayersAsync(millMap.Id, true, gm));
        Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(townMap.Id, new MemoryStream(Png(40, 30)), gm));
        Assert.True(await _maps.PlaceMarkerAsync(townMap.Id, Pin(w.Mill.Id), gm) != null);
        foreach (var reader in new[] { player, gm.PartyPreview()! })
        {
            Assert.Null(await _maps.GetAsync(millMap.Id, reader));
            Assert.Empty(await _maps.ListVersionsAsync(w.Region.Id, w.Mill.Id, reader));
            Assert.Null(await _maps.GetCurrentAsync(w.Region.Id, w.Mill.Id, reader));
            Assert.DoesNotContain(await _maps.ListForRegionAsync(w.Region.Id, reader), m => m.LocationId == w.Mill.Id);
            Assert.DoesNotContain(await _maps.ListForCampaignAsync(reader), m => m.LocationId == w.Mill.Id);
            // The town map has its picture but is still hidden.
            Assert.Null(await _maps.GetAsync(townMap.Id, reader));
            Assert.Null(await _maps.GetImageAsync(townMap.Id, reader));
        }

        // Showing it is the GM's own step; then players get it, picture and all.
        Assert.True(await _maps.SetShownToPlayersAsync(townMap.Id, true, gm));
        Assert.NotNull(await _maps.GetImageAsync(townMap.Id, player));
        Assert.Equal([townMap.Id], (await _maps.ListForCampaignAsync(player)).Select(m => m.Id));
        Assert.Equal(2, (await _maps.ListForCampaignAsync(gm)).Count);
    }

    [Fact]
    public async Task AddMapTwice_OpensTheSameEmptyMap_EvenAtOnce_AndLeavesOneCurrentVersion()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var first = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        Assert.Equal(first.Id, (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, "Winter", gm))?.Id);

        var made = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _maps.CreateAsync(w.Region.Id, w.Mill.Id, null, null, gm)));
        Assert.Single(made.Select(m => m!.Id).Distinct());
        var versions = await _maps.ListVersionsAsync(w.Region.Id, w.Mill.Id, gm);
        Assert.Single(versions);
        Assert.True(versions[0].Current);

        // Once the map has its picture, Add map makes a second version, which is not current.
        Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(first.Id, new MemoryStream(Png(10, 10)), gm));
        var second = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, "Winter", gm))!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal([first.Id], (await _maps.ListVersionsAsync(w.Region.Id, w.Town.Id, gm)).Where(m => m.Current).Select(m => m.Id));
    }

    [Fact]
    public async Task ReplacingAPicture_AndCopyingAMap_AreEachOneSave()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(map.Id, new MemoryStream(Png(30, 20)), gm));

        var saves = 0;
        void Count(object? sender, Raven.Client.Documents.Session.SucceedRequestEventArgs e)
        {
            if (e.Request.RequestUri?.AbsolutePath.EndsWith("/bulk_docs", StringComparison.Ordinal) == true) Interlocked.Increment(ref saves);
        }

        // Counted on the request executor: the store's own event can't be added once it is initialized.
        var executor = _db.Store.GetRequestExecutor();
        executor.OnSucceedRequest += Count;
        try
        {
            var replacement = Png(60, 40);
            Assert.Equal(MapImageProblem.None, await _maps.PutImageAsync(map.Id, new MemoryStream(replacement), gm));
            Assert.Equal(1, saves);
            var image = (await _maps.GetAsync(map.Id, gm))!.Image!;
            Assert.Equal((60, 40, 2), (image.Width, image.Height, image.Version));
            Assert.Equal(replacement, (await _maps.GetImageAsync(map.Id, gm))!.Bytes);

            saves = 0;
            var copy = (await _maps.CopyAsNewVersionAsync(map.Id, "Winter", gm))!;
            Assert.Equal(1, saves);
            Assert.Equal(replacement, (await _maps.GetImageAsync(copy.Id, gm))!.Bytes);
        }
        finally
        {
            executor.OnSucceedRequest -= Count;
        }
    }

    [Fact]
    public async Task ACopy_OverTheOwnersStorageLimit_IsRefused_AndNothingIsMade()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        using (var session = _db.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new UserDocument { Id = "gm", StorageLimitMb = 1 });
            await session.SaveChangesAsync();
        }

        var maps = new RavenMapRepository(_db, new StorageQuota(_db, new RavenServerSettingsStore(_db)));
        var map = (await maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        // 600 KB fits once in 1 MB, not twice.
        Assert.Equal(MapImageProblem.None, await maps.PutImageAsync(map.Id, new MemoryStream(Png(10, 10, extraBytes: 600 * 1024)), gm));
        await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() => maps.CopyAsNewVersionAsync(map.Id, "Winter", gm));
        Assert.Single(await maps.ListVersionsAsync(w.Region.Id, w.Town.Id, gm));
    }

    [Fact]
    public async Task Renaming_LeavesTheGridAlone_AndSettingTheGrid_LeavesTheName()
    {
        var w = await WorldAsync();
        var gm = Gm(w.CampaignId);
        var map = (await _maps.CreateAsync(w.Region.Id, w.Town.Id, null, null, gm))!;
        Assert.True(await _maps.SetGridAsync(map.Id, new MapGrid { Kind = MapGridKind.Square, CellSize = 25 }, gm));
        Assert.True(await _maps.RenameAsync(map.Id, "Brindle town", "Summer", gm));
        Assert.False(await _maps.RenameAsync(map.Id, "  ", null, gm));

        var stored = (await _maps.GetAsync(map.Id, gm))!;
        Assert.Equal(("Brindle town", "Summer"), (stored.Name, stored.Label));
        Assert.Equal((MapGridKind.Square, 25.0), (stored.Grid.Kind, stored.Grid.CellSize));
    }

    // A PNG's structure, no real pixels: the server reads headers and never decodes, so this is all it needs to see.
    private static byte[] Png(int width, int height, int extraBytes = 0)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BigEndian(header, 0, width);
        BigEndian(header, 4, height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", new byte[8 + extraBytes]);
        Chunk(output, "IEND", []);
        return output.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            var length = new byte[4];
            BigEndian(length, 0, data.Length);
            s.Write(length);
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            s.Write(typed);
            var crc = new byte[4];
            BigEndian(crc, 0, unchecked((int)Crc32(typed)));
            s.Write(crc);
        }

        static void BigEndian(byte[] b, int at, int value)
        {
            b[at] = (byte)(value >> 24);
            b[at + 1] = (byte)(value >> 16);
            b[at + 2] = (byte)(value >> 8);
            b[at + 3] = (byte)value;
        }

        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            return ~crc;
        }
    }
}
