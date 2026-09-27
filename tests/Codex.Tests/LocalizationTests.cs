using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Codex.Tests;

/// <summary>i18n guard (6.5): every localizer key used by the creation wizard must exist in
/// the Hebrew .resx with matching format placeholders - otherwise Hebrew users silently
/// get English for that string. Pure file parsing, no Web reference needed.</summary>
public class LocalizationTests
{
    private static string FindRepoFile(params string[] parts)
    {
        var start = Path.GetDirectoryName(typeof(LocalizationTests).Assembly.Location) ?? AppContext.BaseDirectory;
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = parts.Aggregate(dir.FullName, Path.Combine);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Repo file not found: {string.Join("/", parts)}");
    }

    [Fact]
    public void HebrewResx_CoversEveryWizardKey()
    {
        var razor = File.ReadAllText(FindRepoFile("src", "Codex.Web", "Components", "Pages", "CreateCharacter.razor"));
        var resx = XDocument.Load(FindRepoFile("src", "Codex.Web", "Resources", "Components", "Pages", "CreateCharacter.he.resx"));
        var entries = resx.Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? "");

        // L["key"] and L["key", args...] usages...
        var keys = Regex.Matches(razor, "L\\[\"((?:[^\"]|\\\\\")*)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        // ...plus step titles rendered through the localizer.
        foreach (Match match in Regex.Matches(razor, "new\\(\"[A-Za-z]+\", \"([A-Za-z ]+)\"\\)"))
        {
            keys.Add(match.Groups[1].Value);
        }

        Assert.NotEmpty(keys);
        var missing = keys.Where(k => !entries.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0,
            $"Hebrew .resx is missing {missing.Count} wizard key(s): {string.Join("; ", missing)}");

        // Placeholder parity: a translated "{0}" dropped or added breaks String.Format at runtime.
        foreach (var key in keys)
        {
            var expected = Regex.Matches(key, @"\{\d+\}").Count;
            var actual = Regex.Matches(entries[key], @"\{\d+\}").Count;
            Assert.True(expected == actual,
                $"Key '{key}': expected {expected} placeholder(s), translation has {actual}.");
            Assert.False(string.IsNullOrWhiteSpace(entries[key]), $"Key '{key}' has an empty translation.");
        }
    }
}
