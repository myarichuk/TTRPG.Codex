using Codex.Core.Book;

namespace Codex.Tests;

/// <summary>The facts check of a rewrite (docs/BOOK_EXPORT.md 42, step 1): no name or figure added, none dropped, in English, Russian and
/// Hebrew, read from the passage as the model was sent it.</summary>
public class BookFactsTests
{
    private static BookFactNames Names(params string[] names) => new(names, ["Wisdom", "Perception", "Strength"]);

    private static IReadOnlyList<string> Check(string passage, string reply, BookFactNames names, string language = "en", string context = "") =>
        BookFacts.Check(passage, context, reply, names, language);

    [Fact]
    public void AFaithfulRewrite_InEachLanguage_Passes()
    {
        Assert.Empty(Check(
            "Vera Saltmire keeps the lamp at the Drowned Bell. She owes 3 debts and rolls 2d6.",
            "### The Drowned Bell\nAt the Drowned Bell, Vera keeps the lamp burning. She owes three debts, and her luck is 2d6.\n\nVera's lamp is never out.",
            Names("Vera Saltmire", "Drowned Bell", "Oren the ferryman")));

        Assert.Empty(Check(
            "Вера держит маяк у Мира. У неё 3 долга, бросок 2к6.",
            "У Мира стоит маяк, и держит его Вера. За Верой три долга; её удача — 2к6. С Верой говорят шёпотом.",
            Names("Вера", "Мир"), "ru"));

        Assert.Empty(Check(
            "ורה שומרת על המגדלור במגדל. יש לה 3 חובות, וגלגול 2d6.",
            "לוורה יש שלושה חובות. היא שומרת על המגדלור ובמגדל דולק אור תמיד. מזלה: 2d6.",
            Names("ורה", "מגדל"), "he"));
    }

    [Fact]
    public void ANameDropped_IsRefused_InEachLanguage_AndNamedForTheRetry()
    {
        var en = Check("Vera Saltmire and Oren row to the reef.", "Two people row to the reef.", Names("Vera Saltmire", "Oren"));
        Assert.Contains(en, p => p.Contains("drops names") && p.Contains("Vera Saltmire") && p.Contains("Oren"));

        Assert.Contains(Check("Вера и Орен гребут к рифу.", "Двое гребут к рифу.", Names("Вера", "Орен"), "ru"), p => p.Contains("Вера"));
        Assert.Contains(Check("ורה ואורן חותרים לשונית.", "שניים חותרים לשונית.", Names("ורה", "אורן"), "he"), p => p.Contains("אורן"));
    }

    [Fact]
    public void AShortFormOfAName_KeepsIt_ButAHalfNamedPassage_DoesNotDemandTheWholeName()
    {
        // "Oren" keeps "Oren the ferryman".
        Assert.Empty(Check("Oren the ferryman waits.", "Oren waits by the boats.", Names("Oren the ferryman")));
        // The passage says only "the ferryman": the full name is not in it, so it is not demanded, and not quoted back to the model.
        Assert.Empty(Check("The ferryman waits.", "A boatman waits.", Names("Oren the ferryman")));
    }

    [Fact]
    public void ANameOfTheCampaigns_ThePassageDoesNotHold_IsRefused_UnlessTheTextAroundItHoldsIt()
    {
        var names = Names("Vera", "Captain Harl");
        Assert.Contains(Check("Vera keeps the lamp.", "Vera keeps the lamp for Captain Harl.", names), p => p.Contains("Captain Harl"));
        Assert.Empty(Check("Vera keeps the lamp.", "Vera keeps the lamp for Captain Harl.", names, context: "Captain Harl owns the light."));
        // A common word that is one word of a longer name is not that name.
        Assert.Empty(Check("Vera rings a bell.", "Vera rings the bell.", Names("Vera", "Drowned Bell")));
        Assert.Empty(Check("Vera keeps the lamp.", "Vera keeps the lamp by the bell.", Names("Vera", "Drowned Bell")));
        // Hebrew has no capitals: the edition's names are what find an added one.
        Assert.Contains(Check("ורה שומרת על האור.", "ורה שומרת על האור בשביל הרל.", Names("ורה", "הרל"), "he"), p => p.Contains("הרל"));
    }

    [Fact]
    public void AnInventedCapitalisedName_IsRefused_ButTheGamesOwnWordsAreNot()
    {
        Assert.Contains(Check("Vera keeps the lamp.", "Vera keeps the lamp with her brother Tomas.", Names("Vera")), p => p.Contains("Tomas"));
        Assert.Empty(Check("Vera keeps the lamp; spotting her needs a check.", "Vera keeps the lamp; spotting her takes a Wisdom (Perception) check.", Names("Vera")));
    }

    [Fact]
    public void Figures_NoneAddedAndNoneDropped_DiceKeptWhole_AndWordsKeepAFigure()
    {
        Assert.Contains(Check("The gate holds 4 guards.", "The gate holds guards.", Names()), p => p.Contains("drops figures") && p.Contains('4'));
        Assert.Contains(Check("The gate holds 4 guards.", "The gate holds 4 guards (DC 15).", Names()), p => p.Contains("15"));
        Assert.Contains(Check("Damage is 2d6.", "Damage is 2d8.", Names()), p => p.Contains("2d6"));
        // A die is kept as a die: "2" and "6" elsewhere in the reply do not keep "2d6".
        Assert.Contains(Check("Damage is 2d6.", "Damage, 2 hands, 6 times.", Names()), p => p.Contains("2d6"));
        Assert.Empty(Check("The gate holds 4 guards on the 3rd floor.", "Four guards hold the gate on the third floor.", Names()));
        Assert.Empty(Check("У ворот 4 стражника, урон 1к8.", "Ворота стерегут четыре стражника; урон их — 1к8.", Names(), "ru"));
        Assert.Empty(Check("בשער 4 שומרים.", "ארבעה שומרים בשער.", Names(), "he"));
        // A list's own numbering is not a figure of the passage.
        Assert.Empty(Check("1. Vera\n2. Oren", "Vera and Oren.", Names("Vera", "Oren")));
    }

    [Fact]
    public void Matching_TheLanguagesOwnWays_AndNotTheirFalseFriends()
    {
        // Russian case endings on a stem; not a longer word that only starts the same.
        foreach (var form in new[] { "вера", "веру", "веры", "верой", "вере" }) Assert.True(BookFacts.Matches(form, "вера"), form);
        Assert.False(BookFacts.Matches("вермут", "вера"));
        Assert.True(BookFacts.Matches("мира", "мир"));
        Assert.False(BookFacts.Matches("мирный", "мир"));
        Assert.True(BookFacts.Matches("ореном", "орен"));
        // Hebrew prefix letters, up to three; a name that itself starts with one is matched whole.
        foreach (var form in new[] { "לוורה", "וורה", "שבוורה", "ורה" }) Assert.True(BookFacts.Matches(form, "ורה"), form);
        Assert.False(BookFacts.Matches("אורה", "ורה"));
        Assert.True(BookFacts.Matches("מירה", "מירה"));
        Assert.True(BookFacts.Matches("למירה", "מירה"));
        // Hebrew points and an English possessive are not part of the word.
        Assert.Equal(["ורה"], BookFacts.Words("וֶרָה"));
        Assert.Equal(["vera", "lamp"], BookFacts.Words("Vera's lamp"));
        // "ל-אורן": the hyphen parts the prefix from a foreign name.
        Assert.Empty(Check("אורן חותר.", "ל-אורן יש סירה והוא חותר.", Names("אורן"), "he"));
    }

    [Fact]
    public void AChapter_IsSentInItsEntries_AndPartsAPieceCrosses_AreOne()
    {
        const string chapter = "{{chapter,#ch-cast\n# Cast\n}}\n\nThe cast of the coast.\n\n{{entry,#e-vera}}\n## Vera\n\nVera keeps the lamp.\n\n" +
                               "{{entry,#e-oren}}\n## Oren\n\nOren rows.\n";
        var parts = Codex.Web.Book.BookDraftAi.Parts(chapter)!;
        Assert.Equal(["", "Vera", "Oren"], parts.Select(p => p.Label));
        Assert.Equal(chapter, string.Concat(parts.Select(p => chapter[p.From..p.To])));
        Assert.Equal(chapter.Length, parts[^1].To);

        // A secret never closed runs on to the end: the entries it crosses are one part, so no two replies overlap.
        var open = chapter.Replace("Vera keeps the lamp.", "Vera keeps the lamp.\n\n{{secret\nShe smuggles.");
        var joined = Codex.Web.Book.BookDraftAi.Parts(open)!;
        Assert.Equal(["", "Vera"], joined.Select(p => p.Label));
        Assert.Equal(open.Length, joined[^1].To);

        // An entry copied with its marker: the merge cannot tell them apart, and neither can this.
        Assert.Null(Codex.Web.Book.BookDraftAi.Parts(chapter + "\n{{entry,#e-oren}}\n## Oren\n"));
    }

    [Fact]
    public void ANameInsideAPiece_IsNeverCounted_NorQuotedBack()
    {
        const string chapter = "{{entry,#e-vera}}\n## Vera\n\nVera keeps the lamp.\n\n{{secret\nMorrow the smuggler pays her.\n}}\n";
        var passage = BookDraftEdits.Cut(chapter, 0, chapter.Length)!;
        Assert.DoesNotContain("Morrow", passage.Text);
        var problems = BookFacts.Check(passage.Text, passage.Before + "\n" + passage.After, "{{block:1}}\n## Vera\n\nVera keeps the lamp.\n\n{{block:2}}\n",
            Names("Vera", "Morrow"), "en");
        Assert.Empty(problems);
        // Even a reply that drops everything is told only of what the passage it was sent holds.
        var dropped = BookFacts.Check(passage.Text, string.Empty, "{{block:1}}\n{{block:2}}\nA keeper.", Names("Vera", "Morrow"), "en");
        Assert.All(dropped, p => Assert.DoesNotContain("Morrow", p));
        Assert.Contains(dropped, p => p.Contains("Vera"));
    }
}
