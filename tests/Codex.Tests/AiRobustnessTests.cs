using Codex.Core;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
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
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance, new ConfigurationBuilder().Build(), new ServerAiUsage(new ConfigurationBuilder().Build(), NullLogger<ServerAiUsage>.Instance));
        var provider = new AiProviderConfig { Id = "p", Name = "mine", Kind = "openai", BaseUrl = "https://api.example.test/v1", ModelId = "m1", ProtectedApiKey = service.Protect("users/1", "key-1") };
        var user = new UserDocument { Id = "users/1", AiProviders = { provider } };

        // The client is what remembers that the model refuses a temperature, so every call has to reach the same one.
        var first = service.ClientFor(user);
        Assert.NotNull(first);
        Assert.Same(first, service.ClientFor(user));

        provider.ProtectedApiKey = service.Protect("users/1", "key-2");
        var rekeyed = service.ClientFor(user);
        Assert.NotSame(first, rekeyed);

        provider.ModelId = "m2";
        Assert.NotSame(rekeyed, service.ClientFor(user));
    }

    [Fact]
    public void AStoredKey_IsReadableOnlyForTheUserItWasStoredFor()
    {
        var service = new AiProviderService(new EphemeralDataProtectionProvider(), Substitute.For<IUserRepository>(), new AIConfiguration(), new AIImageConfiguration(),
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance, new ConfigurationBuilder().Build(), new ServerAiUsage(new ConfigurationBuilder().Build(), NullLogger<ServerAiUsage>.Instance));
        var blob = service.Protect("users/1", "key-1");

        Assert.Equal("key-1", service.Unprotect("users/1", blob));
        Assert.False(service.NeedsReentry("users/1", blob));

        // Copied onto another account, the blob is no key at all, and the settings page asks for one.
        Assert.Equal(string.Empty, service.Unprotect("users/2", blob));
        Assert.True(service.NeedsReentry("users/2", blob));
        Assert.False(service.NeedsReentry("users/2", string.Empty));
    }

    [Fact]
    public async Task DescribingAPlaceForPlayers_NeverShowsTheModelTheDmBlocks_AndKeepsThemOnTheReply()
    {
        const string current = "A lighthouse.\n:::dm\nThe keeper is the cult leader.\n:::";
        string? sent = null;

        var reply = await AiFeatures.WithSecretsAside(current, AiAudience.Players, text =>
        {
            sent = text;
            return Task.FromResult("A tall, salt-worn lighthouse.");
        });

        Assert.Equal("A lighthouse.", sent);
        Assert.StartsWith("A tall, salt-worn lighthouse.", reply);
        Assert.Single(SecretText.SecretBlocks(reply), "The keeper is the cult leader.");

        // The DM's own text (the homebrew wizard's DM audience) goes as it is.
        Assert.Equal(current, await AiFeatures.WithSecretsAside(current, AiAudience.Dm, Task.FromResult));
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

    [Fact]
    public async Task GearPickedWithTheProposal_IsStoredOnTheSheetAtApply_WithoutAModel_AndAnItemGoneSinceIsLeftOut()
    {
        var service = new AiProviderService(new EphemeralDataProtectionProvider(), Substitute.For<IUserRepository>(), new AIConfiguration(), new AIImageConfiguration(),
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance, new ConfigurationBuilder().Build(), new ServerAiUsage(new ConfigurationBuilder().Build(), NullLogger<ServerAiUsage>.Instance));
        var ai = new AiFeatures(service, Substitute.For<IContentRegistry>(), Substitute.For<ISystemCatalog>());
        var system = new SystemDefinition { SheetRules = new SheetRules { Inventory = new InventoryRules() } };
        var gear = new List<DraftGear>
        {
            new() { Id = "hammer", Name = "Hammer", Quantity = 1, Equipped = true },
            new() { Id = "rope", Name = "Rope", Quantity = 2 },
            new() { Id = "gone", Name = "Lost Lantern" }
        };
        Task<IReadOnlyList<ContentEntry>> Items(string type) => Task.FromResult<IReadOnlyList<ContentEntry>>(type == "equipment"
            ? new[] { new ContentEntry { Type = "equipment", Id = "hammer", Name = "Hammer" }, new ContentEntry { Type = "equipment", Id = "rope", Name = "Rope" } }
            : Array.Empty<ContentEntry>());

        var stored = await ai.StoredGearAsync(gear, system, Items);

        Assert.Equal(new object[] { "hammer|equipped:1", "rope|qty:2" }, stored!);
        Assert.Null(await ai.StoredGearAsync(new List<DraftGear>(), system, Items));
        Assert.Null(await ai.StoredGearAsync(gear, new SystemDefinition(), Items));
    }

    [Fact]
    public void AnthropicSayingThePromptIsTooLong_IsTooLong()
    {
        var tooLong = new Anthropic.Exceptions.AnthropicBadRequestException(new HttpRequestException("Bad Request", null, System.Net.HttpStatusCode.BadRequest))
        {
            StatusCode = System.Net.HttpStatusCode.BadRequest,
            ResponseBody = """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 210000 tokens > 200000 maximum"}}"""
        };

        Assert.True(WorldChat.TooLong(tooLong));
        Assert.False(WorldChat.TooLong(new HttpRequestException("busy", null, System.Net.HttpStatusCode.ServiceUnavailable)));
    }

    // R14: read from what the error says, not from its stack trace, where a frame named *Tool* and "NotSupported" would match.
    [Fact]
    public void OnlyAnErrorSayingSo_MeansTheModelCannotUseTools()
    {
        Assert.True(WorldChat.SaysNoTools(new HttpRequestException("registry.ollama.ai/library/llama2 does not support tools")));

        Exception ours;
        try { ToolStepThatHasABug(); throw new InvalidOperationException(); }
        catch (NotSupportedException ex) { ours = ex; }
        Assert.Contains("ToolStepThatHasABug", ours.ToString());
        Assert.False(WorldChat.SaysNoTools(ours));
    }

    private static void ToolStepThatHasABug() => throw new NotSupportedException();
}
