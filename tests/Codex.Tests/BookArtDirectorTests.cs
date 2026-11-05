using System.Text.RegularExpressions;
using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

/// <summary>The art director (docs/BOOK_EXPORT.md 28.3, step 5): what it is shown, what of its reply is kept, and how the run spends.</summary>
public class BookArtDirectorReplyTests
{
    private static readonly BookDirectorCall Call = new("Cast", "en",
    [
        new BookDirectorSlot(1, "portrait", "Vera", "Vera keeps the harbour ledgers and counts 12 ships."),
        new BookDirectorSlot(2, "portrait", "Oskar", "Oskar mends nets.")
    ], ["Vera", "Oskar", "Saltmere"]);

    private static string Item(int slot, string depicts = "The harbourmaster at her desk", string visual = "A woman in a grey coat bent over a ledger by lamplight", string mood = "serene") =>
        $$"""{"slot":{{slot}},"depicts":"{{depicts}}","visual":"{{visual}}","mood":"{{mood}}"}""";

    private static string Reply(params string[] items) => "{\"slots\":[" + string.Join(",", items) + "]}";

    [Fact]
    public void AGoodReply_IsKept_UnderTheCallsOwnRefs_EvenFencedOrWithAWordFirst()
    {
        var (kept, refused, problem) = BookArtDirector.Read(Call, "Here you go:\n```json\n" + Reply(Item(1, "Vera with 12 ships in the ledger"), Item(2, "Oskar at the nets")) + "\n```");
        Assert.Null(problem);
        Assert.Empty(refused);
        Assert.Equal([1, 2], kept.Select(k => k.Ref));
        Assert.Equal("serene", kept[0].Mood);
    }

    [Fact]
    public void UnknownRepeatedAndExtraRefs_AreDropped()
    {
        // A ref the call did not send: ignored. A ref twice: both copies go.
        var (kept, refused, problem) = BookArtDirector.Read(Call, Reply(Item(7), Item(2)));
        Assert.Null(problem);
        Assert.Equal([2], kept.Select(k => k.Ref));
        (kept, refused, _) = BookArtDirector.Read(Call, Reply(Item(1), Item(1)));
        Assert.Empty(kept);
        Assert.Contains("given more than once", refused[1]);
        // More entries than slots: the whole reply.
        (kept, _, problem) = BookArtDirector.Read(Call, Reply(Item(1), Item(2), Item(1)));
        Assert.Empty(kept);
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("The harbourmaster", "a woman, Vera, at her desk", "serene", "visual capital:Vera")]
    // A name at the start of a sentence is not a mid-sentence capital, and the names list still finds it.
    [InlineData("The harbourmaster", "Vera at her desk", "serene", "visual name:Vera")]
    [InlineData("The harbourmaster", "the woman known as vera, by lamplight", "serene", "visual name:Vera")]
    [InlineData("The harbourmaster", "a woman in the style of rackham", "serene", "visual phrase:in the style of")]
    [InlineData("The harbourmaster", "a woman by lamplight", "a foreboding sense of doom", "mood not in the list")]
    [InlineData("The harbourmaster", "a woman by lamplight", "", "mood not in the list")]
    [InlineData("Vera and Lord Ashcombe", "a woman by lamplight", "serene", "depicts names the book does not hold")]
    [InlineData("Vera with 40 ships", "a woman by lamplight", "serene", "depicts numbers the book does not hold")]
    [InlineData("", "a woman by lamplight", "serene", "depicts empty")]
    [InlineData("Vera <script>", "a woman by lamplight", "serene", "depicts has markup")]
    public void ABadField_DropsThatSlotOnly_WithItsReason(string depicts, string visual, string mood, string reason)
    {
        var (kept, refused, problem) = BookArtDirector.Read(Call, Reply(Item(1, depicts, visual, mood), Item(2, "Oskar at the nets")));
        Assert.Null(problem);
        Assert.Equal([2], kept.Select(k => k.Ref));
        Assert.Contains(refused[1], p => p.StartsWith(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void AQuotedSlotNumber_AndAMoodInCapitals_AreRead_AndTheMoodIsKeptAsTheVocabularysId()
    {
        var (kept, _, problem) = BookArtDirector.Read(Call, Reply(Item(1, mood: "Tense").Replace("\"slot\":1", "\"slot\":\"1\"")));
        Assert.Null(problem);
        Assert.Equal("tense", Assert.Single(kept).Mood);
        // A slot that is not one of the numbers sent is said to be so, for the retry's reasons.
        var (none, refused, _) = BookArtDirector.Read(Call, Reply(Item(1).Replace("\"slot\":1", "\"slot\":\"first\"")));
        Assert.Empty(none);
        Assert.Contains("slot must be one of the numbers given", refused[0]);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"slots\": 3}")]
    [InlineData("{\"other\":[]}")]
    [InlineData("{\"slots\":[{\"slot\":1,")]
    public void AnUnreadableReply_IsAProblem_NotAnEmptySuccess(string reply)
    {
        var (kept, _, problem) = BookArtDirector.Read(Call, reply);
        Assert.Empty(kept);
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task AnUnreadableReply_IsAskedAgainOnce_WithTheReason_ThenGivesUp()
    {
        var chat = new ScriptedChat((_, call) => call == 1 ? "sorry, no" : Reply(Item(1), Item(2)));
        var result = await new BookArtDirector(chat, new BookBudget(1_000_000)).RunAsync(Call);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, result.Directions.Count);
        Assert.Contains("could not be used", chat.Requests[1], StringComparison.Ordinal);

        var never = new ScriptedChat((_, _) => "still no");
        var failed = await new BookArtDirector(never, new BookBudget(1_000_000)).RunAsync(Call);
        Assert.Equal("unreadable", failed.Reason);
        Assert.Equal(2, never.Calls);
        Assert.Empty(failed.Directions);

        // A cap below one call's worst case sends nothing.
        var none = new ScriptedChat((_, _) => Reply(Item(1)));
        Assert.Equal("budget", (await new BookArtDirector(none, new BookBudget(100)).RunAsync(Call)).Reason);
        Assert.Equal(0, none.Calls);
    }

    // Found on a real model: a chapter header's "depicts" named the chapter's people and was refused, because only the header's own text
    // (its title) was read. The names of the call's other slots are the book's; a name in none of them is still refused (see the theory above).
    [Fact]
    public void AHeaderMayNameThePeopleOfItsChapter()
    {
        var call = new BookDirectorCall("Cast", "en",
        [
            new BookDirectorSlot(1, "header", "Cast", ""),
            new BookDirectorSlot(2, "portrait", "Vera", "Vera keeps the harbour ledgers.")
        ], ["Vera"]);
        var (kept, refused, _) = BookArtDirector.Read(call, Reply(Item(1, "Vera on the quay at dusk"), Item(2)));
        Assert.Equal([1, 2], kept.Select(k => k.Ref).Order());
        Assert.Empty(refused);
    }

    // Found on a real model: three portraits' JSON did not fit 160 tokens a slot and stopped mid-string, twice, and the tray only said
    // "0 of 1". The limit leaves room now; a reply that still stops at it is asked again more briefly, and the run says it was cut off.
    [Fact]
    public async Task AReplyCutOffAtTheLimit_IsAskedAgainMoreBriefly_AndSaysSoWhenItGivesUp()
    {
        Assert.True(BookArtDirector.MaxOutputFor(Call) >= 2 * 400);
        // The largest call still gets the full allowance per slot: the cap on output does not squeeze a long chapter back to the cut-off.
        var full = new BookDirectorCall("Cast", "en", Enumerable.Range(1, BookArtDirector.MaxSlotsPerCall).Select(n => new BookDirectorSlot(n, "portrait", "P" + n, "")).ToList(), []);
        Assert.True(BookArtDirector.MaxOutputFor(full) >= BookArtDirector.OutputPerSlot * BookArtDirector.MaxSlotsPerCall);
        var limit = BookArtDirector.MaxOutputFor(Call);
        var cut = Reply(Item(1))[..40];

        var once = new ScriptedChat((_, call) => call == 1 ? cut : Reply(Item(1), Item(2)), call => new UsageDetails { OutputTokenCount = call == 1 ? limit : 80 });
        var result = await new BookArtDirector(once, new BookBudget(1_000_000)).RunAsync(Call);
        Assert.Equal(2, result.Directions.Count);
        Assert.Contains(BookArtDirector.CutOff, once.Requests[1], StringComparison.Ordinal);

        var always = new ScriptedChat((_, _) => cut, _ => new UsageDetails { OutputTokenCount = limit });
        Assert.Equal("cutoff", (await new BookArtDirector(always, new BookBudget(1_000_000)).RunAsync(Call)).Reason);

        // A reply that is simply unreadable, well inside the limit, is not called cut off.
        var short_ = new ScriptedChat((_, _) => cut, _ => new UsageDetails { OutputTokenCount = 20 });
        Assert.Equal("unreadable", (await new BookArtDirector(short_, new BookBudget(1_000_000)).RunAsync(Call)).Reason);
    }

    [Fact]
    public void ThePrompt_SendsRefsLabelsAndExcerpts_AndNotTheNamesList()
    {
        var call = Call with { Names = ["CANARY_NameOnlyForChecks"] };
        var (system, user) = BookDirectorPrompts.Build(call);
        Assert.Contains("Slot 1 (a portrait of this character): Vera", user, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY", system + user, StringComparison.Ordinal);
        foreach (var mood in BookArtStyles.Moods) Assert.Contains(mood.Id, system, StringComparison.Ordinal);
    }
}

[Collection("RavenDb")]
public class BookArtDirectionTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "dir_dm_user";
    private const string PlayerUser = "dir_player_user";

    private readonly CampaignRepository _campaigns;
    private readonly ActorRepository _actors;
    private readonly RavenWikiRepository _wiki;
    private readonly RavenHomebrewRepository _homebrew;
    private readonly BookSourceReader _reader;
    private readonly CampaignAccessResolver _resolver;
    private readonly RavenBookArtRepository _art;
    private readonly RavenBookBuildRepository _builds;

    public BookArtDirectionTests(RavenDbFixture fixture)
    {
        var db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actors = new ActorRepository(db);
        var sessions = new RavenSessionRepository(db);
        var notes = new RavenNoteRepository(db);
        var regions = new RegionRepository(db);
        var facts = new FactRepository(db);
        var encounters = new RavenEncounterRepository(db);
        _homebrew = new RavenHomebrewRepository(db);
        _wiki = new RavenWikiRepository(db);
        _builds = new RavenBookBuildRepository(db);
        _campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), _actors, sessions, notes, regions, facts, _homebrew, encounters, _wiki, bookBuildRepository: _builds);
        _reader = new BookSourceReader(_campaigns, _actors, sessions, encounters, facts, notes, regions, _homebrew, _wiki);
        _resolver = new CampaignAccessResolver(_campaigns);
        _art = new RavenBookArtRepository(db);
    }

    private sealed class FakeModels(IChatClient chat, AiPriceQuote? price = null) : IBookModels
    {
        public int RequestsAsked { get; private set; }
        public Task<BookModelInfo?> InfoAsync(string userId) => Task.FromResult<BookModelInfo?>(new BookModelInfo("Test · model-x", price, null));

        public Task<BookModelHandle> OpenAsync(string userId, int requests)
        {
            RequestsAsked = requests;
            return Task.FromResult(new BookModelHandle(chat, "Test · model-x", 2, price, null));
        }
    }

    /// <summary>A director that suggests the same harmless picture for every slot it is shown, naming each by its label.</summary>
    private static string Direct(IList<ChatMessage> messages, int _)
    {
        var user = messages.Last().Text;
        var items = Regex.Matches(user, @"^Slot (?<n>\d+) \([^)]*\): (?<label>.*)$", RegexOptions.Multiline)
            .Select(m => $$"""{"slot":{{m.Groups["n"].Value}},"depicts":"{{m.Groups["label"].Value.Trim().Replace("\"", "'")}}","visual":"a figure in a grey cloak on a wet stone quay at dusk","mood":"tense"}""");
        return "{\"slots\":[" + string.Join(",", items) + "]}";
    }

    private BookArtDirection Director(IBookModels models, BookGate? gate = null) => new(_reader, _art, models, _resolver.ResolveAsync, gate ?? new BookGate());

    private static async Task<BookRunSnapshot> Finish(BookRun run)
    {
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return run.Snapshot();
    }

    private async Task<(CampaignAccess Dm, string Id)> SeedAsync()
    {
        var id = "dir-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Ashfall", SystemId = "srd5e", OwnerId = DmUser, Description = "The coast burns.\n\n:::dm\nCANARY_CampaignSecret\n:::",
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM }, new CampaignMember { UserId = PlayerUser, Role = CampaignRole.Player } },
            ActivePacks = { "srd5e" }
        });
        await _actors.SaveAsync(new ActorDocument { Id = "a-vera-" + id, CampaignId = id, Name = "Vera", Visibility = ActorVisibility.Known, Biography = "Keeps the ledgers.\n\n:::dm\nCANARY_VeraSecret\n:::" });
        await _actors.SaveAsync(new ActorDocument { Id = "a-hid-" + id, CampaignId = id, Name = "GMONLY_Hidden", Visibility = ActorVisibility.Hidden, Biography = "GMONLY_HiddenBio" });
        await _actors.SaveAsync(new ActorDocument { Id = "a-unk-" + id, CampaignId = id, Name = "GMONLY_RealName", PublicName = "Hooded figure", PublicDescription = "PUBLIC_Hood", Visibility = ActorVisibility.Unknown, Biography = "GMONLY_TrueStory" });
        // An unclosed GM block runs to the end of the chapter: the entry after it is shown to the director with no text at all.
        await _actors.SaveAsync(new ActorDocument { Id = "a-bram-" + id, CampaignId = id, Name = "Bram", Visibility = ActorVisibility.Known, Biography = "A smith.\n\n:::dm\nCANARY_Unclosed" });
        await _actors.SaveAsync(new ActorDocument { Id = "a-cora-" + id, CampaignId = id, Name = "Cora", Visibility = ActorVisibility.Known, Biography = "AFTER_UNCLOSED_text" });
        await _wiki.SaveAsync(new WikiPageDocument { Id = "w-dm-" + id, CampaignId = id, Slug = "plot", Title = "GMONLY_Plot", Visibility = WikiVisibility.DmOnly, Body = "GMONLY_PlotBody" });
        await _homebrew.SaveAsync(new HomebrewEntryDocument { Id = "h-un-" + id, CampaignId = id, EntryId = "un", Type = "monster", Name = "GMONLY_Unpublished", IsPublished = false });
        return (new CampaignAccess(id, DmUser, CampaignRole.DM), id);
    }

    [Fact]
    public async Task ThePartyDirector_IsShownNothingThePartyMayNotRead_AndTheGmsIsShownNoSecret()
    {
        var (dm, _) = await SeedAsync();
        var party = new ScriptedChat(Direct);
        var snapshot = await Finish((await Director(new FakeModels(party)).StartAsync(dm, "party", "en"))!);
        Assert.Equal(BookRunStatus.Done, snapshot.Status);
        var sent = string.Join("\n", party.Requests);
        Assert.Contains("Vera", sent, StringComparison.Ordinal);
        Assert.Contains("Hooded figure", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("GMONLY", sent, StringComparison.Ordinal);

        var gm = new ScriptedChat(Direct);
        await Finish((await Director(new FakeModels(gm)).StartAsync(dm, "dm", "en"))!);
        var gmSent = string.Join("\n", gm.Requests);
        Assert.Contains("GMONLY_RealName", gmSent, StringComparison.Ordinal); // a positive control: the GM's outline is the GM's
        Assert.DoesNotContain("CANARY", gmSent, StringComparison.Ordinal);
        // The entry after the unclosed block is named, with none of its text: the block may have held it.
        Assert.Contains("Cora", gmSent, StringComparison.Ordinal);
        Assert.DoesNotContain("AFTER_UNCLOSED", gmSent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suggestions_AreTaggedWithTheEditionRead_AndEachTrayShowsOnlyItsOwn()
    {
        var (dm, _) = await SeedAsync();
        await Finish((await Director(new FakeModels(new ScriptedChat(Direct))).StartAsync(dm, "dm", "en"))!);
        var stored = (await _art.GetAsync(dm))!.Suggestions;
        Assert.NotEmpty(stored);
        Assert.All(stored, s => Assert.Equal(BookArtDocument.Dm, s.BuiltFrom));

        // Vera's slot has one key in both editions; the GM's suggestion for it must not show in the party's tray.
        var trays = new BookArtService(_reader, _art, _resolver);
        var partyTray = (await trays.TrayAsync(dm, "party", "en"))!;
        Assert.All(partyTray.Chapters.SelectMany(c => c.Slots).Append(partyTray.Cover), s => Assert.Null(s.Suggestion));
        var gmTray = (await trays.TrayAsync(dm, "dm", "en"))!;
        var vera = gmTray.Chapters.SelectMany(c => c.Slots).Single(s => s.Label == "Vera");
        Assert.Equal("Vera", vera.Suggestion!.Depicts);
        Assert.False(vera.Suggestion.Stale);

        await Finish((await Director(new FakeModels(new ScriptedChat(Direct))).StartAsync(dm, "party", "en"))!);
        partyTray = (await trays.TrayAsync(dm, "party", "en"))!;
        Assert.NotNull(partyTray.Chapters.SelectMany(c => c.Slots).Single(s => s.Label == "Vera").Suggestion);
        Assert.Contains((await _art.GetAsync(dm))!.Suggestions, s => s.BuiltFrom == BookArtDocument.Party);
    }

    [Fact]
    public async Task ASecondRunAsksNothing_AndAnEditAsksAgainOnlyAboutWhatChanged()
    {
        var (dm, id) = await SeedAsync();
        var director = Director(new FakeModels(new ScriptedChat(Direct)));
        var first = (await director.PreviewAsync(dm, "party", "en"))!;
        Assert.True(first.Slots > 3);
        Assert.NotNull(first.Preflight);
        await Finish((await director.StartAsync(dm, "party", "en"))!);

        // A suggestion for a slot that has since gone is dropped with the edition's next save.
        Assert.True(await _art.SaveSuggestionsAsync(dm, BookArtDocument.Party, [new BookArtSuggestion { Slot = BookSlots.KeyFor("actor:gone"), Visual = "x", Mood = "tense" }]));
        await Finish((await director.StartAsync(dm, "party", "en", all: true))!);
        Assert.DoesNotContain((await _art.GetAsync(dm))!.Suggestions, x => x.Slot == BookSlots.KeyFor("actor:gone"));

        var again = (await director.PreviewAsync(dm, "party", "en"))!;
        Assert.Equal(0, again.Slots);
        Assert.Null(again.Preflight);
        Assert.Equal(first.Slots, again.Current);
        Assert.Null(await director.StartAsync(dm, "party", "en"));
        Assert.True((await director.PreviewAsync(dm, "party", "en", all: true))!.Slots == first.Slots);

        // An edit to one homebrew-free entry: its slot, and at most the chapter's header whose excerpt holds it, are asked about again.
        var vera = (await _actors.GetVisibleAsync("a-vera-" + id, dm))!;
        vera.Biography = "Keeps the ledgers, and now the keys.";
        await _actors.SaveAsync(vera);
        var edited = (await director.PreviewAsync(dm, "party", "en"))!;
        Assert.InRange(edited.Slots, 1, 2);
        var tray = (await new BookArtService(_reader, _art, _resolver).TrayAsync(dm, "party", "en"))!;
        Assert.True(tray.Chapters.SelectMany(c => c.Slots).Single(s => s.Label == "Vera").Suggestion!.Stale);
        Assert.False(tray.Chapters.SelectMany(c => c.Slots).Single(s => s.Label == "Cora").Suggestion!.Stale);
    }

    [Fact]
    public async Task OnePaidRunPerGm_ADirectorRunAndAChapterBuildCannotOverlap()
    {
        var (dm, _) = await SeedAsync();
        var gate = new BookGate();
        var slow = new ScriptedChat(Direct, delay: TimeSpan.FromMilliseconds(400));
        var director = Director(new FakeModels(slow), gate);
        var builds = new BookBuilds(_reader, _builds, new FakeModels(new ScriptedChat((m, _) => m.Last().Text)), _resolver.ResolveAsync, gate: gate);

        var run = (await director.StartAsync(dm, "party", "en"))!;
        await Assert.ThrowsAsync<BookBuildBusyException>(() => builds.StartAsync(dm, new BookBuildRequest("dm", "en", string.Empty, 10_000_000)));
        await Assert.ThrowsAsync<BookBuildBusyException>(() => director.StartAsync(dm, "dm", "en"));
        await Finish(run);

        // Free again once it is over, and the other way round.
        var build = (await builds.StartAsync(dm, new BookBuildRequest("dm", "en", string.Empty, 10_000_000)))!;
        await Assert.ThrowsAsync<BookBuildBusyException>(() => director.StartAsync(dm, "dm", "en"));
        await build.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(await director.StartAsync(dm, "dm", "en"));
    }

    [Fact]
    public async Task TheRunsSpend_IsKeptOnce_TheServerRequestsAreReservedUpFront_AndAPlayerGetsNothing()
    {
        var (dm, id) = await SeedAsync();
        var chat = new ScriptedChat(Direct, usage: _ => new UsageDetails { InputTokenCount = 500, OutputTokenCount = 100 });
        var models = new FakeModels(chat, new AiPriceQuote(1m, 5m, "$"));
        var director = Director(models);
        var preview = (await director.PreviewAsync(dm, "party", "en"))!;
        Assert.NotNull(preview.Preflight!.CostHigh);
        var snapshot = await Finish((await director.StartAsync(dm, "party", "en"))!);
        Assert.Equal(preview.Calls * BookArtDirector.MaxAttempts, models.RequestsAsked);
        Assert.Equal(chat.Calls, snapshot.Budget.Calls);

        var art = (await _art.GetAsync(dm))!;
        Assert.Equal(1, art.DirectorRuns);
        Assert.Equal(chat.Calls, art.DirectorCalls);
        Assert.Equal(500L * chat.Calls, art.DirectorInputTokens);
        Assert.True(art.DirectorCost > 0);

        var player = new CampaignAccess(id, PlayerUser, CampaignRole.Player);
        Assert.Null(await director.PreviewAsync(player, "party", "en"));
        Assert.Null(await director.StartAsync(player, "party", "en"));
        Assert.Null(await director.PreviewAsync(dm, "both", "en"));
    }
}
