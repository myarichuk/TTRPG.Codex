using Codex.Core.Book;
using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>
/// The editable book (docs/BOOK_EXPORT.md 41): the generator, the draft store and the service. The party draft is generated from the party source
/// only, so it holds no <c>CANARY_*</c> string and not even the id of an actor the party knows only by a mask; the GM's edits are the GM's, and a
/// chapter nobody edited follows the campaign by itself.
/// </summary>
[Collection("RavenDb")]
public class BookDraftTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "draft_dm_user";
    private const string PlayerUser = "draft_player_user";

    private readonly CampaignRepository _campaigns;
    private readonly ActorRepository _actors;
    private readonly RavenWikiRepository _wiki;
    private readonly BookSourceReader _reader;
    private readonly RavenBookDraftRepository _store;
    private readonly BookDrafts _drafts;
    private readonly RavenDbService _db;

    public BookDraftTests(RavenDbFixture fixture)
    {
        var db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actors = new ActorRepository(db);
        var sessions = new RavenSessionRepository(db);
        var notes = new RavenNoteRepository(db);
        var regions = new RegionRepository(db);
        var facts = new FactRepository(db);
        var encounters = new RavenEncounterRepository(db);
        var homebrew = new RavenHomebrewRepository(db);
        _wiki = new RavenWikiRepository(db);
        _store = new RavenBookDraftRepository(db);
        _db = db;
        _campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), _actors, sessions, notes, regions, facts, homebrew, encounters, _wiki, bookDraftRepository: _store);
        _reader = new BookSourceReader(_campaigns, _actors, sessions, encounters, facts, notes, regions, homebrew, _wiki);
        _drafts = new BookDrafts(_reader, _store, new RavenBookArtRepository(db), new BookArtReader(new RavenMediaStore(db)), new CampaignAccessResolver(_campaigns));
    }

    private async Task<(CampaignAccess Dm, string Id)> SeedAsync()
    {
        var id = "draft-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Saltmere", SystemId = "srd5e", OwnerId = DmUser, Tagline = "PUBLIC_TAGLINE",
            Description = "PUBLIC_DESCRIPTION\n\n:::dm\nCANARY_CAMPAIGN_DM\n:::",
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM }, new CampaignMember { UserId = PlayerUser, Role = CampaignRole.Player } },
            ActivePacks = { "srd5e" }
        });
        var dm = new CampaignAccess(id, DmUser, CampaignRole.DM);
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-vera-" + id, CampaignId = id, Name = "PUBLIC_Vera", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Known,
            // A mention of the hidden actor whose own label carries its name: the label must never be printed.
            Biography = "PUBLIC_BIO_VERA met [[actor:a-hidden-" + id + "|CANARY_LABEL]] at <b>dawn</b> & dusk.\n\n:::dm\nCANARY_BIO_DM\n:::"
        });
        await _actors.SaveAsync(new ActorDocument { Id = "a-hidden-" + id, CampaignId = id, Name = "CANARY_HIDDEN_NAME", Visibility = ActorVisibility.Hidden, Biography = "CANARY_HIDDEN_BIO" });
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "CANARYID-unknown-" + id, CampaignId = id, Name = "CANARY_UNKNOWN_REALNAME", Visibility = ActorVisibility.Unknown,
            PublicName = "PUBLIC_hooded stranger", PublicDescription = "PUBLIC_HOODED_LOOK", Biography = "CANARY_UNKNOWN_BIO"
        });
        await _wiki.SaveAsync(new WikiPageDocument
        {
            Id = "w-dm-" + id, CampaignId = id, Slug = "dm-page", Title = "CANARY_WIKI_DM_TITLE", Body = "CANARY_WIKI_DM_BODY", Visibility = WikiVisibility.DmOnly, AuthorUserId = DmUser
        });
        await _wiki.SaveAsync(new WikiPageDocument
        {
            Id = "w-party-" + id, CampaignId = id, Slug = "docks", Title = "PUBLIC_Docks", Visibility = WikiVisibility.Party, AuthorUserId = DmUser,
            Body = "PUBLIC_DOCKS_BODY, see [[CANARY_WIKI_DM_TITLE]] and [[CANARY_WIKI_DM_TITLE|PUBLIC_SHOWN]].\n\n:::dm\nCANARY_WIKI_BLOCK\n:::"
        });
        return (dm, id);
    }

    private Task BioAsync(string campaignId, string biography) => _actors.UpdateAsync("a-vera-" + campaignId, a => a.Biography = biography);

    private static string All(IEnumerable<BookDraftGenerated> chapters) => string.Join("\n", chapters.Select(c => c.Markdown));

    [Fact]
    public async Task APictureTheBookLeavesOut_IsNamedByItsChapterAndTheEditionsOwnLabel_NeverByAnId()
    {
        var (dm, id) = await SeedAsync();
        var media = new RavenMediaStore(_db);
        // Vera and the hidden actor have portraits; a cap of one byte leaves every picture out.
        foreach (var actorId in new[] { "a-vera-" + id, "a-hidden-" + id })
        {
            var actor = (await _actors.GetVisibleAsync(actorId, dm))!;
            actor.HasPortrait = true;
            actor.PortraitVersion = 1;
            await _actors.SaveAsync(actor);
            await media.PutAsync(dm, actorId, RavenMediaStore.ActorPortrait, new MemoryStream(BookArtTests.Png(202)), "image/png");
        }

        var drafts = new BookDrafts(_reader, _store, new RavenBookArtRepository(_db), new BookArtReader(media, new BookImageScaler(), 1), new CampaignAccessResolver(_campaigns));
        var party = await drafts.ArtAsync(dm, "party", BookArtQuality.Preview, CancellationToken.None);
        var gm = await drafts.ArtAsync(dm, "dm", BookArtQuality.Preview, CancellationToken.None);

        Assert.Empty(party.Art);
        var vera = Assert.Single(party.LeftOut);
        Assert.Equal(("PUBLIC_Vera", "limit"), (vera.Label, vera.Reason));
        Assert.True(BookDraftDocument.IsSection(vera.Section), vera.Section);
        Assert.DoesNotContain(party.LeftOut, l => (l.Label + l.Section).Contains("CANARY") || (l.Label + l.Section).Contains(id));
        // The GM edition names both, the hidden one by its real name.
        Assert.Equal(["CANARY_HIDDEN_NAME", "PUBLIC_Vera"], gm.LeftOut.Select(l => l.Label).Order());
        // Not the GM of this campaign: nothing.
        Assert.Same(BookDraftArt.None, await drafts.ArtAsync(new CampaignAccess(id, PlayerUser, CampaignRole.Player), "party", BookArtQuality.Preview, CancellationToken.None));
    }

    [Fact]
    public async Task ThePartyDraft_HoldsNoCanary_NotEvenTheIdOfAMaskedActor_AndStillHasThePublicText()
    {
        var (dm, id) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;

        var markdown = All(BookDraftWriter.Generate(source, "en", null));

        var leaked = Regex.Matches(markdown, @"CANARY\w*").Select(m => m.Value).Distinct().ToList();
        Assert.True(leaked.Count == 0, "The party draft carries what the party may not read: " + string.Join(", ", leaked));
        Assert.DoesNotContain(":::", markdown);
        foreach (var shown in new[] { "PUBLIC\\_Vera", "PUBLIC_BIO_VERA", "PUBLIC\\_hooded stranger", "PUBLIC_HOODED_LOOK", "PUBLIC_DOCKS_BODY", "PUBLIC\\_SHOWN", "PUBLIC\\_TAGLINE" })
        {
            Assert.Contains(shown, markdown);
        }

        // The stranger's entry is keyed by its mask, as the party's slots are.
        Assert.Contains("{{entry,#e-" + BookSlots.KeyFor("mask:CANARYID-unknown-" + id) + "}}", markdown);
        // HTML in the campaign's text is text, not markup.
        Assert.Contains(@"\<b>dawn\</b> \& dusk", markdown);
    }

    [Fact]
    public async Task TheGmDraft_BoxesSecretsInSecretBlocks_AndNeverWritesTheAppsOwnMarkers()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;

        var markdown = All(BookDraftWriter.Generate(source, "en", null));

        Assert.Contains("{{secret\nCANARY_BIO_DM\n}}", markdown);
        Assert.Contains("CANARY\\_WIKI\\_DM\\_TITLE", markdown);
        // ":::" is a definition list to Homebrewery's renderer: a secret written that way would print as text.
        Assert.DoesNotContain(":::", markdown);
        // The label of a mention is never used; the GM edition names the hidden actor by its name.
        Assert.DoesNotContain("CANARY_LABEL", markdown);
        Assert.Contains("CANARY\\_HIDDEN\\_NAME", markdown);
    }

    [Fact]
    public void TheCampaignsMarkdown_BecomesDraftMarkdown()
    {
        string? Names(string kind, string id) => kind == "actor" && id == "a1" ? "Vera *Holloway*" : null;

        Assert.Equal("Met Vera \\*Holloway\\* and .\n", BookDraftWriter.Text("Met [[actor:a1|Ignored]] and [[actor:a2|CANARY]].", Names, dm: true));
        Assert.Equal("See Docks and here.\n", BookDraftWriter.Text("See [[Docks]] and [[Old page|here]].", Names, dm: false));
        Assert.Equal("A map\\.\n", BookDraftWriter.Text("![A map.](asset:a3)", Names, dm: true));
        Assert.Equal("\\<script>x\\</script> `<kept>` \\& more\n", BookDraftWriter.Text("<script>x</script> `<kept>` & more", Names, dm: true));
        // A lone backtick opens nothing: what follows it is still escaped.
        Assert.Equal("a ` \\<b>\n", BookDraftWriter.Text("a ` <b>", Names, dm: true));
        Assert.Equal("open \n", BookDraftWriter.Text("open [[actor:a1 never closes", Names, dm: true));
        // A break is the GM's to write: one in campaign text (a player's biography) prints as its words, never breaking or restyling a page.
        Assert.Equal("Bio\n\n\\\\page {display:none}\n\\\\column\n", BookDraftWriter.Text("Bio\n\n\\page {display:none}\n\\column", Names, dm: false));
        Assert.Equal("Text\n\n{{secret\nThe truth.\n}}\n\n", BookDraftWriter.Text("Text\n:::dm\nThe truth.\n:::", Names, dm: true));
        Assert.Equal("Text\n", BookDraftWriter.Text("Text\n:::dm\nThe truth.\n:::", Names, dm: false));
    }

    [Fact]
    public async Task TheMarkdownFile_IsTheEditionsOwnChapters_WithFitPagesMarksTakenOut_AndOnlyForTheGm()
    {
        var (dm, id) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var cast = view.Chapters.Single(c => c.Section == "cast");
        await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown + "\n\\page {fitted}\n\nPUBLIC_AFTER\n\n\\page {reliquary, fitted}\n\nPUBLIC_STYLED\n", cast.Version);

        var (title, markdown, _) = (await _drafts.MarkdownAsync(dm, "party", "en"))!.Value;
        Assert.Equal("Saltmere", title);
        Assert.Contains("PUBLIC_BIO_VERA", markdown);
        Assert.Contains("\n\\page\n\nPUBLIC_AFTER", markdown);
        Assert.Contains("\n\\page {reliquary}\n\nPUBLIC_STYLED", markdown);
        Assert.DoesNotContain(BookDraftText.FitClass, markdown);
        Assert.DoesNotContain("CANARY", markdown);
        // Every chapter, in order, a page apart.
        var at = view.Chapters.Select(c => markdown.IndexOf(c.Markdown.Split('\n')[0], StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i >= 0));
        Assert.Equal(at.Order(), at);

        // Only a draft there is: the GM edition has none until it is opened.
        Assert.Null(await _drafts.MarkdownAsync(dm, "dm", "en"));
        await _drafts.OpenAsync(dm, "dm", "en");
        Assert.Contains("CANARY_BIO_DM", (await _drafts.MarkdownAsync(dm, "dm", "en"))!.Value.Markdown);
        Assert.Null(await _drafts.MarkdownAsync(new CampaignAccess(id, PlayerUser, CampaignRole.Player), "party", "en"));
        Assert.Null(await _drafts.MarkdownAsync(dm, "neither", "en"));
    }

    [Fact]
    public async Task AChaptersText_IsSavedOnlyFromItsCurrentVersion_SoAnotherTabCannotOverwriteIt()
    {
        var (dm, _) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var cast = view.Chapters.Single(c => c.Section == "cast");

        var first = await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown + "\nPUBLIC_TAB_ONE\n", cast.Version);
        var second = await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown + "\nTAB_TWO\n", cast.Version);

        Assert.Equal(BookDraftSave.Saved, first.Result);
        Assert.Equal(BookDraftSave.Conflict, second.Result);
        Assert.Equal(first.Version, second.Version);
        var now = (await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        Assert.Contains("PUBLIC_TAB_ONE", now.Markdown);
        Assert.DoesNotContain("TAB_TWO", now.Markdown);
        Assert.True(now.Edited);
    }

    [Fact]
    public async Task WhenTheCampaignChanges_AnUneditedChapterFollows_AndAnEditedOneSaysSo_NamingTheEntry()
    {
        var (dm, id) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var cast = view.Chapters.Single(c => c.Section == "cast");
        var pages = view.Chapters.Single(c => c.Section == "pages");
        await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown.Replace("PUBLIC_BIO_VERA", "PUBLIC_GM_WORDS"), cast.Version);

        await BioAsync(id, "PUBLIC_NEW_BIO");
        await _wiki.SaveAsync(new WikiPageDocument
        {
            Id = "w-party-" + id, CampaignId = id, Slug = "docks", Title = "PUBLIC_Docks", Visibility = WikiVisibility.Party, AuthorUserId = DmUser, Body = "PUBLIC_NEW_DOCKS"
        });

        var after = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var castNow = after.Chapters.Single(c => c.Section == "cast");
        var pagesNow = after.Chapters.Single(c => c.Section == "pages");

        Assert.True(castNow.Stale);
        Assert.Contains("PUBLIC_GM_WORDS", castNow.Markdown);
        Assert.Equal(["PUBLIC_Vera"], castNow.Changed);
        Assert.False(pagesNow.Stale);
        Assert.False(pagesNow.Edited);
        Assert.Contains("PUBLIC_NEW_DOCKS", pagesNow.Markdown);
        Assert.Equal(1, after.Followed!.Updated);
        Assert.True(pagesNow.Version > pages.Version);
    }

    [Fact]
    public async Task Regenerate_TakesTheCampaignsText_AndUndoBringsBackTheGmsText_AndTheNotice()
    {
        var (dm, id) = await SeedAsync();
        var cast = (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        var saved = await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown + "\nPUBLIC_MINE\n", cast.Version);
        await BioAsync(id, "PUBLIC_NEW_BIO");

        // From an older version (another tab) it is refused like any save.
        Assert.Equal(BookDraftSave.Conflict, (await _drafts.RegenerateAsync(dm, "party", "en", "cast", cast.Version)).Result);
        var regenerated = await _drafts.RegenerateAsync(dm, "party", "en", "cast", saved.Version);
        Assert.Equal(BookDraftSave.Saved, regenerated.Result);
        var fresh = (await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        Assert.Contains("PUBLIC_NEW_BIO", fresh.Markdown);
        Assert.DoesNotContain("PUBLIC_MINE", fresh.Markdown);
        Assert.False(fresh.Edited);
        Assert.False(fresh.Stale);
        Assert.True(fresh.CanUndo);

        Assert.Equal(BookDraftSave.Saved, (await _drafts.UndoAsync(dm, "party", "en", "cast", fresh.Version)).Result);
        var back = (await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        Assert.Contains("PUBLIC_MINE", back.Markdown);
        Assert.True(back.Stale);
    }

    [Fact]
    public async Task KeepMine_HidesTheNotice_UntilTheCampaignChangesAgain()
    {
        var (dm, id) = await SeedAsync();
        var cast = (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        var saved = await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown + "\nPUBLIC_MINE\n", cast.Version);
        await BioAsync(id, "PUBLIC_ONE");
        Assert.Equal(BookDraftSave.Saved, (await _drafts.KeepAsync(dm, "party", "en", "cast", saved.Version)).Result);
        Assert.False((await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast").Stale);

        await BioAsync(id, "PUBLIC_TWO");
        Assert.True((await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast").Stale);
    }

    [Fact]
    public async Task PageAndColumnBreaks_AreLayout_NotEdits()
    {
        var (dm, id) = await SeedAsync();
        var cast = (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        var fitted = cast.Markdown.Replace("{{entry,", "\\page\n\n{{entry,").Replace("PUBLIC_HOODED_LOOK", "PUBLIC_HOODED_LOOK\n\n\\column\n");
        await _drafts.SaveChapterAsync(dm, "party", "en", "cast", fitted, cast.Version);

        Assert.False((await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast").Edited);

        // So it still follows the campaign by itself (its breaks are then those of the new text).
        await BioAsync(id, "PUBLIC_NEW_BIO");
        var after = (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        Assert.Contains("PUBLIC_NEW_BIO", after.Markdown);
        Assert.False(after.Stale);
        // A break that styles its page is the GM's choice, so it is an edit: the chapter would not be replaced without a word.
        var dark = await _drafts.SaveChapterAsync(dm, "party", "en", "cast", "\\page {reliquary}\n\n" + after.Markdown, after.Version);
        Assert.Equal(BookDraftSave.Saved, dark.Result);
        Assert.True((await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast").Edited);
    }

    /// <summary>The break Fit pages writes, "\page {fitted}", is layout like a plain one (41.15); with a styled page's classes it is not.</summary>
    [Fact]
    public void AFittedBreak_IsLayout_UnlessItStylesThePage()
    {
        Assert.True(BookDraftText.IsBreak(@"\page {fitted}"));
        Assert.True(BookDraftText.IsBreak(@"\page { fitted }"));
        Assert.False(BookDraftText.IsBreak(@"\page {reliquary,fitted}"));
        Assert.False(BookDraftText.IsBreak(@"\page {fittedx}"));
        Assert.True(BookDraftText.IsFitted(@"\page {reliquary,fitted}") && BookDraftText.IsFitted(@" \page {fitted} "));
        Assert.False(BookDraftText.IsFitted(@"\page") || BookDraftText.IsFitted(@"\page {reliquary}") || BookDraftText.IsFitted("text"));
        Assert.Equal(BookDraftText.Content("A\n\nB\nC"), BookDraftText.Content("A\n\n\\page {fitted}\n\nB\n\\page {fitted}\nC"));
    }

    /// <summary>Fit pages saves each chapter it gave breaks to at the version it read, and only those; the breaks leave a chapter unedited.
    /// A chapter saved meanwhile (another tab) is not overwritten: the run stops there and says so.</summary>
    [Fact]
    public async Task FitPages_SavesTheChaptersItChanged_AtTheirVersions_AndStopsAtAConflict()
    {
        var (dm, _) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var cast = view.Chapters.Single(c => c.Section == "cast");
        var untouched = view.Chapters.Where(c => c.Section != "cast").Select(c => (c.Section, c.Version)).ToList();
        string Fitted(string md) => md.Replace("{{entry,", "\\page {fitted}\n\n{{entry,");
        Task<BookFitted> Layout(BookDraftPage page) => Task.FromResult(new BookFitted
        {
            Chapters = [.. page.Chapters.Select(c => new BookLayoutChapter(c.Section, c.Section == "cast" ? Fitted(c.Markdown) : c.Markdown))],
            Overflow = [new BookOverflow(4, "cast", "PUBLIC_WORDS")]
        });

        var report = (await _drafts.FitAsync(dm, "party", "en", _ => null, Layout, CancellationToken.None))!;
        Assert.Equal(BookDraftSave.Saved, report.Result);
        Assert.Equal(1, report.Chapters);
        Assert.True(report.Breaks > 0);
        Assert.Equal("PUBLIC_WORDS", Assert.Single(report.Overflow).Words);
        var after = (await _drafts.GetAsync(dm, "party", "en"))!;
        var fittedCast = after.Chapters.Single(c => c.Section == "cast");
        Assert.Equal(Fitted(cast.Markdown), fittedCast.Markdown);
        Assert.False(fittedCast.Edited);
        Assert.Equal(untouched, after.Chapters.Where(c => c.Section != "cast").Select(c => (c.Section, c.Version)).ToList());

        // Another tab saves the chapter while the book is being laid out: its text stays, and the report says Conflict.
        async Task<BookFitted> Meanwhile(BookDraftPage page)
        {
            var c = page.Chapters.Single(x => x.Section == "cast");
            await _drafts.SaveChapterAsync(dm, "party", "en", "cast", c.Markdown + "\n\nPUBLIC_OTHER_TAB\n", c.Version);
            return await Layout(page);
        }

        var conflict = (await _drafts.FitAsync(dm, "party", "en", _ => null, Meanwhile, CancellationToken.None))!;
        Assert.Equal(BookDraftSave.Conflict, conflict.Result);
        Assert.Contains("PUBLIC_OTHER_TAB", (await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast").Markdown);

        // Someone who is not the GM gets nothing laid out.
        var player = new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player);
        Assert.Null(await _drafts.FitAsync(player, "party", "en", _ => null, _ => throw new InvalidOperationException("not laid out"), CancellationToken.None));
    }

    /// <summary>The PDF's overflow header: page numbers and section ids only, read back the same; anything else is skipped.</summary>
    [Fact]
    public void TheOverflowHeader_CarriesPagesAndSections_AndReadsBackTheSame()
    {
        var header = Codex.Web.BookEndpoints.OverflowHeader([new BookOverflow(7, "cast", "Ж words"), new BookOverflow(9, null, null), new BookOverflow(3, "Bad Section", null)]);
        Assert.Equal("7:cast,9,3", header);
        Assert.Equal([(7, "cast"), (9, (string?)null), (3, null)], Codex.Web.BookEndpoints.ReadOverflowHeader(header));
        Assert.Equal([(2, (string?)"lore"), (5, null)], Codex.Web.BookEndpoints.ReadOverflowHeader("x,2:lore,-1,0:cast,5:<b>,"));
        Assert.Empty(Codex.Web.BookEndpoints.ReadOverflowHeader(null));
    }

    [Fact]
    public async Task ATheme_IsStoredWithTheDraft_AnUnknownOneIsRefused_AndTheChaptersAreNotTouched()
    {
        var (dm, _) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        Assert.Equal("plain", view.Theme);
        var before = view.Chapters.Select(c => (c.Section, c.Markdown, c.Version)).ToList();

        var (unknown, _) = await _drafts.SaveSettingsAsync(dm, "party", "en", view.Css, view.PageSize, "../phb", true, view.Version);
        Assert.Equal(BookDraftSave.NotAllowed, unknown);
        var (saved, version) = await _drafts.SaveSettingsAsync(dm, "party", "en", view.Css, view.PageSize, "Ashen-Vellum", true, view.Version);
        Assert.Equal(BookDraftSave.Saved, saved);

        var after = (await _drafts.GetAsync(dm, "party", "en"))!;
        Assert.Equal("ashen-vellum", after.Theme);
        Assert.Equal(version, after.Version);
        Assert.Equal(before, after.Chapters.Select(c => (c.Section, c.Markdown, c.Version)).ToList());
        // A save from the copy before the change is refused, as for the stylesheet.
        Assert.Equal(BookDraftSave.Conflict, (await _drafts.SaveSettingsAsync(dm, "party", "en", view.Css, view.PageSize, "plain", true, view.Version)).Result);
    }

    [Fact]
    public async Task TheHomebreweryLayer_IsOnForADraftSavedBeforeItExisted_AndTheGmCanTurnItOff()
    {
        var (dm, id) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        Assert.True(view.Homebrewery);

        var (saved, _) = await _drafts.SaveSettingsAsync(dm, "party", "en", view.Css, view.PageSize, view.Theme, false, view.Version);
        Assert.Equal(BookDraftSave.Saved, saved);
        Assert.False((await _drafts.GetAsync(dm, "party", "en"))!.Homebrewery);

        // A draft stored before the setting existed has no such field (here: the "off" just saved, taken out again): it reads as on.
        await _db.Store.Operations.SendAsync(new Raven.Client.Documents.Operations.PatchOperation(BookDraftDocument.IdFor(id, "party", "en"), null,
            new Raven.Client.Documents.Operations.PatchRequest { Script = "delete this.Homebrewery;" }));
        Assert.True((await _drafts.GetAsync(dm, "party", "en"))!.Homebrewery);
    }

    [Fact]
    public async Task TornEdges_AreOffUntilTheGmTurnsThemOn_AreKeptByOtherSettings_AndReachThePrintedShell()
    {
        var (dm, _) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        Assert.False(view.Edge);
        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveSettingsAsync(dm, "party", "en", view.Css, view.PageSize, view.Theme, true, view.Version, edge: true)).Result);
        view = (await _drafts.GetAsync(dm, "party", "en"))!;
        Assert.True(view.Edge);
        // Another setting saved without saying: the edge stays.
        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveSettingsAsync(dm, "party", "en", view.Css, view.PageSize, view.Theme, false, view.Version)).Result);
        view = (await _drafts.GetAsync(dm, "party", "en"))!;
        Assert.True(view.Edge);
        var (page, _) = await _drafts.PageAsync(dm, "party", "en", BookArtQuality.Preview, _ => null, CancellationToken.None, withArt: false);
        Assert.Contains(".page::before", page!.Shell);
        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveSettingsAsync(dm, "party", "en", view.Css, view.PageSize, view.Theme, false, view.Version, edge: false)).Result);
        (page, _) = await _drafts.PageAsync(dm, "party", "en", BookArtQuality.Preview, _ => null, CancellationToken.None, withArt: false);
        Assert.DoesNotContain(".page::before", page!.Shell);
    }

    [Fact]
    public async Task OnlyTheCampaignsGm_HasADraft_AndAPostedSectionOrLanguageIsChecked()
    {
        var (dm, id) = await SeedAsync();
        var player = new CampaignAccess(id, PlayerUser, CampaignRole.Player);
        Assert.Null(await _drafts.OpenAsync(player, "party", "en"));
        Assert.NotNull(await _drafts.OpenAsync(dm, "party", "en"));
        Assert.Null(await _drafts.GetAsync(player, "party", "en"));
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.SaveChapterAsync(player, "party", "en", "cast", "x", 1)).Result);
        // A forged role in the access is not believed: the campaign is asked.
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.SaveChapterAsync(new CampaignAccess(id, PlayerUser, CampaignRole.DM), "party", "en", "cast", "x", 1)).Result);

        Assert.Null(await _drafts.OpenAsync(dm, "party", "../en"));
        Assert.Null(await _drafts.OpenAsync(dm, "both", "en"));
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.SaveChapterAsync(dm, "party", "en", "../../users/x", "x", 1)).Result);
        Assert.Equal(BookDraftSave.TooLarge, (await _drafts.SaveChapterAsync(dm, "party", "en", "cast", new string('x', BookDraftDocument.MaxChapterChars + 1), 1)).Result);
    }

    [Fact]
    public async Task TheTwoEditions_AreTwoDrafts_AndDeletingTheCampaignDeletesBoth()
    {
        var (dm, id) = await SeedAsync();
        var party = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var gm = (await _drafts.OpenAsync(dm, "dm", "en"))!;
        Assert.DoesNotContain("CANARY", string.Join("", party.Chapters.Select(c => c.Markdown)));
        Assert.Contains("CANARY", string.Join("", gm.Chapters.Select(c => c.Markdown)));

        Assert.Equal(CampaignDeleteResult.Deleted, await _campaigns.DeleteAsync(id, DmUser));
        using var session = _db.Store.OpenAsyncSession();
        Assert.Empty(await session.Advanced.LoadStartingWithAsync<BookDraftChapter>(BookDraftDocument.PrefixFor(id)));
    }

    /// <summary>A site that answers for https://fixture.test/ from a list, so the service's fetches go through the real fetcher and nowhere else.</summary>
    private sealed class FixtureSite(Dictionary<string, (int Status, string Type, byte[] Body)> pages) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var page = pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var p) ? p : (Status: 404, Type: "text/html", Body: "<p>Not found"u8.ToArray());
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)page.Status) { Content = new ByteArrayContent(page.Body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(page.Type);
            return Task.FromResult(response);
        }
    }

    /// <summary>The draft keeps a copy of each web address its stylesheet names (docs/BOOK_EXPORT.md 41.14): fetched once, kept while the sheet
    /// names it, printed from, and gone with "start over". Only the GM reads or writes them.</summary>
    [Fact]
    public async Task TheStylesheetsWebAddresses_AreKeptWithTheDraft_WhileItNamesThem()
    {
        var (dm, id) = await SeedAsync();
        await _drafts.OpenAsync(dm, "party", "en");
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var site = new FixtureSite(new()
        {
            ["https://fixture.test/theme/fonts.css"] = (200, "text/plain", "@font-face{font-family:WebFace;src:url(face.woff2)}\n.page{background:url(tex.png)}"u8.ToArray()),
            ["https://fixture.test/theme/face.woff2"] = (200, "application/octet-stream", "wOF2\0\u0001\0\0FACE"u8.ToArray()),
            ["https://fixture.test/theme/tex.png"] = (200, "image/png", png),
            ["https://fixture.test/note.png"] = (200, "image/png", png)
        });
        var webs = new BookDraftWebs(_store, BookWebFetcher.Over(new HttpClient(site), httpsOnly: true), Microsoft.Extensions.Logging.Abstractions.NullLogger<BookDraftWebs>.Instance);
        const string css = "@import url('https://fixture.test/theme/fonts.css');\n.note{background:url(https://fixture.test/note.png)}\n.gone{background:url(https://fixture.test/gone.png)}";

        var first = (await webs.RefreshAsync(dm, "party", "en", css, CancellationToken.None))!;
        var asked = site.Requests;
        var again = (await webs.RefreshAsync(dm, "party", "en", css, CancellationToken.None))!;

        Assert.Equal((4, 4, true), (first.Fetched, first.Kept, first.Changed));
        Assert.Equal(("https://fixture.test/gone.png", BookWebProblem.Refused, 404), first.Failed.Select(f => (f.Url, f.Problem, f.Status)).Single());
        // Kept copies are not fetched again; the one that failed is tried again.
        Assert.Equal(asked + 1, site.Requests);
        Assert.Equal((0, 4, false), (again.Fetched, again.Kept, again.Changed));

        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveSettingsAsync(dm, "party", "en", css, "Letter", "plain", true, (await _drafts.GetAsync(dm, "party", "en"))!.Version)).Result);
        var (page, _) = await _drafts.PageAsync(dm, "party", "en", BookArtQuality.Preview, _ => null, CancellationToken.None, withArt: false);
        var gm = page!.Shell[page.Shell.IndexOf("<style id=\"gm-css\">", StringComparison.Ordinal)..];
        Assert.Contains("font-family:WebFace", gm);
        Assert.Contains("data:font/woff2;base64,", gm);
        Assert.Contains("data:image/png;base64,", gm);
        Assert.DoesNotContain("fixture.test", gm);

        // Nobody but the GM reads or writes them.
        var player = new CampaignAccess(id, PlayerUser, CampaignRole.Player);
        Assert.Empty((await _store.WebAsync(player, "party", "en")).Files);
        Assert.Equal(BookDraftSave.NotAllowed, await _store.SaveWebAsync(player, "party", "en", [], new HashSet<string>()));

        // A copy the sheet no longer names is let go.
        var fewer = (await webs.RefreshAsync(dm, "party", "en", "@import url('https://fixture.test/theme/fonts.css');", CancellationToken.None))!;
        Assert.Equal((3, true), (fewer.Kept, fewer.Changed));
        Assert.DoesNotContain((await _store.WebAsync(dm, "party", "en")).Files, f => f.Url.EndsWith("note.png", StringComparison.Ordinal));

        Assert.True(await _drafts.StartOverAsync(dm, "party", "en"));
        using var session = _db.Store.OpenAsyncSession();
        Assert.Null(await session.LoadAsync<BookDraftWeb>(BookDraftWeb.IdFor(id, "party", "en")));
    }

    // AI edits (docs/BOOK_EXPORT.md 41.4, phase 4): a scripted model, and the game system's own definition, so its book shape is what is sent.
    private sealed class EditModels(IChatClient chat, Exception? refuse = null, bool none = false) : IBookModels
    {
        public int Opened { get; private set; }

        public Task<BookModelInfo?> InfoAsync(string userId) => Task.FromResult(none ? null : new BookModelInfo("Test · model-x", null, null));

        public Task<BookModelHandle> OpenAsync(string userId, int requests)
        {
            if (refuse != null) throw refuse;
            Opened++;
            return Task.FromResult(new BookModelHandle(chat, "Test · model-x", 1, null, null));
        }
    }

    private sealed class SrdCatalog : Codex.Plugin.Abstractions.ISystemCatalog
    {
        private readonly Codex.Plugin.Abstractions.SystemDefinition _srd =
            Codex.Core.Systems.SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

        public bool IsLoaded => true;
        public IReadOnlySet<string> LoadedSystemIds { get; } = new HashSet<string> { "srd5e" };
        public IEnumerable<Codex.Plugin.Abstractions.SystemSummary> Systems => [];
        public string GetDisplayName(string systemId) => systemId == "srd5e" ? _srd.Name : systemId;
        public Codex.Plugin.Abstractions.SystemDefinition? GetDefinition(string systemId) => systemId == "srd5e" ? _srd : null;
        public IEnumerable<Codex.Plugin.Abstractions.UISchema> GetUISchemas(string systemId) => [];
    }

    private static string Between(string text, string open, string close)
    {
        var from = text.IndexOf(open, StringComparison.Ordinal) + open.Length;
        return text[from..text.IndexOf(close, from, StringComparison.Ordinal)].Trim('\n');
    }

    [Fact]
    public async Task AnAiEdit_OfTheGmDraft_SendsNoSecret_TakesTheSystemsShape_AndSavesNothing()
    {
        var (dm, _) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "dm", "en"))!;
        var chapter = view.Chapters.Single(c => c.Markdown.Contains("CANARY_BIO_DM"));
        // From the biography to inside the secret after it: the selection is widened to take the whole secret.
        var from = chapter.Markdown.IndexOf("PUBLIC_BIO_VERA", StringComparison.Ordinal);
        var to = chapter.Markdown.IndexOf("CANARY_BIO_DM", StringComparison.Ordinal) + 3;
        var chat = new ScriptedChat((messages, _) => Between(messages.Last().Text!, "<passage>", "</passage>").Replace("PUBLIC_BIO_VERA", "EDITED_BIO_VERA"));
        var models = new EditModels(chat);
        var ai = new BookDraftAi(_drafts, models, new BookGate(), new SrdCatalog());

        var preview = await ai.PreviewAsync(dm, "dm", "en", chapter.Section, chapter.Version, from, to, chapter.Markdown[from..to], "shorter");
        Assert.Equal(BookEditOutcome.Ready, preview.Outcome);
        Assert.True(preview.To > to);
        Assert.Equal(0, chat.Calls);

        var proposal = await ai.AskAsync(dm, "dm", "en", chapter.Section, chapter.Version, from, to, chapter.Markdown[from..to], "shorter");
        Assert.Equal(BookEditOutcome.Ready, proposal.Outcome);
        Assert.Equal(preview.From, proposal.From);
        Assert.Equal(chapter.Markdown[proposal.From..proposal.To], proposal.Original);
        var sent = Assert.Single(chat.Requests);
        Assert.DoesNotContain("CANARY_BIO_DM", sent);
        Assert.Contains("fifth-edition adventures", sent);
        Assert.Contains(BookEditPrompts.Presets["shorter"], sent);
        Assert.Contains("{{secret\nCANARY_BIO_DM\n}}", proposal.Replacement);
        Assert.Contains("EDITED_BIO", proposal.Replacement);
        // Nothing is saved until the GM accepts it in the editor.
        var after = (await _drafts.GetAsync(dm, "dm", "en"))!.Chapters.Single(c => c.Section == chapter.Section);
        Assert.Equal((chapter.Markdown, chapter.Version), (after.Markdown, after.Version));
    }

    [Fact]
    public async Task AnAiEdit_IsRefusedBeforeSending_WhenTheTextMovedOn_AnotherRunIsUnderWay_OrThereIsNoModel()
    {
        var (dm, _) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var cast = view.Chapters.Single(c => c.Section == "cast");
        var chat = new ScriptedChat((messages, _) => Between(messages.Last().Text!, "<passage>", "</passage>"));
        var ai = new BookDraftAi(_drafts, new EditModels(chat), new BookGate(), new NoOpSystemCatalog());
        var at = cast.Markdown.IndexOf("PUBLIC", StringComparison.Ordinal);
        var text = cast.Markdown.Substring(at, 20);

        Assert.Equal(BookEditOutcome.Stale, (await ai.AskAsync(dm, "party", "en", "cast", cast.Version + 1, at, at + 20, text, "shorter")).Outcome);
        Assert.Equal(BookEditOutcome.Stale, (await ai.AskAsync(dm, "party", "en", "cast", cast.Version, at + 1, at + 21, text, "shorter")).Outcome);
        Assert.Equal(BookEditOutcome.Empty, (await ai.AskAsync(dm, "party", "en", "cast", cast.Version, at, at + 20, text, "  ")).Outcome);
        Assert.Equal(BookEditOutcome.TooLong, (await ai.AskAsync(dm, "party", "en", "cast", cast.Version, at, at + 20, text, new string('x', BookDraftEdits.MaxAskChars + 1))).Outcome);
        Assert.Equal(BookEditOutcome.NotAllowed, (await ai.AskAsync(new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player), "party", "en", "cast", cast.Version, at, at + 20, text, "shorter")).Outcome);

        var gate = new BookGate();
        Assert.True(gate.TryEnter(DmUser));
        var busy = new BookDraftAi(_drafts, new EditModels(chat), gate, new NoOpSystemCatalog());
        Assert.Equal(BookEditOutcome.Busy, (await busy.AskAsync(dm, "party", "en", "cast", cast.Version, at, at + 20, text, "shorter")).Outcome);
        // The gate it did not take is not let go of: the other run still holds it.
        Assert.False(gate.TryEnter(DmUser));

        var none = new BookDraftAi(_drafts, new EditModels(chat, refuse: new Codex.Web.Services.AiNotConfiguredException(), none: true), new BookGate(), new NoOpSystemCatalog());
        Assert.Equal(BookEditOutcome.NoModel, (await none.PreviewAsync(dm, "party", "en", "cast", cast.Version, at, at + 20, text, "shorter")).Outcome);
        Assert.Equal(BookEditOutcome.NoModel, (await none.AskAsync(dm, "party", "en", "cast", cast.Version, at, at + 20, text, "shorter")).Outcome);
        Assert.Equal(0, chat.Calls);

        // Nothing selected: the whole chapter, entry by entry, and the party draft's text is all the model sees.
        var whole = await ai.AskAsync(dm, "party", "en", "cast", cast.Version, 0, 0, string.Empty, "tidy");
        Assert.Equal(BookEditOutcome.Ready, whole.Outcome);
        Assert.Equal((0, cast.Markdown.Length), (whole.From, whole.To));
        Assert.Equal(cast.Markdown, whole.Replacement);
        Assert.True(chat.Requests.Count > 1);
        Assert.All(chat.Requests, r => Assert.DoesNotContain("CANARY", r));
        // The gate is let go of after a request, so the next one may run.
        Assert.Equal(BookEditOutcome.Ready, (await ai.AskAsync(dm, "party", "en", "cast", cast.Version, 0, 0, string.Empty, "tidy")).Outcome);
    }
    // Book-shaped prose (docs/BOOK_EXPORT.md 42): a preset on a whole chapter goes entry by entry, each reply keeps the passage's facts, the party
    // edition is written for the players, and what is spent is added to the edition's totals.
    private static string PassageOf(IList<ChatMessage> messages) => Between(messages.Last().Text!, "<passage>", "</passage>");

    [Fact]
    public async Task AsTheBook_OnAWholeChapter_GoesEntryByEntry_AnEntryThatLosesAName_KeepsItsText_AndThePartyIsWrittenForThePlayers()
    {
        var (dm, _) = await SeedAsync();
        var cast = (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        Assert.Contains("PUBLIC\\_hooded stranger", cast.Markdown);
        // Vera's entry is shaped; the stranger's reply loses the stranger's name, on both tries.
        var chat = new ScriptedChat((messages, _) => PassageOf(messages).Replace("PUBLIC_BIO_VERA", "SHAPED_BIO_VERA")
            .Replace("PUBLIC\\_hooded stranger", "A figure").Replace("PUBLIC_HOODED_LOOK", "Cloaked."));
        var builds = new RavenBookBuildRepository(_db);
        var ai = new BookDraftAi(_drafts, new EditModels(chat), new BookGate(), new SrdCatalog(), builds);

        var preview = await ai.PreviewAsync(dm, "party", "en", "cast", cast.Version, 0, 0, string.Empty, "shape");
        Assert.Equal(BookEditOutcome.Ready, preview.Outcome);
        Assert.True(preview.Calls >= 2);
        Assert.Equal(0, chat.Calls);

        var proposal = await ai.AskAsync(dm, "party", "en", "cast", cast.Version, 0, 0, string.Empty, "shape");
        Assert.Equal(BookEditOutcome.Ready, proposal.Outcome);
        // One request per part, and one retry for the part that failed.
        Assert.Equal(preview.Calls + 1, chat.Calls);
        Assert.Contains("SHAPED_BIO_VERA", proposal.Replacement);
        Assert.Contains("PUBLIC\\_hooded stranger", proposal.Replacement);
        var kept = Assert.Single(proposal.Kept!);
        Assert.Equal("checks", kept.Reason);
        Assert.Contains("PUBLIC_hooded stranger", kept.Label);
        // The retry was told which name it dropped: one the passage it was sent holds.
        Assert.Contains(chat.Requests, r => r.Contains("It drops names the passage gives: PUBLIC_hooded stranger"));
        foreach (var sent in chat.Requests)
        {
            Assert.DoesNotContain("CANARY", sent);
            Assert.Contains("players' edition", sent);
            Assert.Contains("player's guides", sent);
            Assert.DoesNotContain("fifth-edition adventures", sent);
        }

        // What it spent is in the edition's totals, beside the build's.
        var totals = (await builds.GetAsync(dm, "party"))!;
        Assert.Equal((chat.Calls, 1), (totals.Calls, totals.Runs));
        Assert.True(totals.InputTokens > 0);
        Assert.Null(await builds.GetAsync(dm, "dm"));
    }

    [Fact]
    public async Task AnEntryByEntryEdit_StopsAtAKeyTheProviderRefuses_AndTheEntriesNotAskedKeepTheirText()
    {
        var (dm, _) = await SeedAsync();
        var cast = (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        var chat = new ScriptedChat((_, _) => throw new HttpRequestException("nope", null, System.Net.HttpStatusCode.Unauthorized));
        var ai = new BookDraftAi(_drafts, new EditModels(chat), new BookGate(), new SrdCatalog());
        var calls = (await ai.PreviewAsync(dm, "party", "en", "cast", cast.Version, 0, 0, string.Empty, "shape")).Calls;
        Assert.True(calls >= 2);

        var proposal = await ai.AskAsync(dm, "party", "en", "cast", cast.Version, 0, 0, string.Empty, "shape");
        Assert.Equal((BookEditOutcome.Failed, "auth"), (proposal.Outcome, proposal.Reason));
        Assert.Equal(1, chat.Calls);
        Assert.Equal(calls, proposal.Kept!.Count);
        Assert.All(proposal.Kept!, k => Assert.Equal("auth", k.Reason));
    }

    [Fact]
    public async Task TheGmsOwnWords_MayChangeAFact_ButAPresetMayNot_AndTheGmEditionTakesTheGmsShape()
    {
        var (dm, _) = await SeedAsync();
        var chapter = (await _drafts.OpenAsync(dm, "dm", "en"))!.Chapters.Single(c => c.Markdown.Contains("PUBLIC_BIO_VERA"));
        var from = chapter.Markdown.IndexOf("PUBLIC_BIO_VERA", StringComparison.Ordinal);
        var to = from + "PUBLIC_BIO_VERA".Length;
        var chat = new ScriptedChat((messages, _) => PassageOf(messages).Replace("PUBLIC_BIO_VERA", "A smith, 40 years at the forge"));
        var ai = new BookDraftAi(_drafts, new EditModels(chat), new BookGate(), new SrdCatalog());

        var preset = await ai.AskAsync(dm, "dm", "en", chapter.Section, chapter.Version, from, to, chapter.Markdown[from..to], "shorter");
        Assert.Equal(BookEditOutcome.Failed, preset.Outcome);
        Assert.Contains(chat.Requests, r => r.Contains("It states numbers that are not in the passage: 40"));

        var own = await ai.AskAsync(dm, "dm", "en", chapter.Section, chapter.Version, from, to, chapter.Markdown[from..to], "Say she has been a smith for 40 years.");
        Assert.Equal(BookEditOutcome.Ready, own.Outcome);
        Assert.Contains("fifth-edition adventures", chat.Requests[^1]);
        Assert.DoesNotContain("players' edition", chat.Requests[^1]);
    }

    [Fact]
    public async Task TheWholeBook_IsPricedFirst_ChapterByChapter_AndNothingIsSent()
    {
        var (dm, _) = await SeedAsync();
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var chat = new ScriptedChat((messages, _) => PassageOf(messages));
        var ai = new BookDraftAi(_drafts, new EditModels(chat), new BookGate(), new SrdCatalog());

        var book = await ai.BookPreviewAsync(dm, "party", "en", "shape");
        Assert.Equal(BookEditOutcome.Ready, book.Outcome);
        Assert.Contains("cast", book.Sections);
        // The cover and contents are the book's title and its list of chapters: not rewritten.
        Assert.DoesNotContain(BookDraftWriter.Front, book.Sections);
        var each = 0;
        foreach (var section in book.Sections)
        {
            var chapter = view.Chapters.Single(c => c.Section == section);
            each += (await ai.PreviewAsync(dm, "party", "en", section, chapter.Version, 0, 0, string.Empty, "shape")).Calls;
        }

        Assert.Equal(each, book.Calls);
        Assert.NotNull(book.Preflight);
        Assert.Equal(0, chat.Calls);
        Assert.Equal(BookEditOutcome.Empty, (await ai.BookPreviewAsync(dm, "party", "en", "Rewrite it all")).Outcome);
        Assert.Equal(BookEditOutcome.NotAllowed, (await ai.BookPreviewAsync(new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player), "party", "en", "shape")).Outcome);
    }

    [Fact]
    public async Task ThePartyEditionsNames_AreTheMasks_NeverTheGms()
    {
        var (dm, _) = await SeedAsync();
        var party = BookCompiler.Names((await _reader.ReadAsync(dm, BookAudience.Party))!);
        Assert.Contains("PUBLIC_Vera", party);
        Assert.Contains("PUBLIC_hooded stranger", party);
        Assert.DoesNotContain(party, n => n.Contains("CANARY", StringComparison.Ordinal));
        var gm = BookCompiler.Names((await _reader.ReadAsync(dm, BookAudience.Dm))!);
        Assert.Contains("CANARY_HIDDEN_NAME", gm);
    }

    // "Bring up to date" (docs/BOOK_EXPORT.md 41.18): the code merges entry by entry, a model sees only the entries both sides changed.
    private static string MineOf(IList<ChatMessage> messages)
    {
        var text = messages.Last().Text!;
        var from = text.IndexOf("\n<mine>\n", StringComparison.Ordinal) + 8;
        return text[from..text.IndexOf("\n</mine>", from, StringComparison.Ordinal)].Trim('\n');
    }

    private async Task<BookDraftChapterView> ChapterAsync(CampaignAccess dm, string edition, Func<BookDraftChapterView, bool> which) =>
        (await _drafts.GetAsync(dm, edition, "en"))!.Chapters.Single(which);

    [Fact]
    public async Task AMergeWithNoConflict_SendsNothing_TakesTheCampaignsAndKeepsTheGms_AndKeepingItMovesTheBase_WhichUndoTakesBack()
    {
        var (dm, id) = await SeedAsync();
        var cast = (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        var saved = await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown.Replace("PUBLIC_HOODED_LOOK", "PUBLIC_GM_LOOK"), cast.Version);
        await BioAsync(id, "PUBLIC_NEW_BIO");
        var chat = new ScriptedChat((messages, _) => MineOf(messages));
        var merges = new BookDraftMerges(_drafts, new EditModels(chat), new BookGate(), new NoOpSystemCatalog());
        var stale = await ChapterAsync(dm, "party", c => c.Section == "cast");
        Assert.True(stale.Stale);

        var preview = await merges.PreviewAsync(dm, "party", "en", "cast", saved.Version);
        Assert.Equal((BookMergeOutcome.Ready, 1, 1, 0), (preview.Outcome, preview.Theirs, preview.Mine, preview.Conflicts));
        Assert.Null(preview.Preflight);

        var proposal = await merges.MergeAsync(dm, "party", "en", "cast", saved.Version, useAi: false);
        Assert.Equal(BookMergeOutcome.Ready, proposal.Outcome);
        Assert.Equal(stale.Markdown, proposal.Original);
        Assert.Contains("PUBLIC_GM_LOOK", proposal.Replacement);
        Assert.Contains("PUBLIC_NEW_BIO", proposal.Replacement);
        Assert.DoesNotContain("PUBLIC_BIO_VERA", proposal.Replacement);
        Assert.Equal(0, chat.Calls);
        // Nothing is saved until the GM keeps the review, and then only at the version it was made from.
        Assert.Equal(stale.Markdown, (await ChapterAsync(dm, "party", c => c.Section == "cast")).Markdown);
        Assert.Equal(BookMergeSave.Conflict, (await merges.SaveAsync(dm, "party", "en", "cast", saved.Version - 1, proposal.Replacement!, proposal.Key)).Result);

        Assert.Equal(BookMergeSave.Saved, (await merges.SaveAsync(dm, "party", "en", "cast", saved.Version, proposal.Replacement!, proposal.Key)).Result);
        var merged = await ChapterAsync(dm, "party", c => c.Section == "cast");
        Assert.Equal(proposal.Replacement, merged.Markdown);
        Assert.False(merged.Stale);
        Assert.True(merged.Edited);
        Assert.True(merged.CanUndo);
        Assert.Equal(BookMergeOutcome.UpToDate, (await merges.PreviewAsync(dm, "party", "en", "cast", merged.Version)).Outcome);

        // Undo takes back the text and the base together: the GM's old text, and the notice with it.
        Assert.Equal(BookDraftSave.Saved, (await _drafts.UndoAsync(dm, "party", "en", "cast", merged.Version)).Result);
        var back = await ChapterAsync(dm, "party", c => c.Section == "cast");
        Assert.Equal(stale.Markdown, back.Markdown);
        Assert.True(back.Stale);
    }

    [Fact]
    public async Task AMergesConflict_AloneGoesToTheModel_WithNoSecret_AndKeepingItIsRefusedOnceTheCampaignMovesAgain()
    {
        var (dm, id) = await SeedAsync();
        var vera = (await _drafts.OpenAsync(dm, "dm", "en"))!.Chapters.Single(c => c.Markdown.Contains("CANARY_BIO_DM"));
        // Fitted, as most books will be by then: a break inside the entry is the GM's layout, not a piece to line up.
        var saved = await _drafts.SaveChapterAsync(dm, "dm", "en", vera.Section,
            vera.Markdown.Replace("PUBLIC_BIO_VERA met", "PUBLIC_GM_VERA met").Replace("{{secret", "\\page {fitted}\n\n{{secret"), vera.Version);
        await BioAsync(id, "PUBLIC_BIO_VERA met no one at PUBLIC_NEW_DUSK.\n\n:::dm\nCANARY_BIO_DM\n:::");
        var chat = new ScriptedChat((messages, _) => MineOf(messages) + "\n\nPUBLIC_NEW_DUSK, merged.");
        var models = new EditModels(chat);
        var merges = new BookDraftMerges(_drafts, models, new BookGate(), new SrdCatalog());

        var preview = await merges.PreviewAsync(dm, "dm", "en", vera.Section, saved.Version);
        Assert.Equal((1, 1), (preview.Conflicts, preview.Sendable));
        Assert.NotNull(preview.Preflight);
        Assert.Equal("Test · model-x", preview.Model!.Label);
        Assert.Equal(0, chat.Calls);

        var proposal = await merges.MergeAsync(dm, "dm", "en", vera.Section, saved.Version, useAi: true);
        Assert.Equal((1, 1, 0), (proposal.Conflicts, proposal.Merged, proposal.Fallback));
        var sent = Assert.Single(chat.Requests);
        // The GM edition names the hidden actor (CANARY_HIDDEN_NAME is only hidden from the party); its secrets are never sent.
        Assert.DoesNotContain("CANARY_BIO_DM", sent);
        Assert.DoesNotContain("{{secret", sent);
        Assert.Contains("PUBLIC_GM_VERA", sent);
        Assert.Contains("met no one at PUBLIC_NEW_DUSK", sent);
        Assert.Contains("{{secret\nCANARY_BIO_DM\n}}", proposal.Replacement);
        Assert.Contains("PUBLIC_NEW_DUSK, merged.", proposal.Replacement);
        Assert.Contains("\\page {fitted}\n\n{{secret", proposal.Replacement);

        // The campaign changed again while the GM reviewed: nothing is saved, and the notice stays.
        await BioAsync(id, "PUBLIC_THIRD");
        Assert.Equal(BookMergeSave.Moved, (await merges.SaveAsync(dm, "dm", "en", vera.Section, saved.Version, proposal.Replacement!, proposal.Key)).Result);
        var after = await ChapterAsync(dm, "dm", c => c.Section == vera.Section);
        Assert.Equal(saved.Version, after.Version);
        Assert.True(after.Stale);
    }

    [Fact]
    public async Task AMerge_IsRefusedForAPlayer_AnOldVersion_AnotherRun_OrNothingToBringIn_AndWithoutAModelOffersTheCampaignsText()
    {
        var (dm, id) = await SeedAsync();
        var cast = (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == "cast");
        var saved = await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown.Replace("PUBLIC_BIO_VERA", "PUBLIC_GM_VERA"), cast.Version);
        var chat = new ScriptedChat((_, _) => "No blocks at all.");
        var merges = new BookDraftMerges(_drafts, new EditModels(chat), new BookGate(), new NoOpSystemCatalog());

        Assert.Equal(BookMergeOutcome.UpToDate, (await merges.PreviewAsync(dm, "party", "en", "cast", saved.Version)).Outcome);
        await BioAsync(id, "PUBLIC_NEW_BIO");
        Assert.Equal(BookMergeOutcome.Stale, (await merges.PreviewAsync(dm, "party", "en", "cast", saved.Version - 1)).Outcome);
        Assert.Equal(BookMergeOutcome.NotAllowed, (await merges.PreviewAsync(new CampaignAccess(id, PlayerUser, CampaignRole.Player), "party", "en", "cast", saved.Version)).Outcome);
        Assert.Equal(BookMergeOutcome.NotAllowed, (await merges.MergeAsync(new CampaignAccess(id, PlayerUser, CampaignRole.Player), "party", "en", "cast", saved.Version, false)).Outcome);

        var gate = new BookGate();
        Assert.True(gate.TryEnter(DmUser));
        var busy = new BookDraftMerges(_drafts, new EditModels(chat), gate, new NoOpSystemCatalog());
        Assert.Equal(BookMergeOutcome.Busy, (await busy.MergeAsync(dm, "party", "en", "cast", saved.Version, useAi: true)).Outcome);
        Assert.False(gate.TryEnter(DmUser));

        // No model: the conflict is the campaign's text, for the GM to accept or reject.
        var none = new BookDraftMerges(_drafts, new EditModels(chat, refuse: new Codex.Web.Services.AiNotConfiguredException(), none: true), new BookGate(), new NoOpSystemCatalog());
        var preview = await none.PreviewAsync(dm, "party", "en", "cast", saved.Version);
        Assert.Equal((1, 1), (preview.Conflicts, preview.Sendable));
        Assert.Null(preview.Model);
        var plain = await none.MergeAsync(dm, "party", "en", "cast", saved.Version, useAi: true);
        Assert.Equal((0, 1, "nomodel"), (plain.Merged, plain.Fallback, plain.Reason));
        Assert.Contains("PUBLIC_NEW_BIO", plain.Replacement);
        Assert.DoesNotContain("PUBLIC_GM_VERA", plain.Replacement);

        // A model whose replies fail the checks: the same, after its two tries, and the gate is let go of.
        var failing = await merges.MergeAsync(dm, "party", "en", "cast", saved.Version, useAi: true);
        Assert.Equal((0, 1, "checks"), (failing.Merged, failing.Fallback, failing.Reason));
        Assert.Equal(BookEditWorker.MaxAttempts, chat.Calls);
        Assert.Equal(BookMergeOutcome.Ready, (await merges.MergeAsync(dm, "party", "en", "cast", saved.Version, useAi: true)).Outcome);
        Assert.Equal(0, (await merges.MergeAsync(dm, "party", "en", "cast", saved.Version, useAi: false)).Merged);
    }

    [Fact]
    public async Task TheLeakCheck_FindsEveryCanaryPastedIntoThePartyDraft_AndNothingInTheDraftAsGenerated()
    {
        var (dm, _) = await SeedAsync();
        var party = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var gm = (await _drafts.OpenAsync(dm, "dm", "en"))!;

        // The party draft as the campaign makes it says nothing only the GM edition has.
        Assert.Equal(0, (await _drafts.LeaksAsync(dm, "en"))!.Total);
        Assert.Null(await _drafts.LeaksAsync(new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player), "en"));

        // A secret the GM typed into the GM draft, which the campaign never had.
        var gmCast = gm.Chapters.Single(c => c.Section == "cast");
        await _drafts.SaveChapterAsync(dm, "dm", "en", "cast", gmCast.Markdown + "\n{{secret\nGM_TYPED the vault key is under the altar\n}}\n", gmCast.Version);
        // Pasted across, as the GM edition prints them (escaped) and as typed.
        var cast = party.Chapters.Single(c => c.Section == "cast");
        const string pasted = "CANARY\\_BIO\\_DM. CANARY_HIDDEN_NAME and CANARY_UNKNOWN_REALNAME read CANARY_WIKI_DM_TITLE: CANARY_WIKI_DM_BODY, " +
            "CANARY_WIKI_BLOCK, CANARY_HIDDEN_BIO, CANARY_UNKNOWN_BIO, CANARY_CAMPAIGN_DM. The *vault key* is under the altar.";
        await _drafts.SaveChapterAsync(dm, "party", "en", "cast", cast.Markdown + "\n" + pasted + "\n", cast.Version);
        await _drafts.SaveSettingsAsync(dm, "party", "en", ".page::after { content: \"CANARY_HIDDEN_NAME\"; }", party.PageSize, party.Theme, party.Homebrewery, party.Version);

        var report = (await _drafts.LeaksAsync(dm, "en"))!;

        var found = report.Hits.Where(h => h.Section == "cast").Select(h => (h.Kind, Text: Regex.Replace(h.Text, @"[\\*]", ""))).ToList();
        Assert.Contains((BookLeakKind.Secret, "CANARY_BIO_DM"), found);
        Assert.Contains((BookLeakKind.Secret, "CANARY_WIKI_BLOCK"), found);
        Assert.Contains((BookLeakKind.Secret, "CANARY_CAMPAIGN_DM"), found);
        Assert.Contains((BookLeakKind.Secret, "GM_TYPED the vault key is under the altar"), found);
        Assert.Contains((BookLeakKind.HiddenName, "CANARY_HIDDEN_NAME"), found);
        Assert.Contains((BookLeakKind.RealName, "CANARY_UNKNOWN_REALNAME"), found);
        Assert.Contains((BookLeakKind.GmOnly, "CANARY_WIKI_DM_TITLE"), found);
        Assert.Contains((BookLeakKind.GmOnly, "CANARY_WIKI_DM_BODY"), found);
        Assert.Contains((BookLeakKind.GmOnly, "CANARY_HIDDEN_BIO"), found);
        Assert.Contains((BookLeakKind.GmOnly, "CANARY_UNKNOWN_BIO"), found);
        Assert.Equal("PUBLIC_hooded stranger", report.Hits.Single(h => h.Kind == BookLeakKind.RealName).From);
        Assert.Equal("PUBLIC_Vera", report.Hits.Single(h => h.Text.Contains("CANARY_BIO_DM")).From);
        Assert.Contains(report.Hits, h => h is { Section: BookLeakCheck.Css, Kind: BookLeakKind.HiddenName });
        // Nothing the party may read is reported, though it is all over the chapter.
        Assert.DoesNotContain(report.Hits, h => h.Text.Contains("PUBLIC"));
    }

    /// <summary>A theme the GM brought into the campaign (41.22) prints into the party book too, so the leak check looks in its stylesheet.</summary>
    [Fact]
    public async Task TheLeakCheck_LooksInACampaignThemesStylesheet_WhenThePartyDraftUsesIt()
    {
        var (dm, _) = await SeedAsync();
        var party = (await _drafts.OpenAsync(dm, "party", "en"))!;
        var (_, theme) = await _store.SaveThemeAsync(dm, null, "Tasha", ".page::before { content: \"CANARY_HIDDEN_NAME\"; }", true, ["en"],
            new Dictionary<string, Dictionary<string, string>>(), ready: true, 0);
        Assert.Equal(0, (await _drafts.LeaksAsync(dm, "en"))!.Total);

        await _drafts.SaveSettingsAsync(dm, "party", "en", "", party.PageSize, BookCustomTheme.Prefix + theme!.Key, true, party.Version);

        var report = (await _drafts.LeaksAsync(dm, "en"))!;
        Assert.Contains(report.Hits, h => h is { Section: BookLeakCheck.Css, Kind: BookLeakKind.HiddenName });
    }

    // A chapter of the GM's own (docs/BOOK_EXPORT.md 51): it stands where it was put, the campaign never makes it stale or takes it away, and it
    // is in everything the book is: the contents, the Markdown file, Fit pages and the leak check.
    [Fact]
    public async Task AChapterOfTheGmsOwn_StaysAsTheCampaignMoves_IsNeverStale_AndIsInTheContentsTheFileFitPagesAndTheLeakCheck()
    {
        var (dm, id) = await SeedAsync();
        await _drafts.OpenAsync(dm, "party", "en");

        var (result, section) = await _drafts.AddChapterAsync(dm, "party", "en", "  GM_OWN [Notes]  ", "pages");
        Assert.Equal(BookDraftSave.Saved, result);
        Assert.True(BookDraftDocument.IsOwn(section));
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        Assert.Equal(["front", "overview", "pages", section!, "cast"], view.Chapters.Select(c => c.Section));
        var own = view.Chapters.Single(c => c.Section == section);
        Assert.True(own is { Own: true, Edited: false, Stale: false, Gone: false, CanUndo: false, Title: "GM_OWN [Notes]" });
        Assert.Equal("{{chapter,#ch-" + section + "\n# GM\\_OWN \\[Notes\\]\n}}\n", own.Markdown);
        // The contents, which nobody edited, took it in where it stands.
        Assert.Contains("](#ch-pages)\n  - [PUBLIC\\_Docks](#e-", view.Chapters[0].Markdown);
        Assert.Matches(@"\n- \[GM\\_OWN \\\[Notes\\\]\]\(#ch-" + section + @"\)\n- \[[^\n]*\]\(#ch-cast\)\n", view.Chapters[0].Markdown);

        // The GM writes in it; the campaign changes; it is as it was, with no notice, and the generated chapters followed around it.
        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveChapterAsync(dm, "party", "en", section!, own.Markdown + "\nGM_OWN_TEXT of CANARY_HIDDEN_NAME.\n", own.Version)).Result);
        await BioAsync(id, "PUBLIC_BIO_CHANGED");
        view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        own = view.Chapters.Single(c => c.Section == section);
        Assert.True(own is { Own: true, Edited: false, Stale: false, Gone: false });
        Assert.Contains("GM_OWN_TEXT", own.Markdown);
        Assert.Contains("PUBLIC_BIO_CHANGED", view.Chapters.Single(c => c.Section == "cast").Markdown);
        Assert.Equal(["front", "overview", "pages", section!, "cast"], view.Chapters.Select(c => c.Section));
        // Nothing of the campaign's can be asked for it: there is no generated text to take or to merge.
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.RegenerateAsync(dm, "party", "en", section!, own.Version)).Result);
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.KeepAsync(dm, "party", "en", section!, own.Version)).Result);

        // The Markdown file has it, between the chapters it stands between.
        var file = (await _drafts.MarkdownAsync(dm, "party", "en"))!.Value.Markdown;
        Assert.True(file.IndexOf("GM_OWN_TEXT", StringComparison.Ordinal) > file.IndexOf("{{chapter,#ch-pages", StringComparison.Ordinal));
        Assert.True(file.IndexOf("GM_OWN_TEXT", StringComparison.Ordinal) < file.IndexOf("{{chapter,#ch-cast", StringComparison.Ordinal));

        // Fit pages lays it out with the rest and saves the breaks it is given.
        IReadOnlyList<string>? laidOut = null;
        Task<BookFitted> Layout(BookDraftPage page)
        {
            laidOut = [.. page.Chapters.Select(c => c.Section)];
            return Task.FromResult(new BookFitted
            {
                Chapters = [.. page.Chapters.Select(c => new BookLayoutChapter(c.Section, c.Section == section ? c.Markdown.Replace("\nGM_OWN_TEXT", "\n\\page {fitted}\n\nGM_OWN_TEXT") : c.Markdown))]
            });
        }

        var report = (await _drafts.FitAsync(dm, "party", "en", _ => null, Layout, CancellationToken.None))!;
        Assert.Equal((BookDraftSave.Saved, 1), (report.Result, report.Chapters));
        Assert.Contains(section, laidOut!);
        Assert.Contains("\\page {fitted}", (await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == section).Markdown);

        // The leak check reads it as it reads any chapter of the party draft.
        var leaks = (await _drafts.LeaksAsync(dm, "en"))!;
        Assert.Contains(leaks.Hits, h => h.Section == section && h.Kind == BookLeakKind.HiddenName);
    }

    // A title is the GM's typing, and the contents print it: the leak check must not take the contents' word for it.
    [Fact]
    public async Task TheTitleOfAnOwnChapter_IsCheckedForLeaks_WhereTheContentsPrintIt()
    {
        var (dm, _) = await SeedAsync();
        await _drafts.OpenAsync(dm, "party", "en");
        await _drafts.OpenAsync(dm, "dm", "en");
        Assert.Equal(0, (await _drafts.LeaksAsync(dm, "en"))!.Total);

        Assert.Equal(BookDraftSave.Saved, (await _drafts.AddChapterAsync(dm, "party", "en", "CANARY_HIDDEN_NAME", null)).Result);
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        Assert.Contains("CANARY\\_HIDDEN\\_NAME", view.Chapters[0].Markdown);

        var leaks = (await _drafts.LeaksAsync(dm, "en"))!;
        Assert.Contains(leaks.Hits, h => h is { Section: "front", Kind: BookLeakKind.HiddenName });
    }

    [Fact]
    public async Task OnlyAnOwnChapter_IsRenamedMovedOrDeleted_OnlyByTheGm_NeverBeforeTheFrontPages_AndTheDraftHoldsNoMoreThanItMay()
    {
        var (dm, _) = await SeedAsync();
        var player = new CampaignAccess(dm.CampaignId, PlayerUser, CampaignRole.Player);
        await _drafts.OpenAsync(dm, "party", "en");
        async Task<List<string>> OrderAsync() => [.. (await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Select(c => c.Section)];

        // Refused: no title, a player, a draft that is not there.
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.AddChapterAsync(dm, "party", "en", "   ", null)).Result);
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.AddChapterAsync(player, "party", "en", "PLAYER_CHAPTER", null)).Result);
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.AddChapterAsync(dm, "party", "ru", "No draft", null)).Result);
        var section = (await _drafts.AddChapterAsync(dm, "party", "en", "First title", null)).Section!;
        Assert.Equal(["front", "overview", "pages", "cast", section], await OrderAsync());

        // Rename: the title the list and the contents show; the text is the GM's and stays.
        var own = (await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == section);
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.RenameChapterAsync(player, "party", "en", section, "PLAYER_TITLE", own.Version)).Result);
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.RenameChapterAsync(dm, "party", "en", section, " ", own.Version)).Result);
        Assert.Equal(BookDraftSave.Conflict, (await _drafts.RenameChapterAsync(dm, "party", "en", section, "Too late", own.Version + 1)).Result);
        Assert.Equal(BookDraftSave.NotAllowed, (await _drafts.RenameChapterAsync(dm, "party", "en", "cast", "PUBLIC_RENAMED", 0)).Result);
        Assert.Equal(BookDraftSave.Saved, (await _drafts.RenameChapterAsync(dm, "party", "en", section, "Second title", own.Version)).Result);
        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        Assert.Equal("Second title", view.Chapters.Single(c => c.Section == section).Title);
        Assert.Contains("# First title", view.Chapters.Single(c => c.Section == section).Markdown);
        Assert.Contains("- [Second title](#ch-" + section + ")", view.Chapters[0].Markdown);
        Assert.NotEqual("PUBLIC_RENAMED", view.Chapters.Single(c => c.Section == "cast").Title);

        // Move: one place at a time, never before the front pages, and only an own chapter.
        Assert.Equal(BookDraftSave.NotAllowed, await _drafts.MoveChapterAsync(player, "party", "en", section, -1));
        Assert.Equal(BookDraftSave.NotAllowed, await _drafts.MoveChapterAsync(dm, "party", "en", "cast", -1));
        Assert.Equal(BookDraftSave.Saved, await _drafts.MoveChapterAsync(dm, "party", "en", section, 1));
        Assert.Equal(["front", "overview", "pages", "cast", section], await OrderAsync());
        for (var i = 0; i < 6; i++) Assert.Equal(BookDraftSave.Saved, await _drafts.MoveChapterAsync(dm, "party", "en", section, -1));
        Assert.Equal(["front", section, "overview", "pages", "cast"], await OrderAsync());
        // It keeps its place when the draft is next brought in step with the campaign.
        await _drafts.OpenAsync(dm, "party", "en");
        Assert.Equal(["front", section, "overview", "pages", "cast"], await OrderAsync());

        // Emptied by the GM, it is still the GM's chapter: following the campaign never takes it away.
        own = (await _drafts.GetAsync(dm, "party", "en"))!.Chapters.Single(c => c.Section == section);
        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveChapterAsync(dm, "party", "en", section, string.Empty, own.Version)).Result);
        await _drafts.OpenAsync(dm, "party", "en");
        Assert.Equal(["front", section, "overview", "pages", "cast"], await OrderAsync());

        // Delete: only an own chapter, and it leaves the contents too.
        Assert.Equal(BookDraftSave.NotAllowed, await _drafts.DeleteChapterAsync(player, "party", "en", section));
        Assert.Equal(BookDraftSave.NotAllowed, await _drafts.DeleteChapterAsync(dm, "party", "en", "cast"));
        Assert.Equal(BookDraftSave.NotAllowed, await _drafts.DeleteChapterAsync(dm, "party", "en", "own-" + new string('0', 32)));
        Assert.Equal(BookDraftSave.Saved, await _drafts.DeleteChapterAsync(dm, "party", "en", section));
        view = (await _drafts.OpenAsync(dm, "party", "en"))!;
        Assert.Equal(["front", "overview", "pages", "cast"], view.Chapters.Select(c => c.Section));
        Assert.DoesNotContain(section, view.Chapters[0].Markdown);

        // The draft holds no more chapters than it may, whoever adds them.
        var added = 0;
        while (added < BookDraftDocument.MaxChapters + 5 && (await _drafts.AddChapterAsync(dm, "party", "en", "Chapter " + added, null)).Result == BookDraftSave.Saved) added++;
        Assert.Equal(BookDraftDocument.MaxChapters - 4, added);
        Assert.Equal(BookDraftSave.TooLarge, (await _drafts.AddChapterAsync(dm, "party", "en", "One too many", null)).Result);
        // Start over takes the GM's own chapters with the rest.
        Assert.True(await _drafts.StartOverAsync(dm, "party", "en"));
        Assert.Equal(["front", "overview", "pages", "cast"], (await _drafts.OpenAsync(dm, "party", "en"))!.Chapters.Select(c => c.Section));
    }

    // A draft the generator before this one made (51): its chapters in the old order, its text in the old shape. Opened now, it is in the new
    // order, the chapters nobody edited are the new text, and the edited one says the campaign moved on and can be merged entry by entry.
    [Fact]
    public async Task ADraftOfTheOlderGenerator_TakesTheNewOrder_FollowsWhereNobodyEdited_AndOffersAMergeWhereTheGmDid()
    {
        var (dm, id) = await SeedAsync();
        var vera = "{{entry,#e-" + BookSlots.KeyFor("actor:a-vera-" + id) + "}}";
        var oldCast = "{{chapter,#ch-cast\n# Cast\n}}\n\n" + vera + "\n## PUBLIC\\_Vera\n\nOLD_BIO\n";
        BookDraftGenerated Old(string section, string text) => new(section, section, text, "old-key-" + section);
        var created = await _store.CreateAsync(dm, "party", "en",
            [Old("front", "OLD_FRONT\n"), Old("overview", "OLD_OVERVIEW\n"), Old("cast", oldCast), Old("pages", "OLD_PAGES\n")]);
        Assert.Equal(["front", "overview", "cast", "pages"], created!.Chapters.Select(c => c.Section));
        Assert.Equal(BookDraftSave.Saved, (await _drafts.SaveChapterAsync(dm, "party", "en", "cast", oldCast + "\nGM_EDIT\n", 0)).Result);

        var view = (await _drafts.OpenAsync(dm, "party", "en"))!;

        Assert.Equal(["front", "overview", "pages", "cast"], view.Chapters.Select(c => c.Section));
        foreach (var followed in view.Chapters.Where(c => c.Section != "cast"))
        {
            Assert.DoesNotContain("OLD_", followed.Markdown);
            Assert.True(followed is { Edited: false, Stale: false });
        }

        Assert.Contains("# Introduction", view.Chapters.Single(c => c.Section == "overview").Markdown);
        var cast = view.Chapters.Single(c => c.Section == "cast");
        Assert.True(cast is { Edited: true, Stale: true });
        Assert.Contains("GM_EDIT", cast.Markdown);
        // The merge finds the same entry in all three versions by its marker, and the entry the old text did not have as a new one.
        var input = (await _drafts.MergeInputAsync(dm, "party", "en", "cast"))!;
        var plan = BookDraftMerge.Plan(input.Base, input.Markdown, input.Now!.Markdown)!;
        Assert.Equal(BookMergeKind.Conflict, plan.Units.Single(u => u.Id == "e-" + BookSlots.KeyFor("actor:a-vera-" + id)).Kind);
        Assert.Contains(plan.Units, u => u.Kind == BookMergeKind.Added && u.Text.Contains("PUBLIC\\_hooded stranger"));
    }

    // A draft made by an older generator, which ordered the chapters differently, takes the order the generator gives now; a chapter the
    // campaign does not make keeps the chapter it stood behind.
    [Fact]
    public void TheCampaignsChapters_TakeTheGeneratorsOrder_AndTheOthersFollowTheChapterTheyStoodBehind()
    {
        string[] stored = ["own-first", "front", "overview", "cast", "own-a", "own-b", "gazetteer", "chronicle", "gone", "lore"];
        var generated = new[] { "front", "overview", "chronicle", "gazetteer", "lore", "pages", "cast" }.Select(s => new BookDraftGenerated(s, s, "", "")).ToList();

        Assert.Equal(["own-first", "front", "overview", "chronicle", "gone", "gazetteer", "lore", "cast", "own-a", "own-b"], RavenBookDraftRepository.Ordered(stored, generated));
        string[] inOrder = ["front", "overview", "own-a", "cast"];
        Assert.Equal(inOrder, RavenBookDraftRepository.Ordered(inOrder, generated));
    }

    [Theory]
    [InlineData("own-0123456789abcdef0123456789abcdef", true)]
    [InlineData("own-0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("own-0123456789abcdef0123456789abcde", false)]
    [InlineData("own-", false)]
    [InlineData("cast", false)]
    [InlineData("owned-123456789abcdef0123456789abcde", false)]
    public void AnOwnChaptersId_HasOneForm(string section, bool own)
    {
        Assert.Equal(own, BookDraftDocument.IsOwn(section));
        Assert.True(BookDraftDocument.IsOwn(BookDraftDocument.NewOwn()));
        Assert.True(BookDraftDocument.IsSection(BookDraftDocument.NewOwn()));
    }
}
