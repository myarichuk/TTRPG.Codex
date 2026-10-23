using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Localization;

namespace Codex.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 2, "other")]
    [InlineData("ru", 1, "one")]
    [InlineData("ru", 21, "one")]
    [InlineData("ru", 11, "many")]
    [InlineData("ru", 2, "few")]
    [InlineData("ru", 24, "few")]
    [InlineData("ru", 12, "many")]
    [InlineData("ru", 5, "many")]
    [InlineData("ru", 100, "many")]
    [InlineData("he", 1, "one")]
    [InlineData("he", 2, "two")]
    [InlineData("he", 5, "other")]
    public void PluralRules_FollowCldr(string language, long n, string expected) =>
        Assert.Equal(expected, PluralRules.Category(language, n));

    [Theory]
    [InlineData("en", 1, "1 round")]
    [InlineData("en", 3, "3 rounds")]
    [InlineData("ru", 1, "1 раунд")]
    [InlineData("ru", 3, "3 раунда")]
    [InlineData("ru", 5, "5 раундов")]
    [InlineData("ru", 21, "21 раунд")]
    [InlineData("he", 1, "סבב 1")]
    [InlineData("he", 2, "2 סבבים")]
    [InlineData("he", 7, "7 סבבים")]
    public void Localizer_FormatsPlurals(string language, int n, string expected) =>
        Assert.Equal(expected, Localizer.For(language).Get("Count.Rounds", n));

    [Fact]
    public void MessageFormatter_HandlesEscapesAndFormats()
    {
        var c = CultureInfo.InvariantCulture;
        Assert.Equal("{x} 1,234", MessageFormatter.Format("{{x}} {0:N0}", c, 1234));
        Assert.Equal("{9}", MessageFormatter.Format("{9}", c, 1));
    }

    [Fact]
    public void Localizer_FallsBackToEnglishThenKey()
    {
        var l = Localizer.For("de");
        Assert.Equal("Back", l["Common.Back"]);
        Assert.Equal("No.Such.Key", l["No.Such.Key"]);
    }

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("iw", "he")]
    [InlineData("HE", "he")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    public void Normalize_MapsToSupported(string? input, string expected) =>
        Assert.Equal(expected, SupportedCultures.Normalize(input));

    [Fact]
    public void Resources_HaveSameKeysAndPlaceholdersInEveryLanguage()
    {
        var en = Localizer.Keys("en");
        Assert.NotEmpty(en);
        var source = Localizer.For("en");
        foreach (var language in SupportedCultures.Tags.Where(t => t != "en"))
        {
            var other = Localizer.Keys(language);
            Assert.Empty(en.Except(other));
            Assert.Empty(other.Except(en));

            var translated = Localizer.For(language);
            foreach (var key in en)
            {
                var translation = translated.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(translation), $"{language}:{key} is empty");
                Assert.Equal(Placeholders(source.Get(key)), Placeholders(translation));
            }
        }
    }

    [Fact]
    public void EveryKeyUsedInSource_ExistsInEveryLanguage()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "TTRPG.Codex.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var defined = Localizer.Keys("en").ToHashSet();
        var pattern = new Regex(@"\bL\[\s*""([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""");
        var missing = new List<string>();
        foreach (var dir in new[] { "src/Codex.Web", "src/Codex.Launcher" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, dir), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs") || f.EndsWith(".axaml"))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in pattern.Matches(File.ReadAllText(file)))
                if (!defined.Contains(m.Groups[1].Value))
                    missing.Add($"{Path.GetRelativePath(root!, file)}: {m.Groups[1].Value}");
        }

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("he", true)]
    [InlineData("ru", false)]
    [InlineData("en", false)]
    public void Bidi_IsolatesNumbersOnlyInRightToLeftLanguages(string language, bool isolated)
    {
        var shown = Bidi.Ltr("+2", CultureInfo.GetCultureInfo(language));

        Assert.Equal(isolated, shown != "+2");
        Assert.Equal("+2", shown.Trim('\u2066', '\u2069'));
        Assert.Equal("", Bidi.Ltr("", CultureInfo.GetCultureInfo("he")));
    }

    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
}
