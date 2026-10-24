using Codex.Core;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Loads the real <c>content/</c> folder and checks the shipped ru/he translation packs.</summary>
public class ShippedTranslationPackTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
    private readonly PluginLoader _loader;

    public ShippedTranslationPackTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("ru", "Варвар")]
    [InlineData("he", "ברברי")]
    public void ClassNames_AreTranslated_WithoutChangingTheEntry(string locale, string barbarian)
    {
        var original = _registry.GetEntry("srd5e", "class", "barbarian")!;
        var shown = _registry.Localize(original, locale);

        Assert.Equal("Barbarian", original.Name);
        Assert.Equal(barbarian, shown.Name);
        Assert.Equal(original.Description, shown.Description);                 // rules text stays English until the SRD is translated
        Assert.Equal(original.Properties["hitDie"], shown.Properties["hitDie"]); // rules data untouched
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void TranslationPacks_NameOnlyEntriesThatExist_AndLoadCleanly(string locale)
    {
        var pack = _registry.GetPacks("srd5e").Single(p => p.Locale == locale && p.TranslationOf == "codex-flavor");
        Assert.True(pack.IsTranslation);
        Assert.Equal("codex-flavor", pack.TranslationOf);

        var folder = Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "packs", pack.Id);
        var ids = Directory.GetFiles(folder, "*.yaml", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "manifest.yaml")
            .SelectMany(f => File.ReadLines(f).Where(l => l.StartsWith("- id: ")).Select(l => (Type: Path.GetFileName(Path.GetDirectoryName(f))!, Id: l[6..].Trim())))
            .ToList();

        Assert.Equal(69, ids.Count);
        Assert.All(ids, x => Assert.NotNull(_registry.GetEntry("srd5e", x.Type, x.Id)));
        Assert.Empty(_loader.LoadWarnings);
    }

    [Fact]
    public void SearchingInRussian_FindsTheClass_ByEitherName()
    {
        var byRussian = _registry.Search("srd5e", "варвар", type: "class", culture: "ru").Select(e => e.Id);
        var byEnglish = _registry.Search("srd5e", "barbarian", type: "class", culture: "ru").Select(e => e.Id);

        Assert.Contains("barbarian", byRussian);
        Assert.Contains("barbarian", byEnglish);
        Assert.Empty(_registry.Search("srd5e", "варвар", type: "class"));
    }

    [Fact]
    public void RussianSpells_AreTranslated_AndTheRulesDataIsUntouched()
    {
        var original = _registry.GetEntry("srd5e", "spell", "fireball")!;
        var shown = _registry.Localize(original, "ru");

        Assert.Equal("Огненный шар", shown.Name);
        Assert.Contains("8d6", shown.Description);                              // dice unchanged
        Assert.Equal(original.Properties["school"], shown.Properties["school"]);
        Assert.Equal(original.Properties["damageType"], shown.Properties["damageType"]);
        Assert.Equal(original.Properties["level"], shown.Properties["level"]);
        Assert.DoesNotContain("Огненн", original.Description);               // the English entry is not mutated
    }

    [Fact]
    public void RussianSpellPack_CoversEverySpell_AndCarriesTheCcByNotice()
    {
        var pack = _registry.GetPacks("srd5e").Single(p => p.Locale == "ru" && p.TranslationOf == "srd-5.2.1");
        Assert.True(pack.IsTranslation);
        Assert.True(pack.License?.Modified);

        var translated = _registry.GetEntries("srd5e", "spell").Select(e => _registry.Localize(e, "ru")).Count(e => e.Name != _registry.GetEntry("srd5e", "spell", e.Id)!.Name);
        Assert.Equal(_registry.GetEntries("srd5e", "spell").Count(), translated);
    }

    [Fact]
    public void RussianCreatures_ShowTheTranslatedStatBlock_WithoutAnEnglishCopyOfItsRows()
    {
        var original = _registry.GetEntry("srd5e", "monster", "aboleth")!;
        var shown = _registry.Localize(original, "ru");

        Assert.Equal("Аболет", shown.Name);
        Assert.Contains("Амфибия", shown.Description);
        Assert.Contains("5d10", shown.Description);                                  // dice unchanged
        Assert.Equal(original.Properties["hitPoints"], shown.Properties["hitPoints"]);
        Assert.Equal(original.Properties["challengeRating"], shown.Properties["challengeRating"]);
        Assert.Equal("Large", shown.Properties["size"]);                             // filtered on, so left in English
        Assert.Equal("srd-5.2.1-ru", shown.Properties[ContentEntry.TranslationSourceKey]); // the footer credits the translator
        Assert.False(original.Properties.ContainsKey(ContentEntry.TranslationSourceKey));
        Assert.False(shown.Properties.ContainsKey("traits"));                        // the translated description already lists them
        Assert.False(shown.Properties.ContainsKey("actions"));
        Assert.True(original.Properties.ContainsKey("actions"));                     // the rules data is untouched
        Assert.DoesNotContain("Амфибия", original.Description);
    }

    [Fact]
    public void RussianMagicItems_AreTranslated_AndTheirFilterPropertiesStayEnglish()
    {
        var original = _registry.GetEntry("srd5e", "magic-item", "adamantine-armor")!;
        var shown = _registry.Localize(original, "ru");

        Assert.Equal("Адамантиновый доспех", shown.Name);
        Assert.Contains("адамантин", shown.Description);
        Assert.Equal("Uncommon", shown.Properties["rarity"]);   // filtered on, so never replaced
        Assert.Equal("Armor", shown.Properties["category"]);
        Assert.DoesNotContain("адамантин", original.Description);

        var translated = _registry.GetEntries("srd5e", "magic-item").Count(e => _registry.Localize(e, "ru").Name != e.Name);
        Assert.True(translated >= 230, $"only {translated} magic items are translated");
    }
}
