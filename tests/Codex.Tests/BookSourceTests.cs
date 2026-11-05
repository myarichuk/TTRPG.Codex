using System.Text.RegularExpressions;
using Codex.Persistence;
using Newtonsoft.Json;

namespace Codex.Tests;

/// <summary>
/// The book's input must never carry what the reader may not read (docs/BOOK_EXPORT.md 2.3). Every DM-only field of a seeded
/// campaign holds a <c>CANARY_*</c> string; the party edition is serialised whole and none may appear in it. The <c>PUBLIC_*</c>
/// strings are positive controls: without them an empty source would pass.
/// </summary>
[Collection("RavenDb")]
public class BookSourceTests : IClassFixture<RavenDbFixture>
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
        var db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actors = new ActorRepository(db);
        _sessions = new RavenSessionRepository(db);
        _notes = new RavenNoteRepository(db);
        _regions = new RegionRepository(db);
        _facts = new FactRepository(db);
        _encounters = new RavenEncounterRepository(db);
        _homebrew = new RavenHomebrewRepository(db);
        _wiki = new RavenWikiRepository(db);
        _campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), _actors, _sessions, _notes, _regions, _facts, _homebrew, _encounters, _wiki);
        _reader = new BookSourceReader(_campaigns, _actors, _sessions, _encounters, _facts, _notes, _regions, _homebrew, _wiki);
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
    public async Task PartyBook_AsHtml_CarriesNoDmOnlyData_AndStillHasThePublicText()
    {
        var (dm, _) = await SeedAsync();
        var source = await _reader.ReadAsync(dm, BookAudience.Party);
        Assert.NotNull(source);

        var built = Codex.Core.Book.BookHtml.Build(BookCompiler.Compile(source, "en"), Codex.Core.Book.BookThemes.Plain,
            new Codex.Core.Book.BookHtmlOptions { Mention = BookCompiler.Mentions(source) });

        Assert.NotNull(built.Html);
        var leaked = Regex.Matches(built.Html, @"CANARY_\w+").Select(m => m.Value).Distinct().Order().ToList();
        Assert.True(leaked.Count == 0, "The party book carries DM-only data: " + string.Join(", ", leaked));
        Assert.DoesNotContain(":::", built.Html);
        foreach (var text in new[] { "PUBLIC_DESCRIPTION", "PUBLIC_Vera", "PUBLIC_Saltmere", "PUBLIC_Session One", "PUBLIC_RECAP", "PUBLIC_FACT" })
        {
            Assert.Contains(text, built.Html);
        }
    }

    [Fact]
    public async Task DmBook_AsHtml_BoxesTheDmsSecrets_AndStillHasNoAccountId()
    {
        var (dm, _) = await SeedAsync();
        var source = await _reader.ReadAsync(dm, BookAudience.Dm);
        Assert.NotNull(source);

        var built = Codex.Core.Book.BookHtml.Build(BookCompiler.Compile(source, "en"), Codex.Core.Book.BookThemes.Plain,
            new Codex.Core.Book.BookHtmlOptions { BoxSecrets = true, Mention = BookCompiler.Mentions(source) });

        Assert.NotNull(built.Html);
        Assert.Contains("class=\"secret secret-dm\"", built.Html);
        Assert.DoesNotContain(":::", built.Html);
        Assert.DoesNotContain(DmUser, built.Html);
        Assert.DoesNotContain(PlayerUser, built.Html);
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
            "CANARY_SECRET_NOTE", "CANARY_EFFECT", "CANARY_BIO_DM_VERA", "CANARY_UNPUBLISHED", ":::dm"
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

    // What the model is sent is a second place a secret can leak, and a stored build or a log would keep it (docs/BOOK_EXPORT.md 2.3).
    private static async Task<(string Requests, string Html, int Refined)> ThroughAModelAsync(BookSource source)
    {
        string Echo(IList<Microsoft.Extensions.AI.ChatMessage> messages, int _)
        {
            var user = messages.Last().Text;
            var from = user.IndexOf("<source>", StringComparison.Ordinal) + "<source>".Length;
            return user[from..user.IndexOf("</source>", StringComparison.Ordinal)].Trim();
        }

        var chat = new ScriptedChat(Echo);
        var mention = BookCompiler.Mentions(source);
        var render = new Codex.Core.Book.BookMarkdownOptions { BoxSecrets = source.Audience == BookAudience.Dm, Mention = mention };
        var document = BookCompiler.Compile(source, "en");
        var report = await new Codex.Core.Book.BookBuildRunner(chat, new Codex.Core.Book.BookBudget(10_000_000), 1)
            .RunAsync(document, s => Codex.Core.Book.BookBuildRunner.Item(s, "en", "Warm and brief.", mention, render));
        var html = Codex.Core.Book.BookHtml.Build(report.Document, Codex.Core.Book.BookThemes.Plain,
            new Codex.Core.Book.BookHtmlOptions { BoxSecrets = source.Audience == BookAudience.Dm, Mention = mention }).Html!;
        return (string.Join("\n=====\n", chat.Requests), html, report.Refined);
    }

    [Fact]
    public async Task PartyEdition_SendsNoDmOnlyDataToTheModel_AndTheBookItMakesCarriesNone()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;

        var (requests, html, refined) = await ThroughAModelAsync(source);

        Assert.True(refined > 0, "the model path ran");
        Assert.Contains("PUBLIC_", requests, StringComparison.Ordinal); // a positive control: the requests are not empty
        var leaked = Regex.Matches(requests + html, @"CANARY_\w+").Select(m => m.Value).Distinct().Order().ToList();
        Assert.True(leaked.Count == 0, "A model request or the book carries DM-only data: " + string.Join(", ", leaked));
        Assert.DoesNotContain(":::", SourcesIn(requests), StringComparison.Ordinal);
    }

    // The chapter text a request carries; the system prompt's own rules say ":::" and are not the campaign's.
    private static string SourcesIn(string requests) =>
        string.Concat(Regex.Matches(requests, @"<source>(.*?)</source>", RegexOptions.Singleline).Select(m => m.Groups[1].Value));

    [Fact]
    public async Task DmEdition_KeepsItsSecretsOutOfTheModel_AndStillPrintsThemInTheirBox()
    {
        var (dm, _) = await SeedAsync();
        var source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;

        var (requests, html, refined) = await ThroughAModelAsync(source);

        Assert.True(refined > 0);
        // The DM's own secret blocks are placed by the program: their words never go to the model, and come out in the box.
        foreach (var secret in new[] { "CANARY_BIO_DM_VERA", "CANARY_RECAP_DM", "CANARY_DMSUMMARY", "CANARY_LOC_BLOCK", "CANARY_FACT_BLOCK", "CANARY_PUBDESC_DM" })
        {
            Assert.DoesNotContain(secret, requests, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(":::", SourcesIn(requests), StringComparison.Ordinal);
        Assert.Contains("CANARY_BIO_DM_VERA", html, StringComparison.Ordinal);
        Assert.Contains("class=\"secret secret-dm\"", html, StringComparison.Ordinal);
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
        await _homebrew.SaveAsync(new HomebrewEntryDocument { Id = "h-pub", CampaignId = id, EntryId = "beast", Type = "monster", Name = "PUBLIC_Homebrew Beast", IsPublished = true, AuthorUserId = DmUser });
        await _encounters.SaveAsync(new EncounterDocument
        {
            Id = "e-fight", CampaignId = id, SessionId = "s-done",
            Participants = { new EncounterParticipant { ActorId = "a-hidden" } },
            ActiveEffects = { ["CANARY_EFFECT"] = "x" }
        });

        return (dm, id);
    }
}
