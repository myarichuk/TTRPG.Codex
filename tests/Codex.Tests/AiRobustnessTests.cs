using Codex.Core.AI;
using Codex.Persistence;
using Codex.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

public class AiRobustnessTests
{
    [Fact]
    public async Task AGearPickThatTimesOut_LeavesTheCharacterWithoutGear_ButAStopStillStops()
    {
        // A provider's timeout reaches the caller as a cancellation the caller did not ask for.
        var picked = await AiFeatures.GearOrNoneAsync(() => throw new TaskCanceledException("The request timed out."), CancellationToken.None);
        Assert.Empty(picked);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AiFeatures.GearOrNoneAsync(() => throw new OperationCanceledException(stopped.Token), stopped.Token));
    }

    [Fact]
    public void OneProviderSetup_KeepsOneClient_AndAChangedKeyOrModelGetsItsOwn()
    {
        var service = new AiProviderService(new EphemeralDataProtectionProvider(), Substitute.For<IUserRepository>(), new AIConfiguration(), new AIImageConfiguration(),
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance, new ConfigurationBuilder().Build());
        var provider = new AiProviderConfig { Id = "p", Name = "mine", Kind = "openai", BaseUrl = "https://api.example.test/v1", ModelId = "m1", ProtectedApiKey = service.Protect("key-1") };
        var user = new UserDocument { Id = "users/1", AiProviders = { provider } };

        // The client is what remembers that the model refuses a temperature, so every call has to reach the same one.
        var first = service.ClientFor(user);
        Assert.NotNull(first);
        Assert.Same(first, service.ClientFor(user));

        provider.ProtectedApiKey = service.Protect("key-2");
        var rekeyed = service.ClientFor(user);
        Assert.NotSame(first, rekeyed);

        provider.ModelId = "m2";
        Assert.NotSame(rekeyed, service.ClientFor(user));
    }

    [Theory]
    [InlineData("HTTP 400 (invalid_request_error: )\r\n\r\nUnsupported parameter: 'max_tokens'.", "Unsupported parameter: 'max_tokens'. (HTTP 400)")]
    [InlineData("HTTP 400 (invalid_request_error: )", "HTTP 400 (invalid_request_error: )")]
    [InlineData("Something broke\nat a stack frame", "Something broke")]
    public void AnOpenAiError_LeadsWithTheProvidersReason(string message, string expected) =>
        Assert.Equal(expected, AiFailure.From(new System.ClientModel.ClientResultException(message)).Detail);

    [Fact]
    public void A408_IsATimeout() =>
        Assert.Equal(AiFailureKind.Timeout, AiFailure.From(new HttpRequestException("Request Timeout", null, System.Net.HttpStatusCode.RequestTimeout)).Kind);
}
