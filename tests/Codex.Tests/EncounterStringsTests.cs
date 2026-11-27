using System.Text.RegularExpressions;
using Codex.Localization;

namespace Codex.Tests;

/// <summary>The encounter builder's words in Russian and Hebrew (ENCOUNTER_BUILDER_DESIGN.md 20): one word for an encounter, no English left in a translation, plurals that keep the count.
/// <see cref="LocalizationTests"/> already makes the three languages agree on keys and placeholders.</summary>
public class EncounterStringsTests
{
    private static readonly string[] Prefixes =
    [
        "Encounters.", "Combat.Prepared.", "Event.Encounter.", "Event.Type.EncounterEnded", "Page.Campaign.Section.Encounters", "WorldChat.Proposal.Encounter.",
        "WorldChat.Kind.encounter", "WorldChat.Edit.Encounter", "WorldChat.Proposal.DeletesEncounter", "WorldChat.Tool.Encounter", "WorldChat.Tool.Hazards", "WorldChat.Starter.encounter.",
        "WorldBuilder.Error.Encounter", "Combat.StartEncounter", "Combat.NoEncounterHint", "Combat.EndEncounter", "Combat.AddToEncounter", "Combat.AddHazardToOrder", "Combat.QuickAddPlaceHazards"
    ];

    private static IEnumerable<string> Keys() => Localizer.Keys("en").Where(k => Prefixes.Any(k.StartsWith)).Order();

    /// <summary>The words of a message, without its placeholders and the plural syntax (<c>{0, plural, one{...}</c>), which are English by design.</summary>
    private static string Words(string message)
    {
        var text = Regex.Replace(message, @"\{\d+(,\s*plural,)?", " ");
        text = Regex.Replace(text, @"\b(one|two|few|many|other)\{", " ");
        return text.Replace("#", " ").Replace("{", " ").Replace("}", " ");
    }

    [Fact]
    public void TheEncounterStrings_AreFound_InEveryLanguage()
    {
        Assert.True(Keys().Count() > 150);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void ATranslatedEncounterString_HasNoEnglishWordsLeft(string language)
    {
        var l = Localizer.For(language);
        // "AI" is the name of the feature in Hebrew, as in the rest of the app.
        var stray = Keys().Where(k => Regex.IsMatch(Words(l.Get(k)).Replace("AI", ""), "[A-Za-z]")).ToList();

        Assert.Empty(stray);
    }

    [Fact]
    public void Hebrew_SaysMifgashKrav_ForAnEncounter_EverywhereAndNeverIimut()
    {
        var he = Localizer.For("he");
        foreach (var key in Localizer.Keys("he")) Assert.DoesNotContain("עימות", he.Get(key));

        Assert.Equal("התחלת מפגש קרב", he.Get("Combat.StartEncounter"));
        Assert.Contains("מפגש קרב", he.Get("Combat.NoEncounterHint"));
        // A bare "the encounter" in the table's messages is the session in Hebrew: they name the encounter in full.
        foreach (var key in new[] { "Combat.Prepared.Started", "Combat.Prepared.StartedSome", "Combat.Prepared.RunNotRecorded", "Combat.Prepared.Refused.TableClosed", "Event.Encounter.Ended", "Event.Encounter.StartRefused", "Event.Encounter.StartEmpty" })
            Assert.Contains("מפגש הקרב", he.Get(key, 1, "x"));
        Assert.Contains("מפגש קרב", he.Get("Event.Encounter.None"));
        Assert.Contains("מפגש קרב", he.Get("Event.Type.EncounterEnded"));
    }

    [Fact]
    public void Russian_SaysStolknovenie_NeverSchvatka_ForAnEncounter()
    {
        var ru = Localizer.For("ru");
        foreach (var key in Keys()) Assert.DoesNotContain("схват", ru.Get(key).ToLowerInvariant());
        Assert.Equal("Конец столкновения", ru.Get("Event.Type.EncounterEnded"));
    }

    [Theory]
    [InlineData(1, "Проведено 5 мая")]
    [InlineData(2, "Проведено 2 раза, последний раз 5 мая")]
    [InlineData(5, "Проведено 5 раз, последний раз 5 мая")]
    [InlineData(21, "Проведено 21 раз, последний раз 5 мая")]
    [InlineData(22, "Проведено 22 раза, последний раз 5 мая")]
    public void RussianRunCount_KeepsItsNumber_ForOneWhichIsAlso21(int runs, string expected) =>
        Assert.Equal(expected, Localizer.For("ru").Get("Encounters.RanOn", runs, "5 мая"));

    [Theory]
    [InlineData("ru", 1, "Столкновение закончилось через 1 раунд.")]
    [InlineData("ru", 3, "Столкновение закончилось через 3 раунда.")]
    [InlineData("ru", 7, "Столкновение закончилось через 7 раундов.")]
    [InlineData("he", 1, "מפגש הקרב הסתיים אחרי סבב אחד.")]
    [InlineData("he", 2, "מפגש הקרב הסתיים אחרי שני סבבים.")]
    [InlineData("he", 6, "מפגש הקרב הסתיים אחרי 6 סבבים.")]
    public void TheEndOfAnEncounter_CountsItsRounds_InEveryLanguage(string language, int rounds, string expected) =>
        Assert.Equal(expected, Localizer.For(language).Get("Event.Encounter.Ended", rounds));

    [Fact]
    public void Hebrew_DoesNotGlueAOnePrefixToAPlaceholder_ThatHoldsAUserSuppliedName()
    {
        // A Latin name or a date after a bare prefix letter ("ב{0}") reads wrong in a right-to-left line; the words name the thing instead ("במקום {0}"), or take a hyphen ("ב-{1}").
        var he = Localizer.For("he");
        var glued = Keys().Where(k => Regex.IsMatch(he.Get(k), @"(^|[\s(])[במלכה]\{\d+\}")).ToList();

        Assert.Empty(glued);
    }

    [Fact]
    public void ARussianBandLabel_AgreesWithDifficulty_AndTheAboveBelowWordsQuoteIt_SoTheSummaryHasOneColon()
    {
        var ru = Localizer.For("ru");
        Assert.Equal("Выше «Высокая»", ru.Get("Encounters.Difficulty.Above", "Высокая"));
        Assert.Equal("Выше «Высокая»: 2100 ОО для 5 персонажей уровня 3", ru.Get("Encounters.Difficulty.Summary", ru.Get("Encounters.Difficulty.Above", "Высокая"), "2100 ОО", ru.Get("Encounters.Difficulty.Party", 5, 3)));
        Assert.Equal("מעל לרמה גבוהה", Localizer.For("he").Get("Encounters.Difficulty.Above", "גבוהה"));
        Assert.Equal("Низкая: до 30", ru.Get("Encounters.Difficulty.UpTo", "Низкая", "30"));
        // The party is in the genitive after "для".
        Assert.Equal("Умеренная: 250 ОО для 4 персонажей уровня 3", ru.Get("Encounters.Difficulty.Summary", "Умеренная", "250 ОО", ru.Get("Encounters.Difficulty.Party", 4, 3)));
        Assert.Equal("Оценка для 1 персонажа уровня 3 (другой отряд).", ru.Get("Encounters.Difficulty.Source.Override", ru.Get("Encounters.Difficulty.Party", 1, 3)));
    }
}
