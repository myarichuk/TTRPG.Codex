using System.Net;
using System.Text;
using Codex.Web.Services;

namespace Codex.Tests;

/// <summary>OpenRouter's model list as the settings page gets it: what is kept, what is left out, the prices in the units the fields use,
/// and what happens when OpenRouter does not answer.</summary>
public sealed class OpenRouterModelsTests
{
    private const string TextList = """
    { "data": [
      { "id": "openai/gpt-4o-mini", "name": "OpenAI: GPT-4o mini", "context_length": 128000,
        "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "0.00000015", "completion": "0.0000006", "input_cache_read": "0.000000075" },
        "supported_parameters": ["temperature", "tools", "tool_choice"] },
      { "id": "acme/no-tools", "name": "Acme: Plain", "context_length": 1048576,
        "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "0.000002", "completion": "0.00001" },
        "supported_parameters": ["temperature"] },
      { "id": "acme/gratis:free", "name": "Acme: Gratis (free)", "context_length": 32000,
        "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "0", "completion": "0" }, "supported_parameters": ["tools"] },
      { "id": "openrouter/auto", "name": "Auto Router", "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "-1", "completion": "-1" }, "supported_parameters": ["tools"] },
      { "id": "openrouter/free", "name": "Free Models Router", "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "0", "completion": "0" }, "supported_parameters": ["tools"] },
      { "id": "nvidia/switchyard", "name": "Switchyard", "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "-1", "completion": "-1" }, "supported_parameters": ["tools"] },
      { "id": "acme/talker", "name": "Acme: Talker", "architecture": { "output_modalities": ["audio", "text"] },
        "pricing": { "prompt": "0.000001", "completion": "0.000002" } },
      { "id": "acme/mover", "name": "Acme: Mover", "architecture": { "output_modalities": ["text", "video"] },
        "pricing": { "prompt": "0.000001", "completion": "0.000002" } },
      { "id": "acme/no-architecture", "name": "Unknown", "pricing": { "prompt": "0.000001", "completion": "0.000002" } },
      { "id": "openai/gpt-4o-mini", "name": "Repeated", "architecture": { "output_modalities": ["text"] }, "pricing": { "prompt": "1", "completion": "1" } },
      { "id": "acme/odd-price", "name": "Acme: Odd", "architecture": { "output_modalities": ["text"] },
        "pricing": { "prompt": "cheap", "completion": "0.000003" } },
      { "name": "No id", "architecture": { "output_modalities": ["text"] } },
      "not an object"
    ] }
    """;

    private const string ImageList = """
    { "data": [
      { "id": "acme/draw-flat", "name": "Acme: Draw", "architecture": { "output_modalities": ["image"] },
        "pricing": { "image": "0.04", "image_output": "0.00003" } },
      { "id": "acme/draw-tokens", "name": "Acme: Tokens", "architecture": { "output_modalities": ["image", "text"] },
        "pricing": { "image_output": "0.00003", "prompt": "0.000001" } },
      { "id": "openrouter/router-art", "name": "Router", "architecture": { "output_modalities": ["image"] }, "pricing": { "image": "0.01" } },
      { "id": "acme/words-only", "name": "Words", "architecture": { "output_modalities": ["text"] }, "pricing": { "prompt": "0.000001" } }
    ] }
    """;

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return await respond(request);
        }

        public static Handler Json(string text, string image = ImageList) => new(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Query.Contains("output_modalities=image") ? image : text, Encoding.UTF8, "application/json")
        }));

        public static Handler Failing(HttpStatusCode status = HttpStatusCode.ServiceUnavailable) => new(_ => Task.FromResult(new HttpResponseMessage(status)));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static OpenRouterModels Service(Handler handler, Clock? clock = null) =>
        new(new Factory(handler), clock ?? new Clock(DateTimeOffset.Parse("2026-11-16T12:00:00Z")));

    [Fact]
    public async Task TextList_KeepsRealModels_AndLeavesOutRoutersAndTheOnesThatAreNotChat()
    {
        var models = await Service(Handler.Json(TextList)).ListAsync(imageModels: false);

        // The routers (openrouter/auto, openrouter/free), a negative-priced one outside that namespace, anything that also speaks or makes video,
        // a model with no architecture, the repeat and the entries that are not models are all gone.
        Assert.Equal(["openai/gpt-4o-mini", "acme/no-tools", "acme/gratis:free", "acme/odd-price"], models!.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task TextList_PricesAreDollarsPerMillionTokens_Exactly()
    {
        var models = await Service(Handler.Json(TextList)).ListAsync(false);

        var mini = models!.Single(m => m.Id == "openai/gpt-4o-mini");
        Assert.Equal("OpenAI: GPT-4o mini", mini.Name);
        Assert.Equal(128000, mini.ContextLength);
        Assert.Equal(0.15m, mini.InputPerMTok);
        Assert.Equal(0.6m, mini.OutputPerMTok);
        Assert.Equal(0.075m, mini.CachedInputPerMTok);
        Assert.Null(mini.PerImage);

        var plain = models!.Single(m => m.Id == "acme/no-tools");
        Assert.Equal(2m, plain.InputPerMTok);
        Assert.Equal(10m, plain.OutputPerMTok);
        Assert.Null(plain.CachedInputPerMTok);
    }

    [Fact]
    public async Task TextList_AFreeModelCostsZero_AndAPriceThatIsNotANumberIsMissingNotZero()
    {
        var models = await Service(Handler.Json(TextList)).ListAsync(false);

        var free = models!.Single(m => m.Id == "acme/gratis:free");
        Assert.Equal(0m, free.InputPerMTok);
        Assert.Equal(0m, free.OutputPerMTok);

        var odd = models!.Single(m => m.Id == "acme/odd-price");
        Assert.Null(odd.InputPerMTok);
        Assert.Equal(3m, odd.OutputPerMTok);
    }

    [Fact]
    public async Task TextList_MarksTheModelsThatCanCallTools()
    {
        var models = await Service(Handler.Json(TextList)).ListAsync(false);

        Assert.True(models!.Single(m => m.Id == "openai/gpt-4o-mini").Tools);
        Assert.False(models!.Single(m => m.Id == "acme/no-tools").Tools);
    }

    [Fact]
    public async Task ImageList_AsksForImageModels_AndOnlyAPerPictureFigureBecomesAPrice()
    {
        var handler = Handler.Json(TextList);
        var models = await Service(handler).ListAsync(imageModels: true);

        Assert.Contains("output_modalities=image", handler.Requests.Single().RequestUri!.Query);
        Assert.Equal(["acme/draw-flat", "acme/draw-tokens"], models!.Select(m => m.Id).ToArray());
        Assert.Equal(0.04m, models.Single(m => m.Id == "acme/draw-flat").PerImage);
        // Priced per token of picture, which is not the price of a picture: better none than a wrong one.
        Assert.Null(models.Single(m => m.Id == "acme/draw-tokens").PerImage);
        Assert.All(models, m => Assert.Null(m.InputPerMTok));
    }

    [Fact]
    public async Task Request_GoesToOpenRouter_WithNoCredentials()
    {
        var handler = Handler.Json(TextList);
        await Service(handler).ListAsync(false);

        var request = handler.Requests.Single();
        Assert.Equal("openrouter.ai", request.RequestUri!.Host);
        Assert.Equal("/api/v1/models", request.RequestUri.AbsolutePath);
        // The list is public: nobody's key has any business in this request.
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task List_IsKeptForAnHour_ThenAskedForAgain()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-16T12:00:00Z"));
        var handler = Handler.Json(TextList);
        var service = Service(handler, clock);

        await service.ListAsync(false);
        clock.Now += TimeSpan.FromMinutes(59);
        await service.ListAsync(false);
        Assert.Single(handler.Requests);

        clock.Now += TimeSpan.FromMinutes(2);
        await service.ListAsync(false);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TextAndImageLists_AreKeptApart()
    {
        var handler = Handler.Json(TextList);
        var service = Service(handler);

        var text = await service.ListAsync(false);
        var image = await service.ListAsync(true);
        await service.ListAsync(false);
        await service.ListAsync(true);

        Assert.Equal(2, handler.Requests.Count);
        Assert.NotEqual(text!.Select(m => m.Id), image!.Select(m => m.Id));
    }

    [Fact]
    public async Task WhenOpenRouterCannotBeReached_ThereIsNoList_AndItIsNotAskedAgainAtOnce()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-16T12:00:00Z"));
        var handler = Handler.Failing();
        var service = Service(handler, clock);

        Assert.Null(await service.ListAsync(false));
        Assert.Null(await service.ListAsync(false));
        Assert.Single(handler.Requests);

        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Null(await service.ListAsync(false));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AListThatStopsAnswering_IsKeptStaleRatherThanLost()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-16T12:00:00Z"));
        var up = true;
        var handler = new Handler(_ => Task.FromResult(up
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TextList, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var service = Service(handler, clock);

        var first = await service.ListAsync(false);
        up = false;
        clock.Now += TimeSpan.FromHours(2);
        var later = await service.ListAsync(false);

        Assert.NotNull(first);
        Assert.Equal(first!.Select(m => m.Id), later!.Select(m => m.Id));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "data": "nope" }""")]
    [InlineData("""{ "models": [] }""")]
    public async Task AReplyThatIsNotAModelList_IsNoList(string body)
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

        Assert.Null(await Service(handler).ListAsync(false));
    }

    [Fact]
    public async Task TwoPagesAskingAtOnce_MakeOneRequest()
    {
        var release = new TaskCompletionSource();
        var handler = new Handler(async _ =>
        {
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TextList, Encoding.UTF8, "application/json") };
        });
        var service = Service(handler);

        var one = service.ListAsync(false);
        var two = service.ListAsync(false);
        release.SetResult();
        await Task.WhenAll(one, two);

        Assert.Single(handler.Requests);
        Assert.Equal(one.Result!.Count, two.Result!.Count);
    }

    [Fact]
    public async Task ACancelledCaller_IsNotTakenForAFailure()
    {
        var handler = new Handler(async request =>
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(50));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(handler).ListAsync(false, cts.Token));
    }
}
