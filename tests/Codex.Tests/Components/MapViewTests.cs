using Bunit;
using Codex.Persistence;
using Codex.Web.Components.Campaign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>
/// The map page (MAPS_DESIGN.md 9.5, 9.6): a player is never drawn a place they don't know, the GM places a pin or an area by keyboard at
/// the pixel the cross-hair shows, hidden places are told apart by more than colour, and the version tools are the GM's alone.
/// </summary>
public class MapViewTests : ComponentTestBase
{
    private const string Campaign = "c1";
    private const string RegionId = "regions/1";

    private readonly IMapRepository maps = Substitute.For<IMapRepository>();
    private readonly IRegionRepository regions = Substitute.For<IRegionRepository>();

    public MapViewTests()
    {
        var wiki = Substitute.For<IWikiRepository>();
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(Array.Empty<WikiPageDocument>().AsEnumerable());
        Services.AddSingleton(maps);
        Services.AddSingleton(regions);
        Services.AddSingleton(wiki);
    }

    private static LocationDocument Mill(ActorVisibility visibility = ActorVisibility.Known) =>
        new() { Id = "mill", Name = "Old Mill", Visibility = visibility, Description = "A mill by the river." };

    private static LocationDocument Tower(ActorVisibility visibility = ActorVisibility.Hidden) =>
        new() { Id = "tower", Name = "Watchtower", Visibility = visibility };

    private static RegionDocument Region(params LocationDocument[] places) =>
        new() { Id = RegionId, CampaignId = Campaign, Name = "Valley", Locations = places.ToList() };

    private static MapDocument Map(string id = "maps/1", MapGrid? grid = null, params MapMarker[] markers) => new()
    {
        Id = id, CampaignId = Campaign, RegionId = RegionId, Name = "Valley", Current = true, ShownToPlayers = true,
        Image = new MapImage { Width = 1000, Height = 500, ContentType = "image/png", Version = 1 }, Grid = grid ?? new MapGrid(), Markers = markers.ToList()
    };

    private static MapMarker Pin(string locationId, double x, double y) => new() { Id = "m-" + locationId, LocationId = locationId, Kind = MapMarkerKind.Pin, Points = [new MapPoint(x, y)] };

    private IRenderedComponent<CascadingValue<CampaignContext>> Draw(bool gm, MapDocument map, RegionDocument region, params MapDocument[] versions)
    {
        var access = new CampaignAccess(Campaign, gm ? "gm" : "p1", gm ? CampaignRole.DM : CampaignRole.Player);
        maps.GetAsync(map.Id, Arg.Any<CampaignAccess>()).Returns(map);
        maps.ListForRegionAsync(RegionId, Arg.Any<CampaignAccess>()).Returns(versions.Length > 0 ? versions : [map]);
        var ctx = new CampaignContext
        {
            Campaign = new CampaignDocument { Id = Campaign, Name = "Test" }, Access = access, UserId = access.UserId, Actors = Array.Empty<ActorDocument>(),
            AllActors = Array.Empty<ActorDocument>(), ReloadAsync = () => Task.CompletedTask, Notify = _ => { }, AccessChangedAsync = () => Task.CompletedTask
        };
        return Render<CascadingValue<CampaignContext>>(p => p.Add(x => x.Value, ctx)
            .AddChildContent<MapView>(c => c.Add(x => x.MapId, map.Id).Add(x => x.Regions, new[] { region })));
    }

    [Fact]
    public void APlayer_IsDrawnOnlyThePlacesTheyKnow_EvenIfTheMapHeldAnother()
    {
        // The repository already leaves the tower's marker out for a player; the page also draws nothing whose place it wasn't given.
        var map = Map(markers: [Pin("mill", 100, 100), Pin("tower", 300, 200)]);
        var cut = Draw(gm: false, map, Region(Mill()));

        var markers = cut.FindAll(Id("map-marker"));
        Assert.Single(markers);
        Assert.Equal("Old Mill", markers[0].GetAttribute("aria-label"));
        Assert.Single(cut.FindAll(Id("map-on-row")));
        Assert.DoesNotContain("Watchtower", cut.Markup);
        Assert.DoesNotContain("tower", cut.Find(Id("map-markers")).OuterHtml);
        Assert.DoesNotContain("Hidden", cut.Markup);
        Assert.Empty(cut.FindAll(Id("map-off-row")));
    }

    [Fact]
    public void TheGm_SeesAHiddenPlace_DashedAndLabelledHidden()
    {
        var map = Map(markers: [Pin("mill", 100, 100), Pin("tower", 300, 200)]);
        var cut = Draw(gm: true, map, Region(Mill(), Tower()));

        var tower = cut.FindAll(Id("map-marker")).Single(m => m.GetAttribute("data-marker") == "tower");
        Assert.Equal("Watchtower, hidden", tower.GetAttribute("aria-label"));
        Assert.Contains("is-hidden", tower.ClassName);
        Assert.Contains(">Hidden<", tower.InnerHtml);
        var mill = cut.FindAll(Id("map-marker")).Single(m => m.GetAttribute("data-marker") == "mill");
        Assert.Equal("Old Mill", mill.GetAttribute("aria-label"));
        Assert.Equal("Map: Valley, of the region Valley", cut.Find(Id("map-image")).GetAttribute("alt"));
    }

    [Fact]
    public void ThePlayersView_ShowsThePublicDescription_WhenAPlaceIsSelected()
    {
        var cut = Draw(gm: false, Map(markers: [Pin("mill", 100, 100)]), Region(Mill()));

        cut.Find(Id("map-on-row")).QuerySelector("button")!.Click();

        Assert.Contains("A mill by the river.", cut.Find(Id("map-details-description")).TextContent);
    }

    // A key as map-view.js sends it: only while placing, through the JSInvokable, never as a Blazor keydown.
    private static Task Key(IRenderedComponent<CascadingValue<CampaignContext>> cut, string key, bool shift = false) =>
        cut.InvokeAsync(() => cut.FindComponent<MapView>().Instance.PlacingKey(key, shift));

    private static Task Click(IRenderedComponent<CascadingValue<CampaignContext>> cut, double x, double y, double slop = 8) =>
        cut.InvokeAsync(() => cut.FindComponent<MapView>().Instance.MapClicked(x, y, slop));

    [Fact]
    public async Task KeyboardPlacing_DropsAPinWhereTheCrossHairIs()
    {
        var map = Map();
        maps.PlaceMarkerAsync(map.Id, Arg.Any<MapMarker>(), Arg.Any<CampaignAccess>()).Returns(ci => ci.Arg<MapMarker>());
        var cut = Draw(gm: true, map, Region(Tower()));

        cut.Find(Id("map-place")).Click();
        // No grid on a 1000 x 500 picture: a step is 1% of it (10 across, 5 down), Shift ten of them. The cross-hair starts in the middle.
        for (var i = 0; i < 3; i++) await Key(cut, "ArrowRight");
        await Key(cut, "ArrowDown", shift: true);
        Assert.Equal("translate(530 300)", cut.Find(Id("map-cross")).GetAttribute("transform"));
        await Key(cut, "Enter");

        await maps.Received(1).PlaceMarkerAsync(map.Id, Arg.Is<MapMarker>(m => m.LocationId == "tower" && m.Kind == MapMarkerKind.Pin && m.Points.Count == 1
            && m.Points[0] == new MapPoint(530, 300)), Arg.Any<CampaignAccess>());
        Assert.Empty(cut.FindAll(Id("map-cross")));
    }

    [Fact]
    public async Task KeyboardPlacing_AnArea_StepsOneGridCell_AndNeedsThreeCorners()
    {
        var map = Map(grid: new MapGrid { Kind = MapGridKind.Square, CellSize = 40 });
        maps.PlaceMarkerAsync(map.Id, Arg.Any<MapMarker>(), Arg.Any<CampaignAccess>()).Returns(ci => ci.Arg<MapMarker>());
        var cut = Draw(gm: true, map, Region(Tower()));

        cut.Find(Id("map-place")).Click();
        cut.Find(Id("map-placing-area")).Click();
        await Key(cut, " ");
        await Key(cut, "ArrowRight");
        await Key(cut, " ");
        await Key(cut, "Enter");
        // Two corners are not an area: nothing is saved, and the page says why.
        await maps.DidNotReceive().PlaceMarkerAsync(Arg.Any<string>(), Arg.Any<MapMarker>(), Arg.Any<CampaignAccess>());
        Assert.Contains("at least 3 corners", cut.Find(Id("map-status")).TextContent);
        await Key(cut, "ArrowDown");
        await Key(cut, " ");
        await Key(cut, "Enter");

        await maps.Received(1).PlaceMarkerAsync(map.Id, Arg.Is<MapMarker>(m => m.Kind == MapMarkerKind.Area
            && m.Points.SequenceEqual(new[] { new MapPoint(500, 250), new MapPoint(540, 250), new MapPoint(540, 290) })), Arg.Any<CampaignAccess>());
    }

    [Fact]
    public async Task EscapeStopsPlacing_WithoutSavingAnything()
    {
        var map = Map();
        var cut = Draw(gm: true, map, Region(Tower()));

        cut.Find(Id("map-place")).Click();
        await Key(cut, "Escape");

        Assert.Empty(cut.FindAll(Id("map-cross")));
        Assert.DoesNotContain(maps.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IMapRepository.PlaceMarkerAsync));
    }

    [Fact]
    public async Task Keys_NeverReachTheServer_AsBlazorEvents_AndDoNothingWhenNotPlacing()
    {
        var map = Map();
        var cut = Draw(gm: true, map, Region(Tower()));

        // The viewport has no server keydown handler: map-view.js sends placing keys only while placing.
        Assert.Throws<Bunit.MissingEventHandlerException>(() => cut.Find(Id("map-viewport")).KeyDown(new KeyboardEventArgs { Key = "ArrowRight" }));
        await Key(cut, "Enter");
        Assert.Empty(cut.FindAll(Id("map-cross")));
        Assert.DoesNotContain(maps.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IMapRepository.PlaceMarkerAsync));
    }

    [Fact]
    public async Task ClickingTheFirstCorner_FinishesAnArea_AndASecondClickOnACornerAddsNone()
    {
        var map = Map();
        maps.PlaceMarkerAsync(map.Id, Arg.Any<MapMarker>(), Arg.Any<CampaignAccess>()).Returns(ci => ci.Arg<MapMarker>());
        var cut = Draw(gm: true, map, Region(Tower()));

        cut.Find(Id("map-place")).Click();
        cut.Find(Id("map-placing-area")).Click();
        await Click(cut, 100, 100);
        await Click(cut, 300, 100);
        await Click(cut, 302, 101); // a double-click's second click
        await Click(cut, 300, 300);
        Assert.Contains("3 corners", cut.Find(Id("map-status")).TextContent);
        await Click(cut, 104, 97);

        await maps.Received(1).PlaceMarkerAsync(map.Id, Arg.Is<MapMarker>(m => m.Kind == MapMarkerKind.Area
            && m.Points.SequenceEqual(new[] { new MapPoint(100, 100), new MapPoint(300, 100), new MapPoint(300, 300) })), Arg.Any<CampaignAccess>());
        Assert.Empty(cut.FindAll(Id("map-cross")));
    }

    [Fact]
    public async Task ADoubleClick_FinishesAnArea()
    {
        var map = Map();
        maps.PlaceMarkerAsync(map.Id, Arg.Any<MapMarker>(), Arg.Any<CampaignAccess>()).Returns(ci => ci.Arg<MapMarker>());
        var cut = Draw(gm: true, map, Region(Tower()));

        cut.Find(Id("map-place")).Click();
        cut.Find(Id("map-placing-area")).Click();
        await Click(cut, 100, 100);
        await Click(cut, 300, 100);
        await Click(cut, 300, 300);
        await Click(cut, 300, 300);
        await cut.InvokeAsync(() => cut.FindComponent<MapView>().Instance.FinishAreaByDoubleClick());

        await maps.Received(1).PlaceMarkerAsync(map.Id, Arg.Is<MapMarker>(m => m.Kind == MapMarkerKind.Area && m.Points.Count == 3), Arg.Any<CampaignAccess>());
    }

    [Fact]
    public void SelectingAPlace_SaysSoInTheLiveRegion_AndTheRowSaysItControlsTheDetails()
    {
        var cut = Draw(gm: true, Map(markers: [Pin("tower", 100, 100)]), Region(Tower()));

        var row = cut.Find(Id("map-on-row")).QuerySelector("button")!;
        Assert.Equal("false", row.GetAttribute("aria-expanded"));
        Assert.Equal("map-details", row.GetAttribute("aria-controls"));
        Assert.Equal("map-row-name-tower", row.Id);
        row.Click();

        Assert.Equal("true", cut.Find("#map-row-name-tower").GetAttribute("aria-expanded"));
        Assert.NotNull(cut.Find("#map-details"));
        Assert.Equal("Watchtower selected. It's hidden from players.", cut.Find(Id("map-status")).TextContent);
    }

    [Fact]
    public void TheViewport_HasItsOwnShortName_AndThePictureKeepsTheMapsName()
    {
        var cut = Draw(gm: false, Map(), Region(Mill()));

        Assert.Equal("Map view", cut.Find(Id("map-viewport")).GetAttribute("aria-label"));
        Assert.Equal("Map: Valley, of the region Valley", cut.Find(Id("map-image")).GetAttribute("alt"));
    }

    [Fact]
    public void APlaceWithOneMap_SaysMap_NotVersion()
    {
        var map = Map();
        map.ShownToPlayers = false;
        var cut = Draw(gm: true, map, Region(Mill()));

        Assert.Equal("This map", cut.Find("#map-version-tools-heading").TextContent);
        Assert.Contains("Delete map", cut.Find(Id("map-delete")).TextContent);
        Assert.Equal("This map is hidden from players. Once you show it, they see the whole picture.", cut.Find(Id("map-subtitle")).TextContent);
        Assert.DoesNotContain("current", cut.Find(Id("map-version-tools")).TextContent);
        Assert.Empty(cut.FindAll(Id("map-versions")));
    }

    [Fact]
    public void FeatureNotes_AreNeverShown_ToAPlayer_OrInThePartyPreview()
    {
        var mill = Mill();
        mill.Feature = new PlaceFeature { Trap = "Poison needle in the lock" };
        var region = Region(mill);
        var map = Map(markers: [Pin("mill", 100, 100)]);
        regions.GetAsync(RegionId, Arg.Any<CampaignAccess>()).Returns(region);
        RenderFragment<LocationDocument> notes = place => b => b.AddMarkupContent(0, $"<p data-testid=\"feature-notes\">{place.Feature!.Trap}</p>");

        IRenderedComponent<CascadingValue<CampaignContext>> DrawWithNotes(bool gm)
        {
            var access = new CampaignAccess(Campaign, gm ? "gm" : "p1", gm ? CampaignRole.DM : CampaignRole.Player);
            maps.GetAsync(map.Id, Arg.Any<CampaignAccess>()).Returns(map);
            maps.ListForRegionAsync(RegionId, Arg.Any<CampaignAccess>()).Returns([map]);
            var ctx = new CampaignContext
            {
                Campaign = new CampaignDocument { Id = Campaign, Name = "Test" }, Access = access, UserId = access.UserId, Actors = Array.Empty<ActorDocument>(),
                AllActors = Array.Empty<ActorDocument>(), ReloadAsync = () => Task.CompletedTask, Notify = _ => { }, AccessChangedAsync = () => Task.CompletedTask
            };
            return Render<CascadingValue<CampaignContext>>(p => p.Add(x => x.Value, ctx)
                .AddChildContent<MapView>(c => c.Add(x => x.MapId, map.Id).Add(x => x.Regions, new[] { region }).Add(x => x.FeatureNotes, notes)));
        }

        // The GM's own view shows them, so the fixture does draw notes.
        var gmView = DrawWithNotes(gm: true);
        gmView.Find(Id("map-on-row")).QuerySelector("button")!.Click();
        Assert.Contains("Poison needle", gmView.Find(Id("feature-notes")).TextContent);

        gmView.Find(Id("map-view-as-party")).Click();
        // The mill stays selected in the preview: its details are there, its notes are not.
        gmView.WaitForAssertion(() => Assert.NotEmpty(gmView.FindAll(Id("map-details"))));
        Assert.DoesNotContain("Poison needle", gmView.Markup);

        var playerView = DrawWithNotes(gm: false);
        playerView.Find(Id("map-on-row")).QuerySelector("button")!.Click();
        Assert.NotNull(playerView.Find(Id("map-details")));
        Assert.DoesNotContain("Poison needle", playerView.Markup);
    }

    [Fact]
    public void TheVersionTools_AreTheGmsAlone()
    {
        var summer = Map("maps/1");
        var winter = Map("maps/2");
        winter.Current = false;
        winter.Label = "Winter";
        winter.CreatedAt = summer.CreatedAt.AddMinutes(1);

        var gmView = Draw(gm: true, summer, Region(Mill()), summer, winter);
        Assert.NotNull(gmView.Find(Id("map-version-tools")));
        Assert.NotNull(gmView.Find(Id("map-copy")));
        Assert.NotNull(gmView.Find(Id("map-toggle-shown")));
        Assert.NotNull(gmView.Find(Id("map-delete")));
        Assert.NotNull(gmView.Find(Id("map-view-as")));
        Assert.Equal(2, gmView.FindAll(Id("map-version")).Count);

        // A player is handed only the versions shown to them, and gets no tools.
        var playerView = Draw(gm: false, summer, Region(Mill()), summer);
        Assert.Empty(playerView.FindAll(Id("map-version-tools")));
        Assert.Empty(playerView.FindAll(Id("map-copy")));
        Assert.Empty(playerView.FindAll(Id("map-delete")));
        Assert.Empty(playerView.FindAll(Id("map-view-as")));
        Assert.Empty(playerView.FindAll(Id("map-file")));
        Assert.Empty(playerView.FindAll(Id("map-versions")));
    }

    [Fact]
    public void AHiddenVersion_IsMarkedForTheGm()
    {
        var summer = Map("maps/1");
        var winter = Map("maps/2");
        winter.Current = false;
        winter.ShownToPlayers = false;
        winter.Label = "Winter";
        winter.CreatedAt = summer.CreatedAt.AddMinutes(1);

        var cut = Draw(gm: true, summer, Region(Mill()), summer, winter);

        var listed = cut.FindAll(Id("map-version"));
        Assert.Contains("current", listed[0].TextContent);
        Assert.Contains("Winter", listed[1].TextContent);
        Assert.Contains("hidden from players", listed[1].TextContent);
    }
}
