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

        // The draft's Markdown (41.24) is the draft's: with no draft there is nothing to download (the editor makes one when it opens).
        Assert.Equal(404, (await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm/draft/en/md")).Status);

        // Russian prints (docs/BOOK_EXPORT.md 41.21); a language no theme has faces for gets a reason, not a page in the wrong fonts.
        var russian = await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm/pdf?lang=ru");
        Assert.Equal(200, russian.Status);
        var german = await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm/pdf?lang=de");
        Assert.Equal(422, german.Status);
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
        foreach (var route in new[] { "dm", "party", "dm/pdf", "party/pdf", "dm/draft/en/md", "party/draft/en/md" })
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

        // The theme prints English, Russian and Hebrew (41.21): every language the app has can be chosen.
        await Assertions.Expect(dm.GetByTestId("book-language").Locator("option[value=ru]")).ToBeEnabledAsync();
        await Assertions.Expect(dm.GetByTestId("book-language").Locator("option[value=he]")).ToBeEnabledAsync();
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
        // The Pictures tab opens the tray (and reads its slots) the first time it is shown.
        var toggle = dm.GetByTestId("book-art-toggle");
        await dm.GetByTestId("book-tab-art").ClickAsync();
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
        await Assertions.Expect(slots.Locator("li").First.GetByRole(AriaRole.Button, new() { Name = "Take out" })).ToBeVisibleAsync();

        // The campaign has no cover of its own, so a cover on the party book's title page is the one just added.
        var party = await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/party");
        Assert.Equal(200, party.Status);
        Assert.Contains("class=\"cover-art\"", await party.TextAsync(), StringComparison.Ordinal);

        // In the GM's tray the same cover is the party edition's (it prints in both books), and there is nothing of the GM's to remove.
        await dm.GetByTestId("book-edition").SelectOptionAsync("dm");
        await Assertions.Expect(slots.Locator("li").First).ToContainTextAsync("party edition's picture");
        await Assertions.Expect(slots.Locator("li").First.GetByRole(AriaRole.Button, new() { Name = "Take out" })).ToHaveCountAsync(0);
        Assert.Contains("class=\"cover-art\"", await (await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/dm")).TextAsync(), StringComparison.Ordinal);

        // Back in the party tray, removing it takes it out of the book.
        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await slots.Locator("li").First.GetByRole(AriaRole.Button, new() { Name = "Take out" }).ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-status")).ToContainTextAsync("Picture taken out of the book.");
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

    internal sealed class FakeModelServer : IAsyncDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly Task _loop;
        public string Url { get; }
        public List<string> Requests { get; } = [];
        public List<string> ImagePrompts { get; } = [];

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
                    // A picture model: one small PNG for every prompt, the prompt kept.
                    if (context.Request.Url!.AbsolutePath.EndsWith("/images/generations", StringComparison.Ordinal))
                    {
                        lock (ImagePrompts) ImagePrompts.Add(System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString() ?? string.Empty);
                        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(96, 128));
                        bitmap.Erase(new SkiaSharp.SKColor(40, 60, 90));
                        var png = Convert.ToBase64String(SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray());
                        var picture = Encoding.UTF8.GetBytes("{\"created\":1,\"data\":[{\"b64_json\":\"" + png + "\"}]}");
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = picture.Length;
                        await context.Response.OutputStream.WriteAsync(picture);
                        context.Response.Close();
                        continue;
                    }

                    var last = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? string.Empty;
                    string reply;
                    if (last.Contains("\n<mine>\n", StringComparison.Ordinal))
                    {
                        // A conflict of "Bring up to date": the GM's entry back, with the campaign's new fact in it.
                        var from = last.IndexOf("\n<mine>\n", StringComparison.Ordinal) + "\n<mine>\n".Length;
                        reply = last[from..last.IndexOf("\n</mine>", from, StringComparison.Ordinal)].Trim().Replace("on the point", "on the far point");
                    }
                    else if (last.Contains("<passage>", StringComparison.Ordinal))
                    {
                        // An AI edit of the editable book: the passage back, one word changed.
                        var from = last.IndexOf("<passage>", StringComparison.Ordinal) + "<passage>".Length;
                        reply = last[from..last.IndexOf("</passage>", StringComparison.Ordinal)].Trim().Replace("lighthouse", "beacon tower");
                        // Rewritten throughout, as a real model does "as the book": every prose line's words in reverse order.
                        if (reply.Contains("REWRITE_WHOLE", StringComparison.Ordinal))
                        {
                            reply = string.Join("\n", reply.Split('\n').Select(l => l.StartsWith("{{", StringComparison.Ordinal) || l.StartsWith('#') || l.StartsWith("}}", StringComparison.Ordinal)
                                ? l : string.Join(' ', l.Split(' ').Reverse())));
                        }
                    }
                    else if (!last.Contains("<source>", StringComparison.Ordinal))
                    {
                        // The art director: the same harmless picture for every slot it is shown.
                        var slots = System.Text.RegularExpressions.Regex.Matches(last, @"^Slot (?<n>\d+) \([^)]*\): (?<label>.*)$", System.Text.RegularExpressions.RegexOptions.Multiline)
                            .Select(m => new { slot = int.Parse(m.Groups["n"].Value), depicts = m.Groups["label"].Value.Trim(), visual = "a lamp burning in a high window above dark water", mood = "serene" });
                        reply = System.Text.Json.JsonSerializer.Serialize(new { slots });
                    }
                    else
                    {
                        var from = last.IndexOf("<source>", StringComparison.Ordinal) + "<source>".Length;
                        var source = last[from..last.IndexOf("</source>", StringComparison.Ordinal)].Trim();
                        reply = source.Replace("walks", "strolls");
                    }

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
        await dm.GetByTestId("book-tab-ai").ClickAsync();
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
        await dm.GetByTestId("book-tab-ai").ClickAsync();
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
        await dm.GetByTestId("book-tab-print").ClickAsync();
        await dm.GetByTestId("book-preview-toggle").ClickAsync();
        await dm.GetByTestId("book-tab-ai").ClickAsync();
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

    // Step 6 through the real panel: a picture model with a price, a suggestion from the director, the cost shown before anything is drawn,
    // one ticked place drawn with the description the GM edited, and the picture in the party's book. Nothing is drawn that was not ticked.
    [Fact]
    public async Task TheGmTicksASuggestedPlace_SeesThePriceFirst_AndThePartyBookPrintsThePictureDrawn()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bd-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var model = new FakeModelServer();

        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Lamplight");
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor { Id = "bd-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Biography = "Keeps the lighthouse." }]);

        await dm.GotoAsync("/settings/ai");
        await dm.GetByTestId("ai-add-provider").ClickAsync();
        await dm.GetByTestId("ai-kind").SelectOptionAsync("custom");
        await dm.GetByTestId("ai-name").FillAsync("Fake model");
        await dm.GetByTestId("ai-url").FillAsync(model.Url);
        await dm.GetByTestId("ai-model").FillAsync("fake-model");
        await dm.GetByTestId("ai-save").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("ai-provider-list")).ToContainTextAsync("Fake model");
        await dm.GetByTestId("ai-add-image-provider").ClickAsync();
        await dm.GetByTestId("ai-kind").SelectOptionAsync("custom");
        await dm.GetByTestId("ai-name").FillAsync("Fake pictures");
        await dm.GetByTestId("ai-url").FillAsync(model.Url);
        await dm.GetByTestId("ai-model").FillAsync("fake-pictures");
        await dm.GetByTestId("ai-price-image").FillAsync("0.05");
        await dm.GetByTestId("ai-price-image-label").FillAsync("USD");
        await dm.GetByTestId("ai-save").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("ai-image-provider-list")).ToContainTextAsync("0.050 USD per picture");

        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await dm.GetByTestId("book-tab-art").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-draw-preflight")).ToContainTextAsync("Tick a place");
        await dm.GetByTestId("book-art-style-medium").SelectOptionAsync("woodcut");
        await Assertions.Expect(dm.GetByTestId("book-art-status")).ToContainTextAsync("Style saved");

        await dm.GetByTestId("book-art-suggest").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-director-progress")).ToContainTextAsync("Done");
        var vera = dm.GetByTestId("book-art-slots").Locator("li", new() { HasText = "Vera" });
        await Assertions.Expect(vera).ToContainTextAsync("a lamp burning in a high window");
        Assert.Empty(model.ImagePrompts);

        await vera.GetByRole(AriaRole.Checkbox, new() { Name = "Draw Vera" }).CheckAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-draw-preflight")).ToContainTextAsync("About 0.050 USD");
        // A name in the description is refused before anything is drawn, and the button counts nothing.
        var visual = vera.Locator("textarea");
        await visual.FillAsync("Vera by the lamp");
        await visual.BlurAsync();
        await Assertions.Expect(vera.Locator(".field-error")).ToContainTextAsync("can't be used");
        await Assertions.Expect(dm.GetByTestId("book-art-draw-start")).ToBeDisabledAsync();
        await visual.FillAsync("a keeper trimming a lamp in a storm");
        await visual.BlurAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-draw-start")).ToContainTextAsync("Draw selected (1)");
        Assert.Empty(model.ImagePrompts);

        await dm.GetByTestId("book-art-draw-start").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-draw-progress")).ToContainTextAsync("Done: drew 1 of 1");
        await Assertions.Expect(vera).ToContainTextAsync("your picture · drawn");
        var prompt = Assert.Single(model.ImagePrompts);
        Assert.Contains("a keeper trimming a lamp in a storm", prompt);
        Assert.Contains("Woodcut print", prompt);
        Assert.DoesNotContain("Vera", prompt);
        await Assertions.Expect(dm.GetByTestId("book-art-draw-totals")).ToContainTextAsync("Pictures drawn for this book so far: 1. Cost so far: 0.050 USD.");

        // Draw again (28.3, item 3): the pre-flight says the picture in use is kept, and after the run the slot has two versions to choose from.
        await vera.GetByRole(AriaRole.Checkbox, new() { Name = "Draw again Vera" }).CheckAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-draw-replaces")).ToContainTextAsync("kept as an earlier version: Vera");
        await dm.GetByTestId("book-art-draw-start").ClickAsync();
        // The progress line reads "Done" from the first run too: the versions button is what says the second picture is in.
        await vera.GetByRole(AriaRole.Button, new() { Name = "Versions (2)" }).ClickAsync();
        Assert.Equal(2, model.ImagePrompts.Count);
        var versions = vera.GetByTestId("book-art-version");
        await Assertions.Expect(versions).ToHaveCountAsync(2);
        await Assertions.Expect(versions.Locator("img")).ToHaveCountAsync(2);
        await Assertions.Expect(versions.First).ToHaveAttributeAsync("data-in-use", "true");

        // The earlier one is put in use; the one drawn second is kept.
        await versions.Nth(1).GetByTestId("book-art-version-use").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-status")).ToContainTextAsync("The picture in use is changed.");
        await Assertions.Expect(vera.Locator("[data-testid=book-art-version][data-in-use=true]")).ToHaveCountAsync(1);

        // Deleting a version asks first; "Keep it" keeps it, and "Delete for good" leaves the slot one picture and no versions to show.
        var kept = vera.Locator("[data-testid=book-art-version][data-in-use=false]");
        await kept.GetByTestId("book-art-version-delete").ClickAsync();
        await Assertions.Expect(kept).ToContainTextAsync("can't be brought back");
        await kept.GetByTestId("book-art-version-delete-no").ClickAsync();
        await Assertions.Expect(versions).ToHaveCountAsync(2);
        // The keyboard keeps its place: focus is back on Delete.
        await Assertions.Expect(kept.GetByTestId("book-art-version-delete")).ToBeFocusedAsync();
        await kept.GetByTestId("book-art-version-delete").ClickAsync();
        await kept.GetByTestId("book-art-version-delete-yes").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-status")).ToContainTextAsync("Version deleted.");
        await Assertions.Expect(versions).ToHaveCountAsync(0);
        await Assertions.Expect(vera).ToContainTextAsync("your picture · drawn");
        Assert.Equal(2, model.ImagePrompts.Count);

        // Two more draws make three versions; ticking a fourth says first, in the pre-flight, that it deletes a paid one.
        foreach (var count in new[] { 2, 3 })
        {
            await vera.GetByRole(AriaRole.Checkbox, new() { Name = "Draw again Vera" }).CheckAsync();
            await Assertions.Expect(dm.GetByTestId("book-art-draw-start")).ToContainTextAsync("Draw selected (1)");
            await dm.GetByTestId("book-art-draw-start").ClickAsync();
            await Assertions.Expect(vera.GetByRole(AriaRole.Button, new() { Name = $"Versions ({count})" })).ToBeVisibleAsync();
        }

        await vera.GetByRole(AriaRole.Checkbox, new() { Name = "Draw again Vera" }).CheckAsync();
        await Assertions.Expect(dm.GetByTestId("book-art-draw-drops")).ToContainTextAsync("deletes the oldest earlier one, which was paid for: Vera");
        await Assertions.Expect(dm.GetByTestId("book-art-draw-replaces")).ToHaveCountAsync(0);
        await vera.GetByRole(AriaRole.Checkbox, new() { Name = "Draw again Vera" }).UncheckAsync();
        Assert.Equal(4, model.ImagePrompts.Count);

        var party = await (await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/party")).TextAsync();
        Assert.Contains("class=\"art\"", party, StringComparison.Ordinal);
        Assert.Empty(pageErrors);
    }
}

/// <summary>The editable book in the real app (docs/BOOK_EXPORT.md 41): the editor opens on a draft made from the campaign, saves what the GM
/// types, shows it in the preview, tells the GM when the campaign changed under an edited chapter, regenerates it, and prints the draft.</summary>
[Collection("RavenDb")]
public class BookDraftE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BookDraftE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task TheGmEditsAChapter_IsToldWhenTheCampaignChanges_RegeneratesIt_AndPrintsTheDraft()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bdr-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl, AcceptDownloads = true });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        var veraId = "bdr-vera-" + Guid.NewGuid();
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor { Id = veraId, CampaignId = campaignId, Name = "Vera", Biography = "Keeps the lighthouse.", Visibility = Codex.Persistence.ActorVisibility.Known }]);
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");

        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        var dialog = dm.GetByTestId("book-draft-dialog");
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        // The preview frame runs no script; same-origin only, so the editor can draw into it.
        Assert.Equal("allow-same-origin", await dm.GetByTestId("book-draft-preview").GetAttributeAsync("sandbox"));
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator(".page h1").First).ToHaveTextAsync("Saltmere Chronicle");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Keeps the lighthouse." })).ToHaveCountAsync(1);

        // The GM writes into the Cast chapter: it is saved, marked edited, and drawn.
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+End");
        await dm.Keyboard.TypeAsync("\nPUBLIC_GM_LINE\n");
        await Assertions.Expect(dm.Locator("[data-testid=book-draft-chapter][data-section=cast] .book-draft-badge-edited")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_GM_LINE" })).ToHaveCountAsync(1);

        // The campaign changes under the edited chapter: on opening again the chapter says so, naming the entry.
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor { Id = veraId, CampaignId = campaignId, Name = "Vera", Biography = "Rings the bell at dusk.", Visibility = Codex.Persistence.ActorVisibility.Known }]);
        await dm.GetByTestId("book-draft-close").ClickAsync();
        await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        var stale = dm.GetByTestId("book-draft-stale");
        await Assertions.Expect(stale).ToBeVisibleAsync();
        await Assertions.Expect(stale).ToContainTextAsync("Vera");

        // Regenerate takes the campaign's text, and the GM's line goes (undo is offered).
        await dm.GetByTestId("book-draft-regenerate").ClickAsync();
        await Assertions.Expect(stale).Not.ToBeVisibleAsync();
        await Assertions.Expect(dm.Locator(".cm-content")).ToContainTextAsync("Rings the bell at dusk.");
        await Assertions.Expect(dm.Locator(".cm-content")).Not.ToContainTextAsync("PUBLIC_GM_LINE");
        await Assertions.Expect(dm.GetByTestId("book-draft-undo")).ToBeVisibleAsync();
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Rings the bell at dusk." })).ToHaveCountAsync(1);

        // Another theme, straight after typing: what was typed is saved first, the theme is stored, and the preview is drawn again in the new
        // theme's page with the same text.
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+End");
        await dm.Keyboard.TypeAsync("\nPUBLIC_BEFORE_THEME\n");
        await dm.GetByTestId("book-draft-theme").SelectOptionAsync("ashen-vellum");
        await Assertions.Expect(preview.Locator("body.theme-ashen-vellum")).ToHaveCountAsync(1);
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_BEFORE_THEME" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        await Assertions.Expect(dm.GetByTestId("book-draft-theme")).ToHaveValueAsync("ashen-vellum");

        // The Homebrewery layer is on by default; turned off, the preview is drawn again without it, and that is stored too.
        await Assertions.Expect(preview.Locator("body.homebrewery-layer")).ToHaveCountAsync(1);
        await dm.GetByTestId("book-draft-homebrewery").UncheckAsync();
        await Assertions.Expect(preview.Locator("body.theme-ashen-vellum:not(.homebrewery-layer)")).ToHaveCountAsync(1);
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_BEFORE_THEME" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        await Assertions.Expect(dm.GetByTestId("book-draft-homebrewery")).Not.ToBeCheckedAsync();

        // The draft prints.
        var download = await dm.RunAndWaitForDownloadAsync(() => dm.GetByTestId("book-draft-download").ClickAsync());
        Assert.EndsWith(".pdf", download.SuggestedFilename);
        await using (var stream = await download.CreateReadStreamAsync())
        {
            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(head));
        }

        Assert.Empty(pageErrors);
    }

    /// <summary>The party leak check (41.19): a secret the GM pastes into the party draft stops Download with a warning naming it and its chapter,
    /// and "Download anyway" prints the book all the same.</summary>
    [Fact]
    public async Task ASecretPastedIntoThePartyDraft_IsNamedBeforeDownload_AndDownloadAnywayStillPrints()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bdl-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl, AcceptDownloads = true });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        var downloads = 0;
        dm.Download += (_, _) => downloads++;
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor
        {
            Id = "bdl-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Visibility = Codex.Persistence.ActorVisibility.Known,
            Biography = "Keeps the lighthouse.\n\n:::dm\nThe vault key lies under the chapel altar.\n:::"
        }, new Codex.Web.TestSeedActor
        {
            // A portrait the book cannot read: the Problems tab names it (41.24).
            Id = "bdl-oren-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Oren the ferryman", Visibility = Codex.Persistence.ActorVisibility.Known, HasPortrait = true
        }]);
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");

        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+End");
        await dm.Keyboard.TypeAsync("\nShe knows the vault key lies under the chapel altar.\n");
        // Straight to Download: what was typed is saved first, and that is what is checked.
        await dm.GetByTestId("book-draft-download").ClickAsync();

        var leftOut = dm.GetByTestId("book-draft-left-out-item");
        await Assertions.Expect(leftOut).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        await Assertions.Expect(leftOut).ToContainTextAsync("Oren the ferryman");
        await Assertions.Expect(leftOut).ToContainTextAsync("the file could not be read");
        await Assertions.Expect(leftOut).ToHaveAttributeAsync("data-reason", "missing");

        var leaks = dm.GetByTestId("book-draft-leaks");
        await Assertions.Expect(leaks).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(leaks).ToContainTextAsync("only the GM edition should have");
        var hit = dm.GetByTestId("book-draft-leak");
        await Assertions.Expect(hit).ToHaveCountAsync(1);
        await Assertions.Expect(hit).ToContainTextAsync("Cast");
        await Assertions.Expect(hit).ToContainTextAsync("words of a secret in Vera");
        await Assertions.Expect(hit).ToContainTextAsync("The vault key lies under the chapel altar.");
        await Assertions.Expect(dm.GetByTestId("book-draft-tab-problems")).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        Assert.Equal(0, downloads);

        // The hit opens its chapter.
        await dm.Locator("[data-testid=book-draft-chapter][data-section=front]").ClickAsync();
        await hit.Locator("button").ClickAsync();
        await Assertions.Expect(dm.Locator("[data-testid=book-draft-chapter][data-section=cast]")).ToHaveAttributeAsync("aria-current", "true");

        var download = await dm.RunAndWaitForDownloadAsync(() => dm.GetByTestId("book-draft-download-anyway").ClickAsync());
        await using (var stream = await download.CreateReadStreamAsync())
        {
            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(head));
        }

        // The Markdown file (41.24) is checked the same way: nothing is downloaded until Download anyway, which then gives the .md.
        await dm.GetByTestId("book-draft-more").ClickAsync();
        await dm.GetByTestId("book-draft-markdown").ClickAsync();
        await Assertions.Expect(hit).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-download-anyway")).ToBeEnabledAsync();
        Assert.Equal(1, downloads);
        var md = await dm.RunAndWaitForDownloadAsync(() => dm.GetByTestId("book-draft-download-anyway").ClickAsync());
        Assert.EndsWith(" (party).md", md.SuggestedFilename);
        await using (var stream = await md.CreateReadStreamAsync())
        {
            var text = await new StreamReader(stream).ReadToEndAsync();
            Assert.Contains("She knows the vault key lies under the chapel altar.", text);
            Assert.Contains("{{entry,#e-", text);
        }

        var route = await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/party/draft/en/md");
        Assert.StartsWith("text/markdown", route.Headers["content-type"]);
        Assert.Equal("no-store", route.Headers["cache-control"]);

        Assert.Empty(pageErrors);
    }

    /// <summary>A player's own book (45): the party draft's menu lists each player by their characters; their PDF is named by them, and their
    /// copy has what only their character was told, which the party's own has not.</summary>
    [Fact]
    public async Task TheGmDownloadsOnePlayersOwnBook_NamedByTheirCharacter_WithWhatOnlyTheyWereTold()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bpp-dm-" + Guid.NewGuid();
        var playerId = "bpp-pl-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(playerId, "pl_" + playerId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl, AcceptDownloads = true });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        var invite = await E2EHelpers.ReadInviteLinkAsync(dm, campaignId);
        await using (var playerContext = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl }))
        {
            var player = await playerContext.NewPageAsync();
            await E2EHelpers.LoginAsync(player, "pl_" + playerId, password);
            await E2EHelpers.JoinAsync(player, invite);
        }

        var mira = "bpp-mira-" + Guid.NewGuid();
        await _app.SeedActorsAsync([
            new Codex.Web.TestSeedActor { Id = mira, CampaignId = campaignId, Name = "Mira", Kind = Codex.Persistence.ActorKind.PlayerCharacter, OwnerUserId = playerId },
            new Codex.Web.TestSeedActor
            {
                Id = "bpp-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Visibility = Codex.Persistence.ActorVisibility.Known,
                Biography = "Keeps the lighthouse.\n\n:::for [[actor:" + mira + "]]\nShe told Mira where the bell is buried.\n:::"
            }]);
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        await Assertions.Expect(dm.Locator("[data-testid=book-draft-chapter][data-section=cast]")).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await dm.GetByTestId("book-draft-more").ClickAsync();
        var forPlayer = dm.GetByTestId("book-draft-for-player");
        await Assertions.Expect(forPlayer).ToHaveCountAsync(1);
        await Assertions.Expect(forPlayer).ToHaveTextAsync("Mira");
        var download = await dm.RunAndWaitForDownloadAsync(() => forPlayer.ClickAsync());
        Assert.Equal("Saltmere Chronicle (Mira).pdf", download.SuggestedFilename);
        await using (var stream = await download.CreateReadStreamAsync())
        {
            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(head));
        }

        var theirs = await (await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/party/draft/en/md?player={Uri.EscapeDataString(playerId)}")).TextAsync();
        Assert.Contains("She told Mira where the bell is buried.", theirs);
        Assert.Contains("##### Only for Mira", theirs);
        var party = await (await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/party/draft/en/md")).TextAsync();
        Assert.Contains("Keeps the lighthouse.", party);
        Assert.DoesNotContain("bell", party);
        // Someone who is not a player of the campaign has no book.
        Assert.Equal(404, (await context.APIRequest.GetAsync($"/campaigns/{campaignId}/book/party/draft/en/md?player={Uri.EscapeDataString(dmId)}")).Status);
        Assert.Empty(pageErrors);
    }

    /// <summary>Fit pages from the editor (41.15): the preview says pages run on, Fit pages writes the breaks into the chapter (shown in the
    /// editor, saved, the chapter not marked edited), and a block too tall for any page is named with its chapter, before the PDF and after it.</summary>
    [Fact]
    public async Task TheGmFitsPages_TheBreaksAreWrittenIntoTheText_AndWhatCannotFitIsNamed()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bdf-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl, AcceptDownloads = true });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        var bio = string.Join("\n\n", Enumerable.Range(0, 70).Select(i => $"Line {i} of the keeper's long story, which goes on past one page and then another."));
        var wall = string.Join(" ", Enumerable.Range(0, 2600).Select(i => "PUBLIC_WALL"));
        await _app.SeedActorsAsync([
            new Codex.Web.TestSeedActor { Id = "bdf-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Biography = bio, Visibility = Codex.Persistence.ActorVisibility.Known },
            new Codex.Web.TestSeedActor { Id = "bdf-wall-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Wall", Biography = wall, Visibility = Codex.Persistence.ActorVisibility.Known }]);
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");

        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Line 69 of" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-run-on")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-overflow")).ToContainTextAsync("Cast");

        await dm.GetByTestId("book-draft-fit").ClickAsync();
        var report = dm.GetByTestId("book-draft-fit-report");
        // Fit pages starts the print browser and lays the book out there: seconds, more when the machine is busy.
        await Assertions.Expect(report).ToContainTextAsync("Page breaks in the book:", new() { Timeout = 60_000 });
        await Assertions.Expect(dm.GetByTestId("book-draft-fit-too-tall")).ToContainTextAsync("Cast");
        await Assertions.Expect(dm.GetByTestId("book-draft-fit-too-tall")).ToContainTextAsync("PUBLIC_WALL");
        // The report opens the panel on its own tab, whatever tab was open.
        await Assertions.Expect(dm.GetByTestId("book-draft-tab-problems")).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(dm.GetByTestId("book-draft-fit-too-tall")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-problems-count")).ToHaveTextAsync("1");
        await Assertions.Expect(dm.GetByTestId("book-draft-run-on")).ToBeHiddenAsync();
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        // CodeMirror draws only the lines in view: the chapter is scrolled through and read as it goes.
        await Assertions.Expect(dm.Locator(".cm-content")).ToContainTextAsync("Line 0 of");
        var lines = await dm.Locator(".cm-scroller").EvaluateAsync<string>(@"async e => {
            let seen = '';
            for (let y = 0; y <= e.scrollHeight; y += Math.max(50, e.clientHeight / 2)) {
                e.scrollTop = y;
                await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
                seen += [...e.querySelectorAll('.cm-line')].map(l => l.textContent).join('\n') + '\n';
            }
            return seen;
        }");
        Assert.Contains(@"\page {fitted}", lines);
        await Assertions.Expect(dm.Locator("[data-testid=book-draft-chapter][data-section=cast] .book-draft-badge-edited")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");

        // Run again, nothing changes; the report says so.
        await dm.GetByTestId("book-draft-fit").ClickAsync();
        await Assertions.Expect(report).ToContainTextAsync("Every page already fits", new() { Timeout = 60_000 });

        // The PDF is given, and what printed cut off is said beside it, with its chapter.
        var download = await dm.RunAndWaitForDownloadAsync(() => dm.GetByTestId("book-draft-download").ClickAsync());
        Assert.EndsWith(".pdf", download.SuggestedFilename);
        await Assertions.Expect(dm.GetByTestId("book-draft-print-overflow")).ToContainTextAsync("(Cast)");
        Assert.Empty(pageErrors);
    }

    /// <summary>
    /// AI edits (docs/BOOK_EXPORT.md 41.4): the GM selects a line, asks for it shorter, sees the cost before anything is sent, and reviews the
    /// reply change by change; the preview shows only what was accepted; what is kept is saved like typing, and a discarded reply changes nothing.
    /// The model (a local process) is sent no secret.
    /// </summary>
    [Fact]
    public async Task TheGmAsksAiToEditASelection_ReviewsTheChange_AndOnlyWhatIsAcceptedIsSaved()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bai-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var model = new BookAiPanelE2ETests.FakeModelServer();
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor
        {
            Id = "bai-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Visibility = Codex.Persistence.ActorVisibility.Known,
            Biography = "Keeps the lighthouse on the point.\n\nHer lighthouse log is kept dry.\n\n:::dm\nCANARY_AI_SECRET\n:::"
        }]);

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
        await Assertions.Expect(dm.GetByTestId("ai-provider-list")).ToContainTextAsync("Fake model", new() { Timeout = 30_000 });

        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        // The GM edition: its secret is in the chapter, and must not reach the model.
        await dm.GetByTestId("book-edition").SelectOptionAsync("dm");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-dialog")).ToBeVisibleAsync();
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Keeps the lighthouse" })).ToHaveCountAsync(1);

        // The line is selected and the GM asks for it shorter: the cost first, nothing sent.
        await dm.Locator(".cm-line", new() { HasText = "Keeps the lighthouse" }).ClickAsync();
        await dm.Keyboard.PressAsync("Home");
        await dm.Keyboard.PressAsync("Shift+End");
        await dm.GetByTestId("book-draft-ai-shorter").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-ai-confirm")).ToContainTextAsync("fake-model");
        await Assertions.Expect(dm.GetByTestId("book-draft-ai-cost")).ToContainTextAsync("USD");
        lock (model.Requests) Assert.Empty(model.Requests);
        await dm.GetByTestId("book-draft-ai-send").ClickAsync();

        // The review: one change, nothing accepted yet, so the preview still has the old words.
        var review = dm.GetByTestId("book-draft-review");
        await Assertions.Expect(review).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(dm.GetByTestId("book-draft-review-left")).ToContainTextAsync("1");
        await Assertions.Expect(dm.GetByTestId("book-draft-code")).ToBeHiddenAsync();
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Keeps the lighthouse" })).ToHaveCountAsync(1);
        lock (model.Requests)
        {
            var sent = Assert.Single(model.Requests);
            Assert.Contains("Keeps the lighthouse", sent);
            Assert.DoesNotContain("CANARY_AI_SECRET", sent);
        }

        // The review's buttons sit on the chapter they lock: switching or folding the panel below does not hide them.
        await dm.GetByTestId("book-draft-tab-problems").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-review-keep")).ToBeVisibleAsync();
        await dm.GetByTestId("book-draft-panel-toggle").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-review-discard")).ToBeVisibleAsync();

        // Accepted: the preview takes it at once, and the review is kept with what was accepted.
        await dm.GetByTestId("book-draft-review-code").GetByRole(AriaRole.Button, new() { Name = "Accept" }).ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-review-left")).ToContainTextAsync("0");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Keeps the beacon tower" })).ToHaveCountAsync(1);
        await dm.GetByTestId("book-draft-review-keep").ClickAsync();
        await Assertions.Expect(review).ToBeHiddenAsync();
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "Keeps the beacon tower" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        await Assertions.Expect(dm.Locator("[data-testid=book-draft-chapter][data-section=cast] .book-draft-badge-edited")).ToBeVisibleAsync();
        // The editor has the focus back with the cursor after what was kept, not with it selected: the next key types, it does not replace the passage.
        await dm.Keyboard.TypeAsync(" PUBLIC_AFTER_KEEP");
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "PUBLIC_AFTER_KEEP" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "Keeps the beacon tower" })).ToHaveCountAsync(1);

        // A second ask, of the whole chapter (nothing selected), in the GM's own words, discarded: nothing changes, and the editor takes typing again.
        await dm.Locator(".cm-line", new() { HasText = "Keeps the beacon tower" }).ClickAsync();
        await dm.GetByTestId("book-draft-tab-ai").ClickAsync();
        await dm.GetByTestId("book-draft-ai-ask").FillAsync("Call it a beacon tower");
        await dm.GetByTestId("book-draft-ai-go").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-ai-confirm")).ToContainTextAsync("whole chapter");
        await dm.GetByTestId("book-draft-tab-blocks").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-ai-send")).ToBeVisibleAsync();
        await dm.GetByTestId("book-draft-ai-send").ClickAsync();
        await Assertions.Expect(review).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await dm.GetByTestId("book-draft-review-discard").ClickAsync();
        await Assertions.Expect(review).ToBeHiddenAsync();
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "Her lighthouse log" })).ToHaveCountAsync(1);
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Her lighthouse log" })).ToHaveCountAsync(1);
        lock (model.Requests) Assert.All(model.Requests, r => Assert.DoesNotContain("CANARY_AI_SECRET", r));
        await dm.Locator(".cm-line", new() { HasText = "Keeps the beacon tower" }).ClickAsync();
        await dm.Keyboard.PressAsync("End");
        await dm.Keyboard.TypeAsync(" PUBLIC_TYPED");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_TYPED" })).ToHaveCountAsync(1);
        Assert.Empty(pageErrors);
    }

    /// <summary>
    /// "As the book" over the whole book (docs/BOOK_EXPORT.md 42, step 3) with a local fake model: the cost of every chapter first and nothing
    /// sent; then chapter by chapter, entry by entry (one request each), each reviewed before the next is sent; a chapter the model left as it
    /// was is not shown for review; the party edition's requests are written for the players and carry no secret.
    /// </summary>
    [Fact]
    public async Task TheGmRewritesTheWholeBookAsTheBook_PricedFirst_ReviewedChapterByChapter()
    {
        // Long enough that a diff of the whole chapter gives up on precision, as it did on the real run (one change for the chapter).
        static string Long(string who) => string.Join(' ', Enumerable.Range(0, 120).Select(i => who + "word" + i));
        const string password = "Correct-Horse-1";
        var dmId = "bshape-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var model = new BookAiPanelE2ETests.FakeModelServer();
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        await _app.SeedActorsAsync([
            new Codex.Web.TestSeedActor
            {
                Id = "bshape-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Visibility = Codex.Persistence.ActorVisibility.Known,
                Biography = "Keeps the lighthouse on the point. REWRITE_WHOLE " + Long("vera") + "\n\n:::dm\nCANARY_SHAPE_SECRET\n:::"
            },
            new Codex.Web.TestSeedActor
            {
                Id = "bshape-oren-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Oren", Visibility = Codex.Persistence.ActorVisibility.Known,
                Biography = "Rows out to the lighthouse at dusk. REWRITE_WHOLE " + Long("oren")
            }]);

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
        await Assertions.Expect(dm.GetByTestId("ai-provider-list")).ToContainTextAsync("Fake model", new() { Timeout = 30_000 });

        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-dialog")).ToBeVisibleAsync();
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Rows out to the lighthouse" })).ToHaveCountAsync(1, new() { Timeout = 30_000 });

        // The cost of the whole book first; nothing is sent.
        await dm.GetByTestId("book-draft-more").ClickAsync();
        await dm.GetByTestId("book-draft-shape-book").ClickAsync();
        var confirm = dm.GetByTestId("book-draft-shape-confirm");
        await Assertions.Expect(confirm).ToContainTextAsync("fake-model");
        await Assertions.Expect(dm.GetByTestId("book-draft-shape-cost")).ToContainTextAsync("USD");
        lock (model.Requests) Assert.Empty(model.Requests);
        await dm.GetByTestId("book-draft-shape-start").ClickAsync();

        // The cast chapter, entry by entry: its two entries changed, reviewed before anything else is sent.
        var review = dm.GetByTestId("book-draft-review");
        await Assertions.Expect(review).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(dm.GetByTestId("book-draft-shape-progress")).ToContainTextAsync("Chapter");
        // Every line rewritten, and still one change per entry: each can be accepted or rejected on its own.
        await Assertions.Expect(dm.GetByTestId("book-draft-review-left")).ToContainTextAsync("2");
        int sentForCast;
        lock (model.Requests)
        {
            sentForCast = model.Requests.Count;
            // Each entry is its own passage (the text around it is sent to read only).
            static string Last(string body) => System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? "";
            static string Passage(string r) => r[(r.IndexOf("<passage>", StringComparison.Ordinal) + 9)..r.IndexOf("</passage>", StringComparison.Ordinal)];
            var passages = model.Requests.Select(Last).Where(r => r.Contains("<passage>", StringComparison.Ordinal)).Select(Passage).ToList();
            Assert.Contains(passages, p => p.Contains("Keeps the lighthouse") && !p.Contains("Rows out"));
            Assert.Contains(passages, p => p.Contains("Rows out") && !p.Contains("Keeps the lighthouse"));
            Assert.All(model.Requests, r => Assert.DoesNotContain("CANARY_SHAPE_SECRET", r));
            Assert.All(model.Requests, r => Assert.Contains("players' edition", r));
        }

        // Oren's change rejected, Vera's accepted: only Vera's entry is rewritten.
        var code = dm.GetByTestId("book-draft-review-code");
        await code.GetByRole(AriaRole.Button, new() { Name = "Reject" }).First.ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-review-left")).ToContainTextAsync("1");
        await dm.GetByTestId("book-draft-review-all").ClickAsync();
        // The rest of the book runs on; chapters with nothing to change are not offered for review, and the run ends.
        await Assertions.Expect(dm.GetByTestId("book-draft-ai-note")).ToContainTextAsync("whole book", new() { Timeout = 60_000 });
        await Assertions.Expect(review).ToBeHiddenAsync();
        // One entry kept as it was, the other rewritten (which is which follows the chapter's order).
        var kept = await dm.Locator(".cm-content").InnerTextAsync();
        Assert.True(kept.Contains("Rows out to the lighthouse at dusk.") ^ kept.Contains("Keeps the lighthouse on the point."), kept);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        lock (model.Requests)
        {
            Assert.True(model.Requests.Count >= sentForCast);
            Assert.All(model.Requests, r => Assert.DoesNotContain("CANARY_SHAPE_SECRET", r));
        }

        Assert.Empty(pageErrors);
    }

    /// <summary>
    /// "Bring up to date" (docs/BOOK_EXPORT.md 41.18) in a real browser with a local fake model: the GM edits two entries, the campaign then
    /// changes one of them and adds another; the merge says what it will do and cost, sends only the entry both changed (never its secret), opens
    /// the review, and keeping it saves the merge with its new base: the notice goes, the editor takes typing, and Undo brings the notice back.
    /// </summary>
    [Fact]
    public async Task TheGmBringsAnEditedChapterUpToDate_OnlyTheConflictGoesToTheModel_AndUndoBringsTheNoticeBack()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bmg-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var model = new BookAiPanelE2ETests.FakeModelServer();
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        var veraId = "bmg-vera-" + Guid.NewGuid();
        Codex.Web.TestSeedActor Vera(string bio) => new()
        {
            Id = veraId, CampaignId = campaignId, Name = "Vera", Visibility = Codex.Persistence.ActorVisibility.Known,
            Biography = bio + "\n\nHer log is kept dry.\n\n:::dm\nCANARY_MERGE_SECRET\n:::"
        };
        await _app.SeedActorsAsync([
            Vera("Keeps the lighthouse on the point."),
            new Codex.Web.TestSeedActor { Id = "bmg-tom-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Tomas", Visibility = Codex.Persistence.ActorVisibility.Known, Biography = "Tom mends nets." }
        ]);

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
        await Assertions.Expect(dm.GetByTestId("ai-provider-list")).ToContainTextAsync("Fake model", new() { Timeout = 30_000 });

        // The GM edits Vera's line and Tom's.
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await dm.GetByTestId("book-edition").SelectOptionAsync("dm");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-dialog")).ToBeVisibleAsync();
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        foreach (var (line, words) in new[] { ("Keeps the lighthouse", " PUBLIC_GM_WORDS"), ("Tom mends nets", " PUBLIC_GM_TOM") })
        {
            await dm.Locator(".cm-line", new() { HasText = line }).ClickAsync();
            await dm.Keyboard.PressAsync("End");
            await dm.Keyboard.TypeAsync(words);
        }

        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved", new() { Timeout = 15_000 });
        await dm.GetByTestId("book-draft-close").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-dialog")).ToBeHiddenAsync();

        // The campaign changes Vera's biography and gains Ann.
        await _app.SeedActorsAsync([
            Vera("Keeps the lighthouse on the far point."),
            new Codex.Web.TestSeedActor { Id = "bmg-ann-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Ann", Visibility = Codex.Persistence.ActorVisibility.Known, Biography = "PUBLIC_ANN sells fish." }
        ]);
        await dm.GetByTestId("book-draft-open").ClickAsync();
        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-stale")).ToBeVisibleAsync();

        // The pre-flight: what the merge does, and that one entry goes to the model at a price; nothing sent yet.
        await dm.GetByTestId("book-draft-merge").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-merge-conflicts")).ToContainTextAsync("1");
        await Assertions.Expect(dm.GetByTestId("book-draft-merge-confirm")).ToContainTextAsync("fake-model");
        await Assertions.Expect(dm.GetByTestId("book-draft-merge-cost")).ToContainTextAsync("USD");
        lock (model.Requests) Assert.Empty(model.Requests);
        await dm.GetByTestId("book-draft-merge-ai").ClickAsync();

        var review = dm.GetByTestId("book-draft-review");
        await Assertions.Expect(review).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(review).ToContainTextAsync("campaign");
        lock (model.Requests)
        {
            var sent = Assert.Single(model.Requests);
            Assert.Contains("PUBLIC_GM_WORDS", sent);
            Assert.DoesNotContain("CANARY_MERGE_SECRET", sent);
            // Only the conflict: Tom's entry (the GM's alone) and Ann's (the campaign's alone) are merged by the code.
            Assert.DoesNotContain("PUBLIC_GM_TOM", sent);
            Assert.DoesNotContain("PUBLIC_ANN", sent);
        }

        await dm.GetByTestId("book-draft-review-all").ClickAsync();
        await Assertions.Expect(review).ToBeHiddenAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-stale")).ToBeHiddenAsync();
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "on the far point. PUBLIC_GM_WORDS" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "PUBLIC_GM_TOM" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "PUBLIC_ANN" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "CANARY_MERGE_SECRET" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-ai-note")).ToContainTextAsync("up to date");
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_ANN" })).ToHaveCountAsync(1);

        // The editor takes typing again, and its own undo history does not hold the merge.
        await dm.Locator(".cm-line", new() { HasText = "PUBLIC_GM_TOM" }).ClickAsync();
        await dm.Keyboard.PressAsync("End");
        await dm.Keyboard.TypeAsync(" PUBLIC_TYPED");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_TYPED" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved", new() { Timeout = 15_000 });

        // Undo takes the merge back with its base: the notice returns.
        await dm.GetByTestId("book-draft-undo").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-stale")).ToBeVisibleAsync();
        await Assertions.Expect(dm.Locator(".cm-line", new() { HasText = "PUBLIC_ANN" })).ToHaveCountAsync(0);
        Assert.Empty(pageErrors);
    }

    /// <summary>
    /// The editor's layout (docs/BOOK_EXPORT.md 41.17): the chapters and the preview fold to a strip and open again, what is open is remembered
    /// when the editor is opened again, a folded preview is still drawn (the editor still measures it), the panel's tabs move with the arrow
    /// keys, and Start over is behind More.
    /// </summary>
    [Fact]
    public async Task TheEditorsPanesFoldAndAreRemembered_TheFoldedPreviewStillDraws_AndThePanelTabsTakeTheArrowKeys()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bly-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        var dialog = dm.GetByTestId("book-draft-dialog");
        await Assertions.Expect(dialog).ToBeVisibleAsync();

        // Both side panes fold to a strip; the chapter list is gone, the Markdown stays.
        await dm.GetByTestId("book-draft-pane-chapters-toggle").ClickAsync();
        await dm.GetByTestId("book-draft-pane-preview-toggle").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-chapters")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.GetByTestId("book-draft-pane-chapters-toggle")).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(dm.GetByTestId("book-draft-pane-preview-toggle")).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(dm.GetByTestId("book-draft-code")).ToBeVisibleAsync();

        // Typing still draws the folded preview: it is laid out off the screen, so the editor can measure it.
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+End");
        await dm.Keyboard.TypeAsync("\nPUBLIC_FOLDED_LINE\n");
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_FOLDED_LINE" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");

        // The panel's tabs: the arrow keys move between them, and the open one shows its content.
        await dm.GetByTestId("book-draft-tab-ai").FocusAsync();
        await dm.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(dm.GetByTestId("book-draft-tab-problems")).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(dm.GetByTestId("book-draft-tab-problems")).ToBeFocusedAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-fit-report")).ToBeVisibleAsync();
        await dm.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(dm.GetByTestId("book-draft-tab-blocks")).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(dm.GetByTestId("book-draft-ai")).ToBeHiddenAsync();

        // Opened again after the page is loaded again, the editor is as it was left: the layout is kept in this browser.
        await dm.GetByTestId("book-draft-close").ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        await dm.ReloadAsync();
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-pane-chapters-toggle")).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(dm.GetByTestId("book-draft-tab-blocks")).ToHaveAttributeAsync("aria-selected", "true");
        await dm.GetByTestId("book-draft-pane-chapters-toggle").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-chapters")).ToBeVisibleAsync();

        // Start over is behind More, and still asks first.
        await Assertions.Expect(dm.GetByTestId("book-draft-start-over")).ToHaveCountAsync(0);
        await dm.GetByTestId("book-draft-more").ClickAsync();
        await dm.GetByTestId("book-draft-start-over").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-start-over-yes")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-start-over")).ToHaveCountAsync(0);
        Assert.Empty(pageErrors);
    }

    [Fact]
    public async Task ATabThatOnlyLooksAtAChapter_NeverSavesIt_SoAnotherTabsEditIsNotAConflict()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bdt-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var a = await context.NewPageAsync();
        await E2EHelpers.LoginAsync(a, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(a, "Two Tabs");
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor { Id = "bdt-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Biography = "Keeps the lighthouse.", Visibility = Codex.Persistence.ActorVisibility.Known }]);

        async Task OpenAsync(IPage page)
        {
            await E2EHelpers.GoToSectionAsync(page, campaignId, "settings");
            await page.GetByTestId("book-edition").SelectOptionAsync("party");
            await page.GetByTestId("book-tab-draft").ClickAsync();
            await page.GetByTestId("book-draft-open").ClickAsync();
            await Assertions.Expect(page.FrameLocator("[data-testid=book-draft-preview]").Locator(".page").First).ToBeVisibleAsync();
        }

        await OpenAsync(a);
        var b = await context.NewPageAsync();
        await OpenAsync(b);

        // Tab B edits Cast and saves it.
        await b.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await b.Locator(".cm-content").ClickAsync();
        await b.Keyboard.PressAsync("ControlOrMeta+End");
        await b.Keyboard.TypeAsync("\nPUBLIC_TAB_B\n");
        await Assertions.Expect(b.Locator("[data-testid=book-draft-chapter][data-section=cast] .book-draft-badge-edited")).ToBeVisibleAsync();
        await Assertions.Expect(b.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");

        // Tab A only looks: Cast, the stylesheet, the front pages, then closes. Nothing it showed was typed, so nothing is saved or refused.
        await a.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await a.GetByTestId("book-draft-css").ClickAsync();
        await a.Locator("[data-testid=book-draft-chapter][data-section=front]").ClickAsync();
        await a.WaitForTimeoutAsync(1500);
        await Assertions.Expect(a.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        await a.GetByTestId("book-draft-close").ClickAsync();
        await Assertions.Expect(a.GetByTestId("book-draft-dialog")).Not.ToBeVisibleAsync();

        // B's line is still the chapter's.
        await a.GetByTestId("book-tab-draft").ClickAsync();
        await a.GetByTestId("book-draft-open").ClickAsync();
        await a.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await Assertions.Expect(a.Locator(".cm-content")).ToContainTextAsync("PUBLIC_TAB_B");
    }

    /// <summary>
    /// The theme wizard (docs/BOOK_EXPORT.md 41.22) on a Hebrew party draft: a pasted theme whose faces have Latin letters only is measured, a
    /// stand-in is proposed for each of its families for Hebrew, the sample pages draw under it, and once kept it is the draft's theme, the
    /// preview carries its stand-ins, it prints, and the wizard lists it. The pasted sheet is far over a message's 32 KB (its faces are written
    /// in), as a real one often is.
    /// </summary>
    [Fact]
    public async Task ThePastedThemesLatinFaces_GetHebrewStandIns_AndTheThemeIsKeptAndPrints()
    {
        const string password = "Correct-Horse-1";
        var dmId = "btw-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var pageErrors = new List<string>();
        dm.PageError += (_, e) => pageErrors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        await _app.SeedActorsAsync([new Codex.Web.TestSeedActor
        {
            Id = "btw-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "ורה הולוויי", Visibility = Codex.Persistence.ActorVisibility.Known,
            Biography = "ורה שומרת על המגדלור של גרייווטר."
        }]);
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await dm.GetByTestId("book-language").SelectOptionAsync("he");
        await dm.GetByTestId("book-edition").SelectOptionAsync("party");
        await dm.GetByTestId("book-tab-draft").ClickAsync();
        await dm.GetByTestId("book-draft-open").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-dialog")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-homebrewery-credit").Locator("a[href='https://homebrewery.naturalcrit.com/']")).ToBeVisibleAsync();

        await dm.GetByTestId("book-draft-more").ClickAsync();
        await dm.GetByTestId("book-draft-theme-wizard").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-theme-wizard")).ToBeVisibleAsync();
        await dm.GetByTestId("book-theme-name").FillAsync("Saltmere vellum");
        // The theme's faces as data, read from the app's own bundled files (no address outside the test is fetched).
        await dm.EvaluateAsync(@"async () => {
            const b64 = async u => { const a = new Uint8Array(await (await fetch(u)).arrayBuffer()); let s = ''; for (let i = 0; i < a.length; i += 8192) s += String.fromCharCode(...a.subarray(i, i + 8192)); return btoa(s); };
            const text = await b64('/fonts/im-fell-english/im-fell-english-latin-400-normal.woff2'), titles = await b64('/fonts/pirata-one/pirata-one-latin-400-normal.woff2');
            document.querySelector('[data-testid=book-theme-css]').value =
                `@font-face { font-family: 'Saltmere Text'; src: url(data:font/woff2;base64,${text}) format('woff2'); }\n` +
                `@font-face { font-family: 'Saltmere Titles'; src: url(data:font/woff2;base64,${titles}) format('woff2'); }\n` +
                `.page { font-family: 'Saltmere Text', serif; } .page :is(h1, h2, h3) { font-family: 'Saltmere Titles', serif; }`; }");
        await dm.GetByTestId("book-theme-homebrewery").UncheckAsync();
        await dm.GetByTestId("book-theme-next").ClickAsync();

        var hebrew = dm.Locator("[data-testid=book-theme-language][data-language=he]");
        await Assertions.Expect(hebrew.Locator("[data-testid=book-theme-standin]")).ToHaveCountAsync(2);
        await Assertions.Expect(hebrew.Locator("[data-testid=book-theme-standin][data-family='Saltmere Text']")).ToHaveValueAsync("he-david");
        await Assertions.Expect(hebrew.Locator("[data-testid=book-theme-standin][data-family='Saltmere Titles']")).ToHaveValueAsync("he-frank-ruhl-black");
        await Assertions.Expect(hebrew.GetByTestId("book-theme-language-on")).ToBeCheckedAsync();
        await Assertions.Expect(dm.Locator("[data-testid=book-theme-language][data-language=en] [data-testid=book-theme-covered]")).ToBeVisibleAsync();

        await dm.GetByTestId("book-theme-preview-next").ClickAsync();
        var sample = dm.FrameLocator("[data-testid=book-theme-preview]");
        await Assertions.Expect(sample.Locator(".page", new() { HasText = "ורה הולוויי" }).First).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-theme-letters")).ToHaveCountAsync(0);
        await dm.GetByTestId("book-theme-save").ClickAsync();

        await Assertions.Expect(dm.GetByTestId("book-theme-wizard")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.GetByTestId("book-draft-theme")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^custom:[0-9a-f]{32}$"));
        await Assertions.Expect(dm.GetByTestId("book-draft-homebrewery")).Not.ToBeCheckedAsync();
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator("style#theme")).ToHaveCountAsync(1);
        Assert.Contains("Stand-ins for this book", await dm.FrameLocator("[data-testid=book-draft-preview]").Locator("style#theme").TextContentAsync() ?? "");
        var pdf = await context.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/party/draft/he/pdf");
        Assert.Equal(200, pdf.Status);

        await dm.GetByTestId("book-draft-more").ClickAsync();
        await dm.GetByTestId("book-draft-theme-wizard").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-theme-item")).ToContainTextAsync("Saltmere vellum");
        Assert.Empty(pageErrors);
    }
}
