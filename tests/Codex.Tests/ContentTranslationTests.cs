using Codex.Core;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>A translation pack overlays display text on entries other packs define, and never changes what the rules see.</summary>
public class ContentTranslationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "translation-pack-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static readonly SystemDefinition Demo = Codex.Core.Systems.SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        entityTypes:
          - type: spell
            label: Spell
            folder: spells
            fields:
              - { key: level, label: Level, type: Number }
              - { key: school, label: School, type: Dropdown, options: [Evocation, Illusion] }
              - { key: material, label: Material, type: Text }
          - type: monster
            label: Monster
            folder: monsters
            fields:
              - { key: actions, label: Actions, type: Collection }
              - { key: traits, label: Traits, type: Collection }
        """);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<ContentRegistry> LoadAsync()
    {
        Write("base/manifest.yaml", "id: base\nname: Base\nversion: 1.0.0\nsystemId: demo\n");
        Write("base/spells/bolt.yaml", "id: bolt\ntype: spell\nname: Bolt\ndescription: Rules text.\ntags: [evocation]\nproperties:\n  level: 1\n  school: Evocation\n  material: a feather\n");
        Write("base/spells/mist.yaml", "id: mist\ntype: spell\nname: Mist\ndescription: Fog.\n");
        Write("ru/manifest.yaml", "id: base-ru\nname: Base (ru)\nversion: 1.0.0\nsystemId: demo\nkind: translation\nlocale: ru\ntranslationOf: base\ndependencies: [base]\npriority: 5\n");
        Write("ru/spells/spells.yaml", """
            - id: bolt
              name: Стрела
              description: Текст правил.
              tags: [hacked]
              properties:
                level: 9
                school: Воплощение
                material: перо
            - id: nothing-here
              name: Призрак
            """);

        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        var loader = new YamlContentPackLoader(registry);
        await loader.LoadPackAsync(Path.Combine(_root, "base"), Demo);
        await loader.LoadPackAsync(Path.Combine(_root, "ru"), Demo);
        return registry;
    }

    [Fact]
    public async Task Translation_OverlaysDisplayText_AndOnlyText()
    {
        var registry = await LoadAsync();
        var original = registry.GetEntry("demo", "spell", "bolt")!;

        var shown = registry.Localize(original, "ru-RU");

        Assert.Equal("Стрела", shown.Name);
        Assert.Equal("Текст правил.", shown.Description);
        Assert.Equal("перо", shown.Properties["material"]);        // free text: translated
        Assert.Equal("Evocation", shown.Properties["school"]);      // dropdown value: the rules compare it, so never replaced
        Assert.Equal(1L, shown.Properties["level"]);                // number untouched
        Assert.Equal(new[] { "evocation" }, shown.Tags);            // tags untouched
    }

    [Fact]
    public async Task Localize_NeverMutatesTheSharedEntry_AndFallsBack()
    {
        var registry = await LoadAsync();
        var original = registry.GetEntry("demo", "spell", "bolt")!;

        _ = registry.Localize(original, "ru");

        Assert.Equal("Bolt", original.Name);
        Assert.Equal("a feather", original.Properties["material"]);
        Assert.Same(original, registry.Localize(original, "en"));                                       // English is the original
        Assert.Same(original, registry.Localize(original, "he"));                                       // no Hebrew pack: falls back
        var mist = registry.GetEntry("demo", "spell", "mist")!;
        Assert.Same(mist, registry.Localize(mist, "ru"));                                               // untranslated entry
    }

    [Fact]
    public async Task TranslationPack_NeverCreatesOrReplacesEntries()
    {
        var registry = await LoadAsync();

        Assert.Null(registry.GetEntry("demo", "spell", "nothing-here"));
        Assert.Equal("Bolt", registry.GetEntry("demo", "spell", "bolt")!.Name);
        Assert.Equal(2, registry.GetEntries("demo", "spell").Count());
    }

    [Fact]
    public async Task Search_FindsEntriesByTranslatedAndOriginalName()
    {
        var registry = await LoadAsync();

        var byRussian = registry.Search("demo", "стрел", culture: "ru").Select(e => e.Name).ToList();
        var byEnglish = registry.Search("demo", "bolt", culture: "ru").Select(e => e.Name).ToList();
        var plainEnglishUi = registry.Search("demo", "стрел").ToList();

        Assert.Equal(new[] { "Стрела" }, byRussian);
        Assert.Equal(new[] { "Стрела" }, byEnglish);
        Assert.Empty(plainEnglishUi);
    }

    [Theory]
    [InlineData("Ёж", "Еж")]
    [InlineData("שָׁלוֹם", "שלומ")]
    [InlineData("plain ascii", "plain ascii")]
    public void Fold_MakesRussianAndHebrewSearchForgiving(string input, string expected) =>
        Assert.Equal(expected, ContentRegistry.Fold(input));

    [Fact]
    public void SystemTranslation_LocalizesLabels_ButNotKeysOrOptions()
    {
        var system = Codex.Core.Systems.SystemDefinitionLoader.Parse("""
            id: demo
            name: Demo
            attributes:
              - { key: str, label: Strength, abbreviation: STR }
            conditions: [Poisoned]
            entityTypes:
              - type: spell
                label: Spell
                fields:
                  - { key: school, label: School, type: Dropdown, options: [Evocation] }
            """);
        system.Translations["ru"] = new SystemTranslation
        {
            Name = "Демо",
            Attributes = { ["str"] = new LabelTranslation { Label = "Сила", Abbreviation = "СИЛ" } },
            Conditions = { ["Poisoned"] = "Отравлен" },
            Values = { ["Evocation"] = "Воплощение" },
            EntityTypes = { ["spell"] = new TypeTranslation { Label = "Заклинание", Fields = { ["school"] = new LabelTranslation { Label = "Школа" } } } }
        };

        var ru = system.Localize("ru-RU");

        Assert.Equal("Демо", ru.Name);
        Assert.Equal("str", ru.Attributes[0].Key);
        Assert.Equal("Сила", ru.Attributes[0].Label);
        Assert.Equal("Школа", ru.EntityTypes[0].Fields[0].Label);
        Assert.Equal(new[] { "Evocation" }, ru.EntityTypes[0].Fields[0].Options);
        Assert.Equal("Воплощение", system.DisplayOption("ru", "school", "Evocation"));
        Assert.Equal("Отравлен", system.DisplayCondition("ru", "Poisoned"));
        Assert.Equal("Evocation", system.DisplayOption("he", "school", "Evocation"));
        Assert.Equal("Strength", system.Localize("en").Attributes[0].Label);
        Assert.Same(system.Localize("he"), system);
        Assert.Equal("Strength", system.Attributes[0].Label); // the original is untouched
    }

    [Fact]
    public void SystemYamlTranslationFiles_AreLoadedNextToSystemYaml()
    {
        Write("sys/system.yaml", "id: demo\nname: Demo\nattributes:\n  - { key: str, label: Strength }\n");
        Write("sys/system.ru.yaml", "name: Демо\nattributes:\n  str: { label: Сила }\n");
        Write("sys/system.he.yaml", "name: [broken\n");

        var system = Codex.Core.Systems.SystemDefinitionLoader.Load(Path.Combine(_root, "sys", "system.yaml"));

        Assert.Equal("Сила", system.Localize("ru").Attributes[0].Label);
        Assert.False(system.Translations.ContainsKey("he")); // a broken translation falls back instead of failing the system
        Assert.Equal("Demo", system.Name);
    }

    [Fact]
    public void LocalizedSystem_ShowsTranslatedValues_ButKeepsCanonicalWordsForTheRules()
    {
        var system = Codex.Core.Systems.SystemDefinitionLoader.Parse("""
            id: demo
            name: Demo
            attributes:
              - { key: str, label: Strength, abbreviation: STR }
            conditions: [Poisoned]
            actorFields:
              - { key: size, label: Size, type: Dropdown, options: [Tiny, Large] }
            """);
        system.Translations["ru"] = new SystemTranslation
        {
            Attributes = { ["str"] = new LabelTranslation { Label = "Сила", Abbreviation = "СИЛ" } },
            Conditions = { ["Poisoned"] = "Отравлен" },
            Values = { ["Tiny"] = "Крошечный", ["gp"] = "зм" },
            ActorFields = { ["size"] = new LabelTranslation { Options = { ["Large"] = "Большой" } } }
        };

        var ru = system.Localize("ru");
        var size = ru.ActorFields[0];

        Assert.Equal("Крошечный", size.Display("Tiny"));   // shared value
        Assert.Equal("Большой", size.Display("Large"));    // this field's own option
        Assert.Equal("Huge", size.Display("Huge"));        // untranslated: unchanged
        Assert.Equal("25 зм", size.Display("25 gp"));      // a price: amount kept, coin translated
        Assert.Equal("25 sp", size.Display("25 sp"));      // coin without a translation: unchanged
        Assert.Equal(new[] { "Tiny", "Large" }, size.Options);
        Assert.Equal("Отравлен", ru.DisplayCondition("Poisoned"));
        Assert.Equal("Крошечный", ru.DisplayValue("Tiny"));   // a shared word the console shows (a rest, a damage type)
        Assert.Equal("Huge", ru.DisplayValue("Huge"));
        Assert.Equal("Tiny", system.DisplayValue("Tiny"));
        Assert.Equal("Poisoned", ru.Conditions[0]);
        Assert.Equal("Strength", ru.Attributes[0].CanonicalLabel);
        Assert.Equal("STR", ru.Attributes[0].CanonicalAbbreviation);
        Assert.Equal("Poisoned", system.DisplayCondition("Poisoned"));
    }

    private const string SkillsYaml = """
        id: demo
        name: Demo
        attributes:
          - { key: str, label: Strength, abbreviation: STR, modifier: half-minus-five }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
        entityTypes:
          - { type: feat, label: Feat }
        sheetRules:
          derived:
            - { key: proficiencyBonus, label: Proficiency Bonus, formula: "2 + (level - 1) / 4" }
          lists:
            - { key: feats, label: Feats, targetEntityType: feat }
          skills:
            label: Skills
            list:
              - { key: athl, label: Athletics, attribute: str }
            passives:
              - { key: passiveAthletics, label: Passive Athletics, skill: athl }
        """;

    private static SystemDefinition TranslatedSkillsSystem()
    {
        var system = Codex.Core.Systems.SystemDefinitionLoader.Parse(SkillsYaml);
        system.Translations["ru"] = new SystemTranslation
        {
            SheetRules =
            {
                Derived = { ["proficiencyBonus"] = "Бонус мастерства" },
                Lists = { ["feats"] = "Черты" },
                Skills = { ["athl"] = "Атлетика" },
                Passives = { ["passiveAthletics"] = "Пассивная Атлетика" },
                SkillsLabel = "Навыки"
            }
        };
        return system;
    }

    [Fact]
    public void SheetRuleLabels_AreTranslated_WithoutTouchingTheOriginal()
    {
        var system = TranslatedSkillsSystem();
        var ru = system.Localize("ru");

        Assert.Equal("Бонус мастерства", ru.SheetRules!.Derived[0].Label);
        Assert.Equal("2 + (level - 1) / 4", ru.SheetRules.Derived[0].Formula);
        Assert.Equal("Черты", ru.SheetRules.Lists[0].Label);
        Assert.Equal("Навыки", ru.SheetRules.Skills!.Label);
        Assert.Equal("Атлетика", ru.SheetRules.Skills.List[0].Label);
        Assert.Equal("Athletics", ru.SheetRules.Skills.List[0].CanonicalLabel);
        Assert.Equal("Proficiency Bonus", system.SheetRules!.Derived[0].Label);
        Assert.Equal("Athletics", system.SheetRules.Skills!.List[0].Label);
    }

    [Fact]
    public void Skills_ListedByTheirEnglishLabel_AreStillProficient_OnALocalizedSystem()
    {
        var ru = TranslatedSkillsSystem().Localize("ru");
        var engine = new Codex.Core.Rules.SheetRuleEngine(ru, Codex.Core.Rules.NullEntryResolver.Instance);
        var sheet = new Dictionary<string, object>
        {
            ["level"] = 5L, ["str"] = 14L, ["attrMethod"] = "manual",
            ["skillProficiencies"] = new List<object> { "Athletics" }
        };

        var result = engine.Evaluate(sheet);

        var skill = Assert.Single(result.Skills!);
        Assert.Equal("Атлетика", skill.Label);
        Assert.True(skill.Proficient);
        Assert.Equal(2 + 3, skill.Bonus); // Strength 14 is +2, proficiency at level 5 is +3
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("is not a skill"));
    }

    [Fact]
    public async Task TranslatedDescription_DropsTheEnglishRowsItAlreadyContained_ButKeepsTranslatedRows()
    {
        Write("base/manifest.yaml", "id: base\nname: Base\nversion: 1.0.0\nsystemId: demo\n");
        Write("base/monsters/imp.yaml", """
            id: imp
            type: monster
            name: Imp
            description: "**Sting.** Hits hard.\n\n**Fly.** It flies."
            properties:
              actions:
                - { name: Sting, description: Hits hard. }
              traits:
                - { name: Fly, description: It flies. }
            """);
        Write("ru/manifest.yaml", "id: base-ru\nname: Base (ru)\nversion: 1.0.0\nsystemId: demo\nkind: translation\nlocale: ru\ntranslationOf: base\ndependencies: [base]\npriority: 5\n");
        Write("ru/monsters/imp.yaml", """
            - id: imp
              name: Бес
              description: "**Жало.** Бьёт сильно.\n\n**Полёт.** Он летает."
              properties:
                traits:
                  - { name: Полёт, description: Он летает. }
            """);

        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        var loader = new YamlContentPackLoader(registry);
        await loader.LoadPackAsync(Path.Combine(_root, "base"), Demo);
        await loader.LoadPackAsync(Path.Combine(_root, "ru"), Demo);
        var original = registry.GetEntry("demo", "monster", "imp")!;

        var shown = registry.Localize(original, "ru");

        Assert.False(shown.Properties.ContainsKey("actions"));       // English rows the translated description makes redundant
        Assert.True(shown.Properties.ContainsKey("traits"));         // the translation supplied these rows itself
        Assert.Equal("Полёт", ((IEnumerable<object>)shown.Properties["traits"]).OfType<IDictionary<string, object>>().Single()["name"]);
        Assert.True(original.Properties.ContainsKey("actions"));     // the shared entry is untouched
        Assert.Equal(registry.Localize(original, "en").Properties.Count, original.Properties.Count);
    }
}
