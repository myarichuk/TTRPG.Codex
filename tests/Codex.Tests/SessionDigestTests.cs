using Codex.Persistence;

namespace Codex.Tests;

public class SessionDigestTests
{
    private static SessionDocument Session() => new()
    {
        Id = "s1",
        CampaignId = "c1",
        Title = "Into the sewers",
        DmSummary = "The villain is the mayor.",
        Events = { new SessionEvent { Type = "Damage", Description = "Goblin takes 7" } },
        RollLog =
        {
            new RollLogEntry { RollerUserId = "dm", Expression = "1d20", Total = 17, IsSecret = true },
            new RollLogEntry { RollerUserId = "p1", Expression = "1d20+3", Total = 12, IsSecret = false }
        }
    };

    private static List<NoteDocument> Notes() => new()
    {
        new NoteDocument { AuthorId = "p1", Content = "I think the mayor is lying", Visibility = CommentVisibility.Private },
        new NoteDocument { AuthorId = "p2", Content = "We found the key", Visibility = CommentVisibility.Public }
    };

    [Fact]
    public void PlayerDigest_NeverContainsSecretRollsPrivateNotesOrTheDmSummary()
    {
        var digest = SessionDigest.Build(Session(), Notes(), forDm: false, id => id.ToUpperInvariant());

        Assert.Single(digest.Rolls);
        Assert.Contains("P1 rolled 1d20+3 = 12", digest.Rolls[0]);
        Assert.Single(digest.Notes);
        Assert.Contains("We found the key", digest.Notes[0]);
        Assert.Null(digest.DmSummary);
        Assert.DoesNotContain(digest.Rolls.Concat(digest.Notes), line => line.Contains("mayor"));
    }

    [Fact]
    public void DmDigest_ContainsEverythingAndMarksSecrets()
    {
        var digest = SessionDigest.Build(Session(), Notes(), forDm: true);

        Assert.Equal(2, digest.Rolls.Count);
        Assert.Contains(digest.Rolls, r => r.EndsWith("(secret)"));
        Assert.Equal(2, digest.Notes.Count);
        Assert.Contains(digest.Notes, n => n.Contains("(private)"));
        Assert.Equal("The villain is the mayor.", digest.DmSummary);
        Assert.Single(digest.Events);
    }

    [Fact]
    public void EmptySession_IsEmpty()
    {
        Assert.True(SessionDigest.Build(new SessionDocument(), Array.Empty<NoteDocument>(), forDm: true).IsEmpty);
    }
}
