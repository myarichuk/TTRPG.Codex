using Codex.Core.AI;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Services;

namespace Codex.Tests;

/// <summary>"Tidy up" on someone's own notes (docs/SERVER_ACCESS_AND_VAULT.md 6): only the notes go to the model, and a reply
/// that loses a mention is refused.</summary>
public class TidyNotesTests
{
    private static readonly MentionDirectory Mentions = new(
        new[] { new ActorDocument { Id = "mara", Name = "Mara of the Tides", Visibility = ActorVisibility.Known } },
        new[] { new LocationDocument { Id = "docks", Name = "Greyharbour Docks", Visibility = ActorVisibility.Known } },
        new[] { new MentionItem("magic-item/sunblade", "Sunblade") },
        isDm: false);

    private const string Notes = "met @Mara of the Tides at #Greyharbour Docks. she wants the $Sunblade back. she wants it back";

    [Fact]
    public async Task TheModelReadsOnlyTheNotes_AndIsToldToAddNothing()
    {
        var chat = new ScriptedChat((_, _) => "Tidied.");
        Assert.Equal("Tidied.", await new AiAssistant(chat).TidyAsync(new TidyRequest(Notes)));

        var request = chat.Requests.Single();
        Assert.Contains(Notes, request);
        Assert.Contains("Never add anything", request);
        Assert.Contains("keep each one exactly as written", request);
    }

    [Fact]
    public void ATidyKeepingEveryMention_IsTaken_ButOneThatLosesOrChangesAMention_IsRefused()
    {
        const string kept = "Met @Mara of the Tides at #Greyharbour Docks.\n\n- She wants the $Sunblade back.";
        Assert.Equal(kept, AiFeatures.Tidied(Notes, "  " + kept + "\n", Mentions.Encode));

        // "@Mara" alone names nobody here, the item went, and an empty reply is no tidy at all.
        foreach (var bad in new[] { "Met @Mara at #Greyharbour Docks. She wants the $Sunblade back.", "Met @Mara of the Tides at #Greyharbour Docks.", "  " })
        {
            var refused = Assert.Throws<LocalizedException>(() => AiFeatures.Tidied(Notes, bad, Mentions.Encode));
            Assert.Equal("Service.Ai.TidyLostMention", refused.Key);
        }

        // Notes with no mentions at all are compared as such, and an empty reply is still no tidy.
        Assert.Equal("Fine.", AiFeatures.Tidied("fine", "Fine.", Mentions.Encode));
        Assert.Throws<LocalizedException>(() => AiFeatures.Tidied("fine", " \n", Mentions.Encode));
    }
}
