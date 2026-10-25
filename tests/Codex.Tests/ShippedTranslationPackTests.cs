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
        Assert.Equal(original.Properties["hitDie"], shown.Properties["hitDie"]); // rules data untouched
        if (locale == "he") Assert.Equal(original.Description, shown.Description); // no Hebrew rules text yet
        else Assert.NotEqual(original.Description, shown.Description);
    }

    [Fact]
    public void RussianClasses_MergeTheRulesTranslationWithTheFlavorText_AndCreditBothPacks()
    {
        var original = _registry.GetEntry("srd5e", "class", "barbarian")!;
        var shown = _registry.Localize(original, "ru");

        Assert.Equal("Варвар", shown.Name);
        Assert.Contains("Основная характеристика", shown.Description);
        Assert.False(string.IsNullOrWhiteSpace(shown.Properties["flavor"] as string));          // from the flavor pack
        Assert.Equal(new[] { "codex-flavor-ru", "srd-5.2.1-ru" }, shown.Properties[ContentEntry.TranslationSourceKey].ToString()!.Split(',').OrderBy(s => s));

        var features = ((IEnumerable<object>)shown.Properties["features"]).OfType<IDictionary<string, object>>().ToList();
        var english = ((IEnumerable<object>)original.Properties["features"]).OfType<IDictionary<string, object>>().ToList();
        Assert.Equal(english.Count, features.Count);                                            // every row translated, none lost
        Assert.Equal("Ярость", features[0]["name"]);
        Assert.Equal(english.Select(r => r["level"].ToString()), features.Select(r => r["level"].ToString())); // levels untouched
        Assert.Equal("Rage", english[0]["name"]);                                               // the shared entry is untouched
    }

    [Fact]
    public void RussianSpeciesAndSubclasses_ShowTheTranslatedText_WithoutEnglishRows()
    {
        var dwarf = _registry.Localize(_registry.GetEntry("srd5e", "species", "dwarf")!, "ru");
        Assert.Equal("Дварф", dwarf.Name);
        Assert.Contains("Тёмное зрение", dwarf.Description);
        Assert.False(dwarf.Properties.ContainsKey("traits"));                                   // the translated description lists them

        var champion = _registry.Localize(_registry.GetEntry("srd5e", "subclass", "champion")!, "ru");
        var rows = ((IEnumerable<object>)champion.Properties["features"]).OfType<IDictionary<string, object>>().ToList();
        Assert.Equal("Чемпион", champion.Name);
        Assert.All(rows, r => Assert.Matches("[А-Яа-я]", r["name"].ToString()!));
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

        Assert.Equal(locale == "ru" ? 991 : 69, ids.Count);   // ru also translates the spell, creature and magic-item flavor
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
        Assert.Equal("codex-flavor-ru,srd-5.2.1-ru", shown.Properties[ContentEntry.TranslationSourceKey]); // the footer credits both translators
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

    [Fact]
    public void RussianEquipmentRulesAndConditions_AreTranslated_WithTheEnglishNumbersAndFiltersKept()
    {
        var battleaxe = _registry.GetEntry("srd5e", "equipment", "battleaxe")!;
        var shown = _registry.Localize(battleaxe, "ru");
        Assert.Equal("Боевой топор", shown.Name);
        Assert.Contains("1d8 рубящий", shown.Description);
        Assert.Contains("10 зм", shown.Description);
        Assert.Equal("10 gp", shown.Properties["cost"]);       // filtered on, so never replaced
        Assert.Equal("Topple", shown.Properties["mastery"]);

        var poison = _registry.Localize(_registry.GetEntry("srd5e", "equipment", "torpor")!, "ru");
        Assert.Equal("Оцепенение", poison.Name);

        var skill = _registry.Localize(_registry.GetEntry("srd5e", "rule", "skill-arcana")!, "ru");
        Assert.Equal("Магия", skill.Name);
        Assert.Equal("Skill", skill.Properties["category"]);
        Assert.Equal("Звук", _registry.Localize(_registry.GetEntry("srd5e", "rule", "damage-type-thunder")!, "ru").Name);

        var blinded = _registry.Localize(_registry.GetEntry("srd5e", "condition", "blinded")!, "ru");
        Assert.Equal("Ослеплённый", blinded.Name);
        Assert.Contains("состоянии «Ослеплённый»", blinded.Description);
        Assert.Equal("Недееспособный", _registry.Localize(_registry.GetEntry("srd5e", "condition", "incapacitated")!, "ru").Name);

        var equipment = _registry.GetEntries("srd5e", "equipment").Count(e => _registry.Localize(e, "ru").Name != e.Name);
        Assert.True(equipment >= 180, $"only {equipment} pieces of equipment are translated");
        var rules = _registry.GetEntries("srd5e", "rule").Count(e => _registry.Localize(e, "ru").Name != e.Name);
        Assert.True(rules >= 85, $"only {rules} rule entries are translated");
    }

    [Fact]
    public void RussianCreatureForms_ReactionsAndMulticlassing_AreCovered()
    {
        var hybrid = _registry.Localize(_registry.GetEntry("srd5e", "monster", "werewolf-hybrid")!, "ru");
        Assert.Equal("Вервольф, гибридная форма", hybrid.Name);
        Assert.Contains("Укус", hybrid.Description);

        var human = _registry.Localize(_registry.GetEntry("srd5e", "monster", "werewolf-human")!, "ru");
        Assert.DoesNotContain("Укус", human.Description);   // the human form has no bite in our English either

        var knight = _registry.Localize(_registry.GetEntry("srd5e", "monster", "knight")!, "ru");
        Assert.Contains("Парирование", knight.Description);  // OmnisGM's text lacks the Reaction, we added it

        var barbarian = _registry.Localize(_registry.GetEntry("srd5e", "class", "barbarian")!, "ru");
        Assert.Equal("Требование: СИЛ 13+. Получаемые владения: Щиты, Воинское оружие", barbarian.Properties["multiclassing"]);

        var untranslated = _registry.GetEntries("srd5e", "monster").Count(e => _registry.Localize(e, "ru").Name == e.Name);
        Assert.True(untranslated <= 5, $"{untranslated} creatures are still English");
    }
}
