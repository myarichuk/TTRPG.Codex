using System.Text.RegularExpressions;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>Step H8 of HAZARDS_DESIGN.md: every string the hazards track added is in English, Russian and Hebrew; the words for the same thing are the same word across the panel, the
/// wizard, the world builder, the book and the system's own translation; the Hebrew log lines do not depend on a name's gender; and the checks the wizard and the world builder's card show
/// are said in the reader's language.</summary>
public class HazardStringsTests
{
    private static readonly SystemDefinition Srd5e = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]", RegexOptions.Compiled);
    private static readonly Regex HebrewLetters = new("[א-ת]", RegexOptions.Compiled);

    private static IEnumerable<string> HazardKeys(string culture) =>
        Localizer.Keys(culture).Where(k => k.Contains("Hazard", StringComparison.Ordinal) || k.StartsWith("Event.Check.", StringComparison.Ordinal) || k.StartsWith("Wizard.Hazard", StringComparison.Ordinal)
            || k.StartsWith("Combat.QuickAdd", StringComparison.Ordinal) || k == "WorldChat.Proposal.Concerns").Order(StringComparer.Ordinal);

    // A Hebrew word, with the one-letter prefixes (ב, ל, ה, ו, מ, ש) it may carry, and not inside a longer word.
    private static Regex HebrewWord(string stems) => new($"(?<![א-ת])[בלהומש]?(?:{stems})(?![א-ת])", RegexOptions.Compiled);

    // Verbs and adjectives that agree with their subject, right after a placeholder that holds a name (a creature's or a hazard's): "{1} מוצא אותה", "{0} נוטרלה".
    // LOCALIZATION.md: Hebrew event sentences avoid gendered verbs ("{0}: …", construct nouns).
    private static readonly Regex GenderedAfterName = new(
        @"\{\d+\}\s+(?:(?:כבר|לא|עכשיו)\s+)*(?:מוצא|מוצאת|יודע|יודעת|עושה|נפגע|נפגעה|עובר|עוברת|סופג|סופגת|חסין|חסינה|עמיד|עמידה|פגיע|פגיעה|מת|מתה|נופל|נופלת|מאבד|מאבדת|חייב|חייבת|מקבל|מקבלת|נוטרל|נוטרלה|מופעל|מופעלת|מבצע|מבצעת|מפעיל|מפעילה|יוצא|יוצאת|חוזר|חוזרת|נכנס|נכנסת|מוסתר|מוסתרת|זוהה|זוהתה|הופעל|הופעלה)(?![א-ת])",
        RegexOptions.Compiled);

    // A pronoun that points back at the hazard's name ("it"), which makes the name's gender matter just as a verb does.
    private static readonly Regex PronounOfTheName = HebrewWord("אותה|קיומה|שלה|היא");

    internal static bool IsGendered(string value) => GenderedAfterName.IsMatch(value);

    [Fact]
    public void TheGenderGuard_CatchesTheFormsItIsFor()
    {
        Assert.True(IsGendered("{0}: {1} מוצא אותה ({2})."));
        Assert.True(IsGendered("{0} נוטרלה."));
        Assert.True(IsGendered("{0} כבר נוטרלה."));
        Assert.True(IsGendered("{0}: {1} לא נפגע."));
        Assert.False(IsGendered("{0}: {1} — זיהוי מוצלח ({2})."));
        Assert.False(IsGendered("{0}: הסכנה נוטרלה."));
        Assert.Matches(PronounOfTheName, "{0}: היא השתנתה מאז");
        Assert.DoesNotMatch(PronounOfTheName, "{0}: הסכנה השתנתה מאז");
    }

    [Fact]
    public void EveryHebrewHazardLine_SaysANameWithoutAGenderedVerb()
    {
        var he = Localizer.For("he");
        foreach (var key in HazardKeys("he"))
        {
            var value = he[key];
            Assert.False(IsGendered(value), $"he: {key} has a gendered word after a name: {value}");
            if (key.StartsWith("Event.", StringComparison.Ordinal)) Assert.False(PronounOfTheName.IsMatch(value), $"he: {key} points back at the name with a gendered pronoun: {value}");
        }
    }

    [Fact]
    public void EveryHazardKey_IsInRussianAndHebrew_AsWordsOfThatLanguage()
    {
        var en = HazardKeys("en").ToList();
        Assert.NotEmpty(en);
        Assert.Contains("Combat.QuickAddHazards", en);
        Assert.Contains("WorldChat.Proposal.Concerns", en);
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
        // The hazard is a feminine word in both languages and the panel's word: the Hebrew wizard and book once said "מפגע"; "DC" is "Сл" in Russian and "דרגת קושי" in Hebrew;
        // a passive score is "קבועה/קבוע" (system.he.yaml: הבחנה קבועה); the routine is the system's "סדר פעולות" / "Порядок действий"; a level is "דרגה" (the glossary), never "רמה".
        var level = HebrewWord("רמה|רמות|רמת");
        var routine = HebrewWord("שגרה|שגרת");
        foreach (var key in HazardKeys("he"))
        {
            var value = Localizer.For("he")[key];
            Assert.DoesNotContain("מפגע", value);
            Assert.DoesNotContain("איתור", value);
            Assert.DoesNotContain("DC", value);
            Assert.DoesNotContain("פסיבי", value);
            Assert.DoesNotContain("סביל", value);
            Assert.False(level.IsMatch(value), $"he: {key} says רמה for a level: {value}");
            Assert.False(routine.IsMatch(value), $"he: {key} says שגרה for the routine: {value}");
        }

        foreach (var key in HazardKeys("ru"))
        {
            var value = Localizer.For("ru")[key];
            Assert.DoesNotContain("СЛ ", value);
            Assert.DoesNotContain("DC", value);
            Assert.DoesNotContain("по раундам", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("шаг действий", value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void TheWizardsWords_AreTheSystemTranslationsWords(string culture)
    {
        var type = Srd5e.Localize(culture).FindType("hazard")!;
        string Label(string key) => type.Fields.Single(f => f.Key == key).Label;
        var said = Localizer.For(culture);

        Assert.Equal(Label("routine"), said["Wizard.Hazard.Step.Routine"]);
        Assert.StartsWith(Label("level"), said["Wizard.Hazard.Col.Levels"], StringComparison.Ordinal);
        Assert.StartsWith(Label("routine"), said["Combat.Hazard.Routine"], StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnglishTemplatesOfTheChecks_AreTheEnglishResourceStrings()
    {
        var en = Localizer.For("en");
        foreach (var (key, template) in HazardMessages.English)
        {
            Assert.Equal(template.Replace("{{", "{").Replace("}}", "}"), en[HazardMessages.Prefix + key]);
        }

        var problemKeys = Localizer.Keys("en").Where(k => k.StartsWith(HazardMessages.Prefix, StringComparison.Ordinal)).Select(k => k[HazardMessages.Prefix.Length..]);
        Assert.Equal(problemKeys.Order(StringComparer.Ordinal), HazardMessages.English.Keys.Order(StringComparer.Ordinal));
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
    [InlineData("ru", "способ обнаружения «Look»: есть Сл, но нет проверки", "выше нормы", "от 10 до 13", "Сложность спасброска", "помеха")]
    [InlineData("he", "דרך הזיהוי ״Look״: יש דרגת קושי אך אין בדיקה", "מעל", "10 עד 13", "דרגת קושי להצלה", "מטרד")]
    public void TheShapeAndGuideChecks_AreSaidInTheLanguage_WithTheEnglishWhenNoneIsGiven(string culture, string shape, string high, string range, string label, string severity)
    {
        var type = Srd5e.FindType("hazard")!;
        var shown = Srd5e.Localize(culture);
        var shownType = shown.FindType("hazard")!;
        var said = Localizer.For(culture);
        string Say(string key, object?[] args) => said.Get(key, args);

        var noCheck = Entry(("detect", new List<object> { Row(("name", "Look"), ("dc", 15L)) }));
        Assert.Contains(HazardNumbers.Problems(noCheck, type, Srd5e), p => p.Contains("detect route 'Look' has a dc and no check", StringComparison.Ordinal));
        var theirs = HazardNumbers.Problems(noCheck, shownType, Srd5e, Say).ToList();
        Assert.Contains(theirs, p => p.Contains(shape, StringComparison.Ordinal));
        Assert.DoesNotContain(theirs, p => p.Contains("has a dc", StringComparison.Ordinal) || p.Contains("route", StringComparison.Ordinal));

        var steep = Entry(("level", "1-4"), ("severity", "Nuisance"), ("saveAttribute", "Dexterity"), ("saveDc", 19L),
            ("damage", new List<object> { Row(("name", "Blades"), ("dice", "1d6")) }));
        Assert.Equal("Save DC 19 is high for a level 1 Nuisance hazard; the system's guide gives 10 to 13.", Assert.Single(HazardNumbers.ScalingProblems(steep, type, Srd5e)));
        var steepSaid = Assert.Single(HazardNumbers.ScalingProblems(steep, shownType, Srd5e, Say, shown.DisplayValue));
        Assert.Contains(high, steepSaid);
        Assert.Contains(range, steepSaid);
        Assert.Contains(label, steepSaid);
        Assert.Contains(severity, steepSaid);
        Assert.DoesNotContain("is high for", steepSaid);
        Assert.DoesNotContain("Nuisance", steepSaid);
        Assert.DoesNotContain("Save DC", steepSaid);
    }

    [Fact]
    public void ARowWithNoName_IsNamedOnce()
    {
        var said = Localizer.For("he");
        var nameless = Entry(("detect", new List<object> { Row(("dc", 15L)) }));
        var message = Assert.Single(HazardNumbers.Problems(nameless, Srd5e.Localize("he").FindType("hazard")!, Srd5e, (key, args) => said.Get(key, args)), p => p.Contains("דרגת קושי", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(message, "דרך הזיהוי"));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void EveryCheckMessage_KeepsItsPlaceholders_AndAddsNone(string culture)
    {
        var said = Localizer.For(culture);
        foreach (var (key, template) in HazardMessages.English)
        {
            static HashSet<string> Holes(string text) => Regex.Matches(text.Replace("{{", string.Empty).Replace("}}", string.Empty), @"\{(\d+)\}").Select(m => m.Value).ToHashSet();
            var value = said[HazardMessages.Prefix + key];
            Assert.True(Holes(template).SetEquals(Holes(value)), $"{culture}: {key} does not hold the English placeholders ({string.Join(" ", Holes(template))}): {value}");
        }
    }
}
