using Codex.Plugin.Abstractions;
using Codex.TranslationCheck;

namespace Codex.Tests;

public class TranslationCheckerTests
{
    private const string English = "**Casting Time:** Action\n\nA bolt deals 3d6 fire damage to a creature within 120 feet.\n\n- Save DC 15\n- Range 60";
    private const string Russian = "**Время накладывания:** Действие\n\nМолния наносит 3d6 урона огнём существу в пределах 120 футов.\n\n- Спасбросок СЛ 15\n- Дистанция 60";

    private static readonly IReadOnlySet<string> Fields = new HashSet<string> { "castingTime", "higherLevel" };

    private static ContentEntry Source() => new()
    {
        Id = "bolt", Type = "spell", Name = "Fire Bolt", Description = English,
        Properties = { ["castingTime"] = "Action" }
    };

    private static Dictionary<string, object> Item(string description, string name = "Огненная молния", Dictionary<string, object>? properties = null)
    {
        var item = new Dictionary<string, object> { ["id"] = "bolt", ["name"] = name, ["description"] = description };
        if (properties != null) item["properties"] = properties;
        return item;
    }

    private static List<Finding> Check(Dictionary<string, object> item, string lang = "ru", ContentEntry? source = null, Dictionary<string, string>? glossary = null) =>
        new TranslationChecker(lang, glossary).Check(source ?? Source(), "spell", item, Fields);

    private static bool Has(List<Finding> findings, string code, Severity severity = Severity.Error) =>
        findings.Any(f => f.Code == code && f.Severity == severity);

    [Fact]
    public void A_faithful_translation_has_no_findings() => Assert.Empty(Check(Item(Russian)));

    [Fact]
    public void A_missing_number_is_an_error()
    {
        var findings = Check(Item(Russian.Replace("120 футов", "сто футов")));
        Assert.True(Has(findings, "numbers-missing"));
    }

    [Fact]
    public void An_invented_number_is_only_a_warning()
    {
        var findings = Check(Item(Russian + "\n- Ещё 7"));
        Assert.True(Has(findings, "numbers-extra", Severity.Warning));
        Assert.False(Has(findings, "numbers-missing"));
    }

    [Fact]
    public void Changed_dice_are_an_error()
    {
        Assert.True(Has(Check(Item(Russian.Replace("3d6", "2d6"))), "dice"));
        Assert.True(Has(Check(Item(Russian.Replace("3d6", "3к6"))), "dice"));
    }

    [Fact]
    public void Dice_case_does_not_matter() => Assert.False(Has(Check(Item(Russian.Replace("3d6", "3D6"))), "dice"));

    [Fact]
    public void Dropped_bullets_and_headings_are_errors()
    {
        Assert.True(Has(Check(Item(Russian.Replace("\n- Дистанция 60", ""))), "numbers-missing")); // the 60 went with it
        Assert.True(Has(Check(Item(Russian.Replace("- Спасбросок СЛ 15", "Спасбросок СЛ 15"))), "structure"));
        Assert.True(Has(Check(Item("# Заголовок\n" + Russian)), "structure"));
    }

    [Fact]
    public void Moved_bold_only_warns()
    {
        var findings = Check(Item(Russian.Replace("**", "")));
        Assert.True(Has(findings, "structure", Severity.Warning));
        Assert.False(Has(findings, "structure"));
    }

    [Fact]
    public void Hebrew_letters_in_a_russian_text_are_an_error() =>
        Assert.True(Has(Check(Item(Russian + " שלום")), "script"));

    [Fact]
    public void Cyrillic_in_a_hebrew_text_is_an_error() =>
        Assert.True(Has(Check(Item("שלום עולם " + Russian), "he"), "script"));

    [Fact]
    public void A_word_mixing_both_scripts_is_an_error() =>
        Assert.True(Has(Check(Item(Russian + " приветשלום")), "script"));

    [Fact]
    public void A_long_text_left_in_english_is_an_error() =>
        Assert.True(Has(Check(Item(English)), "script"));

    [Fact]
    public void Fields_the_rules_read_cannot_be_translated()
    {
        var item = Item(Russian);
        item["tags"] = new List<object> { "огонь" };
        item["properties"] = new Dictionary<string, object> { ["school"] = "Воплощение", ["castingTime"] = "Действие" };
        var findings = Check(item);
        Assert.True(Has(findings, "forbidden-field"));
        Assert.True(Has(findings, "forbidden-property"));
        Assert.Equal(1, findings.Count(f => f.Code == "forbidden-property"));
    }

    [Fact]
    public void Flavor_text_is_allowed_and_needs_no_description()
    {
        var item = new Dictionary<string, object> { ["id"] = "bolt", ["name"] = "Огненная молния", ["flavor"] = "Яркая вспышка." };
        Assert.Empty(Check(item));
    }

    [Fact]
    public void A_missing_description_name_or_id_is_an_error()
    {
        Assert.True(Has(Check(new() { ["id"] = "bolt", ["name"] = "Молния" }), "no-description"));
        Assert.True(Has(Check(Item(Russian, name: "")), "no-name"));
        Assert.True(Has(Check(new() { ["name"] = "Молния" }), "no-id"));
        Assert.True(Has(new TranslationChecker("ru").Check(null, "spell", Item(Russian), Fields), "unknown-id"));
    }

    [Fact]
    public void An_unchanged_name_warns() =>
        Assert.True(Has(Check(Item(Russian, name: "Fire Bolt")), "name-unchanged", Severity.Warning));

    [Fact]
    public void Property_text_keeps_its_numbers()
    {
        var source = Source();
        source.Properties["range"] = "120 feet";
        var fields = new HashSet<string> { "range" };
        var bad = new TranslationChecker("ru").Check(source, "spell", Item(Russian, properties: new() { ["range"] = "90 футов" }), fields);
        Assert.True(Has(bad, "numbers-missing"));
        var good = new TranslationChecker("ru").Check(source, "spell", Item(Russian, properties: new() { ["range"] = "120 футов" }), fields);
        Assert.Empty(good);
    }

    [Fact]
    public void Agreed_terms_are_found_by_stem_so_inflection_passes()
    {
        var glossary = new Dictionary<string, string> { ["damage"] = "урон", ["creature"] = "существо" };
        Assert.Empty(Check(Item(Russian), glossary: glossary)); // "урона", "существу" share the stem
        var other = Check(Item(Russian.Replace("огнём", "пламенем").Replace("существу", "персонажу")), glossary: new() { ["creature"] = "существо" });
        Assert.True(Has(other, "terms", Severity.Warning));
    }

    [Fact]
    public void Stems_cut_short_words_less()
    {
        Assert.Equal("урон", TranslationChecker.Stem("урон"));       // short: kept whole
        Assert.Equal("кос", TranslationChecker.Stem("костер"[..5])); // 5 letters: two cut
        Assert.Equal("сущес", TranslationChecker.Stem("существо"));  // 8 letters: three cut
        Assert.Equal("сущес", TranslationChecker.Stem("существо магическое"));
    }
}
