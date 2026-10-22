using System.Net;
using System.Net.NetworkInformation;
using Codex.Launcher;

namespace Codex.Tests;

public class LauncherTests
{
    [Fact]
    public void Server_listens_on_loopback_until_the_dm_lets_the_network_in()
    {
        var settings = new LauncherSettings { Port = 5080 };

        Assert.Equal("http://localhost:5080", settings.BindUrl);
        Assert.Single(settings.Links("192.168.1.20", "100.101.102.103"));

        settings.AllowLan = true;

        Assert.Equal("http://0.0.0.0:5080", settings.BindUrl);
        Assert.Equal(
            new[] { "http://localhost:5080", "http://192.168.1.20:5080", "http://100.101.102.103:5080" },
            settings.Links("192.168.1.20", "100.101.102.103").Select(l => l.Url));
        Assert.Equal(new[] { "http://localhost:5080" }, settings.Links(null, null).Select(l => l.Url));
        Assert.Equal("http://localhost:5080", settings.LocalUrl);
    }

    [Fact]
    public void Invite_links_use_the_address_players_can_reach()
    {
        var settings = new LauncherSettings { AllowLan = true };

        Assert.Equal("http://192.168.1.20:5080", settings.PlayerUrl("192.168.1.20", "100.64.1.1"));
        Assert.Equal("http://100.64.1.1:5080", settings.PlayerUrl(null, "100.64.1.1"));
        Assert.Null(new LauncherSettings().PlayerUrl("192.168.1.20", null));
        Assert.Equal("http://192.168.1.20:5080/campaigns/join/abc", Codex.Web.Services.InviteLinks.Build("http://localhost:5080/", "http://192.168.1.20:5080/", "abc"));
        Assert.Equal("http://localhost:5080/campaigns/join/abc", Codex.Web.Services.InviteLinks.Build("http://localhost:5080/", null, "abc"));
    }

    [Fact]
    public void An_invalid_port_falls_back_to_the_default()
    {
        Assert.Equal("http://localhost:5080", new LauncherSettings { Port = 0 }.LocalUrl);
        Assert.Equal("http://localhost:5080", new LauncherSettings { Port = 70000 }.LocalUrl);
    }

    [Fact]
    public void Settings_round_trip_and_a_damaged_file_gives_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-launcher-" + Guid.NewGuid() + ".json");
        try
        {
            new LauncherSettings { AllowLan = true, Port = 6000, OpenBrowserOnStart = false }.Save(path);
            var loaded = LauncherSettings.Load(path);
            Assert.True(loaded.AllowLan);
            Assert.Equal(6000, loaded.Port);
            Assert.False(loaded.OpenBrowserOnStart);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(LauncherSettings.DefaultPort, LauncherSettings.Load(path).Port);
            Assert.False(LauncherSettings.Load(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid())).AllowLan);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_join_address_prefers_wifi_or_ethernet_over_virtual_adapters()
    {
        var best = LanAddress.Rank(new (string, NetworkInterfaceType, IPAddress)[]
        {
            ("docker0", NetworkInterfaceType.Ethernet, IPAddress.Parse("172.17.0.1")),
            ("lo0", NetworkInterfaceType.Loopback, IPAddress.Parse("127.0.0.1")),
            ("en0", NetworkInterfaceType.Wireless80211, IPAddress.Parse("192.168.1.20")),
            ("en5", NetworkInterfaceType.Ethernet, IPAddress.Parse("169.254.3.4")),
        });

        Assert.Equal("192.168.1.20", best);
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.1.1", false)]
    public void Tailscale_addresses_are_the_carrier_grade_range(string address, bool expected)
    {
        Assert.Equal(expected, LanAddress.IsTailnet(IPAddress.Parse(address)));
    }

    [Fact]
    public void No_usable_network_gives_no_join_address()
    {
        Assert.Null(LanAddress.Rank(new (string, NetworkInterfaceType, IPAddress)[] { ("lo0", NetworkInterfaceType.Loopback, IPAddress.Loopback) }));
    }
}
