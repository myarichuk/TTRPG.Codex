using Codex.Plugin.Abstractions;
using Xunit;

namespace Codex.Tests;

public class PackManifestTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("2.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.1.0", false)]
    [InlineData("1.0.0-preview", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0-preview", true)]
    [InlineData("1.0.0+meta", "1.0.0", true)]
    [InlineData("invalid", "1.0.0", false)]
    [InlineData("1.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0", true)]
    public void IsAppVersionCompatible_WorksCorrectly(string currentAppVersion, string minAppVersion, bool expected)
    {
        var manifest = new PackManifest("id", "name", "1.0.0", "sysId", minAppVersion: minAppVersion);
        Assert.Equal(expected, manifest.IsAppVersionCompatible(currentAppVersion));
    }

    [Fact]
    public void A_pack_that_names_no_minimum_loads_in_every_app_version()
    {
        Assert.True(new PackManifest("id", "name", "1.0.0", "sysId").IsAppVersionCompatible("0.1.0"));
    }

    [Fact]
    public void Shipped_packs_do_not_need_a_newer_app_than_the_releases_have_been()
    {
        // A pack that wants app 1.0.0 is skipped by a 0.x release: the SRD would vanish from it.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "content"));
        foreach (var manifest in Directory.EnumerateFiles(root, "manifest.yaml", SearchOption.AllDirectories))
        {
            var line = File.ReadAllLines(manifest).FirstOrDefault(l => l.StartsWith("minAppVersion:", StringComparison.Ordinal));
            if (line != null) Assert.Equal("0.0.0", line.Split(':')[1].Trim());
        }
    }
}
