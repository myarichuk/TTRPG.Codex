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
        // An enum number is not an edition: "2" must not read as a party book that was never cleaned.
        foreach (var number in new[] { "0", "1", "2", "-1" })
        {
            Assert.Equal(404, (await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/{number}")).Status);
            Assert.Equal(404, (await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/{number}/pdf")).Status);
        }

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

        // The torn-edge look: the preview is the page of that theme, and what the frame loads says so.
        await dm.GetByTestId("book-theme").SelectOptionAsync("parchment");
        await Assertions.Expect(dm.GetByTestId("book-preview")).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("theme=parchment"));
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

    [Fact]
    public async Task TheGmPutsAPictureInTheCoverSlot_ThePartyBookShowsIt_AndTheGmTrayCallsItTheParty()
    {
        const string password = "Correct-Horse-1";
        var dmId = "ba-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Gullwatch");
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");

        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        var toggle = dm.GetByTestId("book-art-toggle");
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await toggle.ClickAsync();
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        var slots = dm.GetByTestId("book-art-slots");
        await Assertions.Expect(slots).ToBeVisibleAsync();

        // The cover is the first row. Its file input can take keyboard focus, and the ring is drawn on its label.
        var cover = slots.Locator("li").First;
        await Assertions.Expect(cover).ToContainTextAsync("Cover");
        await Assertions.Expect(cover).ToContainTextAsync("no picture");
        var input = cover.Locator("input[type=file]");
        await input.FocusAsync();
        Assert.True(await input.EvaluateAsync<bool>("e => document.activeElement === e"));
        Assert.NotEqual("none", await cover.Locator("label.book-art-file").EvaluateAsync<string>("l => getComputedStyle(l).outlineStyle"));

        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(64, 80));
        bitmap.Erase(new SkiaSharp.SKColor(120, 40, 30));
        var png = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray();
        await input.SetInputFilesAsync(new FilePayload { Name = "cover.png", MimeType = "image/png", Buffer = png });
        await Assertions.Expect(dm.GetByTestId("book-art-status")).ToContainTextAsync("Picture added.");
        await Assertions.Expect(slots.Locator("li").First).ToContainTextAsync("your picture");
        await Assertions.Expect(slots.Locator("li").First.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToBeVisibleAsync();

        // The campaign has no cover of its own, so a cover on the party book's title page is the one just added.
        var party = await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/party");
        Assert.Equal(200, party.Status);
        Assert.Contains("class=\"cover-art\"", await party.TextAsync(), StringComparison.Ordinal);

        // In the GM's tray the same cover is the party edition's (it prints in both books), and there is nothing of the GM's to remove.
        await dm.GetByTestId("book-edition").SelectOptionAsync("dm");
        await Assertions.Expect(slots.Locator("li").First).ToContainTextAsync("party edition's picture");
        await Assertions.Expect(slots.Locator("li").First.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToHaveCountAsync(0);
        Assert.Contains("class=\"cover-art\"", await (await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/dm")).TextAsync(), StringComparison.Ordinal);

        // Back in the party tray, removing it takes it out of the book.
        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await slots.Locator("li").First.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-status")).ToContainTextAsync("Picture removed.");
        await Assertions.Expect(slots.Locator("li").First).ToContainTextAsync("no picture");
        Assert.DoesNotContain("class=\"cover-art\"", await (await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/party")).TextAsync(), StringComparison.Ordinal);
        Assert.Empty(pageErrors);
    }

    [InlineData(500, "could not be printed", "print-failed")]
    [Theory]
    [InlineData(429, "Another book is being printed", "busy")]
    [InlineData(503, "There is no browser to print with", "no-browser")]
    [InlineData(503, "too old to print a book", "browser-too-old")]
    [InlineData(503, "would not start", "launch-failed")]
    [InlineData(504, "took too long", "timeout")]
    public async Task ARefusedPrintIsSaidOnThePage_NotShownAsAnErrorDocument(int status, string expected, string code)
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
            Body = "{\"detail\":\"set CODEX_BOOK_BROWSER to its executable\",\"code\":\"" + code + "\"}"
        }));
        var url = dm.Url;
        await dm.GetByTestId("book-download").ClickAsync();

        var error = dm.GetByTestId("book-error");
        await Assertions.Expect(error).ToContainTextAsync(expected);
        // The setup sentence is for "no browser" only; the other causes keep their details in the server log.
        if (code == "no-browser")
        {
            await Assertions.Expect(error).ToContainTextAsync("CODEX_BOOK_BROWSER");
        }
        else
        {
            Assert.DoesNotContain("CODEX_BOOK_BROWSER", await error.InnerTextAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(url, dm.Url);
        await Assertions.Expect(dm.GetByTestId("book-download")).ToBeEnabledAsync();
        Assert.Equal(0, await dm.GetByTestId("book-done").CountAsync());
    }
}

/// <summary>
/// The AI polish in the book panel, end to end: a DM adds an OpenAI-compatible model through AI settings (a fake server in this
/// process stands in for it, and writes its own request log), sees what the build would cost before anything is sent, runs it, watches
/// the progress, and prints from the stored result. The model is sent no secret, and the page says what was spent.
/// </summary>
[Collection("RavenDb")]
public class BookAiPanelE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BookAiPanelE2ETests(AppFixture app) => _app = app;

    private sealed class FakeModelServer : IAsyncDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly Task _loop;
        public string Url { get; }
        public List<string> Requests { get; } = [];

        public FakeModelServer()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Url = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); } catch { return; }
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    lock (Requests) Requests.Add(body);
                    var last = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? string.Empty;
                    var from = last.IndexOf("<source>", StringComparison.Ordinal) + "<source>".Length;
                    var source = last[from..last.IndexOf("</source>", StringComparison.Ordinal)].Trim();
                    var reply = source.Replace("walks", "strolls");
                    var json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id = "x", @object = "chat.completion", created = 0, model = "fake-model",
                        choices = new[] { new { index = 0, message = new { role = "assistant", content = reply }, finish_reason = "stop" } },
                        usage = new { prompt_tokens = 1000, completion_tokens = 400, total_tokens = 1400 }
                    });
                    var bytes = Encoding.UTF8.GetBytes(json);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch
                {
                    try { context.Response.Abort(); } catch { }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            await _loop;
        }
    }

    [Fact]
    public async Task TheDmSeesTheCostFirst_RunsTheBuild_AndPrintsFromTheStoredChapters()
    {
        const string password = "Correct-Horse-1";
        var dmId = "ba-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var model = new FakeModelServer();

        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        var story = string.Join(' ', Enumerable.Repeat("The keeper of the light walks the cliff path at dusk and trims the lamp.", 6));
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor
        {
            Id = "ba-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Biography = story + "\n\n:::dm\nCANARY_E2E_SECRET\n:::"
        }]);

        // No model yet: the panel says so, and offers the way to set one up instead of a button that fails.
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await Assertions.Expect(dm.GetByTestId("book-ai-nomodel")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-ai-start")).ToBeDisabledAsync();

        // A model, with prices.
        await dm.GotoAsync("/settings/ai");
        await dm.GetByTestId("ai-add-provider").ClickAsync();
        await dm.GetByTestId("ai-kind").SelectOptionAsync("custom");
        await dm.GetByTestId("ai-name").FillAsync("Fake model");
        await dm.GetByTestId("ai-url").FillAsync(model.Url);
        await dm.GetByTestId("ai-model").FillAsync("fake-model");
        await dm.GetByTestId("ai-price-in").FillAsync("1");
        await dm.GetByTestId("ai-price-out").FillAsync("2");
        await dm.GetByTestId("ai-price-label").FillAsync("USD");
        await dm.GetByTestId("ai-save").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("ai-provider-list")).ToContainTextAsync("Fake model");

        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await Assertions.Expect(dm.GetByTestId("book-ai-model")).ToContainTextAsync("fake-model");

        // The estimate comes first: the chapters, the requests, tokens as a range, and money from the prices set. Nothing was sent.
        await Assertions.Expect(dm.GetByTestId("book-ai-preflight")).ToContainTextAsync("requests");
        await Assertions.Expect(dm.GetByTestId("book-ai-preflight")).ToContainTextAsync("USD");
        await Assertions.Expect(dm.GetByTestId("book-ai-chapters")).ToContainTextAsync("not built yet");
        Assert.Empty(model.Requests);

        await dm.GetByTestId("book-ai-start").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-ai-progress")).ToContainTextAsync("Build finished");
        await Assertions.Expect(dm.GetByTestId("book-ai-progress")).ToContainTextAsync("tokens in");
        await Assertions.Expect(dm.GetByTestId("book-ai-progress")).ToContainTextAsync("USD");
        await Assertions.Expect(dm.GetByTestId("book-ai-chapters")).ToContainTextAsync("up to date");
        await Assertions.Expect(dm.GetByTestId("book-ai-totals")).ToBeVisibleAsync();

        // What the model was sent: the chapter, never the DM's secret.
        string requests;
        lock (model.Requests) requests = string.Join("\n", model.Requests);
        Assert.Contains("keeper of the light", requests);
        Assert.DoesNotContain("CANARY_E2E_SECRET", requests);

        // The preview and the PDF print from the stored rewrite when asked; the secret is still boxed, by code.
        var plain = await context.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm");
        var plainText = await plain.TextAsync();
        Assert.Contains("walks the cliff path", plainText);
        var polished = await context.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm?ai=1");
        var polishedText = await polished.TextAsync();
        Assert.Contains("strolls the cliff path", polishedText);
        Assert.Contains("CANARY_E2E_SECRET", polishedText);
        Assert.Contains("class=\"secret secret-dm\"", polishedText);
        // The party edition was never built: asking for it gets the book as written, and no secret.
        var party = await (await context.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/party?ai=1")).TextAsync();
        Assert.DoesNotContain("CANARY_E2E_SECRET", party);
        Assert.DoesNotContain("strolls", party);

        // The checkbox points the preview at it.
        await dm.GetByTestId("book-preview-toggle").ClickAsync();
        await dm.GetByTestId("book-ai-use").CheckAsync();
        await Assertions.Expect(dm.GetByTestId("book-preview")).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("ai=1"));

        // A second build sends nothing: every chapter is current.
        int before;
        lock (model.Requests) before = model.Requests.Count;
        await Assertions.Expect(dm.GetByTestId("book-ai-start")).ToBeDisabledAsync(); // "nothing to build"
        await Assertions.Expect(dm.GetByTestId("book-ai-preflight")).ToContainTextAsync("Nothing to build");
        lock (model.Requests) Assert.Equal(before, model.Requests.Count);
        Assert.Empty(pageErrors);
    }
}
