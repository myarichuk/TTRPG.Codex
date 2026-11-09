using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>The user's own picture style (docs/BOOK_EXPORT.md 41.23): their words, as written, in place of the built-in style phrases.</summary>
public class ImageStyleGuideTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   \n ", null)]
    [InlineData("  painterly,\n\n  chunky   shapes ", "painterly, chunky shapes")]
    public void TheGuide_IsOneLine_OrNone(string? text, string? cleaned) => Assert.Equal(cleaned, ImageStyleGuide.Clean(text));

    [Fact]
    public void ALongGuide_IsCut()
    {
        Assert.Equal(ImageStyleGuide.MaxChars, ImageStyleGuide.Clean(new string('a', ImageStyleGuide.MaxChars + 50))!.Length);
    }

    [Fact]
    public void TheAppsOwnPictures_TakeTheGuideInPlaceOfTheHouseStyle_AndWithoutOneAreAsBefore()
    {
        const string before = "Full-figure portrait of Vera, a keeper. A grey coat. Painterly fantasy illustration, dramatic natural light, rich muted colours. No text, no lettering, no border, no watermark.";
        Assert.Equal(before, AiPrompts.Image("Vera", "keeper", "A grey coat", ArtShape.Tall));
        Assert.Equal(before, AiPrompts.Image("Vera", "keeper", "A grey coat", ArtShape.Tall, "  "));

        var own = AiPrompts.Image("Vera", "keeper", "A grey coat", ArtShape.Tall, "Stylized hand-painted concept art, saturated colours.");

        Assert.Equal("Full-figure portrait of Vera, a keeper. A grey coat. Stylized hand-painted concept art, saturated colours. No text, no lettering, no border, no watermark.", own);
    }

    [Fact]
    public void ABookPicturesHash_IsUnchangedWithoutAGuide_SoNothingDrawnBeforeTurnsOlder()
    {
        var style = new BookArtStyle("ink", "sepia", "fine", "candle");

        Assert.Equal(BookArtDrawing.InputsOf("portrait", "a keeper", "tense", style), BookArtDrawing.InputsOf("portrait", "a keeper", "tense", style, " "));
        Assert.NotEqual(BookArtDrawing.InputsOf("portrait", "a keeper", "tense", style), BookArtDrawing.InputsOf("portrait", "a keeper", "tense", style, "Ink wash."));
        Assert.NotEqual(BookArtDrawing.InputsOf("portrait", "a keeper", "tense", style, "Ink wash."), BookArtDrawing.InputsOf("portrait", "a keeper", "tense", style, "Oil."));
    }
}
