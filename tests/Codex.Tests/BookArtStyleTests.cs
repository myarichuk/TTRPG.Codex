using Codex.Core.Book;

namespace Codex.Tests;

/// <summary>The image prompt is written by code (docs/BOOK_EXPORT.md 28.3, item 2): only the visual description is free text, and it is checked.</summary>
public class BookArtStyleTests
{
    private const string Visual = "A hooded figure on a rain-dark pier at night, lantern held low, fishing boats behind";

    [Fact]
    public void APrompt_IsFramingVisualStyleAndTheNegative_InThatOrder()
    {
        var result = BookImagePrompt.Assemble("portrait", Visual + ".", new BookArtStyle("ink", "sepia", "fine", "candle"));

        Assert.Empty(result.Problems);
        Assert.Equal(
            "Head-and-shoulders portrait, face clearly visible: A hooded figure on a rain-dark pier at night, lantern held low, fishing boats behind. "
            + "Ink linework illustration with crosshatched shading, sepia and cream tones only, fine delicate lines, warm candlelight and deep shadow. "
            + BookArtStyles.Negative,
            result.Prompt);
    }

    [Fact]
    public void EveryChoiceOfTheVocabulary_Assembles_AndEveryPromptEndsWithTheNegative()
    {
        foreach (var kind in BookArtStyles.Framing.Keys)
        foreach (var medium in BookArtStyles.Media)
        foreach (var palette in BookArtStyles.Palettes)
        foreach (var line in BookArtStyles.Lines)
        foreach (var light in BookArtStyles.Lights)
        {
            var prompt = BookImagePrompt.Assemble(kind, Visual, new BookArtStyle(medium.Id, palette.Id, line.Id, light.Id)).Prompt;
            Assert.NotNull(prompt);
            Assert.EndsWith(BookArtStyles.Negative, prompt, StringComparison.Ordinal);
            Assert.Contains(medium.Phrase, prompt, StringComparison.Ordinal);
            // No word of the vocabulary is a proper noun: the only capitals are at the start of a sentence (and the visual's own first word).
            Assert.Empty(BookImagePrompt.Check(prompt.Replace(BookArtStyles.Negative, string.Empty)));
        }

        // Linework and painterly looks are both offered.
        Assert.Contains(BookArtStyles.Media, m => m.Id == "ink");
        Assert.Contains(BookArtStyles.Media, m => m.Id == "painterly");
    }

    [Fact]
    public void AMood_IsAPhraseFromTheVocabulary_AndAnUnknownOneAddsNothing()
    {
        var eerie = BookImagePrompt.Assemble("location", Visual, BookArtStyle.Default, "eerie").Prompt!;
        Assert.Contains(Visual + ", an eerie, uncanny mood. ", eerie, StringComparison.Ordinal);
        var none = BookImagePrompt.Assemble("location", Visual, BookArtStyle.Default, "in the style of Rutkowski").Prompt!;
        Assert.Contains(Visual + ". ", none, StringComparison.Ordinal);
        Assert.DoesNotContain("Rutkowski", none, StringComparison.Ordinal);
        Assert.All(BookArtStyles.Moods, m => Assert.Empty(BookImagePrompt.Check(m.Phrase)));
    }

    [Fact]
    public void AnUnknownStyle_FallsBackToTheDefault_RatherThanToNothing()
    {
        var prompt = BookImagePrompt.Assemble("nonsense-kind", Visual, new BookArtStyle("Rutkowski", "", "x", "neon")).Prompt!;
        Assert.Contains("Painterly fantasy illustration, rich muted colours, clear confident lines, dramatic natural light.", prompt, StringComparison.Ordinal);
        Assert.StartsWith(BookArtStyles.Framing["scene"], prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Rutkowski", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A queen on the throne of Ashfall", "capital:Ashfall")]
    [InlineData("the lich Vexmoor rises", "capital:Vexmoor")]
    [InlineData("a knight of the CROWN", "capital:CROWN")]
    [InlineData("a castle in the style of a fairy tale", "phrase:in the style of")]
    [InlineData("a dragon, inspired by old maps", "phrase:inspired by")]
    [InlineData("a swordsman, greg rutkowski, trending on artstation", "artist:rutkowski")]
    [InlineData("a tower like moebius drew them", "artist:moebius")]
    [InlineData("a ship, see https://example.com", "link")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("the duke stands in the rain", "name:duke")]
    [InlineData("old mirela at her stall", "name:Mirela")]
    public void ABadVisual_IsRefusedWithItsReason_AndNothingIsAssembled(string visual, string reason)
    {
        var result = BookImagePrompt.Assemble("portrait", visual, BookArtStyle.Default, names: ["Duke", "Mirela", "Ashfall"]);

        Assert.Null(result.Prompt);
        Assert.Contains(result.Problems, p => string.Equals(p, reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SentenceStarts_AreNotNames_AndTheLengthIsCapped()
    {
        Assert.Empty(BookImagePrompt.Check("A ruined chapel. Wind bends the grass! Crows wheel above"));
        // German nouns are capitals, so a German visual is refused: the director writes visuals in English.
        Assert.Contains("capital:Turm", BookImagePrompt.Check("Ein Turm im Nebel"));
        // Russian is read like English: a sentence start passes, a capitalised name does not. (Hebrew has no capitals; there only the names list helps.)
        Assert.Empty(BookImagePrompt.Check("Старая башня в тумане"));
        Assert.Contains("capital:Мирела", BookImagePrompt.Check("старуха Мирела у прилавка"));
        Assert.Contains("too-long", BookImagePrompt.Check(new string('a', BookImagePrompt.MaxVisualChars + 1)));
        Assert.Empty(BookImagePrompt.Check(new string('a', BookImagePrompt.MaxVisualChars)));
    }

    [Fact]
    public void ThePrompt_HasNoRoomForASubjectName_TheAssemblerTakesNone()
    {
        // The signature is the guarantee: slot kind, visual, style, names to refuse. A name can reach the prompt only through the visual,
        // and the visual is checked for it.
        var parameters = typeof(BookImagePrompt).GetMethod(nameof(BookImagePrompt.Assemble))!.GetParameters().Select(p => p.Name).ToList();
        Assert.Equal(["slotKind", "visual", "style", "mood", "names"], parameters);
    }
}
