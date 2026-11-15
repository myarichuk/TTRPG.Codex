using System.Text.RegularExpressions;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>Step H8 of HAZARDS_DESIGN.md: every string the hazards track added is in English, Russian and Hebrew; the words for the same thing are the same word across the panel, the
/// wizard, the world builder and the book; and the checks the wizard and the world builder's card show are said in the reader's language.</summary>
public class HazardStringsTests
{
    private static readonly SystemDefinition Srd5e = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]", RegexOptions.Compiled);
    private static readonly Regex HebrewLetters = new("[א-ת]", RegexOptions.Compiled);

    private static IEnumerable<string> HazardKeys(string culture) =>
        Localizer.Keys(culture).Where(k => k.Contains("Hazard", StringComparison.Ordinal) || k.StartsWith("Event.Check.", StringComparison.Ordinal) || k.StartsWith("Wizard.Hazard", StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    [Fact]
    public void EveryHazardKey_IsInRussianAndHebrew_AsWordsOfThatLanguage()
    {
        var en = HazardKeys("en").ToList();
        Assert.NotEmpty(en);
        foreach (var (culture, script) in new[] { ("ru", Cyrillic), ("he", HebrewLetters) })
        {
            var present = Localizer.Keys(culture).ToHashSet(StringComparer.Ordinal);
            var localizer = Localizer.For(culture);
            foreach (var key in en)
            {
                Assert.True(present.Contains(key), $"{culture}: {key} is missing.");
                var value = localizer[key];
                // A key that is only a placeholder (such as "{0}") needs no words.
                if (!Regex.IsMatch(Localizer.For("en")[key], "[A-Za-z]{2}")) continue;
                Assert.True(script.IsMatch(value), $"{culture}: {key} has no {culture} words: {value}");
            }
        }
    }

    [Fact]
    public void OneWordForEachThing_InRussianAndHebrew()
    {
        // The hazard is a feminine word in both languages and the panel's word: the Hebrew wizard and book once said "מפגע"; "DC" is "Сл" in Russian and "דרגת קושי" in Hebrew.
        foreach (var key in HazardKeys("he"))
        {
            var value = Localizer.For("he")[key];
            Assert.DoesNotContain("מפגע", value);
            Assert.DoesNotContain("איתור", value);
            Assert.DoesNotContain("DC", value);
        }

        foreach (var key in HazardKeys("ru"))
        {
            var value = Localizer.For("ru")[key];
            Assert.DoesNotContain("СЛ ", value);
            Assert.DoesNotContain("DC", value);
        }
    }

    [Fact]
    public void TheEnglishTemplatesOfTheChecks_AreTheEnglishResourceStrings()
    {
        var en = Localizer.For("en");
        foreach (var (key, template) in HazardMessages.English)
        {
            Assert.Equal(template.Replace("{{", "{").Replace("}}", "}"), en[HazardMessages.Prefix + key]);
        }

        foreach (var culture in new[] { "ru", "he" })
        {
            var present = Localizer.Keys(culture).ToHashSet(StringComparer.Ordinal);
            Assert.All(HazardMessages.English.Keys, key => Assert.Contains(HazardMessages.Prefix + key, present));
        }
    }

    private static ContentEntry Entry(params (string Key, object Value)[] pairs) =>
        new() { Type = "hazard", Properties = pairs.ToDictionary(p => p.Key, p => p.Value) };

    private static Dictionary<string, object> Row(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("ru", "есть Сл, но нет проверки", "выше нормы", "от 10 до 13")]
    [InlineData("he", "יש דרגת קושי אך אין בדיקה", "גבוה", "10 עד 13")]
    public void TheShapeAndGuideChecks_AreSaidInTheLanguage_WithTheEnglishWhenNoneIsGiven(string culture, string shape, string high, string range)
    {
        var type = Srd5e.FindType("hazard")!;
        var said = Localizer.For(culture);
        string Say(string key, object?[] args) => said.Get(key, args);

        var noCheck = Entry(("detect", new List<object> { Row(("name", "Look"), ("dc", 15L)) }));
        Assert.Contains(HazardNumbers.Problems(noCheck, type, Srd5e), p => p.Contains("has a dc and no check", StringComparison.Ordinal));
        var theirs = HazardNumbers.Problems(noCheck, type, Srd5e, Say).ToList();
        Assert.Contains(theirs, p => p.Contains(shape, StringComparison.Ordinal));
        Assert.DoesNotContain(theirs, p => p.Contains("has a dc", StringComparison.Ordinal) || p.Contains("route", StringComparison.Ordinal));

        var steep = Entry(("level", "1-4"), ("severity", "Nuisance"), ("saveAttribute", "Dexterity"), ("saveDc", 19L),
            ("damage", new List<object> { Row(("name", "Blades"), ("dice", "1d6")) }));
        Assert.Contains("is high for a level 1 Nuisance hazard", Assert.Single(HazardNumbers.ScalingProblems(steep, type, Srd5e)));
        var steepSaid = Assert.Single(HazardNumbers.ScalingProblems(steep, type, Srd5e, Say));
        Assert.Contains(high, steepSaid);
        Assert.Contains(range, steepSaid);
        Assert.DoesNotContain("is high for", steepSaid);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void EveryCheckMessage_CanBeSaid_WithItsArguments_AndKeepsItsPlaceholders(string culture)
    {
        var said = Localizer.For(culture);
        foreach (var (key, template) in HazardMessages.English)
        {
            var holes = Regex.Matches(template, @"\{(\d+)\}").Select(m => m.Value).ToHashSet();
            var value = said[HazardMessages.Prefix + key];
            foreach (var hole in holes) Assert.True(value.Contains(hole, StringComparison.Ordinal), $"{culture}: {key} lost {hole}: {value}");
        }
    }
}
