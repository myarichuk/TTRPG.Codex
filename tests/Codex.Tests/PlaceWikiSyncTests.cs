using Codex.Core.AI;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;

namespace Codex.Tests;

/// <summary>A place on the map and its wiki page: created together, linked both ways, sharing a name, and the page revealing the place.</summary>
[Collection("RavenDb")]
public class PlaceWikiSyncTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private static readonly CampaignAccess Dm = new("camp", "dm", CampaignRole.DM);

    private readonly RavenDbService _db;
    private readonly RegionRepository _regions;
    private readonly RavenWikiRepository _wiki;
    private readonly PlaceWikiSync _sync;

    public PlaceWikiSyncTests(RavenDbFixture fixture)
    {
        _db = new RavenDbService(fixture.DbPath, "PlaceWiki_" + Guid.NewGuid(), runInMemory: true);
        _regions = new RegionRepository(_db);
        _wiki = new RavenWikiRepository(_db);
        // Art is only touched when a picture comes with the page, which these tests don't do.
        _sync = new PlaceWikiSync(_regions, _wiki, new ArtService(null!, null!, null!, _regions, null!, _wiki));
    }

    public void Dispose() => _db.Dispose();

    private async Task<(RegionDocument Region, LocationDocument Place)> SeedPlaceAsync(ActorVisibility visibility, string? description = "Salt and rope.")
    {
        var place = new LocationDocument { Id = Guid.NewGuid().ToString(), Name = "Greyharbour", Description = description, Visibility = visibility };
        var region = new RegionDocument { Id = Guid.NewGuid().ToString(), CampaignId = Dm.CampaignId, Name = "Coast", Locations = { place } };
        Assert.True(await _regions.SaveAsync(region, Dm));
        return (region, place);
    }

    [Fact]
    public async Task CreatePage_ForAHiddenPlace_WritesADmOnlyPageLinkedBothWays()
    {
        var (region, place) = await SeedPlaceAsync(ActorVisibility.Hidden);

        var page = await _sync.CreatePageAsync(region.Id, place, "place", Dm);

        Assert.NotNull(page);
        Assert.Equal("Greyharbour", page!.Title);
        Assert.Equal("Salt and rope.", page.Body);
        Assert.Equal(WikiVisibility.DmOnly, page.Visibility);
        Assert.Equal(place.Id, page.PlaceId);
        Assert.Equal(new[] { "place" }, page.Tags);
        Assert.Equal(page.Id, place.WikiPageId);

        var stored = (await _regions.GetAsync(region.Id, Dm))!.Locations.Single();
        Assert.Equal(page.Id, stored.WikiPageId);
        Assert.Equal(place.Id, (await _wiki.GetVisibleBySlugAsync(page.Slug, Dm))!.PlaceId);
    }

    [Fact]
    public async Task CreatePage_ForAKnownPlace_IsSharedWithTheParty()
    {
        var (region, place) = await SeedPlaceAsync(ActorVisibility.Known);

        var page = await _sync.CreatePageAsync(region.Id, place, "place", Dm);

        Assert.Equal(WikiVisibility.Party, page!.Visibility);
    }

    [Fact]
    public async Task CreatePage_IsNotWrittenForAPlayer()
    {
        var (region, place) = await SeedPlaceAsync(ActorVisibility.Known);

        var page = await _sync.CreatePageAsync(region.Id, place, "place", new CampaignAccess(Dm.CampaignId, "player", CampaignRole.Player));

        Assert.Null(page);
        Assert.Empty(await _wiki.GetAllForCampaignAsync(Dm.CampaignId));
    }

    [Fact]
    public async Task RenamePage_FollowsThePlace()
    {
        var (region, place) = await SeedPlaceAsync(ActorVisibility.Hidden);
        var page = (await _sync.CreatePageAsync(region.Id, place, "place", Dm))!;

        place.Name = "New Greyharbour";
        await _sync.RenamePageAsync(place, Dm);

        Assert.Equal("New Greyharbour", (await _wiki.GetVisibleBySlugAsync(page.Slug, Dm))!.Title);
    }

    [Fact]
    public async Task CreatePlace_FromAPage_MakesARegionWhenThereIsNone_AndKeepsDmOnlyTextOffTheMap()
    {
        var page = new WikiPageDocument { Title = "The Salt Court", Body = "The queen is a drowned thief.", Visibility = WikiVisibility.DmOnly };
        Assert.True(await _wiki.TrySaveAsync(page, Dm));

        var made = await _sync.CreatePlaceAsync(page, regionId: null, "Places", image: null, Dm);

        Assert.NotNull(made);
        Assert.Equal("Places", made!.Value.Region.Name);
        Assert.Equal("The Salt Court", made.Value.Place.Name);
        Assert.Null(made.Value.Place.Description); // players read a place's description once it is revealed
        Assert.Equal(ActorVisibility.Hidden, made.Value.Place.Visibility);
        Assert.Equal(page.Id, made.Value.Place.WikiPageId);
        Assert.Equal(made.Value.Place.Id, (await _wiki.GetVisibleBySlugAsync(page.Slug, Dm))!.PlaceId);
    }

    [Fact]
    public async Task CreatePlace_FromASharedPage_IsKnownWithTheText()
    {
        var (region, _) = await SeedPlaceAsync(ActorVisibility.Hidden);
        var page = new WikiPageDocument { Title = "The Lantern Quay", Body = "Lit all night.", Visibility = WikiVisibility.Party };
        Assert.True(await _wiki.TrySaveAsync(page, Dm));

        var made = await _sync.CreatePlaceAsync(page, region.Id, "Places", image: null, Dm);

        Assert.Equal(region.Id, made!.Value.Region.Id);
        Assert.Equal(ActorVisibility.Known, made.Value.Place.Visibility);
        Assert.Equal("Lit all night.", made.Value.Place.Description);
    }

    [Fact]
    public async Task SharingThePage_RevealsThePlace_AndRenamesIt()
    {
        var (region, place) = await SeedPlaceAsync(ActorVisibility.Hidden);
        var page = (await _sync.CreatePageAsync(region.Id, place, "place", Dm))!;

        page.Title = "Greyharbour Docks";
        page.Visibility = WikiVisibility.Party;
        Assert.True(await _wiki.TrySaveAsync(page, Dm));
        await _sync.UpdatePlaceAsync(page, Dm);

        var stored = (await _regions.GetAsync(region.Id, Dm))!.Locations.Single();
        Assert.Equal(ActorVisibility.Known, stored.Visibility);
        Assert.Equal("Greyharbour Docks", stored.Name);
        Assert.Equal(page.Id, stored.WikiPageId);
    }

    [Fact]
    public async Task SavingAPage_KeepsItsPlaceLink()
    {
        var (region, place) = await SeedPlaceAsync(ActorVisibility.Hidden);
        var page = (await _sync.CreatePageAsync(region.Id, place, "place", Dm))!;

        var edited = new WikiPageDocument { Id = page.Id, Title = page.Title, Body = "More.", Visibility = page.Visibility, PlaceId = page.PlaceId };
        Assert.True(await _wiki.TrySaveAsync(edited, Dm));

        Assert.Equal(place.Id, (await _wiki.GetVisibleBySlugAsync(page.Slug, Dm))!.PlaceId);
    }

    [Fact]
    public async Task FindPlace_IsNullForAPlaceThePlayerCannotSee()
    {
        var (_, place) = await SeedPlaceAsync(ActorVisibility.Hidden);

        Assert.NotNull(await _sync.FindPlaceAsync(place.Id, Dm));
        Assert.Null(await _sync.FindPlaceAsync(place.Id, new CampaignAccess(Dm.CampaignId, "player", CampaignRole.Player)));
        Assert.Null(await _sync.FindPlaceAsync(null, Dm));
    }

    [Fact]
    public async Task CreatePlace_GoesInsideTheChosenPlace_AndMarksThePageAPlace()
    {
        var (region, town) = await SeedPlaceAsync(ActorVisibility.Hidden);
        var page = new WikiPageDocument { Title = "The Salted Eel", Body = "A tavern." };
        Assert.True(await _wiki.TrySaveAsync(page, Dm));

        var made = await _sync.CreatePlaceAsync(page, region.Id, "Places", null, Dm, new PlacePlacement(town.Id, PlaceKind.Site));

        Assert.Equal((town.Id, PlaceKind.Site, WikiPageKind.Place), (made!.Value.Place.ParentId, made.Value.Place.Kind, page.Kind));
        var stored = (await _regions.GetAsync(region.Id, Dm))!.Locations.Single(l => l.Id == made.Value.Place.Id);
        Assert.Equal(town.Id, stored.ParentId);
    }

    [Fact]
    public async Task UpdatePlace_MovesIt_ButNeverInsideItself()
    {
        var (region, town) = await SeedPlaceAsync(ActorVisibility.Hidden);
        var page = new WikiPageDocument { Title = "Dock Ward" };
        Assert.True(await _wiki.TrySaveAsync(page, Dm));
        var ward = (await _sync.CreatePlaceAsync(page, region.Id, "Places", null, Dm, new PlacePlacement(town.Id, PlaceKind.District)))!.Value.Place;

        // The town can't go inside its own district...
        var townPage = new WikiPageDocument { Title = town.Name, PlaceId = town.Id };
        await _sync.UpdatePlaceAsync(townPage, Dm, new PlacePlacement(ward.Id, PlaceKind.Settlement));
        var stored = (await _regions.GetAsync(region.Id, Dm))!.Locations;
        Assert.Null(stored.Single(l => l.Id == town.Id).ParentId);
        Assert.Equal(PlaceKind.Settlement, stored.Single(l => l.Id == town.Id).Kind);

        // ...but the district can move out to the region.
        await _sync.UpdatePlaceAsync(page, Dm, new PlacePlacement(null, PlaceKind.Area));
        Assert.Null((await _regions.GetAsync(region.Id, Dm))!.Locations.Single(l => l.Id == ward.Id).ParentId);
    }
}

public class PlaceTreeTests
{
    private static LocationDocument Place(string id, string name, string? parent = null) => new() { Id = id, Name = name, ParentId = parent };

    [Fact]
    public void Ordered_PutsChildrenUnderTheirParent_ByName_AndLiftsOrphans()
    {
        var places = new[]
        {
            Place("eel", "Salted Eel", "dock"), Place("town", "Greyharbour"), Place("dock", "Dock Ward", "town"),
            Place("abbey", "Abbey", "town"), Place("lost", "Lost Cove", "gone")
        };

        var ordered = PlaceTree.Ordered(places).Select(p => (p.Place.Id, p.Depth)).ToList();

        Assert.Equal(new[] { ("town", 0), ("abbey", 1), ("dock", 1), ("eel", 2), ("lost", 0) }, ordered);
    }

    [Fact]
    public void Ordered_KeepsAParentLoop_InsteadOfLosingIt()
    {
        var places = new[] { Place("a", "A", "b"), Place("b", "B", "a") };

        Assert.Equal(2, PlaceTree.Ordered(places).Count);
    }

    [Fact]
    public void CanParent_RefusesItselfAndWhatIsInsideIt()
    {
        var places = new[] { Place("town", "Town"), Place("dock", "Dock", "town"), Place("eel", "Eel", "dock") };

        Assert.False(PlaceTree.CanParent(places, "town", "town"));
        Assert.False(PlaceTree.CanParent(places, "town", "eel"));
        Assert.True(PlaceTree.CanParent(places, "eel", "town"));
        Assert.True(PlaceTree.CanParent(places, "town", null));
        Assert.Equal(new[] { "town", "dock" }, PlaceTree.Ancestors(places, places[2]).Select(p => p.Id));
    }
}

public class StoredEquipmentTests
{
    private static ContentEntry Item(string id, params (string Key, object Value)[] properties)
    {
        var entry = new ContentEntry { Id = id, Type = "equipment", SystemId = "demo", PackId = "base", Name = id };
        foreach (var (key, value) in properties) entry.Properties[key] = value;
        return entry;
    }

    [Fact]
    public void OnlyOneSuitOfArmorAndOneShieldAreWorn_TheRestAreCarried()
    {
        var entries = new[]
        {
            Item("sword"), Item("plate", ("armorBase", 18L)), Item("mail", ("armorBase", 16L)),
            Item("buckler", ("armorType", "shield")), Item("tower", ("armorType", "shield"))
        }.ToDictionary(e => e.Id);
        var items = new[]
        {
            new DraftItem("sword", 1, true), new DraftItem("plate", 1, true), new DraftItem("mail", 1, true),
            new DraftItem("buckler", 1, true), new DraftItem("tower", 1, true), new DraftItem("ghost", 1, true)
        };

        var stored = Codex.Web.Services.AiFeatures.StoredEquipment(items, entries, new InventoryRules(), new ArmorClassRules()).Cast<string>().ToList();

        Assert.Equal(5, stored.Count); // the unknown id is dropped
        Assert.Contains("equipped:1", stored[0]);
        Assert.Contains("equipped:1", stored[1]);
        Assert.DoesNotContain("equipped", stored[2]);
        Assert.Contains("equipped:1", stored[3]);
        Assert.DoesNotContain("equipped", stored[4]);
    }

    [Fact]
    public void QuantityIsStored()
    {
        var entries = new[] { Item("rope") }.ToDictionary(e => e.Id);

        var stored = Codex.Web.Services.AiFeatures.StoredEquipment(new[] { new DraftItem("rope", 3, false) }, entries, new InventoryRules(), null);

        Assert.Equal(new object[] { "rope|qty:3" }, stored);
    }
}
