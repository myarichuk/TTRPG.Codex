using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web;
using Codex.Web.Book;
using System.Text.RegularExpressions;

namespace Codex.Tests;

/// <summary>
/// A player's own book (docs/BOOK_EXPORT.md 45): the party draft with what that player's characters were told in <c>:::for</c> blocks and
/// lore revealed to them, in notes under the entries they belong to. Never another player's, never the GM's, never a hidden name.
/// </summary>
[Collection("RavenDb")]
public class BookPlayerBookTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "pb_dm_user";
    private const string PlayerA = "pb_player_a";
    private const string PlayerB = "pb_player_b";
    private const string PlayerNone = "pb_player_none";

    private readonly CampaignRepository _campaigns;
    private readonly ActorRepository _actors;
    private readonly FactRepository _facts;
    private readonly RavenSessionRepository _sessions;
    private readonly BookSourceReader _reader;
    private readonly BookDrafts _drafts;

    public BookPlayerBookTests(RavenDbFixture fixture)
    {
        var db = fixture.Open(fixture.DbName);
        _actors = new ActorRepository(db);
        _sessions = new RavenSessionRepository(db);
        var notes = new RavenNoteRepository(db);
        var regions = new RegionRepository(db);
        _facts = new FactRepository(db);
        var encounters = new RavenEncounterRepository(db);
        var homebrew = new RavenHomebrewRepository(db);
        var wiki = new RavenWikiRepository(db);
        var store = new RavenBookDraftRepository(db);
        _campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), _actors, _sessions, notes, regions, _facts, homebrew, encounters, wiki, bookDraftRepository: store);
        _reader = new BookSourceReader(_campaigns, _actors, _sessions, encounters, _facts, notes, regions, homebrew, wiki);
        _drafts = new BookDrafts(_reader, store, new RavenBookArtRepository(db), new BookArtReader(new RavenMediaStore(db)), new CampaignAccessResolver(_campaigns));
    }

    private async Task<(CampaignAccess Dm, string Id)> SeedAsync()
    {
        var id = "pb-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Saltmere", SystemId = "srd5e", OwnerId = DmUser, Description = "PUBLIC_DESCRIPTION\n\n:::for [[actor:a-mira-" + id + "]]\nA_ONLY_OVERVIEW\n:::\n\nPUBLIC_DESCRIPTION_END",
            Members =
            {
                new CampaignMember { UserId = DmUser, Role = CampaignRole.DM }, new CampaignMember { UserId = PlayerA, Role = CampaignRole.Player },
                new CampaignMember { UserId = PlayerB, Role = CampaignRole.Player }, new CampaignMember { UserId = PlayerNone, Role = CampaignRole.Player }
            },
            ActivePacks = { "srd5e" }
        });
        var dm = new CampaignAccess(id, DmUser, CampaignRole.DM);
        await _actors.SaveAsync(new ActorDocument { Id = "a-mira-" + id, CampaignId = id, Name = "PUBLIC_Mira", Kind = ActorKind.PlayerCharacter, OwnerUserId = PlayerA, Biography = "PUBLIC_MIRA_BIO\n\n:::for [[actor:a-mira-" + id + "]]\nA_SELF_BLOCK\n:::" });
        await _actors.SaveAsync(new ActorDocument { Id = "a-tor-" + id, CampaignId = id, Name = "PUBLIC_Tor", Kind = ActorKind.PlayerCharacter, OwnerUserId = PlayerB, Biography = "PUBLIC_TOR_BIO" });
        await _actors.SaveAsync(new ActorDocument { Id = "a-hidden-" + id, CampaignId = id, Name = "CANARY_HIDDEN_NAME", Visibility = ActorVisibility.Hidden });
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-unknown-" + id, CampaignId = id, Name = "CANARY_UNKNOWN_REALNAME", Visibility = ActorVisibility.Unknown, PublicName = "PUBLIC_hooded stranger",
            PublicDescription = "PUBLIC_HOODED_LOOK"
        });
        var mira = "[[actor:a-mira-" + id + "]]";
        var tor = "[[actor:a-tor-" + id + "]]";
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-vera-" + id, CampaignId = id, Name = "PUBLIC_Vera", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Known,
            Biography = "PUBLIC_VERA_BIO with [[actor:a-unknown-" + id + "]].\n\n"
                        + ":::for " + mira + "\nA_ONLY_VERA met [[actor:a-hidden-" + id + "|CANARY_LABEL]] and [[actor:a-unknown-" + id + "]].\n:::\n\n"
                        + "PUBLIC_VERA_MIDDLE\n\n"
                        + ":::for " + mira + " " + tor + "\nAB_SHARED_VERA\n:::\n\n"
                        + ":::for " + tor + "\nB_ONLY_VERA\n:::\n\n"
                        + ":::dm\nCANARY_DM_VERA\n\n:::for " + mira + "\nCANARY_NESTED_FOR\n:::\n:::\n\n"
                        + "PUBLIC_VERA_END"
        });
        await _facts.RestoreAsync(new FactDocument
        {
            Id = "f-mira-" + id, CampaignId = id, Summary = "A_ONLY_FACT", Details = "A_ONLY_FACT_DETAILS", Visibility = FactVisibility.KnowersOnly,
            KnownBy = { new KnowerEntry("a-mira-" + id, KnowledgeLevel.Full) }
        });
        await _facts.RestoreAsync(new FactDocument
        {
            Id = "f-tor-" + id, CampaignId = id, Summary = "B_ONLY_FACT", Visibility = FactVisibility.KnowersOnly,
            KnownBy = { new KnowerEntry("a-tor-" + id, KnowledgeLevel.Full) }
        });
        await _facts.RestoreAsync(new FactDocument { Id = "f-pub-" + id, CampaignId = id, Summary = "PUBLIC_FACT", Visibility = FactVisibility.Public });
        await _sessions.SaveAsync(new SessionDocument
        {
            Id = "s-done-" + id, CampaignId = id, Title = "PUBLIC_Session One", Status = SessionStatus.Completed,
            Recap = "PUBLIC_RECAP\n\n:::for " + mira + "\nA_ONLY_RECAP\n:::"
        });
        return (dm, id);
    }

    private async Task<string> BookAsync(CampaignAccess dm, string? player)
    {
        await _drafts.OpenAsync(dm, "party", "en");
        return (await _drafts.MarkdownAsync(dm, "party", "en", player))!.Value.Markdown;
    }

    // From the chapter's marker to the next chapter's (a page break inside a chapter does not end it).
    private static string ChapterOf(string book, string section)
    {
        var start = book.IndexOf("{{chapter,#ch-" + section + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, section);
        var end = book.IndexOf("{{chapter,", start + 1, StringComparison.Ordinal);
        return end < 0 ? book[start..] : book[start..end];
    }

    [Fact]
    public async Task APlayersBook_HasWhatTheirCharactersWereTold_UnderItsEntry_AndNothingOfAnotherPlayersOrTheGms()
    {
        var (dm, _) = await SeedAsync();
        var party = await BookAsync(dm, null);
        var mira = await BookAsync(dm, PlayerA);
        var tor = await BookAsync(dm, PlayerB);

        // The party's own book has none of it.
        Assert.DoesNotMatch("A_ONLY|AB_SHARED|B_ONLY|A_SELF|CANARY|Only for", party);
        // Mira's player: their blocks, the one shared with Tor, lore revealed to Mira, the block on Mira's own history.
        foreach (var shown in new[] { "A_ONLY_VERA", "AB_SHARED_VERA", "A_ONLY_RECAP", "A\\_ONLY\\_FACT", "A_ONLY_FACT_DETAILS", "A_SELF_BLOCK", "##### Only for PUBLIC\\_Mira" })
        {
            Assert.Contains(shown, mira);
        }

        // Never Tor's own, never the GM's, never a block for Mira inside the GM's, never the hidden actor (not even the mention's own label).
        var leaked = Regex.Matches(mira, @"B_ONLY\w*|CANARY\w*").Select(m => m.Value).Distinct().ToList();
        Assert.True(leaked.Count == 0, "Mira's book carries: " + string.Join(", ", leaked));
        Assert.DoesNotContain(":::", mira);
        // A stranger mentioned in Mira's block is named by its mask.
        Assert.Contains("A_ONLY_VERA met  and PUBLIC\\_hooded stranger.", mira);
        // Tor's player: the shared block and Tor's own, nothing of Mira's.
        Assert.Contains("AB_SHARED_VERA", tor);
        Assert.Contains("B_ONLY_VERA", tor);
        Assert.Contains("B\\_ONLY\\_FACT", tor);
        Assert.DoesNotMatch("A_ONLY|A_SELF|CANARY", tor);
        // The party's text is all still there, once.
        foreach (var kept in new[] { "PUBLIC_VERA_BIO", "PUBLIC_VERA_MIDDLE", "PUBLIC_VERA_END", "PUBLIC_RECAP" })
        {
            Assert.Single(Regex.Matches(mira, kept));
        }
    }

    [Fact]
    public async Task EachNote_StandsUnderItsOwnEntry_BeforeTheNextOne()
    {
        var (dm, id) = await SeedAsync();
        var mira = await BookAsync(dm, PlayerA);
        var cast = ChapterOf(mira, "cast");
        var vera = cast.IndexOf("{{entry,#e-" + BookSlots.KeyFor("actor:a-vera-" + id) + "}}", StringComparison.Ordinal);
        // The cast is the people the party meets (51): no player character has an entry in the party's draft, the reader's own included.
        Assert.True(vera > 0);
        Assert.DoesNotContain(BookSlots.KeyFor("actor:a-mira-" + id), mira);
        Assert.DoesNotContain(BookSlots.KeyFor("actor:a-tor-" + id), mira);
        Assert.DoesNotContain("PUBLIC_TOR_BIO", mira);
        // Vera's note after Vera's text; Mira's own after her history and before Tor's entry.
        Assert.True(cast.IndexOf("A_ONLY_VERA", StringComparison.Ordinal) > cast.IndexOf("PUBLIC_VERA_END", StringComparison.Ordinal));
        Assert.True(cast.IndexOf("A_ONLY_VERA", StringComparison.Ordinal) > vera);
        var self = cast.IndexOf("A_SELF_BLOCK", StringComparison.Ordinal);
        // Mira's own character is only in Mira's copy: a note at the cast's end, her history and then the block on it.
        Assert.True(self > cast.IndexOf("PUBLIC_MIRA_BIO", StringComparison.Ordinal) && cast.IndexOf("PUBLIC_MIRA_BIO", StringComparison.Ordinal) > cast.LastIndexOf("{{entry,", StringComparison.Ordinal), cast);
        Assert.Contains("###### PUBLIC\\_Mira", cast);
        // One note per entry, both shared and own blocks in it.
        var note = cast[cast.LastIndexOf("{{note", cast.IndexOf("A_ONLY_VERA", StringComparison.Ordinal), StringComparison.Ordinal)..];
        Assert.True(note.IndexOf("AB_SHARED_VERA", StringComparison.Ordinal) < note.IndexOf("}}", StringComparison.Ordinal));
        Assert.Contains("A_ONLY_RECAP", ChapterOf(mira, "chronicle"));
        Assert.Contains("A_ONLY_FACT_DETAILS", ChapterOf(mira, "lore"));
        // The campaign's own text, which only this reader cleans: its note in the overview, after the overview's text.
        var overview = ChapterOf(mira, "overview");
        Assert.True(overview.IndexOf("A_ONLY_OVERVIEW", StringComparison.Ordinal) > overview.IndexOf("PUBLIC_DESCRIPTION_END", StringComparison.Ordinal), overview);
    }

    [Fact]
    public async Task ANote_StaysOnItsEntrysPage_BeforeTheGmsPageBreak()
    {
        var (dm, id) = await SeedAsync();
        // An entry after Vera's, and the GM's page break before it.
        await _actors.SaveAsync(new ActorDocument { Id = "a-zed-" + id, CampaignId = id, Name = "PUBLIC_Zed", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Known, Biography = "PUBLIC_ZED_BIO" });
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var cast = view.Chapters.Single(c => c.Section == "cast");
        var zed = "{{entry,#e-" + BookSlots.KeyFor("actor:a-zed-" + id) + "}}";
        Assert.Contains(zed, cast.Markdown);
        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown.Replace(zed, "\\page\n\n" + zed), cast.Version)).Result);

        var mira = ChapterOf(await BookAsync(dm, PlayerA), "cast");
        var note = mira.IndexOf("A_ONLY_VERA", StringComparison.Ordinal);
        Assert.True(note > mira.IndexOf("PUBLIC_VERA_END", StringComparison.Ordinal) && note < mira.IndexOf("\\page", StringComparison.Ordinal), mira);
    }

    [Fact]
    public async Task TheGmsEdits_AreKept_AndANoteWhoseEntryTheGmDeleted_GoesAtTheChaptersEnd()
    {
        var (dm, id) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var cast = view.Chapters.Single(c => c.Section == "cast");
        var marker = "{{entry,#e-" + BookSlots.KeyFor("actor:a-vera-" + id) + "}}";
        // The GM rewrote Vera's text and took her marker out, and broke the page before the stranger.
        var edited = cast.Markdown.Replace("PUBLIC_VERA_MIDDLE", "GM_REWROTE_THIS").Replace(marker + "\n", string.Empty);
        var stranger = "{{entry,#e-" + BookSlots.KeyFor("mask:a-unknown-" + id) + "}}";
        Assert.Contains(stranger, edited);
        edited = edited.Replace(stranger, "\\page\n\n" + stranger);
        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveChapterAsync(dm, "party", "en", "cast", edited, cast.Version)).Result);

        var mira = ChapterOf(await BookAsync(dm, PlayerA), "cast");
        Assert.Contains("GM_REWROTE_THIS", mira);
        Assert.DoesNotContain("PUBLIC_VERA_MIDDLE", mira);
        // Vera's note has no entry to stand under: the chapter's end.
        Assert.True(mira.IndexOf("A_ONLY_VERA", StringComparison.Ordinal) > mira.LastIndexOf("{{entry,", StringComparison.Ordinal));
        // The GM's page break is kept where it was put.
        Assert.Contains("\\page\n\n" + stranger, mira);
        Assert.Contains("A_SELF_BLOCK", mira);
    }

    [Fact]
    public async Task OnlyAPlayerWithACharacter_HasABook_AndOnlyTheGmCanAskForIt()
    {
        var (dm, id) = await SeedAsync();
        await _drafts.OpenAsync(dm, "party", "en");
        await _drafts.OpenAsync(dm, "dm", "en");
        Assert.NotNull(await _drafts.MarkdownAsync(dm, "party", "en", PlayerA));
        // No character, the GM, a stranger, someone who is not a member: no book.
        foreach (var who in new[] { PlayerNone, DmUser, "nobody-at-all", "pb_not_a_member" })
        {
            Assert.Null(await _drafts.MarkdownAsync(dm, "party", "en", who));
            Assert.Null((await _drafts.PageAsync(dm, "party", "en", BookArtQuality.Preview, _ => null, CancellationToken.None, withArt: false, player: who)).Page);
        }

        // Only from the party draft, and only for the GM.
        Assert.Null(await _drafts.MarkdownAsync(dm, "dm", "en", PlayerA));
        Assert.Null(await _drafts.MarkdownAsync(new CampaignAccess(id, PlayerA, CampaignRole.Player), "party", "en", PlayerA));
        Assert.Empty(await _drafts.PlayersAsync(new CampaignAccess(id, PlayerA, CampaignRole.Player)));
        // A player who left has none either.
        await _campaigns.UpdateAsync(id, c => c.Members.RemoveAll(m => m.UserId == PlayerB));
        Assert.Null(await _drafts.MarkdownAsync(dm, "party", "en", PlayerB));

        var players = await _drafts.PlayersAsync(dm);
        var only = Assert.Single(players);
        Assert.Equal((PlayerA, "PUBLIC_Mira"), (only.UserId, string.Join(", ", only.Characters)));
    }

    [Fact]
    public async Task APlayersPdf_IsTheirCopy_WithThePartysPictures_AndNamedByTheirCharacters()
    {
        var (dm, _) = await SeedAsync();
        await _drafts.OpenAsync(dm, "party", "en");
        var (page, _) = await _drafts.PageAsync(dm, "party", "en", BookArtQuality.Preview, _ => null, CancellationToken.None, withArt: false, player: PlayerA);
        Assert.NotNull(page);
        Assert.Equal("PUBLIC_Mira", page.Player);
        Assert.Contains("A_ONLY_VERA", page.Markdown);
        Assert.Contains(page.Chapters, c => c.Markdown.Contains("A_ONLY_VERA", StringComparison.Ordinal));
        Assert.Equal("Saltmere (PUBLIC_Mira).pdf", BookEndpoints.FileName(page.Title, "party", player: page.Player));
        var (plain, _) = await _drafts.PageAsync(dm, "party", "en", BookArtQuality.Preview, _ => null, CancellationToken.None, withArt: false);
        Assert.Null(plain!.Player);
        Assert.DoesNotContain("A_ONLY", plain.Markdown);
    }

    [Theory]
    [InlineData("Солеморье", "Мира, Тор", "Солеморье (Мира, Тор).pdf")]
    [InlineData("מלחים", "מירה", "מלחים (מירה).pdf")]
    [InlineData("Salt/mere", "Mi:ra?\u202e", "Saltmere (Mira).pdf")]
    [InlineData("Saltmere", "  ", "Saltmere (party).pdf")]
    public void APlayersFile_IsNamedByTheirCharacters_WithNothingAFileSystemRefuses(string title, string player, string expected) =>
        Assert.Equal(expected, BookEndpoints.FileName(title, "party", player: System.Text.RegularExpressions.Regex.Unescape(player)));

    [Fact]
    public void Extra_IsWhatTheLongerCopyHas_OffTheirCommonOrder()
    {
        Assert.Equal(["x", "y"], BookPlayerBook.Extra(["a", "x", "b", "y"], ["a", "b"]));
        Assert.Equal(["a", "b"], BookPlayerBook.Extra(["a", "b"], []));
        // A paragraph the party has in another form is the player's whole; a repeated one is matched once.
        Assert.Equal(["a+", "a"], BookPlayerBook.Extra(["a+", "a", "a"], ["a"]));
        Assert.Empty(BookPlayerBook.Extra(["a", "b"], ["a", "b"]));
    }
}
