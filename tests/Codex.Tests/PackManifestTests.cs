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
        var manifest = new PackManifest("id", "name", "1.0.0", "sysId", MinAppVersion: minAppVersion);
        Assert.Equal(expected, manifest.IsAppVersionCompatible(currentAppVersion));
    }
}
