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
    public void TableNotes_ReachBothDigests_SecretOnesOnlyTheDms()
    {
        var session = Session();
        session.Notes.Add(new SessionNote { AuthorId = "p1", Text = "The bells stopped ringing", Timestamp = new DateTime(2026, 1, 1, 20, 0, 0) });
        session.Notes.Add(new SessionNote { AuthorId = "dm", Text = "The bell-ringer is dead", IsSecret = true, Timestamp = new DateTime(2026, 1, 1, 20, 5, 0) });

        var players = SessionDigest.Build(session, Array.Empty<NoteDocument>(), forDm: false, id => id.ToUpperInvariant());
        var dm = SessionDigest.Build(session, Array.Empty<NoteDocument>(), forDm: true, id => id.ToUpperInvariant());

        Assert.Equal(new[] { "P1: The bells stopped ringing" }, players.Notes);
        Assert.Equal(new[] { "P1: The bells stopped ringing", "DM (secret): The bell-ringer is dead" }, dm.Notes);
    }

    [Fact]
    public void TableAndPageNotes_ShareOneTimeline()
    {
        var session = Session();
        session.Notes.Add(new SessionNote { AuthorId = "p1", Text = "second", Timestamp = new DateTime(2026, 1, 1, 20, 10, 0) });
        var pageNotes = new[]
        {
            new NoteDocument { AuthorId = "p2", Content = "first", Visibility = CommentVisibility.Public, CreatedAt = new DateTime(2026, 1, 1, 20, 0, 0) },
            new NoteDocument { AuthorId = "p2", Content = "third", Visibility = CommentVisibility.Public, CreatedAt = new DateTime(2026, 1, 1, 21, 0, 0) }
        };

        var digest = SessionDigest.Build(session, pageNotes, forDm: false);

        Assert.Equal(new[] { "p2: first", "p1: second", "p2: third" }, digest.Notes);
    }

    [Fact]
    public void SessionWithOnlyTableNotes_IsNotEmpty()
    {
        var session = new SessionDocument { Notes = { new SessionNote { AuthorId = "p1", Text = "Remember the ferryman" } } };

        Assert.False(SessionDigest.Build(session, Array.Empty<NoteDocument>(), forDm: false).IsEmpty);
    }

    private static readonly ActorDocument Mayor = new() { Id = "mayor", Name = "Mayor Hollin", Visibility = ActorVisibility.Known };
    private static readonly ActorDocument Stranger = new() { Id = "stranger", Name = "Strahd", PublicName = "Hooded stranger", Visibility = ActorVisibility.Unknown };
    private static readonly ActorDocument Lurker = new() { Id = "lurker", Name = "The Drowned One", Visibility = ActorVisibility.Hidden };

    private static SessionDocument SessionWithQuotes()
    {
        var session = Session();
        session.Notes.Add(new SessionNote { AuthorId = "dm", SpeakerActorId = "mayor", Text = "The bells haven't rung since the flood.", Timestamp = new DateTime(2026, 1, 1, 20, 0, 0) });
        session.Notes.Add(new SessionNote { AuthorId = "dm", SpeakerActorId = "stranger", Text = "You'll want silver.", Timestamp = new DateTime(2026, 1, 1, 20, 1, 0) });
        session.Notes.Add(new SessionNote { AuthorId = "dm", SpeakerActorId = "lurker", Text = "Come closer.", Timestamp = new DateTime(2026, 1, 1, 20, 2, 0) });
        session.Notes.Add(new SessionNote { AuthorId = "dm", SpeakerActorId = "mayor", Text = "I sank the bell myself.", IsSecret = true, Timestamp = new DateTime(2026, 1, 1, 20, 3, 0) });
        return session;
    }

    [Fact]
    public void PlayerDigest_NamesOnlyKnownSpeakers_AndDropsSecretQuotes()
    {
        var digest = SessionDigest.Build(SessionWithQuotes(), Array.Empty<NoteDocument>(), forDm: false, actors: new[] { Mayor, Stranger, Lurker });

        Assert.Equal(new[]
        {
            "Mayor Hollin: \"The bells haven't rung since the flood.\"",
            "Someone: \"You'll want silver.\"",
            "Someone: \"Come closer.\""
        }, digest.Quotes);
        Assert.DoesNotContain(digest.Quotes.Concat(digest.Notes), line => line.Contains("Strahd") || line.Contains("Drowned") || line.Contains("sank"));
        Assert.Empty(digest.Notes);
    }

    [Fact]
    public void DmDigest_NamesEverySpeaker_AndMarksSecretQuotes()
    {
        var digest = SessionDigest.Build(SessionWithQuotes(), Array.Empty<NoteDocument>(), forDm: true, actors: new[] { Mayor, Stranger, Lurker });

        Assert.Contains("Strahd: \"You'll want silver.\"", digest.Quotes);
        Assert.Contains("The Drowned One: \"Come closer.\"", digest.Quotes);
        Assert.Contains("Mayor Hollin (secret): \"I sank the bell myself.\"", digest.Quotes);
    }

    [Fact]
    public void NoteEvents_AreLeftOut_SinceTheNotesThemselvesAreIn()
    {
        var session = Session();
        session.Events.Add(new SessionEvent { Type = "Note", Description = "Note: Remember the ferryman" });
        session.Notes.Add(new SessionNote { AuthorId = "p1", Text = "Remember the ferryman" });

        var digest = SessionDigest.Build(session, Array.Empty<NoteDocument>(), forDm: true);

        Assert.DoesNotContain(digest.Events, e => e.Contains("ferryman"));
        Assert.Single(digest.Notes, n => n.Contains("ferryman"));
    }

    [Fact]
    public void SaidBy_GathersAnActorsLinesAcrossSessions_SecretOnesForTheDmOnly()
    {
        var first = SessionWithQuotes();
        var second = new SessionDocument { Id = "s2", Notes = { new SessionNote { SpeakerActorId = "mayor", Text = "Welcome back.", Timestamp = new DateTime(2026, 2, 1) } } };

        var forPlayers = SessionQuotes.SaidBy(new[] { second, first }, "mayor", isDm: false);
        var forDm = SessionQuotes.SaidBy(new[] { second, first }, "mayor", isDm: true);

        Assert.Equal(new[] { "The bells haven't rung since the flood.", "Welcome back." }, forPlayers.Select(x => x.Line.Text));
        Assert.Equal(3, forDm.Count);
        Assert.Equal("s2", forPlayers[1].Session.Id);
    }

    [Fact]
    public void SpeakerName_UsesThePublicNameForPlayers_AndSomeoneForActorsTheyCantSee()
    {
        var visible = new[] { Mayor, Stranger, Lurker }.ToDictionary(a => a.Id);

        Assert.Equal("Hooded stranger", SessionQuotes.SpeakerName("stranger", visible, isDm: false));
        Assert.Equal("Strahd", SessionQuotes.SpeakerName("stranger", visible, isDm: true));
        Assert.Equal(SessionQuotes.UnknownSpeaker, SessionQuotes.SpeakerName("lurker", visible, isDm: false));
        Assert.Equal(SessionQuotes.UnknownSpeaker, SessionQuotes.SpeakerName("gone", visible, isDm: true));
    }

    [Fact]
    public void EmptySession_IsEmpty()
    {
        Assert.True(SessionDigest.Build(new SessionDocument(), Array.Empty<NoteDocument>(), forDm: true).IsEmpty);
    }
}
