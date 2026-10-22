using Codex.Persistence;

namespace Codex.Tests;

public class SessionMentionsTests
{
    private static readonly ActorDocument Mara = new() { Id = "mara", Name = "Mara", Visibility = ActorVisibility.Known };
    private static readonly ActorDocument MaraOfTheTides = new() { Id = "tides", Name = "Mara of the Tides", Visibility = ActorVisibility.Known };
    private static readonly ActorDocument Stranger = new() { Id = "stranger", Name = "Strahd", PublicName = "Hooded stranger", Visibility = ActorVisibility.Unknown };
    private static readonly ActorDocument Lurker = new() { Id = "lurker", Name = "The Drowned One", Visibility = ActorVisibility.Hidden };
    private static readonly LocationDocument Docks = new() { Id = "docks", Name = "Greyharbour Docks", Visibility = ActorVisibility.Known };
    private static readonly LocationDocument Vault = new() { Id = "vault", Name = "Sunken Vault", Visibility = ActorVisibility.Hidden };
    private static readonly MentionItem Sunblade = new("magic-item/sunblade", "Sunblade");
    private static readonly MentionItem DraftRing = new("magic-item/tide-ring", "Ring of the Tide", PlayerVisible: false);

    private static MentionDirectory Directory(bool isDm, bool knownActorsOnly = false) =>
        new(new[] { Mara, MaraOfTheTides, Stranger, Lurker }, new[] { Docks, Vault }, new[] { Sunblade, DraftRing }, isDm, knownActorsOnly);

    [Fact]
    public void Encode_StoresIds_PreferringTheLongestName_AndLeavesUnknownNamesAsTyped()
    {
        var text = Directory(isDm: true).Encode("@mara of the tides met @Mara at #Greyharbour Docks with the $Sunblade. @Nobody, me@mail.com");

        Assert.Equal("[[actor:tides]] met [[actor:mara]] at [[place:docks]] with the [[item:magic-item/sunblade]]. @Nobody, me@mail.com", text);
    }

    [Fact]
    public void Encode_OnlyOffersWhatTheWriterCanSee()
    {
        var text = Directory(isDm: false).Encode("@The Drowned One in #Sunken Vault, @Hooded stranger has the $Ring of the Tide");

        Assert.Equal("@The Drowned One in #Sunken Vault, [[actor:stranger]] has the $Ring of the Tide", text);
    }

    [Fact]
    public void Read_NamesEachMentionForTheViewer_AndHidesWhatTheyCantSee()
    {
        const string note = "[[actor:stranger]] and [[actor:lurker]] took [[item:magic-item/tide-ring]] to [[place:vault]] via [[place:docks]]";

        Assert.Equal("Strahd and The Drowned One took Ring of the Tide to Sunken Vault via Greyharbour Docks", Directory(isDm: true).Plain(note));
        Assert.Equal("Hooded stranger and someone took something to somewhere via Greyharbour Docks", Directory(isDm: false).Plain(note));
        Assert.Equal("someone and someone took something to somewhere via Greyharbour Docks", Directory(isDm: false, knownActorsOnly: true).Plain(note));
    }

    [Fact]
    public void ForEditing_GivesBackTypedNames_ButKeepsTheIdsTheEditorCantSee()
    {
        const string note = "[[actor:mara]] hid it in [[place:vault]]";

        var forPlayer = Directory(isDm: false).ForEditing(note);

        Assert.Equal("@Mara hid it in [[place:vault]]", forPlayer);
        Assert.Equal(note, Directory(isDm: false).Encode(forPlayer));
    }

    [Fact]
    public void MentionedIds_ListsEachMentionOnce()
    {
        Assert.Equal(new[] { "mara", "tides" }, SessionMentions.MentionedIds("[[actor:mara]] [[actor:tides]] [[actor:mara]] [[place:docks]]", MentionKind.Actor));
    }

    [Fact]
    public void NotesMentioning_FindsTheNotesThatMentionSomething_NewestSessionFirst_SecretOnesForTheDmOnly()
    {
        var older = new SessionDocument { Id = "s1", Date = new DateTime(2026, 1, 1) };
        older.Notes.Add(new SessionNote { Text = "Met [[actor:mara]] at [[place:docks]]", Timestamp = new DateTime(2026, 1, 1, 20, 0, 0) });
        older.Notes.Add(new SessionNote { Text = "[[actor:mara]] lied", IsSecret = true, Timestamp = new DateTime(2026, 1, 1, 21, 0, 0) });
        var newer = new SessionDocument { Id = "s2", Date = new DateTime(2026, 2, 1) };
        newer.Notes.Add(new SessionNote { Text = "Back at [[place:docks]] without [[actor:maraz]]" });

        var forPlayers = SessionMentions.NotesMentioning(new[] { older, newer }, MentionKind.Actor, "mara", isDm: false);
        var forDm = SessionMentions.NotesMentioning(new[] { older, newer }, MentionKind.Actor, "mara", isDm: true);
        var docks = SessionMentions.NotesMentioning(new[] { older, newer }, MentionKind.Place, "docks", isDm: false);

        Assert.Equal(new[] { "Met [[actor:mara]] at [[place:docks]]" }, forPlayers.Select(x => x.Note.Text));
        Assert.Equal(2, forDm.Count);
        Assert.Equal(new[] { "s2", "s1" }, docks.Select(x => x.Session.Id));
    }

    [Fact]
    public void Digest_ReadsMentionsForItsAudience()
    {
        var session = new SessionDocument { Id = "s1", CampaignId = "c1" };
        session.Notes.Add(new SessionNote { AuthorId = "dm", Text = "[[actor:lurker]] waits at [[place:docks]] with [[item:magic-item/sunblade]]" });
        var actors = new[] { Mara, MaraOfTheTides, Stranger, Lurker };

        var players = SessionDigest.Build(session, Array.Empty<NoteDocument>(), forDm: false, actors: actors, places: new[] { Docks, Vault }, items: new[] { Sunblade, DraftRing });
        var dm = SessionDigest.Build(session, Array.Empty<NoteDocument>(), forDm: true, actors: actors, places: new[] { Docks, Vault }, items: new[] { Sunblade, DraftRing });

        Assert.Equal("dm: someone waits at Greyharbour Docks with Sunblade", Assert.Single(players.Notes));
        Assert.Equal("dm: The Drowned One waits at Greyharbour Docks with Sunblade", Assert.Single(dm.Notes));
    }

    [Fact]
    public void ToHtml_FormatsTheText_AndLinksMentions_EncodingNamesAndLinks()
    {
        var sly = new ActorDocument { Id = "sly", Name = "<b>Sly</b> & co", Visibility = ActorVisibility.Known };
        var mentions = new MentionDirectory(new[] { sly, Lurker }, new[] { Docks }, Array.Empty<MentionItem>(), isDm: false);

        var html = mentions.ToHtml("**Met** [[actor:sly]] at [[place:docks]]\n\n- [[actor:lurker]] watched", (kind, id) => $"/{kind}/{id}?a=1&b=2");

        Assert.Equal("<p><strong>Met</strong> <a class=\"mention mention-actor\" href=\"/Actor/sly?a=1&amp;b=2\">&lt;b&gt;Sly&lt;/b&gt; &amp; co</a> at "
            + "<a class=\"mention mention-place\" href=\"/Place/docks?a=1&amp;b=2\">Greyharbour Docks</a></p>"
            + "<ul><li><span class=\"mention mention-unknown\">someone</span> watched</li></ul>", html);
    }

    [Fact]
    public void Excerpt_IsTheLineWithTheMention_CutAroundIt_NeverThroughAnotherMention()
    {
        var longLine = new string('a', 300) + " [[place:docks]] then [[actor:mara]] " + new string('b', 300);

        Assert.Equal("Met [[actor:mara]] at dawn", SessionMentions.Excerpt("First line\n- **Met** [[actor:mara]] at dawn\nLast", "[[actor:mara]]"));
        var cut = SessionMentions.Excerpt(longLine, "[[actor:mara]]", max: 60);
        Assert.StartsWith("…", cut);
        Assert.EndsWith("…", cut);
        Assert.Contains("[[actor:mara]]", cut);
        Assert.DoesNotContain("[[place:do", cut.Replace("[[place:docks]]", ""));
    }

    [Fact]
    public void Writings_FindEachSource_ForTheViewerWhoMayReadIt()
    {
        const string mara = "[[actor:mara]]";
        var session = new SessionDocument { Id = "s1", Title = "The docks", Recap = "We met " + mara, DmSummary = mara + " is a spy" };
        var page = new WikiPageDocument { Id = "w1", Slug = "harbour", Title = "Harbour", Visibility = WikiVisibility.Party };
        var secretPage = new WikiPageDocument { Id = "w2", Slug = "plot", Title = "Plot", Visibility = WikiVisibility.DmOnly };
        var comments = new[]
        {
            new NoteDocument { TargetId = "session:s1", AuthorId = "p1", Content = "Party: " + mara, Visibility = CommentVisibility.Public },
            new NoteDocument { TargetId = "session:s1", AuthorId = "p2", Content = "Private: " + mara, Visibility = CommentVisibility.Private },
            new NoteDocument { TargetId = "wiki:w1", AuthorId = "p1", Content = "Wiki: " + mara, Visibility = CommentVisibility.Public },
            new NoteDocument { TargetId = "wiki:w2", AuthorId = "dm", Content = "Plot: " + mara, Visibility = CommentVisibility.Public },
        };
        var journals = new[]
        {
            new JournalEntryDocument { Id = "j1", OwnerUserId = "p1", Title = "Mine", Body = "I trust " + mara },
            new JournalEntryDocument { Id = "j2", OwnerUserId = "dm", Title = "DM's", Body = "Kill " + mara },
        };
        var actors = new[]
        {
            new ActorDocument { Id = "aria", Name = "Aria", Visibility = ActorVisibility.Known, Biography = "Sister of " + mara, DmNotes = "Hates " + mara },
            new ActorDocument { Id = "mara", Name = "Mara", Visibility = ActorVisibility.Known, Biography = "I am " + mara },
        };
        MentionWritings For(bool isDm, string userId) => new(new[] { session }, comments, journals, actors, new[] { page, secretPage }, isDm, userId);

        var player = For(isDm: false, userId: "p1").Mentioning(MentionKind.Actor, "mara");
        var dm = For(isDm: true, userId: "dm").Mentioning(MentionKind.Actor, "mara");

        Assert.Equal(
            new[] { "Biography", "Comment:Party: [[actor:mara]]", "Comment:Wiki: [[actor:mara]]", "Journal:I trust [[actor:mara]]", "Recap" },
            player.Select(Describe).OrderBy(x => x));
        Assert.Equal(
            new[] { "Biography", "Comment:Party: [[actor:mara]]", "Comment:Plot: [[actor:mara]]", "Comment:Private: [[actor:mara]]", "Comment:Wiki: [[actor:mara]]",
                "DM notes", "DM summary", "Journal:Kill [[actor:mara]]", "Recap" },
            dm.Select(Describe).OrderBy(x => x));
        Assert.Equal("harbour", player.Single(w => w.Home == MentionHome.WikiPage).HomeId);

        static string Describe(WrittenMention w) => w.Source is WrittenIn.Comment or WrittenIn.Journal ? $"{MentionWritings.SourceLabel(w.Source)}:{w.Excerpt}" : MentionWritings.SourceLabel(w.Source);
    }

    [Fact]
    public void Digest_ReadsMentionsInPageNotesAndTheDmSummary()
    {
        var session = new SessionDocument { Id = "s1", CampaignId = "c1", DmSummary = "[[actor:lurker]] escaped" };
        var notes = new[] { new NoteDocument { AuthorId = "p1", Content = "Follow [[actor:lurker]]", Visibility = CommentVisibility.Public } };

        var dm = SessionDigest.Build(session, notes, forDm: true, actors: new[] { Lurker });
        var players = SessionDigest.Build(session, notes, forDm: false, actors: new[] { Lurker });

        Assert.Equal("The Drowned One escaped", dm.DmSummary);
        Assert.Equal("p1: Follow The Drowned One", Assert.Single(dm.Notes));
        Assert.Equal("p1: Follow someone", Assert.Single(players.Notes));
    }
}
