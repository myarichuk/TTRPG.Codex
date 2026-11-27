using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The hazards in Russian and Hebrew (ENCOUNTER_BUILDER_DESIGN.md 20): <c>codex-hazards-ru</c> and <c>-he</c> translate the two homebrew hazards, and the eight SRD hazards are named
/// in <c>srd-5.2.1-ru</c> and <c>-he</c>. A translation lays text over the entry at display time; the rules read the original, and a restated row keeps every number and key it had.</summary>
public class HazardTranslationPackTests : IAsyncLifetime
{
    private static readonly string[] Homebrew = ["homebrew-cursed-doorway", "homebrew-flooding-room"];
    private static readonly string[] Srd =
        ["collapsing-roof", "falling-net", "fire-casting-statue", "hidden-pit", "poisoned-darts", "poisoned-needle", "rolling-stone", "spiked-pit"];

    /// <summary>The words of a restated row that are text; every other key of a row is data the rules read and must come through unchanged.</summary>
    private static readonly string[] RowText = ["name", "text", "action", "description"];

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public HazardTranslationPackTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private ContentEntry Hazard(string id) => _registry.GetEntry("srd5e", "hazard", id)!;

    private static string Script(string locale) => locale == "ru" ? "[А-Яа-яЁё]" : "[א-ת]";

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void TheHomebrewHazardsPack_IsATranslationOfCodexHazards_ThatLoadsWithoutWarnings(string locale)
    {
        var pack = _registry.GetPacks("srd5e").Single(p => p.Locale == locale && p.TranslationOf == "codex-hazards");

        Assert.Equal($"codex-hazards-{locale}", pack.Id);
        Assert.True(pack.IsTranslation);
        Assert.Equal("MIT", pack.License?.Name);
        Assert.Empty(_loader.LoadWarnings);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void EveryHazard_IsShownByATranslatedName_AndTheEntryItselfStaysEnglish(string locale)
    {
        foreach (var id in Homebrew.Concat(Srd))
        {
            var original = Hazard(id);
            var shown = _registry.Localize(original, locale);

            Assert.Matches(Script(locale), shown.Name);
            Assert.DoesNotMatch("[A-Za-z]", shown.Name);
            Assert.NotEqual(original.Name, shown.Name);
            Assert.DoesNotMatch("[А-Яа-яЁёא-ת]", original.Name);
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void SearchingByTheTranslatedNameFindsTheHazard_AndSearchingByTheEnglishOneStillDoes(string locale)
    {
        var name = _registry.Localize(Hazard("spiked-pit"), locale).Name;

        Assert.Contains("spiked-pit", _registry.Search("srd5e", name, type: "hazard", culture: locale).Select(e => e.Id));
        Assert.Contains("spiked-pit", _registry.Search("srd5e", "Spiked Pit", type: "hazard", culture: locale).Select(e => e.Id));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void TheSrdHazards_AreNamedOnly_SoTheirRulesAndTextAreTheEnglishOnes(string locale)
    {
        foreach (var id in Srd)
        {
            var original = Hazard(id);
            var shown = _registry.Localize(original, locale);

            Assert.Equal(original.Description, shown.Description);
            Assert.Equal(original.Properties.Keys.Order(), shown.Properties.Keys.Where(k => k != ContentEntry.TranslationSourceKey).Order());
            foreach (var (key, value) in original.Properties) Assert.Equal(value, shown.Properties[key]);
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void TheHomebrewHazards_AreTranslatedInText_AndKeepEveryNumberKeyAndRowOfTheOriginal(string locale)
    {
        foreach (var id in Homebrew)
        {
            var original = Hazard(id);
            var shown = _registry.Localize(original, locale);

            Assert.Matches(Script(locale), shown.Description!);
            Assert.NotEqual(original.Description, shown.Description);
            foreach (var key in new[] { "trigger", "effect", "reset", "duration" })
            {
                if (!original.Properties.ContainsKey(key)) continue;
                Assert.Matches(Script(locale), shown.Properties[key].ToString()!);
            }

            // Same properties; the ones that are not text are the very values the rules read.
            Assert.Equal(original.Properties.Keys.Order(), shown.Properties.Keys.Where(k => k != ContentEntry.TranslationSourceKey).Order());
            foreach (var key in new[] { "kind", "severity", "level", "saveAttribute", "saveDc", "onSave", "complex", "damage", "conditions", "higherLevels" })
            {
                if (original.Properties.TryGetValue(key, out var value)) Assert.Equal(Describe(value), Describe(shown.Properties[key]));
            }

            // A restated list of rows: the same rows in the same order, each with the same keys, and everything but its words equal.
            foreach (var key in new[] { "detect", "disarm", "methods", "routine" })
            {
                if (!original.Properties.TryGetValue(key, out var ours)) continue;
                var english = Rows(ours);
                var translated = Rows(shown.Properties[key]);
                Assert.Equal(english.Count, translated.Count);
                for (var i = 0; i < english.Count; i++)
                {
                    Assert.Equal(english[i].Keys.Order(), translated[i].Keys.Order());
                    foreach (var field in english[i].Keys.Where(k => !RowText.Contains(k))) Assert.Equal(Describe(english[i][field]), Describe(translated[i][field]));
                    foreach (var field in english[i].Keys.Where(k => k is "name" or "text" or "description")) Assert.Matches(Script(locale), translated[i][field].ToString()!);
                }
            }
        }
    }

    [Fact]
    public void TheTranslationLeavesTheEnglishEntryUntouched()
    {
        var before = Hazard("homebrew-flooding-room").Name;
        _ = _registry.Localize(Hazard("homebrew-flooding-room"), "ru");
        _ = _registry.Localize(Hazard("homebrew-flooding-room"), "he");

        Assert.Equal("Flooding Room", before);
        Assert.Equal("Flooding Room", Hazard("homebrew-flooding-room").Name);
        Assert.Contains("water starts to pour", Hazard("homebrew-flooding-room").Description);
    }

    private static List<IDictionary<string, object>> Rows(object value) =>
        ((IEnumerable<object>)value).OfType<IDictionary<string, object>>().ToList();

    /// <summary>A value as the text of what it holds, so a list of rows compares by content.</summary>
    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => s,
        IDictionary<string, object> map => "{" + string.Join(",", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + Describe(p.Value))) + "}",
        IEnumerable<object> list => "[" + string.Join(",", list.Select(Describe)) + "]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
