using System.Text.RegularExpressions;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;
using Newtonsoft.Json;

namespace Codex.Tests;

/// <summary>
/// The book's input must never carry what the reader may not read (docs/BOOK_EXPORT.md 2.3). Every DM-only field of a seeded
/// campaign holds a <c>CANARY_*</c> string; the party edition is serialised whole and none may appear in it. The <c>PUBLIC_*</c>
/// strings are positive controls: without them an empty source would pass.
/// </summary>
[Collection("RavenDb")]
public partial class BookSourceTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "CANARY_dm_user";
    private const string PlayerUser = "CANARY_player_user";

    private readonly CampaignRepository _campaigns;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenNoteRepository _notes;
    private readonly RegionRepository _regions;
    private readonly FactRepository _facts;
    private readonly RavenEncounterRepository _encounters;
    private readonly RavenHomebrewRepository _homebrew;
    private readonly RavenWikiRepository _wiki;
    private readonly BookSourceReader _reader;

    public BookSourceTests(RavenDbFixture fixture)
    {
        var db = fixture.Open(fixture.DbName);
        _actors = new ActorRepository(db);
        _sessions = new RavenSessionRepository(db);
        _notes = new RavenNoteRepository(db);
        _regions = new RegionRepository(db);
        _facts = new FactRepository(db);
        _encounters = new RavenEncounterRepository(db);
        _homebrew = new RavenHomebrewRepository(db);
        _wiki = new RavenWikiRepository(db);
        _campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), _actors, _sessions, _notes, _regions, _facts, _homebrew, _encounters, _wiki);
        _reader = new BookSourceReader(_campaigns, _actors, _sessions, _encounters, _facts, _notes, _regions, _homebrew, _wiki, new BookTestCatalog());
    }

    [Fact]
    public async Task PartyEdition_CarriesNoDmOnlyData()
    {
        var (dm, _) = await SeedAsync();

        var source = await _reader.ReadAsync(dm, BookAudience.Party);

        Assert.NotNull(source);
        var json = Serialize(source);
        var leaked = Regex.Matches(json, @"CANARY_\w+").Select(m => m.Value).Distinct().Order().ToList();
        Assert.True(leaked.Count == 0, "The party edition carries DM-only data or account ids: " + string.Join(", ", leaked));
        Assert.DoesNotContain(":::", json);
    }

    [Fact]
    public async Task PartyDraft_CarriesNoDmOnlyData_AndStillHasThePublicText()
    {
        var (dm, _) = await SeedAsync();
        var source = await _reader.ReadAsync(dm, BookAudience.Party);
        Assert.NotNull(source);

        var (_, draft) = ThroughTheDraft(source);

        var leaked = Regex.Matches(draft, @"CANARY_\w+").Select(m => m.Value).Distinct().Order().ToList();
        Assert.True(leaked.Count == 0, "The party book carries DM-only data: " + string.Join(", ", leaked));
        Assert.DoesNotContain(":::", draft);
        Assert.DoesNotContain("{{secret", draft);
        foreach (var text in new[] { "PUBLIC_DESCRIPTION", "PUBLIC_Vera", "PUBLIC_Saltmere", "PUBLIC_Session One", "PUBLIC_RECAP", "PUBLIC_FACT" })
        {
            Assert.Contains(text, draft);
        }
    }

    [Fact]
    public async Task GmDraft_BoxesTheGmsSecrets_AndStillHasNoAccountId()
    {
        var (dm, _) = await SeedAsync();
        var source = await _reader.ReadAsync(dm, BookAudience.Dm);
        Assert.NotNull(source);

        var (_, draft) = ThroughTheDraft(source);

        Assert.Contains("{{secret\n", draft);
        Assert.DoesNotContain(":::", draft);
        Assert.DoesNotContain(DmUser, draft);
        Assert.DoesNotContain(PlayerUser, draft);
    }

    [Fact]
    public async Task PartyEdition_StillHasWhatThePartyMayRead()
    {
        var (dm, _) = await SeedAsync();

        var json = Serialize(await _reader.ReadAsync(dm, BookAudience.Party));

        var missing = new[]
        {
            "PUBLIC_TAGLINE", "PUBLIC_DESCRIPTION", "PUBLIC_Vera", "PUBLIC_BIO_VERA", "PUBLIC_SHEET_NOTE", "PUBLIC_hooded stranger",
            "PUBLIC_HOODED_LOOK", "PUBLIC_PC_BIO", "PUBLIC_Docks", "PUBLIC_DOCKS_BODY", "PUBLIC_REVEALED_BLOCK", "PUBLIC_FACT",
            "PUBLIC_FACT_DETAILS", "PUBLIC_Saltmere", "PUBLIC_SALTMERE_DESC", "PUBLIC_Session One", "PUBLIC_RECAP", "PUBLIC_NOTE",
            "PUBLIC_Homebrew Beast", "PUBLIC_ROLL"
        }.Where(text => !json.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "The party edition lost public material, so the leak test proves little: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task PartyEdition_LeavesOutPlannedSessionsEncountersAndRetiredActors()
    {
        var (dm, _) = await SeedAsync();

        var source = await _reader.ReadAsync(dm, BookAudience.Party);

        Assert.NotNull(source);
        Assert.Empty(source.Encounters);
        Assert.All(source.Sessions, s => Assert.Equal(SessionStatus.Completed, s.Status));
        Assert.DoesNotContain(source.Actors, a => a.Retired);
        Assert.DoesNotContain("RETIRED_NAME_X", Serialize(source));
    }

    [Fact]
    public async Task DmEdition_HasTheDmsMaterial_ButNeitherInviteNorPlayersPrivateNotes()
    {
        var (dm, _) = await SeedAsync();

        var json = Serialize(await _reader.ReadAsync(dm, BookAudience.Dm));

        var missing = new[]
        {
            "CANARY_HIDDEN_NAME", "CANARY_WIKI_DM_BODY", "CANARY_FACT_DM", "CANARY_HIDDEN_PLACE", "CANARY_LOCK", "CANARY_DMSUMMARY",
            "CANARY_SECRET_NOTE", "CANARY_EFFECT", "CANARY_BIO_DM_VERA", "CANARY_UNPUBLISHED", "CANARY_PUBLISHED_MONSTER", ":::dm"
        }.Where(text => !json.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "The DM edition lost the DM's own material: " + string.Join(", ", missing));

        Assert.DoesNotContain("CANARY_PRIVATE_NOTE", json);
        Assert.DoesNotContain("CANARY_INVITE", json);
        Assert.DoesNotContain("CANARY_WORLD_VALUE", json);
        Assert.DoesNotContain("RETIRED_NAME_X", json);
    }

    [Fact]
    public async Task OnlyTheDmCanRead()
    {
        var (_, campaignId) = await SeedAsync();
        var player = new CampaignAccess(campaignId, PlayerUser, CampaignRole.Player);

        Assert.Null(await _reader.ReadAsync(player, BookAudience.Party));
        Assert.Null(await _reader.ReadAsync(player, BookAudience.Dm));
    }

    [Fact]
    public void ABookSourceCanOnlyBeMadeByTheReader()
    {
        Assert.Empty(typeof(BookSource).GetConstructors());
    }

    // What a model is sent is a second place a secret can leak (docs/BOOK_EXPORT.md 2.3). On the draft path every AI edit and merge sends a
    // passage the server cut from the chapter (BookDraftEdits.Cut, 41.16); here each whole chapter is cut. Markdown's escapes are read back
    // ("PUBLIC\_Vera" is "PUBLIC_Vera"), so a name is found as it was typed.
    private static (string Sent, string Draft) ThroughTheDraft(BookSource source)
    {
        var chapters = BookDraftWriter.Generate(source, "en", null);
        var sent = string.Join("\n=====\n", chapters.Select(c => BookDraftEdits.Cut(c.Markdown, 0, c.Markdown.Length)!.Text));
        return (Plain(sent), Plain(string.Join("\n", chapters.Select(c => c.Markdown))));
    }

    private static string Plain(string markdown) => Regex.Replace(markdown, @"\\(.)", "$1");

    [Fact]
    public async Task PartyEdition_SendsNoDmOnlyDataToTheModel_AndTheBookItMakesCarriesNone()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;

        var (sent, draft) = ThroughTheDraft(source);

        Assert.Contains("PUBLIC_", sent, StringComparison.Ordinal); // a positive control: the passages are not empty
        var leaked = Regex.Matches(sent + draft, @"CANARY_\w+").Select(m => m.Value).Distinct().Order().ToList();
        Assert.True(leaked.Count == 0, "A model request or the book carries DM-only data: " + string.Join(", ", leaked));
        Assert.DoesNotContain(":::", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DmEdition_KeepsItsSecretsOutOfTheModel_AndStillPrintsThemInTheirBox()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;

        var (sent, draft) = ThroughTheDraft(source);

        // The GM's own secret blocks are cut out by the server: their words never go to the model, and stay in the draft's box.
        foreach (var secret in new[] { "CANARY_BIO_DM_VERA", "CANARY_RECAP_DM", "CANARY_DMSUMMARY", "CANARY_LOC_BLOCK", "CANARY_FACT_BLOCK", "CANARY_PUBDESC_DM" })
        {
            Assert.DoesNotContain(secret, sent, StringComparison.Ordinal);
            Assert.Contains(secret, draft, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("{{secret", sent, StringComparison.Ordinal);
        Assert.Matches(@"\{\{secret\n[^}]*CANARY_BIO_DM_VERA", draft);
    }

    // A party page that links to a GM-only page: the party edition prints the link's own words and never the hidden page's title, in the
    // book, in what a model is sent, and in the art director's outline. A link to a page the party may open stays a link.
    [Fact]
    public async Task PartyEdition_NeverPrintsTheTitleOfAPageThePartyMayNotOpen_ThroughALink()
    {
        var (dm, id) = await SeedAsync();
        await _wiki.SaveAsync(new WikiPageDocument
        {
            Id = "w-tower", CampaignId = id, Slug = "tower", Title = "LINKSECRET Tower", Body = "LINKSECRET_BODY",
            Visibility = WikiVisibility.DmOnly, AuthorUserId = DmUser
        });
        await _wiki.SaveAsync(new WikiPageDocument
        {
            Id = "w-harbour", CampaignId = id, Slug = "harbour", Title = "PUBLIC_Harbour", Visibility = WikiVisibility.Party, AuthorUserId = DmUser,
            Body = "Seen from [[LINKSECRET Tower]], by its slug [[tower]], spelled [[ linksecret tower ]], with an empty label [[LINKSECRET Tower|]], "
                   + "and named for the reader [[LINKSECRET Tower|PUBLIC the old tower]]. The way to [[PUBLIC_Docks]] is open. [[Not Written Yet]]. Meet [[actor:a-known]] and [[actor:a-unknown]], not [[Actor:a-hidden|LINKSECRET lich]]."
        });

        var party = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var compiled = Codex.Web.Book.BookComposer.Compose(party, "en", out var entries);
        var (requests, html) = ThroughTheDraft(party);
        var outline = string.Join("\n", Codex.Web.Book.BookArtOutline.For(party, compiled, entries, BookSlots.For(party, compiled, entries))
            .SelectMany(c => c.Slots).Select(s => s.Text));

        foreach (var (where, text) in new[] { ("book", html), ("model requests", requests), ("director outline", outline) })
        {
            Assert.DoesNotContain("linksecret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[[tower]]", text, StringComparison.Ordinal);
            Assert.True(text.Contains("PUBLIC the old tower", StringComparison.Ordinal), where + " lost the link's own words");
        }

        // A page the party may open, and a character and a stranger it has entries for, are cross-references to those entries (51): the
        // stranger's is the mask's, never the actor's.
        var docks = "[PUBLIC_Docks](#e-" + BookSlots.KeyFor(BookSlots.WikiSubject("w-party")) + ")";
        var vera = "[PUBLIC_Vera](#e-" + BookSlots.KeyFor("actor:a-known") + ")";
        var stranger = "[PUBLIC_hooded stranger](#e-" + BookSlots.KeyFor("mask:a-unknown") + ")";
        Assert.Contains("The way to " + docks + " is open.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(BookSlots.KeyFor("actor:a-unknown"), html, StringComparison.Ordinal);
        // Mentions are the mention resolver's: named as the party knows them, and a hidden one's label is never printed (the check above).
        Assert.Contains("Meet " + vera + " and " + stranger + ", not .", html, StringComparison.Ordinal);

        // The GM edition is the GM's own: every link keeps its text.
        var gm = (await _reader.ReadAsync(dm, BookAudience.Dm))!;
        var (_, gmHtml) = ThroughTheDraft(gm);
        var tower = BookSlots.KeyFor(BookSlots.WikiSubject("w-tower"));
        Assert.Contains("Seen from [LINKSECRET Tower](#e-" + tower + ")", gmHtml, StringComparison.Ordinal);
        // A page nobody wrote has no entry: its words, and no link.
        Assert.Contains(" Not Written Yet.", gmHtml, StringComparison.Ordinal);
        // The key of the page the party may not open is the GM edition's alone.
        Assert.DoesNotContain(tower, html, StringComparison.Ordinal);
        // The party source cannot tell a page that is not written from one it may not see, so both print nothing.
        Assert.DoesNotContain("Not Written Yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReaderIsAStrangerWhoseIdMatchesNoOwner()
    {
        var a = BookSourceReader.Stranger("c1");
        var b = BookSourceReader.Stranger("c1");

        Assert.Equal(CampaignRole.Player, a.Role);
        Assert.False(string.IsNullOrEmpty(a.UserId));
        Assert.NotEqual(a.UserId, b.UserId);
    }

    private static string Serialize(object? value) =>
        JsonConvert.SerializeObject(value, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });

    private async Task<(CampaignAccess Dm, string CampaignId)> SeedAsync()
    {
        var id = "book-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id,
            Name = "Ashfall",
            SystemId = "srd5e",
            OwnerId = DmUser,
            Tagline = "PUBLIC_TAGLINE",
            Description = "PUBLIC_DESCRIPTION\n\n:::dm\nCANARY_CAMPAIGN_DESC_DM\n:::",
            InviteCode = "CANARY_INVITE",
            Members =
            {
                new CampaignMember { UserId = DmUser, Role = CampaignRole.DM },
                new CampaignMember { UserId = PlayerUser, Role = CampaignRole.Player }
            },
            WorldState = { ["CANARY_WORLD_KEY"] = "CANARY_WORLD_VALUE" },
            ActivePacks = { "srd5e" }
        });
        var dm = new CampaignAccess(id, DmUser, CampaignRole.DM);

        // The cast.
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-known", CampaignId = id, Name = "PUBLIC_Vera", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Known,
            DmNotes = "CANARY_DMNOTES_KNOWN",
            Biography = "PUBLIC_BIO_VERA\n\n:::dm\nCANARY_BIO_DM_VERA\n:::",
            Sheet = { ["notes"] = "PUBLIC_SHEET_NOTE\n\n:::dm\nCANARY_SHEET_DM\n:::" }
        });
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-hidden", CampaignId = id, Name = "CANARY_HIDDEN_NAME", Visibility = ActorVisibility.Hidden,
            DmNotes = "CANARY_DMNOTES_HIDDEN", Biography = "CANARY_HIDDEN_BIO"
        });
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-unknown", CampaignId = id, Name = "CANARY_UNKNOWN_REALNAME", Visibility = ActorVisibility.Unknown,
            PublicName = "PUBLIC_hooded stranger",
            PublicDescription = "PUBLIC_HOODED_LOOK\n\n:::dm\nCANARY_PUBDESC_DM\n:::",
            Biography = "CANARY_UNKNOWN_BIO", DmNotes = "CANARY_UNKNOWN_NOTES", Sheet = { ["trait"] = "CANARY_UNKNOWN_SHEET" }
        });
        await _actors.SaveAsync(new ActorDocument
        {
            // Stored with an empty owner, which an empty user id would match: the mask must hold for it all the same.
            Id = "a-emptyowner", CampaignId = id, Name = "CANARY_EMPTYOWNER_NAME", Visibility = ActorVisibility.Unknown, OwnerUserId = string.Empty,
            PublicName = "PUBLIC_stranger two", Biography = "CANARY_EMPTYOWNER_BIO", DmNotes = "CANARY_EMPTYOWNER_NOTES"
        });
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-pc", CampaignId = id, Name = "PUBLIC_PC_Mira", Kind = ActorKind.PlayerCharacter, OwnerUserId = PlayerUser,
            DmNotes = "CANARY_PC_DMNOTES",
            Biography = "PUBLIC_PC_BIO\n\n:::dm\nCANARY_PC_BIO_DM\n:::\n\n:::for [[actor:a-pc]]\nCANARY_PC_FOR_SELF\n:::"
        });
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-retired", CampaignId = id, Name = "RETIRED_NAME_X", Retired = true, RetiredAt = DateTime.UtcNow
        });

        // The wiki.
        await _wiki.SaveAsync(new WikiPageDocument
        {
            Id = "w-dm", CampaignId = id, Slug = "dm-page", Title = "CANARY_WIKI_DM_TITLE", Body = "CANARY_WIKI_DM_BODY",
            Visibility = WikiVisibility.DmOnly, AuthorUserId = DmUser
        });
        await _wiki.SaveAsync(new WikiPageDocument
        {
            Id = "w-party", CampaignId = id, Slug = "docks", Title = "PUBLIC_Docks", Visibility = WikiVisibility.Party, AuthorUserId = DmUser,
            Body = "PUBLIC_DOCKS_BODY\n\n:::dm\nCANARY_WIKI_BLOCK_DM\n:::\n\n:::for [[actor:a-pc]]\nCANARY_WIKI_FOR\n:::\n\n"
                   + ":::party 2026-10-29T14:05:00Z\nPUBLIC_REVEALED_BLOCK\n:::"
        });

        // Lore.
        await _facts.RestoreAsync(new FactDocument
        {
            Id = "f-dm", CampaignId = id, Summary = "CANARY_FACT_DM", Details = "CANARY_FACT_DM_DETAILS", Visibility = FactVisibility.DmOnly
        });
        await _facts.RestoreAsync(new FactDocument
        {
            Id = "f-knowers", CampaignId = id, Summary = "CANARY_FACT_KNOWERS", Visibility = FactVisibility.KnowersOnly,
            KnownBy = { new KnowerEntry("a-pc", KnowledgeLevel.Full) }
        });
        await _facts.RestoreAsync(new FactDocument
        {
            Id = "f-public", CampaignId = id, Summary = "PUBLIC_FACT", Visibility = FactVisibility.Public,
            Details = "PUBLIC_FACT_DETAILS\n\n:::dm\nCANARY_FACT_BLOCK\n:::",
            Metadata = { ["CANARY_META_KEY"] = "CANARY_META_VALUE" }
        });

        // The map.
        Assert.True(await _regions.SaveAsync(new RegionDocument
        {
            Id = "r-coast", CampaignId = id, Name = "PUBLIC_Grey Coast",
            Locations =
            {
                new LocationDocument
                {
                    Id = "l-known", Name = "PUBLIC_Saltmere", Visibility = ActorVisibility.Known,
                    Description = "PUBLIC_SALTMERE_DESC\n\n:::dm\nCANARY_LOC_BLOCK\n:::",
                    Feature = new PlaceFeature { Container = true, Lock = "CANARY_LOCK", Trap = "CANARY_TRAP", Contents = "CANARY_CONTENTS" },
                    Metadata = { ["note"] = "CANARY_LOC_META" }
                },
                new LocationDocument { Id = "l-hidden", Name = "CANARY_HIDDEN_PLACE", Visibility = ActorVisibility.Hidden, Description = "CANARY_HIDDEN_PLACE_DESC" }
            }
        }, dm));

        // Sessions.
        await _sessions.SaveAsync(new SessionDocument
        {
            Id = "s-done", CampaignId = id, Title = "PUBLIC_Session One", Status = SessionStatus.Completed,
            Recap = "PUBLIC_RECAP\n\n:::dm\nCANARY_RECAP_DM\n:::",
            DmSummary = "CANARY_DMSUMMARY",
            EncounterIds = { "e-fight" },
            Notes =
            {
                new SessionNote { Id = "n1", AuthorId = DmUser, Text = "CANARY_SECRET_NOTE", IsSecret = true },
                new SessionNote { Id = "n2", AuthorId = DmUser, Text = "PUBLIC_SESSION_NOTE" }
            },
            Events = { new SessionEvent { Type = "ActorJoined", Description = "CANARY_EVENT_HIDDEN_ACTOR joined", ActorIds = { "a-hidden" } } },
            RollLog =
            {
                new RollLogEntry { Expression = "1d20", Label = "CANARY_SECRET_ROLL", IsSecret = true, RollerUserId = DmUser },
                new RollLogEntry { Expression = "1d20", Label = "PUBLIC_ROLL", RollerUserId = DmUser },
                new RollLogEntry { Expression = "1d20", Label = "CANARY_HIDDEN_ACTOR_ROLL", ActorId = "a-hidden" }
            }
        });
        await _sessions.SaveAsync(new SessionDocument
        {
            Id = "s-planned", CampaignId = id, Title = "CANARY_PLANNED_TITLE", Status = SessionStatus.Planned, Recap = "CANARY_PLANNED_RECAP"
        });

        // Notes, homebrew and an encounter.
        await _notes.CreateNoteAsync(new NoteDocument { Id = "note-private", CampaignId = id, TargetId = "wiki:docks", AuthorId = PlayerUser, Content = "CANARY_PRIVATE_NOTE", Visibility = CommentVisibility.Private });
        await _notes.CreateNoteAsync(new NoteDocument { Id = "note-public", CampaignId = id, TargetId = "wiki:docks", AuthorId = PlayerUser, Content = "PUBLIC_NOTE", Visibility = CommentVisibility.Public });
        await _homebrew.SaveAsync(new HomebrewEntryDocument { Id = "h-unpub", CampaignId = id, EntryId = "unpub", Type = "monster", Name = "CANARY_UNPUBLISHED", IsPublished = false, AuthorUserId = DmUser });
        await _homebrew.SaveAsync(new HomebrewEntryDocument { Id = "h-pub", CampaignId = id, EntryId = "beast", Type = "magic-item", Name = "PUBLIC_Homebrew Beast", IsPublished = true, AuthorUserId = DmUser });
        // Published, but of a type players may not open in the Library (the system's creatures): the GM's, as in the app.
        await _homebrew.SaveAsync(new HomebrewEntryDocument
        {
            Id = "h-monster", CampaignId = id, EntryId = "drowned", Type = "monster", Name = "CANARY_PUBLISHED_MONSTER", IsPublished = true, AuthorUserId = DmUser,
            Properties = { ["armorClass"] = 13 }
        });
        await _encounters.SaveAsync(new EncounterDocument
        {
            Id = "e-fight", CampaignId = id, SessionId = "s-done",
            Participants = { new EncounterParticipant { ActorId = "a-hidden" } },
            ActiveEffects = { ["CANARY_EFFECT"] = "x" }
        });

        return (dm, id);
    }
}
