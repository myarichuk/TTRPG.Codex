using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Codex.Launcher;

/// <summary>The self-signed certificate the server uses for players on the network. Nothing public can vouch for a
/// private address, so it is made here, once, and kept in the data folder: it is the same certificate on every start
/// until it is close to expiring or this computer's addresses stop being covered by it. Browsers still warn on a
/// first visit (players choose to continue); the connection is encrypted and the page counts as secure.</summary>
internal static class LocalCertificate
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>The names and addresses worth putting in the certificate: this computer by name and by every address
    /// players might use.</summary>
    public static IReadOnlyList<string> Names(IEnumerable<string> addresses, string? machineName = null) =>
        new[] { "localhost", "127.0.0.1", "::1" }
            .Concat(string.IsNullOrWhiteSpace(machineName ?? Environment.MachineName) ? Array.Empty<string>() : new[] { (machineName ?? Environment.MachineName).ToLowerInvariant() })
            .Concat(addresses)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Makes sure there is a usable certificate for <paramref name="names"/> at <paramref name="pfxPath"/> and
    /// returns that path, or <c>null</c> when none could be made (the server then stays on plain HTTP).</summary>
    public static string? Ensure(string pfxPath, IReadOnlyList<string> names, DateTimeOffset? now = null, Action<string>? note = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var namesPath = pfxPath + ".names";
        try
        {
            if (File.Exists(pfxPath) && File.Exists(namesPath) && StillGood(pfxPath, namesPath, names, at)) return pfxPath;

            Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
            using var cert = Create(names, at);
            // Written beside the final name and moved into place, so a crash never leaves half a certificate behind.
            var temp = pfxPath + ".tmp";
            File.WriteAllBytes(temp, cert.Export(X509ContentType.Pfx));
            // It holds the private key: this user only.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, pfxPath, overwrite: true);
            File.WriteAllLines(namesPath, names);
            note?.Invoke($"made a certificate for {string.Join(", ", names)}");
            return pfxPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            note?.Invoke("could not make a certificate: " + ex.Message);
            return null;
        }
    }

    private static bool StillGood(string pfxPath, string namesPath, IReadOnlyList<string> names, DateTimeOffset at)
    {
        try
        {
            var have = File.ReadAllLines(namesPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!names.All(have.Contains)) return false;
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
            return cert.NotBefore <= at && cert.NotAfter - at > RenewBefore;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            // Damaged or unreadable: make a new one.
            return false;
        }
    }

    internal static X509Certificate2 Create(IReadOnlyList<string> names, DateTimeOffset at)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=TTRPG Codex (this computer)", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        // A little before now: a player's clock may run slightly behind this computer's.
        using var made = request.CreateSelfSigned(at.AddDays(-1), at + Lifetime);
        // Round-trips through a PFX so the key is exportable on every platform.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }
}
