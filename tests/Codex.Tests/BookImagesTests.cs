using System.Net;
using Codex.Core.AI;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Book;
using Codex.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>
/// Book pictures on the server's image key (docs/BOOK_EXPORT.md 28.3, step 6): a run sets its pictures aside up front, all or nothing; each
/// picture it draws spends one of those and is never counted again; it cannot draw more than it was opened for; and what it did not draw
/// goes back when it ends.
/// </summary>
public class BookImagesTests
{
    // A 1x1 PNG.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private sealed class FakeImagesApi : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"created\":1,\"data\":[{\"b64_json\":\"" + Png + "\"}]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static (AiBookImages Images, ServerAiUsage Usage, FakeImagesApi Api) Server(int limit = 3, string? price = null)
    {
        var settings = new Dictionary<string, string?> { ["AIImage:DailyLimitPerUser"] = limit.ToString() };
        if (price != null) { settings["AIImage:PricePerImage"] = price; settings["AIImage:PriceLabel"] = "USD"; }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var usage = new ServerAiUsage(configuration, NullLogger<ServerAiUsage>.Instance);
        var api = new FakeImagesApi();
        var image = new AIImageConfiguration { Provider = "custom", Endpoint = "https://images.example.test/v1", ModelId = "picture-model", Http = new HttpClient(api) };
        var users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("users/1").Returns(new UserDocument { Id = "users/1" });
        var providers = new AiProviderService(new EphemeralDataProtectionProvider(), users, new AIConfiguration(), image,
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance, configuration, usage);
        return (new AiBookImages(providers, users, usage), usage, api);
    }

    [Fact]
    public async Task ARunCountsEachPictureOnce_DrawsNoMoreThanItWasOpenedFor_AndGivesBackTheRest()
    {
        var (images, usage, api) = Server(limit: 3);

        using (var run = await images.OpenAsync("users/1", 2))
        {
            Assert.Equal(2, usage.UsedToday("users/1", ServerAiUsage.Kind.Image));
            await run.DrawAsync("a lighthouse", ArtShape.Wide, CancellationToken.None);
            await run.DrawAsync("a harbour", ArtShape.Tall, CancellationToken.None);
            Assert.Equal(2, usage.UsedToday("users/1", ServerAiUsage.Kind.Image));
            Assert.Equal(2, api.Calls);

            await Assert.ThrowsAsync<InvalidOperationException>(() => run.DrawAsync("one too many", ArtShape.Wide, CancellationToken.None));
            Assert.Equal(2, api.Calls);
        }

        Assert.Equal(2, usage.UsedToday("users/1", ServerAiUsage.Kind.Image));

        using (var run = await images.OpenAsync("users/1", 1))
        {
            Assert.Equal(3, usage.UsedToday("users/1", ServerAiUsage.Kind.Image));
        }

        // Nothing drawn: the picture set aside went back.
        Assert.Equal(2, usage.UsedToday("users/1", ServerAiUsage.Kind.Image));
    }

    [Fact]
    public async Task ARunThatDoesNotFitTheAllowance_IsRefusedBeforeAnythingIsDrawnOrTaken()
    {
        var (images, usage, api) = Server(limit: 3);

        var refused = await Assert.ThrowsAsync<LocalizedException>(() => images.OpenAsync("users/1", 4));

        Assert.Equal("Service.Ai.ServerAllowanceSpent", refused.Key);
        Assert.Equal(0, usage.UsedToday("users/1", ServerAiUsage.Kind.Image));
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task TheTrayIsToldThePricePerPictureAndWhatIsLeftToday_OnlyWhenTheyAreKnown()
    {
        var (priced, _, _) = Server(limit: 5, price: "0.04");
        var info = await priced.InfoAsync("users/1");
        Assert.NotNull(info);
        Assert.Equal(new AiImagePrice(0.04m, "USD"), info.Price);
        Assert.Equal((0, 5), info.ServerUsedToday);

        var (unpriced, _, _) = Server(limit: 5);
        Assert.Null((await unpriced.InfoAsync("users/1"))!.Price);
    }
}
