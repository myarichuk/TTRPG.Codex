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
    private static AiProviderService Service(bool exposed) => new(
        new EphemeralDataProtectionProvider(),
        Substitute.For<IUserRepository>(),
        new AIConfiguration { Provider = "openrouter", ApiKey = "server-key", ModelId = "m" },
        new AIImageConfiguration(),
        Substitute.For<IHttpClientFactory>(),
        NullLogger<AiProviderService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Codex:PublicExposure"] = exposed ? "true" : "false" }).Build());

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
