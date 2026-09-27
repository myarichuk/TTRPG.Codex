using Xunit;

namespace Codex.Tests;

/// <summary>
/// The nav menu links at "settings", so a matching page must exist - and because the
/// page reads server configuration, it must never render a secret value, only
/// Configured/Not configured indicators.
/// </summary>
public class ConfigurationPageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TTRPG.Codex.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Web(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot(), "src", "Codex.Web" }.Concat(parts).ToArray()));

    [Fact]
    public void SettingsRoute_MatchesNavMenuLink()
    {
        var nav = Web("Components", "Layout", "NavMenu.razor");
        Assert.Contains("href=\"settings\"", nav);

        var settings = Web("Components", "Pages", "Settings.razor");
        Assert.Contains("@page \"/settings\"", settings);
    }

    [Fact]
    public void SettingsPage_NeverRendersSecretValues()
    {
        var settings = Web("Components", "Pages", "Settings.razor");

        // Secrets may only be probed through HasValue(...) for the
        // Configured/Not configured indicator - never interpolated into markup.
        Assert.DoesNotContain("@AiConfig.ApiKey<", settings);
        Assert.DoesNotContain("@(AiConfig.ApiKey)", settings);
        Assert.DoesNotContain("Google:ClientSecret\"])", settings);
        Assert.DoesNotContain("Apple:PrivateKey\"])", settings);
        Assert.DoesNotContain("Apple:KeyId\"])", settings);
        Assert.Contains("Not configured", settings);
    }
}
