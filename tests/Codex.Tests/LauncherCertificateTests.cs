using System.Security.Cryptography.X509Certificates;
using Codex.Launcher;

namespace Codex.Tests;

public class LauncherCertificateTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void With_a_certificate_players_get_https_and_the_dm_keeps_plain_loopback()
    {
        var settings = new LauncherSettings { Port = 5080, AllowLan = true, TlsActive = true };

        Assert.Equal("http://localhost:5080;https://0.0.0.0:5443", settings.BindUrl);
        Assert.Equal("http://localhost:5080", settings.LocalUrl);
        Assert.Equal(
            new[] { "http://localhost:5080", "https://192.168.1.20:5443", "https://100.101.102.103:5443" },
            settings.Links("192.168.1.20", "100.101.102.103").Select(l => l.Url));
        Assert.Equal("https://192.168.1.20:5443", settings.PlayerUrl("192.168.1.20", "100.64.1.1"));
        Assert.Equal("https://100.64.1.1:5443", settings.RemoteUrl("192.168.1.20", "100.64.1.1"));
    }

    [Fact]
    public void Without_a_certificate_or_the_network_nothing_is_served_over_https()
    {
        Assert.Equal("http://0.0.0.0:5080", new LauncherSettings { AllowLan = true }.BindUrl);
        Assert.Equal("http://localhost:5080", new LauncherSettings { TlsActive = true }.BindUrl);
    }

    [Fact]
    public void The_https_port_never_collides_with_the_plain_one()
    {
        Assert.Equal("http://localhost:5443;https://0.0.0.0:5444", new LauncherSettings { Port = 5443, AllowLan = true, TlsActive = true }.BindUrl);
        Assert.Equal("http://localhost:5080;https://0.0.0.0:5443", new LauncherSettings { Port = 5080, HttpsPort = 5080, AllowLan = true, TlsActive = true }.BindUrl);
    }

    [Fact]
    public void A_certificate_is_made_once_and_kept_until_it_is_old_or_the_addresses_change()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-cert-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "certs", "codex-local.pfx");
            var names = LocalCertificate.Names(new[] { "192.168.1.20" }, "table-pc");

            Assert.Equal(path, LocalCertificate.Ensure(path, names, Now));
            var first = File.ReadAllBytes(path);
            using (var cert = X509CertificateLoader.LoadPkcs12FromFile(path, null))
            {
                Assert.True(cert.HasPrivateKey);
                var san = cert.Extensions.OfType<X509Extension>().Single(e => e.Oid?.Value == "2.5.29.17").Format(false);
                Assert.Contains("192.168.1.20", san);
                Assert.Contains("table-pc", san);
                Assert.Contains("localhost", san);
            }

            // The same certificate on the next start...
            LocalCertificate.Ensure(path, names, Now.AddDays(100));
            Assert.Equal(first, File.ReadAllBytes(path));

            // ...a new one when this computer has a new address, or the old one is close to expiring.
            LocalCertificate.Ensure(path, LocalCertificate.Names(new[] { "192.168.1.20", "10.0.0.5" }, "table-pc"), Now.AddDays(100));
            var second = File.ReadAllBytes(path);
            Assert.NotEqual(first, second);

            LocalCertificate.Ensure(path, LocalCertificate.Names(new[] { "192.168.1.20", "10.0.0.5" }, "table-pc"), Now.AddDays(450));
            Assert.NotEqual(second, File.ReadAllBytes(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_certificate_that_cannot_be_written_leaves_the_server_on_plain_http()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The folder the certificate belongs in is a file.
            File.WriteAllText(Path.Combine(dir, "certs"), "in the way");
            Assert.Null(LocalCertificate.Ensure(Path.Combine(dir, "certs", "codex-local.pfx"), LocalCertificate.Names(Array.Empty<string>()), Now));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
