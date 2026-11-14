using Codex.Core;
using Codex.Plugin.Abstractions;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>A roll's line in the table's log is said in each reader's language: the roll's name and the bonuses it counted are parts the reader's
/// terms translate, the stored English text is the fallback, and an entry from before the parts existed is shown as it was.</summary>
public class RollLogTextTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public RollLogTextTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static RollLogEntry Resistance() => new()
    {
        Label = "Resistance roll (+1d From hiding or a trap)",
        Name = "Resistance roll",
        Pool = true,
        Bonuses = { new RollLogBonus { Source = "Ambush", Label = "From hiding or a trap", Amount = 1 } }
    };

    private EventTermText Terms(string culture) =>
        EventTerms.For(_loader.GetDefinition("fitd-srd"), Localizer.For(culture), new Dictionary<string, string>(), _registry);

    [Theory]
    [InlineData("ru", "Бросок сопротивления (+1d Из укрытия или из ловушки)")]
    [InlineData("he", "הטלת התנגדות (+1d ממסתור או ממלכודת)")]
    public void ARollLine_IsInTheReadersLanguage_NameAndBonus(string culture, string expected) =>
        Assert.Equal(expected, RollLogText.Label(Resistance(), Terms(culture)));

    [Theory]
    [InlineData("ru", "Бросок сопротивления (+1d Из укрытия или из ловушки)")]
    [InlineData("he", "הטלת התנגדות (+1d ממסתור או ממלכודת)")]
    public void ARollLine_IsTheSame_WhenTheHostHoldsTheLocalizedCopyOfTheSystem(string culture, string expected)
    {
        var localized = _loader.GetDefinition("fitd-srd")!.Localize(culture);
        var terms = EventTerms.For(localized, Localizer.For(culture), new Dictionary<string, string>(), _registry);
        Assert.Equal(expected, RollLogText.Label(Resistance(), terms));
    }

    [Fact]
    public void English_AndNoTerms_ShowTheStoredText()
    {
        var entry = Resistance();
        Assert.Equal(entry.Label, RollLogText.Label(entry, Terms("en")));
        Assert.Equal(entry.Label, RollLogText.Label(entry, null));
    }

    [Fact]
    public void WhatHasNoTranslation_StaysEnglish_PartByPart()
    {
        var entry = new RollLogEntry
        {
            Label = "Mystery roll (+2 Nobody's feature)",
            Name = "Mystery roll",
            Bonuses = { new RollLogBonus { Source = "Nobody's feature", Label = "Nobody's feature", Amount = 2 } }
        };
        Assert.Equal("Mystery roll (+2 Nobody's feature)", RollLogText.Label(entry, Terms("ru")));
    }

    [Fact]
    public void AnOldEntry_WithoutParts_IsShownAsItWas()
    {
        var old = new RollLogEntry { Label = "Resistance roll (+1d From hiding or a trap)" };
        Assert.Equal(old.Label, RollLogText.Label(old, Terms("ru")));
        Assert.Null(RollLogText.Label(new RollLogEntry(), Terms("ru")));
    }

    [Fact]
    public void ABonusTermsKey_CarriesTheLabelAndTheSource_AndIsNotATranslationOfAnyId()
    {
        var term = EventTerm.Bonus("From hiding or a trap", "Ambush");
        Assert.Equal(EventTerm.BonusKind, term.Kind);
        Assert.Equal("From hiding or a trap", term.Text);
        Assert.Equal("Из укрытия или из ловушки", Terms("ru")(term.Kind, term.Key));
        Assert.Null(Terms("ru")(EventTerm.BonusKind, "no separator"));
    }

    [Theory]
    [InlineData("ru", "Prowl", "Подкрадывание")]
    [InlineData("ru", "prowess", "Сноровка")]
    [InlineData("ru", "Assist, push or bargain", "Помощь, нажим или сделка")]
    [InlineData("ru", "risky", "рискованная")]
    [InlineData("ru", "position", "Позиция")]
    [InlineData("he", "Prowl", "התגנבות")]
    [InlineData("he", "prowess", "כושר")]
    [InlineData("he", "great", "גדולה")]
    [InlineData("he", "effect", "השפעה")]
    public void TheSheetsWords_AreShownInTheReadersLanguage_OnEitherCopyOfTheSystem(string culture, string word, string expected)
    {
        var system = _loader.GetDefinition("fitd-srd")!;
        Assert.Equal(expected, system.DisplayOption(culture, string.Empty, word));
        Assert.Equal(expected, system.Localize(culture).DisplayOption(culture, string.Empty, word));
        Assert.Equal(word, system.DisplayOption("en", string.Empty, word)); // only what is shown: English and the stored word are unchanged
    }

    [Fact]
    public void SheetFeatureNames_AreTranslatedWhole_OrPartByPart_AndEnglishIsUnchanged()
    {
        var russian = RollBonusNames.For(_registry, _loader.GetDefinition("fitd-srd"), "ru");
        Assert.Equal("Засада", russian.Source("Ambush"));
        Assert.Equal("Засада: Nobody's option", russian.Source("Ambush: Nobody's option"));
        Assert.Equal("Ambush", RollBonusNames.For(_registry, _loader.GetDefinition("fitd-srd"), "en").Source("Ambush"));
    }
}
