using System.Text.RegularExpressions;

namespace Codex.Tests;

/// <summary>
/// i18n (6.5): every localizer key the creation wizard renders must exist in its Hebrew
/// .resx - otherwise the key (English) silently shows for Hebrew users and nobody notices.
/// The English side needs no .resx: with no matching resource the key itself renders.
/// </summary>
public class LocalizationCoverageTests
{
    private static string FindRepoRoot()
    {
        var start = Path.GetDirectoryName(typeof(LocalizationCoverageTests).Assembly.Location) ?? AppContext.BaseDirectory;
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TTRPG.Codex.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("TTRPG.Codex.sln not found above " + start);
    }

    [Fact]
    public void CreateCharacter_HebrewResxCoversEveryRenderedKey()
    {
        var root = FindRepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src", "Codex.Web", "Components", "Pages", "CreateCharacter.razor"));
        var resx = File.ReadAllText(Path.Combine(root, "src", "Codex.Web", "Resources", "Components", "Pages", "CreateCharacter.he.resx"));

        // Direct L["..."] lookups plus step titles, which render through L[step.Title].
        var keys = Regex.Matches(razor, @"L\[""((?:[^""\\]|\\.)*)""\]")
            .Select(m => Regex.Unescape(m.Groups[1].Value))
            .Concat(Regex.Matches(razor, @"new\(""\w+"",\s*""((?:[^""\\]|\\.)*)""")
                .Select(m => Regex.Unescape(m.Groups[1].Value)))
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        var translated = Regex.Matches(resx, @"<data name=""((?:[^""\\]|\\.)*)""")
            .Select(m => Regex.Unescape(m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var missing = keys.Where(k => !translated.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            "Hebrew resx is missing keys rendered by the wizard: " + string.Join("; ", missing));
    }
}
