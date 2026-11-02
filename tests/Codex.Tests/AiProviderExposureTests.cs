using Codex.Core.AI;
using Codex.Persistence;
using Codex.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

public class AiProviderExposureTests
{
    private static AiProviderService Service(bool exposed, ServerAiUsage? usage = null) => new(
        new EphemeralDataProtectionProvider(),
        Substitute.For<IUserRepository>(),
        new AIConfiguration { Provider = "openrouter", ApiKey = "server-key", ModelId = "m" },
        new AIImageConfiguration(),
        Substitute.For<IHttpClientFactory>(),
        NullLogger<AiProviderService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Codex:PublicExposure"] = exposed ? "true" : "false" }).Build(),
        usage ?? new ServerAiUsage(new ConfigurationBuilder().Build(), NullLogger<ServerAiUsage>.Instance));

    private static UserDocument User(bool admin, string? providerUrl = null)
    {
        var user = new UserDocument { Roles = admin ? new List<string> { "Player", "ServerAdmin" } : new List<string> { "Player" } };
        if (providerUrl != null)
        {
            user.AiProviders.Add(new AiProviderConfig { Id = "p", Name = "mine", Kind = "ollama", BaseUrl = providerUrl, ModelId = "llama" });
        }

        return user;
    }

    [Fact]
    public void The_servers_own_key_is_everyones_at_home_and_the_admins_alone_when_open()
    {
        Assert.NotNull(Service(false).ClientFor(User(admin: false)));
        Assert.Null(Service(true).ClientFor(User(admin: false)));
        Assert.NotNull(Service(true).ClientFor(User(admin: true)));
        Assert.Null(Service(true).Describe(User(admin: false)));
        Assert.NotNull(Service(true).Describe(User(admin: true)));
    }

    [Fact]
    public void The_servers_key_is_not_handed_out_for_a_missing_account_or_in_place_of_a_key_that_cannot_be_read()
    {
        // No account behind the session (deleted, or gone in a restore): nobody to count the requests against.
        Assert.Null(Service(false).ClientFor(null));

        // A key stored before keys were bound to their user no longer decrypts. The user enters it again; until then the
        // shared allowance is not spent in its place.
        var stale = User(admin: false);
        stale.Id = "users/stale";
        stale.AiProviders.Add(new AiProviderConfig { Id = "p", Name = "mine", Kind = "openrouter", ModelId = "m", ProtectedApiKey = "not-a-key-this-server-wrote" });
        Assert.Null(Service(false).ClientFor(stale));

        // One with no key stored at all (a provider set up but not finished) still falls back, as before.
        var unfinished = User(admin: false);
        unfinished.AiProviders.Add(new AiProviderConfig { Id = "p", Name = "mine", Kind = "openrouter", ModelId = "m" });
        Assert.NotNull(Service(false).ClientFor(unfinished));
    }

    [Fact]
    public void A_players_own_provider_may_not_point_inside_the_hosts_network_when_open()
    {
        var local = User(admin: false, "http://127.0.0.1:11434/v1");

        Assert.NotNull(Service(false).ClientFor(local));
        Assert.Null(Service(true).ClientFor(local));
        Assert.NotNull(Service(true).ClientFor(User(admin: false, "https://example.com/v1")));
    }

    [Fact]
    public async Task The_settings_test_refuses_an_inside_address_when_open()
    {
        var provider = new AiProviderConfig { Id = "p", Name = "x", Kind = "ollama", BaseUrl = "http://192.168.1.9:11434/v1", ModelId = "llama" };

        var (ok, message) = await Service(true).TestAsync(provider, null, User(admin: false));

        Assert.False(ok);
        Assert.Equal("Service.Ai.PrivateBlocked", message.Key);
    }
}
