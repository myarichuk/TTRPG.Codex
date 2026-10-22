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
        Assert.Null(settings.JoinUrl("192.168.1.20"));

        settings.AllowLan = true;

        Assert.Equal("http://0.0.0.0:5080", settings.BindUrl);
        Assert.Equal("http://192.168.1.20:5080", settings.JoinUrl("192.168.1.20"));
        Assert.Equal("http://localhost:5080", settings.LocalUrl);
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

    [Fact]
    public void No_usable_network_gives_no_join_address()
    {
        Assert.Null(LanAddress.Rank(new (string, NetworkInterfaceType, IPAddress)[] { ("lo0", NetworkInterfaceType.Loopback, IPAddress.Loopback) }));
    }
}
