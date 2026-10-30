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
    public void Links_are_told_apart_by_kind_and_labelled_in_the_current_language()
    {
        var links = new LauncherSettings { AllowLan = true }.Links("192.168.1.20", "100.101.102.103");

        Assert.Equal(new[] { LinkKind.This, LinkKind.Network, LinkKind.Tailscale }, links.Select(l => l.Kind));
        Assert.Equal(new[] { "This computer", "Players on your network", "Players on Tailscale" }, links.Select(l => l.Label));
    }

    [Fact]
    public void The_language_is_saved_and_unset_means_follow_the_system()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-launcher-" + Guid.NewGuid() + ".json");
        try
        {
            Assert.Null(new LauncherSettings().Language);
            new LauncherSettings { Language = "ru" }.Save(path);
            Assert.Equal("ru", LauncherSettings.Load(path).Language);
        }
        finally
        {
            File.Delete(path);
        }
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
    public void Tailscale_is_offered_as_a_second_invite_address_only_beside_a_network_one()
    {
        var settings = new LauncherSettings { AllowLan = true };

        Assert.Equal("http://100.64.1.1:5080", settings.RemoteUrl("192.168.1.20", "100.64.1.1"));
        Assert.Null(settings.RemoteUrl(null, "100.64.1.1")); // Tailscale is already the main address
        Assert.Null(settings.RemoteUrl("192.168.1.20", null));
        Assert.Null(new LauncherSettings().RemoteUrl("192.168.1.20", "100.64.1.1"));
        Assert.Equal("http://100.64.1.1:5080/campaigns/join/abc", Codex.Web.Services.InviteLinks.BuildRemote("http://100.64.1.1:5080/", "abc"));
        Assert.Null(Codex.Web.Services.InviteLinks.BuildRemote(null, "abc"));
        Assert.Null(Codex.Web.Services.InviteLinks.BuildRemote("  ", "abc"));
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

public class JoinCheckTests
{
    private static Task<IReadOnlyList<CheckResult>> Run(
        LauncherSettings settings, ServerState state, string[] addresses, bool reaches = true, bool portFree = true, string? problem = null, string? tailnet = null) =>
        JoinCheck.RunAsync(settings, state, problem, addresses, tailnet, _ => Task.FromResult(reaches), _ => portFree);

    [Fact]
    public async Task A_healthy_table_has_nothing_to_fix()
    {
        var results = await Run(new LauncherSettings { AllowLan = true }, ServerState.Running, new[] { "192.168.1.20" });

        Assert.DoesNotContain(results, r => r.Status is CheckStatus.Fail or CheckStatus.Warn);
        Assert.Contains(results, r => r.Detail == "http://192.168.1.20:5080");
    }

    [Fact]
    public async Task A_stopped_server_and_closed_network_are_reported_first()
    {
        var results = await Run(new LauncherSettings(), ServerState.Stopped, new[] { "192.168.1.20" });

        Assert.Equal("The server is stopped", results[0].Title);
        Assert.Contains(results, r => r.Status == CheckStatus.Fail && r.Title == "Network access is off");
    }

    [Fact]
    public async Task A_failed_start_with_a_busy_port_names_the_port()
    {
        var results = await Run(new LauncherSettings { Port = 5080 }, ServerState.Failed, new[] { "192.168.1.20" }, portFree: false, problem: "boom");

        Assert.Contains(results, r => r.Title == "Another program is using port 5080");
        Assert.Contains(results, r => r.Detail == "boom");
    }

    [Fact]
    public async Task No_network_is_a_failure_and_no_address_is_offered()
    {
        var results = await Run(new LauncherSettings { AllowLan = true }, ServerState.Running, Array.Empty<string>());

        Assert.Contains(results, r => r.Status == CheckStatus.Fail && r.Title == "This computer has no network address");
        Assert.DoesNotContain(results, r => r.Title == "Players use this link");
    }

    [Fact]
    public async Task An_unreachable_network_address_suggests_a_restart()
    {
        var results = await Run(new LauncherSettings { AllowLan = true }, ServerState.Running, new[] { "192.168.1.20" }, reaches: false);

        Assert.Contains(results, r => r.Status == CheckStatus.Fail && r.Title.Contains("does not answer"));
    }

    [Fact]
    public async Task Other_addresses_and_tailscale_are_listed_for_the_dm()
    {
        var results = await Run(new LauncherSettings { AllowLan = true }, ServerState.Running, new[] { "192.168.1.20", "172.20.10.2" }, tailnet: "100.64.1.2");

        Assert.Contains(results, r => r.Detail.Contains("http://172.20.10.2:5080"));
        Assert.Contains(results, r => r.Title == "Tailscale is on");
    }

    [Fact]
    public async Task An_address_outside_the_home_ranges_gets_a_warning()
    {
        var results = await Run(new LauncherSettings { AllowLan = true }, ServerState.Running, new[] { "203.0.113.9" });

        Assert.Contains(results, r => r.Status == CheckStatus.Warn);
    }
}
