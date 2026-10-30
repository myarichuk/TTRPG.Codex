using Codex.Launcher;

namespace Codex.Tests;

/// <summary>Tailscale is not installed where these run, so everything is judged from text a real client prints.
/// The shapes are those of Tailscale's own client; replace the fixtures with output from a real machine when
/// one is to hand.</summary>
public class TailscaleTests
{
    private const string Running = """
        {
          "Version": "1.80.0",
          "BackendState": "Running",
          "Self": { "DNSName": "table-pc.tail1234.ts.net.", "HostName": "table-pc", "Online": true },
          "CertDomains": ["table-pc.tail1234.ts.net"],
          "Peer": {}
        }
        """;

    [Fact]
    public void A_running_client_gives_its_name_without_the_trailing_dot()
    {
        var status = TailscaleParser.ParseStatus(Running)!;

        Assert.True(status.SignedIn);
        Assert.Equal("table-pc.tail1234.ts.net", status.DnsName);
        Assert.True(status.HttpsEnabled);
    }

    [Fact]
    public void A_signed_out_client_is_not_signed_in_and_https_off_is_told_apart_from_unknown()
    {
        var signedOut = TailscaleParser.ParseStatus("""{ "BackendState": "NeedsLogin", "Self": null, "CertDomains": null }""")!;

        Assert.False(signedOut.SignedIn);
        Assert.Null(signedOut.DnsName);
        Assert.False(signedOut.HttpsEnabled); // present but empty: certificates are off

        var unknown = TailscaleParser.ParseStatus("""{ "BackendState": "Running" }""")!;

        Assert.True(unknown.SignedIn);
        Assert.Null(unknown.HttpsEnabled); // absent: not known
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Anything_that_is_not_a_status_object_is_nothing(string? text) =>
        Assert.Null(TailscaleParser.ParseStatus(text));

    [Theory]
    [InlineData("https://table-pc.tail1234.ts.net", "Public", "https://table-pc.tail1234.ts.net")]
    [InlineData("  https://table-pc.tail1234.ts.net/", "Public", "https://table-pc.tail1234.ts.net")]
    [InlineData("|-- / proxy http://127.0.0.1:5080", "Other", null)]
    [InlineData("Available on the internet:", "Other", null)]
    [InlineData("Funnel is not enabled on your tailnet. To enable, visit: https://login.tailscale.com/f/funnel?node=n123.", "Approval", "https://login.tailscale.com/f/funnel?node=n123")]
    [InlineData("Access denied: serve config denied", "NeedsPermission", null)]
    [InlineData("Use 'sudo tailscale set --operator=$USER' to not require root.", "NeedsPermission", null)]
    [InlineData("see https://example.com/ts.net.html", "Other", null)]
    [InlineData("Error: certificate for https://table-pc.tail1234.ts.net failed", "Other", null)]
    [InlineData("", "Other", null)]
    [InlineData(null, "Other", null)]
    public void The_funnel_output_is_read_line_by_line(string? line, string kind, string? url)
    {
        var parsed = TailscaleParser.ParseFunnelLine(line);

        Assert.Equal(Enum.Parse<FunnelLineKind>(kind), parsed.Kind);
        Assert.Equal(url, parsed.Url);
    }

    [Fact]
    public void The_command_is_looked_for_on_the_path_and_where_each_system_installs_it()
    {
        var linux = TailscaleCli.Candidates(windows: false, mac: false, "/usr/local/sbin:/opt/bin", "").ToList();
        Assert.Equal(new[] { "/usr/local/sbin/tailscale", "/opt/bin/tailscale", "/usr/bin/tailscale", "/usr/local/bin/tailscale" }, linux);

        var mac = TailscaleCli.Candidates(windows: false, mac: true, "", "").ToList();
        Assert.Contains("/Applications/Tailscale.app/Contents/MacOS/Tailscale", mac);

        var windows = TailscaleCli.Candidates(windows: true, mac: false, null, @"C:\Program Files").ToList();
        Assert.Single(windows);
        Assert.EndsWith("tailscale.exe", windows[0]);
        Assert.Contains("Tailscale", windows[0]);
    }

    [Fact]
    public void A_leftover_funnel_is_recognised_by_its_address_and_port()
    {
        const string stale = "# Funnel on:\n#     - https://table-pc.tail1234.ts.net\n\nhttps://table-pc.tail1234.ts.net (Funnel on)\n|-- / proxy http://127.0.0.1:5080\n";

        Assert.True(TailscaleCli.FunnelPointsAtPort(stale, 5080));
        Assert.False(TailscaleCli.FunnelPointsAtPort(stale, 508));
        Assert.False(TailscaleCli.FunnelPointsAtPort(stale, 3000));
        Assert.False(TailscaleCli.FunnelPointsAtPort("No serve config", 5080));
        Assert.False(TailscaleCli.FunnelPointsAtPort(null, 5080));

        // A tailnet-only Serve for the same port is not a public address.
        const string serveOnly = "https://table-pc.tail1234.ts.net (tailnet only)\n|-- / proxy http://127.0.0.1:5080\n";
        Assert.False(TailscaleCli.FunnelPointsAtPort(serveOnly, 5080));

        // A funnel for another port does not count either, even beside a Serve for this one.
        const string mixed = serveOnly + "\nhttps://table-pc.tail1234.ts.net:8443 (Funnel on)\n|-- / proxy http://127.0.0.1:3000\n";
        Assert.False(TailscaleCli.FunnelPointsAtPort(mixed, 5080));
        Assert.True(TailscaleCli.FunnelPointsAtPort(mixed, 3000));
    }

    [Fact]
    public void The_internet_link_comes_last_and_the_switch_is_never_saved()
    {
        var settings = new LauncherSettings { AllowLan = true, OpenToInternet = true };

        var links = settings.Links("192.168.1.20", "100.64.1.1", "https://table-pc.tail1234.ts.net");

        Assert.Equal(new[] { LinkKind.This, LinkKind.Network, LinkKind.Tailscale, LinkKind.Internet }, links.Select(l => l.Kind));
        Assert.Equal("https://table-pc.tail1234.ts.net", links[^1].Url);
        Assert.Equal(new[] { LinkKind.This }, new LauncherSettings().Links(null, null).Select(l => l.Kind));

        var path = Path.Combine(Path.GetTempPath(), "codex-launcher-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            settings.Save(path);
            Assert.False(File.ReadAllText(path).Contains("OpenToInternet", StringComparison.OrdinalIgnoreCase));
            Assert.False(LauncherSettings.Load(path).OpenToInternet);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
