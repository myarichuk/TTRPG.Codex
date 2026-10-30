using Codex.Persistence;
using Codex.Web;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Codex.Tests;

public class ExposurePolicyTests
{
    [Theory]
    [InlineData("/campaigns/join/abc123", "abc123")]
    [InlineData("/campaigns/join/abc123/", "abc123")]
    [InlineData("/campaigns/join/abc123?x=1", "abc123")]
    [InlineData("/Campaigns/Join/abc123", "abc123")]
    [InlineData("http://192.168.1.20:5080/campaigns/join/abc123", "abc123")] // what the join page used to send
    [InlineData("https://pc.tail1234.ts.net/campaigns/join/abc123", "abc123")]
    [InlineData("/campaigns/join/", null)]
    [InlineData("/campaigns/join/a/b", null)]
    [InlineData("/campaigns", null)]
    [InlineData("/", null)]
    [InlineData("//evil.example/campaigns/join/abc123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_invite_code_is_read_from_a_join_link_only(string? returnUrl, string? expected) =>
        Assert.Equal(expected, ExposurePolicy.InviteCodeFrom(returnUrl));

    [Fact]
    public async Task Sign_up_is_open_at_home_and_needs_a_real_invite_on_an_open_server()
    {
        var campaigns = Substitute.For<ICampaignRepository>();
        campaigns.InviteCodeExistsAsync("real").Returns(true);
        campaigns.InviteCodeExistsAsync("made-up").Returns(false);

        Assert.True(await ExposurePolicy.MaySignUpAsync(exposed: false, returnUrl: null, campaigns));
        Assert.True(await ExposurePolicy.MaySignUpAsync(true, "/campaigns/join/real", campaigns));
        Assert.False(await ExposurePolicy.MaySignUpAsync(true, "/campaigns/join/made-up", campaigns));
        Assert.False(await ExposurePolicy.MaySignUpAsync(true, "/", campaigns));
        Assert.False(await ExposurePolicy.MaySignUpAsync(true, null, campaigns));
    }

    [Fact]
    public void The_server_ai_key_is_the_admins_alone_while_the_server_is_open()
    {
        var admin = new UserDocument { Roles = new List<string> { "Player", "ServerAdmin" } };
        var player = new UserDocument { Roles = new List<string> { "Player" } };

        Assert.True(ExposurePolicy.MayUseServerAi(exposed: false, player));
        Assert.True(ExposurePolicy.MayUseServerAi(false, null));
        Assert.True(ExposurePolicy.MayUseServerAi(true, admin));
        Assert.False(ExposurePolicy.MayUseServerAi(true, player));
        Assert.False(ExposurePolicy.MayUseServerAi(true, null));
    }

    [Fact]
    public void Exposure_is_off_unless_the_launcher_turns_it_on()
    {
        IConfiguration Config(string? value) => new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? new Dictionary<string, string?>() : new() { [ExposurePolicy.ConfigKey] = value })
            .Build();

        Assert.False(ExposurePolicy.IsExposed(null));
        Assert.False(ExposurePolicy.IsExposed(Config(null)));
        Assert.False(ExposurePolicy.IsExposed(Config("false")));
        Assert.True(ExposurePolicy.IsExposed(Config("true")));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://localhost:1234", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://192.168.1.5/v1", true)]
    [InlineData("http://10.0.0.2", true)]
    [InlineData("http://172.20.1.1", true)]
    [InlineData("http://169.254.169.254/latest", true)]
    [InlineData("http://100.64.1.1", true)]
    [InlineData("http://[::ffff:127.0.0.1]", true)]
    [InlineData("http://ollama:11434", true)]
    [InlineData("http://localhost.:11434/v1", true)]
    [InlineData("http://box.local", true)]
    [InlineData("https://openrouter.ai/api/v1", false)]
    [InlineData("https://8.8.8.8/v1", false)]
    [InlineData("http://172.32.0.1", false)]
    [InlineData("", false)]
    public void An_address_inside_this_computer_or_its_network_is_internal(string url, bool internalAddress) =>
        Assert.Equal(internalAddress, ExposurePolicy.IsInternalEndpoint(url));

    [Theory]
    [InlineData("/login", true)]
    [InlineData("/login/external-callback", true)]
    [InlineData("/register", true)]
    [InlineData("/campaigns/join/abc", true)]
    [InlineData("/", false)]
    [InlineData("/campaigns/abc", false)]
    [InlineData("/_blazor", false)]
    public void Only_the_pages_that_reveal_a_right_guess_are_rationed(string path, bool rationed) =>
        Assert.Equal(rationed, ExposurePolicy.IsGuessable(new Microsoft.AspNetCore.Http.PathString(path)));

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData(null, "unknown")]
    public void Clients_are_rationed_by_address_and_by_network_for_ipv6(string? address, string key) =>
        Assert.Equal(key, ExposurePolicy.ClientKey(address == null ? null : System.Net.IPAddress.Parse(address)));

    [Fact]
    public async Task The_public_only_client_refuses_the_hosts_own_addresses()
    {
        Assert.True(ExposurePolicy.IsInternalAddress(System.Net.IPAddress.Loopback));
        Assert.False(ExposurePolicy.IsInternalAddress(System.Net.IPAddress.Parse("8.8.8.8")));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await ExposurePolicy.PublicOnlyClient.GetAsync("http://127.0.0.1:1/"));
    }
}
