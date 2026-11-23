using Microsoft.Playwright;
using SkiaSharp;

namespace Codex.Tests.E2E;

/// <summary>
/// A map of a region (MAPS_DESIGN.md 9.5, 9.9): the GM adds it, uploads its picture, shows it to players (a new map starts hidden),
/// places a pin with the mouse and an area with the keyboard, and reveals the pin's place. A player opening the map sees the revealed
/// place alone: the hidden one's area never reaches their page, and the picture of a hidden map, or of a hidden place's map, is a 404.
/// </summary>
[Collection("RavenDb")]
public class MapPagesE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public MapPagesE2ETests(AppFixture app) => _app = app;

    /// <summary>A small picture, made here: 400 x 300 pixels of green with a darker square, as a PNG.</summary>
    private static byte[] Picture()
    {
        using var bitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(90, 140, 80));
            using var paint = new SKPaint { Color = new SKColor(40, 70, 40) };
            canvas.DrawRect(150, 100, 100, 100, paint);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // The picture streamed whole from its attachment: a PNG, all of it, after the database session it came from was handed to the response.
    private static async Task AssertPngAsync(IAPIResponse response)
    {
        Assert.Equal(200, response.Status);
        Assert.Equal("image/png", response.Headers["content-type"]);
        var body = await response.BodyAsync();
        Assert.True(body.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, body[..4]);
    }

    [Fact]
    public async Task TheGmPlacesAPinAndAnArea_AndAPlayerSeesOnlyTheRevealedOne()
    {
        const string password = "Correct-Horse-1";
        var dmId = "map-dm-" + Guid.NewGuid();
        var playerId = "map-p1-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(playerId, "p_" + playerId, password, "Player");

        var options = new BrowserNewContextOptions { BaseURL = _app.BaseUrl, ViewportSize = new() { Width = 1280, Height = 900 } };
        await using var dmContext = await _app.Browser.NewContextAsync(options);
        await using var playerContext = await _app.Browser.NewContextAsync(options);
        var dm = await dmContext.NewPageAsync();
        var player = await playerContext.NewPageAsync();
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        await E2EHelpers.LoginAsync(player, "p_" + playerId, password);

        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Map campaign");
        await E2EHelpers.JoinAsync(player, await E2EHelpers.ReadInviteLinkAsync(dm, campaignId));

        // A region with two places, both hidden to begin with.
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "world");
        await dm.GetByTestId("new-region-name").FillAsync("Valley");
        await dm.GetByTestId("new-region-add").ClickAsync();
        var newPlace = dm.GetByPlaceholder("New place…");
        await newPlace.FillAsync("Old Mill");
        await newPlace.PressAsync("Enter");
        await Assertions.Expect(dm.GetByTestId("location-row")).ToHaveCountAsync(1);
        await newPlace.FillAsync("Watchtower");
        await newPlace.PressAsync("Enter");
        await Assertions.Expect(dm.GetByTestId("location-row")).ToHaveCountAsync(2);

        // Add map opens the new map, which asks for its picture.
        await dm.GetByTestId("add-region-map").ClickAsync();
        await dm.WaitForURLAsync(url => url.Contains("/world/map/"));
        var mapUrl = new Uri(dm.Url).PathAndQuery;
        await Assertions.Expect(dm.GetByTestId("map-no-picture")).ToBeVisibleAsync();
        await dm.GetByTestId("map-file").SetInputFilesAsync(new FilePayload { Name = "valley.png", MimeType = "image/png", Buffer = Picture() });
        await Assertions.Expect(dm.GetByTestId("map-image")).ToBeVisibleAsync();
        // The zoom script has taken the picture once it has moved it into view.
        await Assertions.Expect(dm.Locator(".map-stage")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("transform"));
        Assert.Empty(await E2EHelpers.UnlabelledControlsAsync(dm));
        // A new map starts hidden: uploading the GM's picture publishes nothing, and the picture's URL is a 404 for the player.
        await Assertions.Expect(dm.GetByTestId("map-subtitle")).ToHaveTextAsync("This map is hidden from players. Once you show it, they see the whole picture.");
        var imageUrl = (await dm.GetByTestId("map-image").GetAttributeAsync("src"))!;
        Assert.Equal(404, (await player.APIRequest.GetAsync(imageUrl)).Status);
        await dm.GetByTestId("map-toggle-shown").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("map-subtitle")).ToHaveTextAsync("Players will see this map once they know a place in Valley.");
        // Shown, but no place of the region is known yet: still nothing for the player.
        Assert.Equal(404, (await player.APIRequest.GetAsync(imageUrl)).Status);

        // The mill by mouse: choose it, then click the middle of the picture.
        await dm.GetByTestId("map-off-row").Filter(new() { HasText = "Old Mill" }).GetByTestId("map-place").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("map-placing")).ToBeVisibleAsync();
        await dm.GetByTestId("map-viewport").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("map-marker")).ToHaveCountAsync(1);

        // The tower as an area, by keyboard alone: Space at each corner, Enter to finish.
        await dm.GetByTestId("map-off-row").Filter(new() { HasText = "Watchtower" }).GetByTestId("map-place").ClickAsync();
        await dm.GetByTestId("map-placing-area").ClickAsync();
        var viewport = dm.GetByTestId("map-viewport");
        await viewport.FocusAsync();
        // While placing, the arrows move the cross-hair and leave the picture where it is.
        var stageBefore = await dm.Locator(".map-stage").GetAttributeAsync("style");
        await viewport.PressAsync("Shift+ArrowLeft");
        await viewport.PressAsync("Space");
        await viewport.PressAsync("Shift+ArrowRight");
        await viewport.PressAsync("Shift+ArrowRight");
        await viewport.PressAsync("Space");
        await viewport.PressAsync("Shift+ArrowDown");
        await viewport.PressAsync("Space");
        Assert.Equal(stageBefore, await dm.Locator(".map-stage").GetAttributeAsync("style"));
        await viewport.PressAsync("Enter");
        await Assertions.Expect(dm.GetByTestId("map-marker")).ToHaveCountAsync(2);
        await Assertions.Expect(dm.Locator(".map-area[data-marker]")).ToHaveAttributeAsync("aria-label", "Watchtower, hidden");
        await Assertions.Expect(dm.GetByTestId("map-on-row")).ToHaveCountAsync(2);
        // The placing panel went with Enter: focus is on the place's row.
        var towerName = dm.GetByTestId("map-on-row").Filter(new() { HasText = "Watchtower" }).Locator(".map-row-name");
        var millName = dm.GetByTestId("map-on-row").Filter(new() { HasText = "Old Mill" }).Locator(".map-row-name");
        await Assertions.Expect(towerName).ToBeFocusedAsync();

        // Escape stops placing from the side panel too, and focus goes back to the row.
        await dm.GetByTestId("map-on-row").Filter(new() { HasText = "Old Mill" }).GetByTestId("map-move").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("map-placing")).ToBeVisibleAsync();
        await dm.GetByTestId("map-placing-pin").FocusAsync();
        await dm.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dm.GetByTestId("map-placing")).ToHaveCountAsync(0);
        await Assertions.Expect(millName).ToBeFocusedAsync();

        // An area is finished by a double-click as well: the tower's moved by mouse.
        await dm.GetByTestId("map-on-row").Filter(new() { HasText = "Watchtower" }).GetByTestId("map-move").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("map-placing-finish")).ToBeVisibleAsync();
        var box = (await viewport.BoundingBoxAsync())!;
        await viewport.ClickAsync(new() { Position = new() { X = box.Width / 2 - 60, Y = box.Height / 2 - 40 } });
        await viewport.ClickAsync(new() { Position = new() { X = box.Width / 2 + 60, Y = box.Height / 2 - 40 } });
        await Assertions.Expect(dm.GetByTestId("map-status")).ToHaveTextAsync("2 corners");
        await viewport.DblClickAsync(new() { Position = new() { X = box.Width / 2, Y = box.Height / 2 + 50 } });
        await Assertions.Expect(dm.GetByTestId("map-placing")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.GetByTestId("map-marker")).ToHaveCountAsync(2);

        // Reveal the mill from the map's own panel; its row keeps the focus.
        await dm.GetByTestId("map-on-row").Filter(new() { HasText = "Old Mill" }).GetByTestId("map-reveal").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("map-on-row").Filter(new() { HasText = "Old Mill" }).GetByTestId("map-reveal")).ToHaveCountAsync(0);
        await Assertions.Expect(millName).ToBeFocusedAsync();
        await Assertions.Expect(dm.Locator(".map-pin[data-marker]")).ToHaveAttributeAsync("aria-label", "Old Mill");
        await Assertions.Expect(dm.GetByTestId("map-subtitle")).ToHaveTextAsync("Players see the whole picture, and the places they know on it.");
        await AssertPngAsync(await player.APIRequest.GetAsync(imageUrl));

        // The GM's own preview shows what players get: the mill alone.
        await dm.GetByTestId("map-view-as-party").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("map-marker")).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("map-markers")).Not.ToContainTextAsync("Watchtower");

        // The player finds the map from the Places page and sees only the mill.
        await E2EHelpers.GoToSectionAsync(player, campaignId, "world");
        await player.GetByTestId("open-region-map").ClickAsync();
        await player.WaitForURLAsync(url => url.Contains("/world/map/"));
        Assert.EndsWith(mapUrl, new Uri(player.Url).PathAndQuery);
        await Assertions.Expect(player.GetByTestId("map-image")).ToBeVisibleAsync();
        await Assertions.Expect(player.GetByTestId("map-marker")).ToHaveCountAsync(1);
        await Assertions.Expect(player.Locator(".map-pin[data-marker]")).ToHaveAttributeAsync("aria-label", "Old Mill");
        await Assertions.Expect(player.GetByTestId("map-on-row")).ToHaveCountAsync(1);
        Assert.DoesNotContain("Watchtower", await player.ContentAsync());
        // Clicking the pin selects the place and shows what the party knows of it.
        await player.Locator(".map-pin[data-marker]").ClickAsync(new() { Force = true });
        await Assertions.Expect(player.GetByTestId("map-details")).ToContainTextAsync("Old Mill");
        await Assertions.Expect(player.GetByTestId("map-version-tools")).ToHaveCountAsync(0);
        await Assertions.Expect(player.GetByTestId("map-file")).ToHaveCountAsync(0);

        // Versions: the GM copies the map as "Winter", which starts hidden from players, makes it current, then deletes it again.
        await dm.GetByTestId("map-view-as").GetByRole(AriaRole.Button).First.ClickAsync();
        await dm.GetByTestId("map-copy").ClickAsync();
        await dm.GetByTestId("map-copy-label").FillAsync("Winter");
        await dm.GetByTestId("map-copy-save").ClickAsync();
        await dm.WaitForURLAsync(url => !url.EndsWith(mapUrl));
        await Assertions.Expect(dm.GetByTestId("map-version")).ToHaveCountAsync(2);
        var winter = dm.GetByTestId("map-version").Filter(new() { HasText = "Winter" });
        await Assertions.Expect(winter).ToContainTextAsync("hidden from players");
        await Assertions.Expect(winter).ToHaveAttributeAsync("aria-current", "page");
        // The copy kept the places on the picture.
        await Assertions.Expect(dm.GetByTestId("map-marker")).ToHaveCountAsync(2);
        await dm.GetByTestId("map-make-current").ClickAsync();
        await Assertions.Expect(winter).ToContainTextAsync("current");
        await Assertions.Expect(dm.GetByTestId("map-make-current")).ToHaveCountAsync(0);
        await dm.GetByTestId("map-delete").ClickAsync();
        await dm.GetByTestId("confirm-accept").ClickAsync();
        await dm.WaitForURLAsync(url => url.EndsWith(mapUrl));
        await Assertions.Expect(dm.GetByTestId("map-versions")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.GetByTestId("map-marker")).ToHaveCountAsync(2);

        // Edit opens the usual place editor over the map.
        await dm.GetByTestId("map-on-row").Filter(new() { HasText = "Old Mill" }).GetByTestId("map-edit").ClickAsync();
        await Assertions.Expect(dm.Locator("#edit-location-dialog")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("location-name")).ToHaveValueAsync("Old Mill");
        await dm.Locator("#edit-location-dialog").GetByRole(AriaRole.Button, new() { Name = "Back" }).ClickAsync();
        await Assertions.Expect(dm.Locator("#edit-location-dialog")).ToBeHiddenAsync();

        // A map of a place the party hasn't found, with its picture and shown to players, is still a 404 for the player.
        await dm.GetByTestId("map-on-row").Filter(new() { HasText = "Watchtower" }).Locator(".map-row-name").ClickAsync();
        await dm.GetByTestId("map-details").GetByRole(AriaRole.Button, new() { Name = "Add a map of Watchtower" }).ClickAsync();
        await dm.WaitForURLAsync(url => !url.EndsWith(mapUrl) && url.Contains("/world/map/"));
        await dm.GetByTestId("map-file").SetInputFilesAsync(new FilePayload { Name = "tower.png", MimeType = "image/png", Buffer = Picture() });
        await Assertions.Expect(dm.GetByTestId("map-image")).ToBeVisibleAsync();
        await dm.GetByTestId("map-toggle-shown").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("map-subtitle")).ToHaveTextAsync("Players will see this map once they know Watchtower.");
        var towerImageUrl = (await dm.GetByTestId("map-image").GetAttributeAsync("src"))!;
        await AssertPngAsync(await dm.APIRequest.GetAsync(towerImageUrl));
        Assert.Equal(404, (await player.APIRequest.GetAsync(towerImageUrl)).Status);

        // In Hebrew the page runs right to left, and the picture is never mirrored.
        await dm.GotoAsync($"/set-culture?culture=he&returnUrl={Uri.EscapeDataString(mapUrl)}");
        await E2EHelpers.WaitForInteractiveAsync(dm);
        await Assertions.Expect(dm.Locator("html")).ToHaveAttributeAsync("dir", "rtl");
        await Assertions.Expect(dm.GetByTestId("map-viewport")).ToHaveAttributeAsync("dir", "ltr");
        await Assertions.Expect(dm.GetByTestId("map-marker")).ToHaveCountAsync(2);
    }
}
