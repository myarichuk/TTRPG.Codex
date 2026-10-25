using Codex.Core.AI;

namespace Codex.Tests;

public class AiPromptsLanguageTests
{
    private static RewriteRequest Rewrite(string? language) =>
        new("Grik", "NPC", new[] { "A goblin" }, "", "", AiAudience.Dm, Language: language);

    [Fact]
    public void English_IsUnchanged()
    {
        Assert.Equal(AiPrompts.Style, AiPrompts.StyleFor(null));
        Assert.Equal(AiPrompts.Style, AiPrompts.StyleFor("en-GB"));
        Assert.Contains(AiPrompts.Style, AiPrompts.Rewrite(Rewrite(null)).System);
    }

    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("he", "Hebrew")]
    [InlineData("iw", "Hebrew")]
    public void OtherLanguages_AreNamedAndHeadingsAreTranslated(string tag, string name)
    {
        var system = AiPrompts.Rewrite(Rewrite(tag)).System;

        Assert.Contains($"plain, concrete {name}", system);
        Assert.Contains($"Write the entire reply in {name}", system);
        Assert.DoesNotContain("plain, concrete English", system);
    }

    [Fact]
    public void CharacterJson_KeepsItsKeysWhateverTheLanguage()
    {
        var request = new CharacterConceptRequest("a thief", "NPC", "demo", Array.Empty<FieldSpec>(), Array.Empty<FieldSpec>(), null, null, Language: "he");

        var system = AiPrompts.CreateCharacter(request).System;

        Assert.Contains("in Hebrew", system);
        Assert.Contains("keep every JSON key", system);
        Assert.DoesNotContain("in Hebrew", AiPrompts.CreateCharacter(request with { Language = null }).System);
    }
}
