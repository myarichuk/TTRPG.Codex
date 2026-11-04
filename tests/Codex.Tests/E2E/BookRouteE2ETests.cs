using System.Text;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>The book routes over HTTP, against the real app: who may ask, what they get back, and that the PDF is a PDF.</summary>
[Collection("RavenDb")]
public class BookRouteE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BookRouteE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task OnlyTheDmGetsABook_AsAPageOrAsAPdf_AndTheLanguageGateAnswersWithAReason()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bk-dm-" + Guid.NewGuid();
        var otherId = "bk-other-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(otherId, "other_" + otherId, password, "Player");

        await using var dmContext = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await dmContext.NewPageAsync();
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");

        foreach (var audience in new[] { "dm", "party" })
        {
            var page = await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/{audience}");
            Assert.Equal(200, page.Status);
            Assert.StartsWith("text/html", page.Headers["content-type"]);
            Assert.Equal("no-store", page.Headers["cache-control"]);
            Assert.Contains("default-src 'none'", page.Headers["content-security-policy"]);
            Assert.Contains("Saltmere Chronicle", await page.TextAsync());
        }

        var pdf = await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/party/pdf");
        Assert.True(pdf.Status == 200, $"pdf status {pdf.Status}: {await pdf.TextAsync()}");
        Assert.Equal("application/pdf", pdf.Headers["content-type"]);
        Assert.Equal("no-store", pdf.Headers["cache-control"]);
        Assert.Contains("attachment", pdf.Headers["content-disposition"]);
        Assert.Contains(".pdf", pdf.Headers["content-disposition"]);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString((await pdf.BodyAsync()).AsSpan(0, 5)));

        // The theme cannot print Russian: a reason, not a half-English page.
        var russian = await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm/pdf?lang=ru");
        Assert.Equal(422, russian.Status);
        Assert.Equal(400, (await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm?theme=nope")).Status);
        Assert.Equal(404, (await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/everyone")).Status);

        // Somebody who is not this campaign's DM gets nothing, and cannot tell a campaign from no campaign.
        await using var otherContext = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var other = await otherContext.NewPageAsync();
        await E2EHelpers.LoginAsync(other, "other_" + otherId, password);
        foreach (var route in new[] { "dm", "party", "dm/pdf", "party/pdf" })
        {
            Assert.Equal(404, (await otherContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/{route}")).Status);
        }

        Assert.Equal(404, (await otherContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/no-such-campaign/book/dm")).Status);
    }
}

/// <summary>The campaign book panel in Settings: preview in a locked frame, a real PDF download, the language gate shown before it
/// is hit, and nothing at all for a player.</summary>
[Collection("RavenDb")]
public class BookPanelE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BookPanelE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task TheDmPreviewsAndDownloadsTheBook_AndAPlayerSeesNoBookPanel()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bp-dm-" + Guid.NewGuid();
        var playerId = "bp-pl-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(playerId, "pl_" + playerId, password, "Player");

        await using var dmContext = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl, AcceptDownloads = true });
        var dm = await dmContext.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");

        var panel = dm.GetByTestId("book-panel");
        await Assertions.Expect(panel).ToBeVisibleAsync();

        // The theme prints English only: the other languages are listed, and said to be unavailable, instead of failing later.
        await Assertions.Expect(dm.GetByTestId("book-language").Locator("option[value=ru]")).ToBeDisabledAsync();
        await Assertions.Expect(dm.GetByTestId("book-language").Locator("option[value=he]")).ToBeDisabledAsync();
        await Assertions.Expect(dm.GetByTestId("book-language").Locator("option[value=en]")).ToBeEnabledAsync();

        // Preview: a frame with no permissions at all, showing the page the PDF is printed from.
        await dm.GetByTestId("book-preview-toggle").ClickAsync();
        var frame = dm.GetByTestId("book-preview");
        await Assertions.Expect(frame).ToBeVisibleAsync();
        Assert.Equal(string.Empty, await frame.GetAttributeAsync("sandbox"));
        Assert.NotNull(await frame.GetAttributeAsync("sandbox"));
        await Assertions.Expect(dm.FrameLocator("[data-testid=book-preview]").Locator("h1").First).ToHaveTextAsync("Saltmere Chronicle");

        // Switching edition reloads the frame (the party edition is a different page).
        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await Assertions.Expect(dm.FrameLocator("[data-testid=book-preview]").Locator("h1").First).ToHaveTextAsync("Saltmere Chronicle");

        // Download: a real PDF named after the campaign.
        var download = await dm.RunAndWaitForDownloadAsync(() => dm.GetByTestId("book-download").ClickAsync());
        Assert.EndsWith(".pdf", download.SuggestedFilename);
        Assert.Contains("Saltmere Chronicle", download.SuggestedFilename);
        await using (var stream = await download.CreateReadStreamAsync())
        {
            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(head));
        }

        await Assertions.Expect(dm.GetByTestId("book-done")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-download")).ToBeEnabledAsync();
        Assert.Empty(pageErrors);

        // A player joins by invite and finds no book panel on the Settings page.
        var invite = await E2EHelpers.ReadInviteLinkAsync(dm, campaignId);
        await using var playerContext = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var player = await playerContext.NewPageAsync();
        await E2EHelpers.LoginAsync(player, "pl_" + playerId, password);
        await E2EHelpers.JoinAsync(player, invite);
        await E2EHelpers.GoToSectionAsync(player, campaignId, "settings");
        await Assertions.Expect(player.GetByTestId("settings-name").Or(player.GetByTestId("campaign-title")).First).ToBeVisibleAsync();
        Assert.Equal(0, await player.GetByTestId("book-panel").CountAsync());
    }

    [Theory]
    [InlineData(429, "Another book is being printed")]
    [InlineData(503, "There is no browser to print with")]
    [InlineData(504, "took too long")]
    [InlineData(500, "could not be printed")]
    public async Task ARefusedPrintIsSaidOnThePage_NotShownAsAnErrorDocument(int status, string expected)
    {
        const string password = "Correct-Horse-1";
        var dmId = "bpe-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Refusals");
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");

        // The server's answers are stood in for here; the real ones are covered by the route and engine tests.
        await dm.RouteAsync("**/book/*/pdf*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/problem+json",
            Body = "{\"detail\":\"set CODEX_BOOK_BROWSER to its executable\"}"
        }));
        var url = dm.Url;
        await dm.GetByTestId("book-download").ClickAsync();

        var error = dm.GetByTestId("book-error");
        await Assertions.Expect(error).ToContainTextAsync(expected);
        if (status == 503)
        {
            await Assertions.Expect(error).ToContainTextAsync("CODEX_BOOK_BROWSER");
        }

        Assert.Equal(url, dm.Url);
        await Assertions.Expect(dm.GetByTestId("book-download")).ToBeEnabledAsync();
        Assert.Equal(0, await dm.GetByTestId("book-done").CountAsync());
    }
}
