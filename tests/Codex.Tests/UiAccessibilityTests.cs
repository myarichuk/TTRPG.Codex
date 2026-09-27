using System.Text.RegularExpressions;
using Xunit;

namespace Codex.Tests;

/// <summary>
/// Static UI-accessibility guardrails for the Blazor Web front end.
/// These assert the shipped invariants from the UI remediation pass
/// (contrast tokens, landmarks, tab semantics, labelled controls,
/// motion safety) without needing a browser.
/// </summary>
public class UiAccessibilityTests
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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string Web(params string[] parts) =>
        Read(new[] { "src", "Codex.Web" }.Concat(parts).ToArray());

    [Fact]
    public void TertiaryText_MeetsContrastFloor()
    {
        var css = Web("wwwroot", "app.css");
        Assert.DoesNotContain("#616161", css);
        Assert.DoesNotContain("#404040", css);

        var splash = Web("Components", "App.razor");
        Assert.DoesNotContain("--text-tertiary: #404040", splash);
    }

    [Fact]
    public void Layout_HasSkipLinkLandmarksAndHiddenDecorations()
    {
        var layout = Web("Components", "Layout", "MainLayout.razor");
        Assert.Contains("skip-link", layout);
        Assert.Contains("id=\"main-content\"", layout);
        Assert.Contains("nav-toggle", layout);

        var nav = Web("Components", "Layout", "NavMenu.razor");
        Assert.Contains("aria-label=\"Primary\"", nav);

        var glyphs = Web("Components", "Layout", "SpellGlyphsBackground.razor");
        Assert.Contains("aria-hidden=\"true\"", glyphs);
    }

    [Fact]
    public void MotionSafety_IsGlobal()
    {
        var css = Web("wwwroot", "app.css");
        Assert.Contains("prefers-reduced-motion", css);
    }

    [Fact]
    public void Home_HasNoNestedInteractiveCard()
    {
        var home = Web("Components", "Pages", "Home.razor");
        Assert.DoesNotContain("@onclick:stopPropagation", home);
        Assert.DoesNotContain("<a class=\"campaign-card campaign-card-link\"", home);
        Assert.Contains("<article class=\"campaign-card\"", home);
        Assert.Contains("role=\"status\"", home);
        Assert.Contains("scope=\"col\"", home);
    }

    [Fact]
    public void CampaignList_HasDialogLiveCountAndNoExternalArt()
    {
        var list = Web("Components", "Pages", "CampaignList.razor");
        Assert.DoesNotContain("unsplash", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Oracle Insights", list);
        Assert.DoesNotContain("javascript:", list);
        Assert.Contains("role=\"status\"", list);
        Assert.Contains("aria-modal=\"true\"", list);
        Assert.Contains("Create campaign", list);

        var css = Web("Components", "Pages", "CampaignList.razor.css");
        Assert.Contains("gap: 1.5rem", css);
    }

    [Fact]
    public void CampaignDetail_UsesAccessibleTabs()
    {
        var detail = Web("Components", "Pages", "CampaignDetail.razor");
        Assert.Contains("role=\"tablist\"", detail);
        Assert.Contains("role=\"tab\"", detail);
        Assert.Contains("aria-selected", detail);
        Assert.Contains("role=\"tabpanel\"", detail);
        Assert.Contains("aria-controls", detail);
        Assert.Contains("role=\"status\"", detail);
        Assert.Contains("Online", detail);
        Assert.Contains("aria-label=\"New actor name\"", detail);
    }

    [Fact]
    public void CombatConsole_HasLabelledControlsAndNoJsLinks()
    {
        var combat = Web("Components", "Shared", "CombatConsole.razor");
        Assert.DoesNotContain("javascript:void(0)", combat);
        Assert.Contains("aria-label=\"Damage or healing amount\"", combat);
        Assert.Contains("aria-label=\"Condition name\"", combat);
        Assert.Contains("aria-label=\"Dice expression\"", combat);
        Assert.Contains("role=\"alert\"", combat);
    }

    [Fact]
    public void Grimoire_IsSearchFirstWithLiveCount()
    {
        var grimoire = Web("Components", "Pages", "Grimoire.razor");
        Assert.Contains("type=\"search\"", grimoire);
        Assert.Contains("role=\"status\"", grimoire);
        Assert.Contains("scope=\"col\"", grimoire);

        var css = Web("Components", "Pages", "Grimoire.razor.css");
        Assert.DoesNotContain("#a855f7", css);
    }

    [Fact]
    public void NoPlaceholderOnlyInputs_RemainInCombatOrDetail()
    {
        foreach (var file in new[]
        {
            Path.Combine("Components", "Shared", "CombatConsole.razor"),
            Path.Combine("Components", "Pages", "CampaignDetail.razor"),
        })
        {
            // Mask C# lambdas so tag matching doesn't stop at the '>' in '=>'.
            var text = Web(file).Replace("=>", "~~");
            foreach (Match m in Regex.Matches(text, "<(input|textarea|select)[^>]*>"))
            {
                var tag = m.Value;
                if (tag.Contains("type=\"hidden\"") || tag.Contains("type=\"checkbox\""))
                {
                    continue;
                }
                Assert.True(
                    tag.Contains("aria-label=") || tag.Contains("aria-labelledby=") || text.Contains($"for=\"{IdOf(tag)}\""),
                    $"Unlabelled control in {file}: {tag}");
            }
        }
    }

    private static string IdOf(string tag)
    {
        var m = Regex.Match(tag, "id=\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : Guid.NewGuid().ToString();
    }
}
