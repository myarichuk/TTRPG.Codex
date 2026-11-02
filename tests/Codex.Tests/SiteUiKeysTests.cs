using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Codex.Tests;

/// <summary>The website (site/) quotes the app's buttons as <c>[[ui:Key]]</c> in its pages and <c>{{ui:Key}}</c> in site.json,
/// and its build fails on a key the app no longer has, or has no translation of. The site is built apart from the app, so a
/// key removed here broke it unseen (the session page's Save buttons); this runs the same check with the app's tests.</summary>
public class SiteUiKeysTests
{
    private static readonly string Resources = Path.Combine(TestPaths.RepoRoot, "src", "Codex.Localization", "Resources");
    private static readonly string Content = Path.Combine(TestPaths.RepoRoot, "site", "src", "content");

    private static HashSet<string> KeysIn(Func<string, bool> file) =>
        Directory.GetFiles(Resources, "*.resx")
            .Where(f => file(Path.GetFileName(f)))
            .SelectMany(f => XDocument.Load(f).Root!.Elements("data").Select(d => (string)d.Attribute("name")!))
            .ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("he")]
    public void EveryButtonTheSiteQuotes_IsAKeyTheAppTranslates(string lang)
    {
        var neutral = KeysIn(f => !Regex.IsMatch(f, @"\.(ru|he)\.resx$"));
        var translated = lang == "en" ? neutral : KeysIn(f => f.EndsWith($".{lang}.resx", StringComparison.Ordinal));

        var missing = Directory.GetFiles(Path.Combine(Content, lang))
            .Where(f => f.EndsWith(".md", StringComparison.Ordinal) || Path.GetFileName(f) == "site.json")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:\[\[|\{\{)ui:([A-Za-z0-9_.]+)(?:\]\]|\}\})")
                .Select(m => m.Groups[1].Value)
                .Where(key => !neutral.Contains(key) || !translated.Contains(key))
                .Select(key => $"{Path.GetFileName(f)}: {key}"))
            .ToList();

        Assert.Empty(missing);
    }
}
