using System.Text.RegularExpressions;
using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Book;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

public class BookBuildKeyTests
{
    private static BookWorkItem Item(string id = "cast", string title = "Cast", string language = "en", string style = "", string text = "Vera keeps the lighthouse.", params BookBlock[] blocks) =>
        new(id, title, language, style, text, new BookWorkText(text, blocks));

    private static string Key(BookWorkItem item, string audience = "dm") => BookBuildKey.For(item, audience);

    [Fact]
    public void TheSameChapter_HasTheSameKey_AndItIsNotTheText()
    {
        var key = Key(Item());
        Assert.Equal(key, Key(Item()));
        Assert.Equal(32, key.Length);
        Assert.DoesNotContain("lighthouse", key);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("block")]
    [InlineData("blocklabel")]
    [InlineData("language")]
    [InlineData("style")]
    [InlineData("title")]
    [InlineData("id")]
    [InlineData("audience")]
    public void EveryThingAChapterIsMadeFrom_ChangesItsKey(string what)
    {
        var block = new BookBlock(1, "DM secret", "<div>box</div>");
        var baseline = Key(Item(blocks: block));
        var changed = what switch
        {
            "text" => Key(Item(text: "Vera keeps the lighthouse!", blocks: block)),
            "block" => Key(Item(blocks: block with { Html = "<div>box, edited</div>" })),
            "blocklabel" => Key(Item(blocks: block with { Label = "secret for some players" })),
            "language" => Key(Item(language: "ru", blocks: block)),
            "style" => Key(Item(style: "Warm and brief.", blocks: block)),
            "title" => Key(Item(title: "Cast of characters", blocks: block)),
            "id" => Key(Item(id: "lore", blocks: block)),
            _ => Key(Item(blocks: block), "party")
        };

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void ASecretThatWasEdited_NoLongerComesBackFromTheStore()
    {
        // The stored chapter holds the box as it was; an edited secret changes the block's HTML and so the key.
        var before = Key(Item(blocks: new BookBlock(1, "DM secret", "<div>the vault is under the chapel</div>")));
        var after = Key(Item(blocks: new BookBlock(1, "DM secret", "<div>the vault was moved</div>")));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void FieldsAreLengthPrefixed_SoTheirBoundariesCannotBeMoved()
    {
        Assert.NotEqual(Key(Item(id: "ab", title: "c")), Key(Item(id: "a", title: "bc")));
    }

    [Fact]
    public void Staleness_SaysCurrentStaleAndNew()
    {
        var states = BookStaleness.Classify(
            [("a", "k1"), ("b", "k2"), ("c", "k3")],
            new Dictionary<string, string> { ["a"] = "k1", ["b"] = "old", ["gone"] = "x" });

        Assert.Equal([BookSectionState.Current, BookSectionState.Stale, BookSectionState.New], states.Select(s => s.State));
    }

    [Fact]
    public async Task ACachedChapter_CostsNoCallAndNoBudget()
    {
        var chat = new ScriptedChat((_, _) => "never");
        var budget = new BookBudget(1_000_000);
        var document = new BookDocument("T", "", "en", [new BookSection("cast", "# Cast\n\nold")], new BookLabels());
        var stored = new BookSection("cast", "# Cast\n\nrewritten");

        var report = await new BookBuildRunner(chat, budget, 1).RunAsync(document, _ => throw new InvalidOperationException("must not prepare a cached chapter"),
            cached: s => new BookSectionResult(s.Id, stored, true, null, 0, []));

        Assert.Equal(0, chat.Calls);
        Assert.Equal(0, report.Budget.Calls);
        Assert.Equal("# Cast\n\nrewritten", report.Document.Sections[0].Markdown);
    }

    [Fact]
    public void ThePreflight_LoadOfAChapter_IsTheWholePromptAsSent()
    {
        var item = Item(style: "Warm and brief.", text: string.Join(' ', Enumerable.Repeat("Vera keeps the lighthouse of Saltmere.", 30)));
        var load = BookSectionWorker.LoadFor(item);
        var (system, user) = BookPrompts.Section(item);

        Assert.Equal(BookTokens.Estimate(system) + BookTokens.Estimate(user), load.InputTokens);
        Assert.Equal(BookSectionWorker.MaxOutputFor(item), load.MaxOutputTokens);
        Assert.True(load.InputTokens > BookTokens.Estimate(item.Work.Text));
    }
}

/// <summary>
/// The stored build and the service that makes it (docs/BOOK_EXPORT.md, step 5): what is sent, what is stored, what may stand in for a
/// chapter at print time, who may see it, and where it must never end up. The <c>CANARY_*</c> strings are DM-only; <c>PUBLIC_*</c>
/// controls prove the paths ran.
/// </summary>
[Collection("RavenDb")]
public class BookBuildsTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "CANARY_build_dm";
    private const string PlayerUser = "CANARY_build_player";
    private static readonly string Long = string.Join(' ', Enumerable.Repeat("PUBLIC_The keeper of the light walks the cliff path at dusk and trims the lamp.", 6));

    private readonly RavenDbService _db;
    private readonly CampaignRepository _campaigns;
    private readonly ActorRepository _actors;
    private readonly RavenWikiRepository _wiki;
    private readonly RavenBookBuildRepository _store;
    private readonly BookSourceReader _reader;
    private readonly CampaignAccessResolver _resolver;

    public BookBuildsTests(RavenDbFixture fixture)
    {
        _db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actors = new ActorRepository(_db);
        var sessions = new RavenSessionRepository(_db);
        var notes = new RavenNoteRepository(_db);
        var regions = new RegionRepository(_db);
        var facts = new FactRepository(_db);
        var encounters = new RavenEncounterRepository(_db);
        var homebrew = new RavenHomebrewRepository(_db);
        _wiki = new RavenWikiRepository(_db);
        _store = new RavenBookBuildRepository(_db);
        _campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, sessions, notes, regions, facts, homebrew, encounters, _wiki, bookBuildRepository: _store);
        _reader = new BookSourceReader(_campaigns, _actors, sessions, encounters, facts, notes, regions, homebrew, _wiki);
        _resolver = new CampaignAccessResolver(_campaigns);
    }

    private sealed class FakeModels(ScriptedChat chat, AiPriceQuote? price = null, Exception? refuse = null) : IBookModels
    {
        public int Opened { get; private set; }
        public int RequestsAsked { get; private set; }

        public Task<BookModelInfo?> InfoAsync(string userId) => Task.FromResult<BookModelInfo?>(new BookModelInfo("Test · model-x", price, null));

        public Task<BookModelHandle> OpenAsync(string userId, int requests)
        {
            if (refuse != null) throw refuse;
            Opened++;
            RequestsAsked = requests;
            return Task.FromResult(new BookModelHandle(chat, "Test · model-x", 2, price, null));
        }
    }

    private static string Echo(IList<ChatMessage> messages, int _)
    {
        var user = messages.Last().Text;
        var from = user.IndexOf("<source>", StringComparison.Ordinal) + "<source>".Length;
        return user[from..user.IndexOf("</source>", StringComparison.Ordinal)].Trim();
    }

    private async Task<(CampaignAccess Dm, string Id)> SeedAsync()
    {
        var id = "bb-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Ashfall", SystemId = "srd5e", OwnerId = DmUser, Tagline = "PUBLIC_TAGLINE",
            Description = Long + "\n\n:::dm\nCANARY_CAMPAIGN_SECRET\n:::",
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM }, new CampaignMember { UserId = PlayerUser, Role = CampaignRole.Player } },
            ActivePacks = { "srd5e" }
        });
        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-vera-" + id, CampaignId = id, Name = "PUBLIC_Vera", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Known,
            Biography = Long + "\n\n:::dm\nCANARY_VERA_SECRET\n:::"
        });
        await _wiki.SaveAsync(new WikiPageDocument
        {
            Id = "w-docks-" + id, CampaignId = id, Slug = "docks", Title = "PUBLIC_Docks", Visibility = WikiVisibility.Party, AuthorUserId = DmUser, Body = Long
        });
        return (new CampaignAccess(id, DmUser, CampaignRole.DM), id);
    }

    private BookBuilds Service(FakeModels models) => new(_reader, _store, models, _resolver);

    private static BookBuildRequest Request(string audience = "dm", string style = "Warm and brief.", long cap = 10_000_000, params string[] redo) =>
        new(audience, "en", style, cap, redo.Length == 0 ? null : redo.ToHashSet());

    private static async Task<BookRunSnapshot> Finish(BookRun run)
    {
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return run.Snapshot();
    }

    [Fact]
    public async Task ABuild_RewritesEachChapterOnce_StoresIt_AndAnotherRunCostsNothing()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        var models = new FakeModels(chat);
        var service = Service(models);

        var first = await Finish((await service.StartAsync(dm, Request()))!);

        Assert.Equal(BookRunStatus.Done, first.Status);
        Assert.Equal(3, chat.Calls); // overview, cast, pages
        Assert.All(first.Rows, r => Assert.Equal(BookRowStatus.Refined, r.Status));
        var stored = (await _store.GetAsync(dm, "dm"))!;
        Assert.Equal(3, stored.Sections.Count(s => s.Refined));
        Assert.Equal(1, stored.Runs);
        Assert.Equal(3, stored.Calls);
        Assert.Equal(6, models.RequestsAsked); // two attempts per chapter set aside

        var second = await Finish((await service.StartAsync(dm, Request()))!);

        Assert.Equal(3, chat.Calls); // nothing new was sent
        Assert.All(second.Rows, r => Assert.Equal(BookRowStatus.Cached, r.Status));
        Assert.Equal(0, second.Budget.Calls);
    }

    [Fact]
    public async Task AChangedChapter_IsTheOnlyOneSentAgain()
    {
        var (dm, id) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        var service = Service(new FakeModels(chat));
        await Finish((await service.StartAsync(dm, Request()))!);
        Assert.Equal(3, chat.Calls);

        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-vera-" + id, CampaignId = id, Name = "PUBLIC_Vera", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Known,
            Biography = Long + " PUBLIC_She now also rings the bell.\n\n:::dm\nCANARY_VERA_SECRET\n:::"
        });
        var preview = (await service.PreviewAsync(dm, Request()))!;
        Assert.Equal(BookSectionState.Stale, preview.Chapters.Single(c => c.Id == "cast").State);
        Assert.Equal(2, preview.Current);
        Assert.Equal(1, preview.ToBuild);

        var run = await Finish((await service.StartAsync(dm, Request()))!);

        Assert.Equal(4, chat.Calls);
        Assert.Equal(BookRowStatus.Cached, run.Rows.Single(r => r.Id == "overview").Status);
        Assert.Equal(BookRowStatus.Refined, run.Rows.Single(r => r.Id == "cast").Status);
    }

    [Fact]
    public async Task AChangedStyle_MakesEveryChapterStale_AndAnExplicitRedoSendsACurrentOne()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        var service = Service(new FakeModels(chat));
        await Finish((await service.StartAsync(dm, Request()))!);

        var restyled = (await service.PreviewAsync(dm, Request(style: "Grim, terse.")))!;
        Assert.Equal(0, restyled.Current);
        Assert.Equal(3, restyled.ToBuild);

        var redo = await Finish((await service.StartAsync(dm, Request(redo: "cast")))!);
        Assert.Equal(4, chat.Calls);
        Assert.Equal(BookRowStatus.Refined, redo.Rows.Single(r => r.Id == "cast").Status);
        Assert.Equal(BookRowStatus.Cached, redo.Rows.Single(r => r.Id == "pages").Status);
    }

    [Fact]
    public async Task TheDmEdition_KeepsItsSecretsOutOfTheModel_AndTheStoreHoldsThemOnlyAsTheBoxes()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        await Finish((await Service(new FakeModels(chat)).StartAsync(dm, Request()))!);

        var requests = string.Join("\n", chat.Requests);
        Assert.Contains("PUBLIC_", requests); // a positive control
        Assert.DoesNotContain("CANARY_", requests);
        var stored = (await _store.GetAsync(dm, "dm"))!;
        var blocks = string.Concat(stored.Sections.SelectMany(s => s.Blocks.Values));
        Assert.Contains("CANARY_VERA_SECRET", blocks); // the box is built by code and printed by code
        Assert.DoesNotContain("CANARY_", string.Concat(stored.Sections.Select(s => s.Markdown)));
    }

    [Fact]
    public async Task TheStoredDmBuild_NeverStandsInForThePartyBook_AndThePartyBuildCarriesNoSecret()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        var service = Service(new FakeModels(chat));
        await Finish((await service.StartAsync(dm, Request()))!);
        var dmBuild = (await _store.GetAsync(dm, "dm"))!;
        var partySource = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var deterministic = BookComposer.Compose(partySource, "en");

        // Even handed the DM's build directly, the party book comes out as the deterministic one.
        var overlaid = BookComposer.Overlay(deterministic, partySource, dmBuild);
        Assert.Equal(deterministic.Sections.Select(s => s.Markdown), overlaid.Sections.Select(s => s.Markdown));
        Assert.Null(await _store.GetAsync(dm, "party"));

        await Finish((await service.StartAsync(dm, Request("party")))!);
        var partyBuild = (await _store.GetAsync(dm, "party"))!;
        // The document names its owner (the DM's own account id, in their own private document); what it holds of the book carries no secret.
        Assert.DoesNotContain("CANARY_", Newtonsoft.Json.JsonConvert.SerializeObject(partyBuild.Sections));
        Assert.DoesNotContain("CANARY_", string.Join("\n", chat.Requests));
    }

    [Fact]
    public async Task ACharacterHiddenAfterTheBuild_IsNotPrintedFromTheStore()
    {
        var (dm, id) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        var service = Service(new FakeModels(chat));
        await Finish((await service.StartAsync(dm, Request("party")))!);
        var build = (await _store.GetAsync(dm, "party"))!;
        var before = BookComposer.Overlay(BookComposer.Compose((await _reader.ReadAsync(dm, BookAudience.Party))!, "en"), (await _reader.ReadAsync(dm, BookAudience.Party))!, build);
        Assert.Contains("PUBLIC_The keeper of the light", before.Sections.Single(s => s.Id == "cast").Markdown);

        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-vera-" + id, CampaignId = id, Name = "PUBLIC_Vera", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Hidden, Biography = Long
        });
        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var after = BookComposer.Overlay(BookComposer.Compose(source, "en"), source, build);

        // The stored chapter named a character the party may no longer read: it is not served, and the chapter is gone with her.
        Assert.DoesNotContain(after.Sections, s => s.Id == "cast");
        Assert.DoesNotContain("The keeper of the light", string.Concat(after.Sections.Where(s => s.Id == "cast").Select(s => s.Markdown)));

        // A build drops what the book no longer has.
        await Finish((await service.StartAsync(dm, Request("party")))!);
        Assert.DoesNotContain((await _store.GetAsync(dm, "party"))!.Sections, s => s.Id == "cast");
    }

    [Fact]
    public async Task AChapterEditedAfterTheBuild_PrintsTheNewText_NotTheStoredOne()
    {
        var (dm, id) = await SeedAsync();
        await Finish((await Service(new FakeModels(new ScriptedChat(Echo))).StartAsync(dm, Request()))!);
        var build = (await _store.GetAsync(dm, "dm"))!;
        var source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;
        Assert.Contains("PUBLIC_The keeper", BookComposer.Overlay(BookComposer.Compose(source, "en"), source, build).Sections.Single(s => s.Id == "cast").Markdown); // the store is used while it is current

        await _actors.SaveAsync(new ActorDocument
        {
            Id = "a-vera-" + id, CampaignId = id, Name = "PUBLIC_Vera", Kind = ActorKind.NonPlayerCharacter, Visibility = ActorVisibility.Known,
            Biography = "PUBLIC_EDITED_AFTER_THE_BUILD " + Long
        });
        source = (await _reader.ReadAsync(dm, BookAudience.Dm))!;
        var cast = BookComposer.Overlay(BookComposer.Compose(source, "en"), source, build).Sections.Single(s => s.Id == "cast");

        Assert.Contains("PUBLIC_EDITED_AFTER_THE_BUILD", cast.Markdown);
        Assert.Null(cast.Blocks); // and the stored box of the old secret does not come with it
    }

    [Fact]
    public async Task ABuildPlantedUnderAnotherAccount_IsNotServedToIt()
    {
        var (dm, id) = await SeedAsync();
        var player = new CampaignAccess(id, PlayerUser, CampaignRole.Player);
        using (var session = _db.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new BookBuildDocument
            {
                Id = BookBuildDocument.IdFor(id, PlayerUser, "dm"), CampaignId = id, OwnerUserId = PlayerUser, Audience = "dm",
                Sections = { new BookBuildSection { Id = "cast", Refined = true, Markdown = "CANARY_PLANTED" } }
            });
            await session.SaveChangesAsync();
        }

        Assert.Null(await _store.GetAsync(player, "dm")); // a player is no DM, whatever is stored under their name
        Assert.Null(await _store.GetAsync(dm, "party"));
    }

    [Fact]
    public async Task ACap_StopsTheBuild_KeepsWhatExists_AndRecordsWhyNoCallWasMade()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo);

        var run = await Finish((await Service(new FakeModels(chat)).StartAsync(dm, Request(cap: 50)))!);

        Assert.Equal(0, chat.Calls);
        Assert.All(run.Rows, r => { Assert.Equal(BookRowStatus.Kept, r.Status); Assert.Equal("budget", r.Reason); });
        var stored = (await _store.GetAsync(dm, "dm"))!;
        Assert.DoesNotContain(stored.Sections, s => s.Refined);
        Assert.All(stored.Sections, s => Assert.Equal("budget", s.Reason));
    }

    [Fact]
    public async Task ABuildThatFailedForABudget_IsTriedAgainByTheNextRun()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        var service = Service(new FakeModels(chat));
        await Finish((await service.StartAsync(dm, Request(cap: 50)))!);

        var preview = (await service.PreviewAsync(dm, Request()))!;
        Assert.Equal(3, preview.ToBuild);
        Assert.Equal("budget", preview.Chapters[0].LastReason);

        await Finish((await service.StartAsync(dm, Request()))!);
        Assert.Equal(3, chat.Calls);
    }

    [Fact]
    public async Task Cancelling_StopsTheCalls_ChargesTheOnesInFlight_AndKeepsTheFinishedChapters()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo, delay: TimeSpan.FromSeconds(20));
        var service = Service(new FakeModels(chat));

        var run = (await service.StartAsync(dm, Request()))!;
        await Task.Delay(300);
        run.Cancel();
        var done = await Finish(run);

        Assert.Equal(BookRunStatus.Stopped, done.Status);
        Assert.DoesNotContain(done.Rows, r => r.Status == BookRowStatus.Refined);
        Assert.True(done.Budget.Failed > 0); // a call that was out may have been billed
        Assert.False(run.Running);
    }

    [Fact]
    public async Task OneBuildAtATimePerDm()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo, delay: TimeSpan.FromSeconds(20));
        var service = Service(new FakeModels(chat));
        var first = (await service.StartAsync(dm, Request()))!;

        await Assert.ThrowsAsync<BookBuildBusyException>(() => service.StartAsync(dm, Request("party")));

        first.Cancel();
        await Finish(first);
    }

    [Fact]
    public async Task AnAllowanceThatDoesNotCover_RefusesBeforeAnythingIsSentOrStored()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        var service = Service(new FakeModels(chat, refuse: new LocalizedException("Service.Ai.ServerAllowanceSpent", 200)));

        await Assert.ThrowsAsync<LocalizedException>(() => service.StartAsync(dm, Request()));

        Assert.Equal(0, chat.Calls);
        Assert.Null(service.Find(dm, "dm"));
        Assert.Null(await _store.GetAsync(dm, "dm"));
    }

    [Fact]
    public async Task ARefusedKey_StopsTheRest_AndNothingIsStoredAsBuilt()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat((_, _) => throw new HttpRequestException("401 Unauthorized", null, System.Net.HttpStatusCode.Unauthorized));
        var service = Service(new FakeModels(chat));

        var run = await Finish((await service.StartAsync(dm, Request()))!);

        Assert.DoesNotContain(run.Rows, r => r.Status == BookRowStatus.Refined);
        Assert.Contains(run.Rows, r => r.Reason == "auth");
        Assert.True(chat.Calls < 3 * BookSectionWorker.MaxAttempts);
        Assert.DoesNotContain((await _store.GetAsync(dm, "dm"))!.Sections, s => s.Refined);
    }

    [Fact]
    public async Task Cost_IsAccumulatedAcrossRuns_OnlyWhereThePricesWereKnown()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo, usage: _ => new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 500 });
        var priced = Service(new FakeModels(chat, new AiPriceQuote(1m, 2m, "USD")));
        await Finish((await priced.StartAsync(dm, Request()))!);

        var stored = (await _store.GetAsync(dm, "dm"))!;
        Assert.Equal(3000, stored.InputTokens);
        Assert.Equal(1500, stored.OutputTokens);
        Assert.Equal(0.006m, stored.Cost); // 3000 in at 1/Mtok plus 1500 out at 2/Mtok
        Assert.Equal(0, stored.UnpricedRuns);
        Assert.False(stored.Estimated);

        await Finish((await Service(new FakeModels(chat)).StartAsync(dm, Request(redo: "cast")))!);
        stored = (await _store.GetAsync(dm, "dm"))!;
        Assert.Equal(2, stored.Runs);
        Assert.Equal(1, stored.UnpricedRuns); // a run with no prices is counted, never priced at zero
        Assert.Equal(0.006m, stored.Cost);
    }

    [Fact]
    public async Task OnlyTheDmReadsOrWritesABuild_AndOnlyTheirOwn()
    {
        var (dm, id) = await SeedAsync();
        await Finish((await Service(new FakeModels(new ScriptedChat(Echo))).StartAsync(dm, Request()))!);

        var player = new CampaignAccess(id, PlayerUser, CampaignRole.Player);
        var stranger = new CampaignAccess(id, "CANARY_nobody", CampaignRole.DM); // an access made for someone who is not a member

        Assert.Null(await _store.GetAsync(player, "dm"));
        Assert.Null(await _store.GetAsync(stranger, "dm"));
        Assert.False(await _store.SaveAsync(player, new BookBuildDocument { Audience = "dm" }));
        Assert.False(await _store.DeleteAsync(player, "dm"));
        Assert.NotNull(await _store.GetAsync(dm, "dm"));
        Assert.Null(await Service(new FakeModels(new ScriptedChat(Echo))).PreviewAsync(player, Request()));
    }

    [Fact]
    public async Task ADmDemotedAfterTheirPageLoaded_GetsNothing()
    {
        var (dm, id) = await SeedAsync();
        var campaign = (await _campaigns.GetAsync(id))!;
        campaign.Members.Single(m => m.UserId == DmUser).Role = CampaignRole.Player;
        campaign.OwnerId = "someone-else";
        await _campaigns.SaveAsync(campaign);

        Assert.Null(await Service(new FakeModels(new ScriptedChat(Echo))).PreviewAsync(dm, Request()));
        Assert.Null(await Service(new FakeModels(new ScriptedChat(Echo))).StartAsync(dm, Request()));
    }

    [Fact]
    public async Task DeletingTheCampaign_DeletesItsBuilds_AndOnlyItsOwn()
    {
        var (dm, id) = await SeedAsync();
        var (other, otherId) = await SeedAsync();
        var service = Service(new FakeModels(new ScriptedChat(Echo)));
        await Finish((await service.StartAsync(dm, Request()))!);
        await Finish((await service.StartAsync(other, Request()))!);

        await _campaigns.DeleteAsync(id, DmUser);

        Assert.Null(await _store.GetAsync(dm, "dm"));
        Assert.NotNull(await _store.GetAsync(other, "dm"));
        using var session = _db.Store.OpenAsyncSession();
        Assert.Empty(await session.Advanced.LoadStartingWithAsync<BookBuildDocument>(BookBuildDocument.PrefixFor(id)));
        Assert.NotEmpty(await session.Advanced.LoadStartingWithAsync<BookBuildDocument>(BookBuildDocument.PrefixFor(otherId)));
    }

    [Fact]
    public async Task ABuild_IsInNeitherTheCampaignExportNorTheBackup()
    {
        var (dm, id) = await SeedAsync();
        await Finish((await Service(new FakeModels(new ScriptedChat(Echo))).StartAsync(dm, Request()))!);
        Assert.Contains("CANARY_VERA_SECRET", string.Concat((await _store.GetAsync(dm, "dm"))!.Sections.SelectMany(s => s.Blocks.Values))); // a positive control

        // The export holds the DM's own secrets in their source documents (that is its job), so the marker is the build itself: its
        // box HTML and its keys appear nowhere in what the export or the backup writes.
        var export = new CampaignExportService(_campaigns, _actors, new RavenSessionRepository(_db), new RavenEncounterRepository(_db), new FactRepository(_db), new RavenNoteRepository(_db), new RegionRepository(_db), new RavenHomebrewRepository(_db), _wiki);
        var bundle = (await export.ExportAsync(dm))!;
        var json = CampaignExportService.Serialize(bundle);
        Assert.DoesNotContain("secret secret-dm", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bookbuilds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BookBuild", json, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "bb-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backups = new JsonBackups(_db, _campaigns, export);
            await backups.WriteAsync(root, DateTime.UtcNow);
            var everything = string.Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => f.Contains(id.Replace('/', '_'))).Select(File.ReadAllText));
            Assert.NotEmpty(everything);
            Assert.DoesNotContain("secret secret-dm", everything, StringComparison.Ordinal);
            Assert.DoesNotContain("bookbuilds", everything, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ShortChapters_AreNotSentToTheModel_AndThePreflightSaysSo()
    {
        var id = "bb-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Tiny", SystemId = "srd5e", OwnerId = DmUser, Tagline = "PUBLIC_TAGLINE", Description = "PUBLIC_short",
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM } }, ActivePacks = { "srd5e" }
        });
        var dm = new CampaignAccess(id, DmUser, CampaignRole.DM);
        var chat = new ScriptedChat(Echo);
        var service = Service(new FakeModels(chat));

        var preview = (await service.PreviewAsync(dm, Request()))!;
        Assert.Equal(0, preview.ToBuild);
        Assert.Null(preview.Preflight);
        Assert.True(preview.TooShort > 0);

        var run = await Finish((await service.StartAsync(dm, Request()))!);
        Assert.Equal(0, chat.Calls);
        Assert.Equal(BookRunStatus.Done, run.Status);
    }

    [Fact]
    public async Task ThePreflight_IsARange_WithMoneyOnlyWhenPricesAreSet()
    {
        var (dm, _) = await SeedAsync();
        var unpriced = (await Service(new FakeModels(new ScriptedChat(Echo))).PreviewAsync(dm, Request()))!;
        var priced = (await Service(new FakeModels(new ScriptedChat(Echo), new AiPriceQuote(3m, 15m, "USD"))).PreviewAsync(dm, Request()))!;

        Assert.Equal(3, unpriced.ToBuild);
        Assert.NotNull(unpriced.Preflight);
        Assert.Null(unpriced.Preflight!.CostLow);
        Assert.True(unpriced.Preflight.InputHigh > unpriced.Preflight.InputLow);
        Assert.Equal(3, unpriced.Preflight.RequestsMin);
        Assert.Equal(6, unpriced.Preflight.RequestsMax);
        Assert.NotNull(priced.Preflight!.CostHigh);
        Assert.True(priced.Preflight.CostHigh > priced.Preflight.CostLow);
        Assert.True(unpriced.SuggestedCap >= unpriced.Preflight.InputHigh + unpriced.Preflight.OutputHigh);
        Assert.Equal(0, unpriced.SuggestedCap % 1000);
    }

    [Fact]
    public async Task AStyleSheet_IsClipped_AndSentAsTheStyle()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Echo);
        await Finish((await Service(new FakeModels(chat)).StartAsync(dm, Request(style: new string('x', 5000) + "\nCANARY_TAIL")))!);

        var system = chat.Requests[0];
        Assert.DoesNotContain("CANARY_TAIL", system);
        Assert.DoesNotContain(new string('x', BookBuildRequest.MaxStyleChars + 1), system);
        Assert.Contains(new string('x', 100), system);
        Assert.Matches(new Regex("Style sheet from the book's editor"), system);
    }
}
