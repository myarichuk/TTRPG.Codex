using System.Text.RegularExpressions;
using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

/// <summary>
/// Drawing a book's pictures (docs/BOOK_EXPORT.md 28.3, step 6). Nothing is drawn but the slots the GM ticked; each pick is checked again on
/// the server (the slot is the edition's, the suggestion is the edition's and current, the visual passes the checks); the prompt is the
/// code's; the picture is sanitised, tagged with the edition and counted once; a run shares the per-GM gate.
/// </summary>
[Collection("RavenDb")]
public partial class BookArtDrawingTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "draw_dm_user";

    private readonly CampaignRepository _campaigns;
    private readonly ActorRepository _actors;
    private readonly BookSourceReader _reader;
    private readonly CampaignAccessResolver _resolver;
    private readonly RavenBookArtRepository _art;
    private readonly RavenMediaStore _media;
    private readonly RavenSessionRepository _sessions;

    public BookArtDrawingTests(RavenDbFixture fixture)
    {
        var db = fixture.Open(fixture.DbName);
        _actors = new ActorRepository(db);
        var sessions = _sessions = new RavenSessionRepository(db);
        var notes = new RavenNoteRepository(db);
        var regions = new RegionRepository(db);
        var facts = new FactRepository(db);
        var encounters = new RavenEncounterRepository(db);
        var homebrew = new RavenHomebrewRepository(db);
        var wiki = new RavenWikiRepository(db);
        _campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), _actors, sessions, notes, regions, facts, homebrew, encounters, wiki);
        _reader = new BookSourceReader(_campaigns, _actors, sessions, encounters, facts, notes, regions, homebrew, wiki);
        _resolver = new CampaignAccessResolver(_campaigns);
        _art = new RavenBookArtRepository(db);
        _media = new RavenMediaStore(db);
    }

    private sealed class FakeModels(IChatClient chat) : IBookModels
    {
        public Task<BookModelInfo?> InfoAsync(string userId) => Task.FromResult<BookModelInfo?>(new BookModelInfo("Test · model-x", null, null));
        public Task<BookModelHandle> OpenAsync(string userId, int requests) => Task.FromResult(new BookModelHandle(chat, "Test · model-x", 2, null, null));
    }

    /// <summary>A picture model that draws what <paramref name="picture"/> gives, and remembers every prompt and every run it was opened for.</summary>
    private sealed class FakeImages(Func<int, GeneratedImage>? picture = null, AiImagePrice? price = null, string? guide = null) : IBookImages
    {
        public List<string> Prompts { get; } = [];

        public Task<string?> StyleAsync(string userId) => Task.FromResult(guide);
        public List<ArtShape> Shapes { get; } = [];
        public List<int> Opened { get; } = [];

        public Task<BookImageInfo?> InfoAsync(string userId) => Task.FromResult<BookImageInfo?>(new BookImageInfo("Test · pictures", price, null));

        public Task<BookImageHandle> OpenAsync(string userId, int pictures)
        {
            Opened.Add(pictures);
            return Task.FromResult(new BookImageHandle((prompt, shape, _) =>
            {
                lock (Prompts) { Prompts.Add(prompt); Shapes.Add(shape); }
                return Task.FromResult(picture?.Invoke(Prompts.Count) ?? new GeneratedImage(BookArtTests.Png(300), "image/png"));
            }, pictures, "Test · pictures", price, null));
        }
    }

    // Suggests the same harmless picture for every slot, naming each by its label.
    private static string Direct(IList<ChatMessage> messages, int _)
    {
        var user = messages.Last().Text;
        var items = Regex.Matches(user, @"^Slot (?<n>\d+) \([^)]*\): (?<label>.*)$", RegexOptions.Multiline)
            .Select(m => $$"""{"slot":{{m.Groups["n"].Value}},"depicts":"{{m.Groups["label"].Value.Trim().Replace("\"", "'")}}","visual":"a figure in a grey cloak on a wet stone quay at dusk","mood":"tense"}""");
        return "{\"slots\":[" + string.Join(",", items) + "]}";
    }

    private BookArtDrawing Drawing(IBookImages images, BookGate? gate = null) => new(_reader, _art, images, _resolver.ResolveAsync, gate ?? new BookGate());

    private static async Task<BookRunSnapshot> Finish(BookRun run)
    {
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return run.Snapshot();
    }

    private async Task<(CampaignAccess Dm, string Id)> SeedAsync()
    {
        var id = "draw-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Ashfall", SystemId = "srd5e", OwnerId = DmUser, Description = "The coast burns.",
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM } }, ActivePacks = { "srd5e" }
        });
        await _actors.SaveAsync(new ActorDocument { Id = "a-vera-" + id, CampaignId = id, Name = "Vera", Visibility = ActorVisibility.Known, Biography = "Keeps the ledgers." });
        await _actors.SaveAsync(new ActorDocument { Id = "a-hid-" + id, CampaignId = id, Name = "Morrow", Visibility = ActorVisibility.Hidden, Biography = "Watches the harbour." });
        var dm = new CampaignAccess(id, DmUser, CampaignRole.DM);

        // Suggestions for both editions, as the art director makes them.
        foreach (var edition in new[] { "party", "dm" })
        {
            var director = new BookArtDirection(_reader, _art, new FakeModels(new ScriptedChat(Direct)), _resolver.ResolveAsync, new BookGate());
            Assert.Equal(BookRunStatus.Done, (await Finish((await director.StartAsync(dm, edition, "en"))!)).Status);
        }

        return (dm, id);
    }

    private async Task<string> SlotOf(CampaignAccess dm, string edition, string depicts) =>
        (await _art.GetAsync(dm))!.Suggestions.First(s => s.BuiltFrom == edition && s.Depicts == depicts).Slot;

    [Fact]
    public async Task TheTickedSlotIsDrawnFromTheCodesPrompt_StoredUnderTheEditionsTag_AndCountedOnce()
    {
        var (dm, _) = await SeedAsync();
        var vera = await SlotOf(dm, "party", "Vera");
        var images = new FakeImages(price: new AiImagePrice(0.04m, "USD"));
        var drawing = Drawing(images);
        Assert.True(await drawing.SetStyleAsync(dm, new BookArtStyle("ink", "sepia", "fine", "candle")));

        var preview = (await drawing.PreviewAsync(dm, "party", [new BookDrawPick(vera)]))!;
        Assert.Equal(1, preview.Pictures);
        Assert.Equal(0.04m, preview.Cost);
        Assert.Empty(images.Opened);

        var snapshot = await Finish((await drawing.StartAsync(dm, "party", [new BookDrawPick(vera)]))!);

        Assert.Equal(BookRunStatus.Done, snapshot.Status);
        Assert.Equal([1], images.Opened);
        var prompt = Assert.Single(images.Prompts);
        Assert.StartsWith(BookArtStyles.Framing["portrait"] + ": a figure in a grey cloak on a wet stone quay at dusk, a tense", prompt);
        Assert.Contains(BookArtStyles.Find(BookArtStyles.Media, "ink")!.Phrase, prompt);
        Assert.EndsWith(BookArtStyles.Negative, prompt);
        Assert.Equal(ArtShape.Tall, Assert.Single(images.Shapes));

        var art = (await _art.GetAsync(dm))!;
        var picture = Assert.Single(art.Pictures);
        Assert.Equal((vera, BookArtDocument.Party, "drawn"), (picture.Slot, picture.BuiltFrom, picture.Origin));
        Assert.Equal("a figure in a grey cloak on a wet stone quay at dusk", picture.Visual);
        Assert.Equal(BookArtDrawing.InputsOf("portrait", picture.Visual!, "tense", new BookArtStyle("ink", "sepia", "fine", "candle")), picture.Inputs);
        Assert.Equal((1, 0.04m, 0), (art.DrawnPictures, art.DrawCost, art.DrawUnpricedPictures));
    }

    // Found on a real model: its visuals run past 200 characters, the stored one was cut at a label's length, and every picture it drew showed
    // "drawn from an older description" at once. A long visual is kept whole, and the tray calls the picture current until something changes.
    [Fact]
    public async Task APictureDrawnFromALongVisual_IsCurrentInTheTray_UntilTheStyleChanges()
    {
        var (dm, _) = await SeedAsync();
        var vera = await SlotOf(dm, "party", "Vera");
        var visual = string.Join(" ", Enumerable.Repeat("a keeper in a grey coat trims the lamp,", 9)).TrimEnd(',') + ".";
        Assert.InRange(visual.Length, 300, BookImagePrompt.MaxVisualChars);
        var drawing = Drawing(new FakeImages());

        Assert.Equal(BookRunStatus.Done, (await Finish((await drawing.StartAsync(dm, "party", [new BookDrawPick(vera, visual)]))!)).Status);

        Assert.Equal(visual, Assert.Single((await _art.GetAsync(dm))!.Pictures).Visual);
        var trays = new BookArtService(_reader, _art, _resolver);
        BookTraySlot Vera(BookTray tray) => tray.Chapters.SelectMany(c => c.Slots).Single(s => s.Key == vera);
        Assert.False(Vera((await trays.TrayAsync(dm, "party", "en"))!).Drawn!.Stale);

        Assert.True(await drawing.SetStyleAsync(dm, new BookArtStyle("woodcut", "sepia", "bold", "dusk")));
        Assert.True(Vera((await trays.TrayAsync(dm, "party", "en"))!).Drawn!.Stale);
    }

    // Step 7 (28.3): the director may suggest a spot with an entry, never alone; the tray shows only spots that have one; a spot is drawn square
    // from its own suggestion, and the draft closes its entry with it. A session of the Chronicle is an entry too, with a scene's place.
    [Fact]
    public async Task ASpotSuggestedWithItsEntry_IsShownDrawnSquare_AndClosesTheEntryInTheDraft_AndASessionIsAnEntry()
    {
        var id = "spot-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Ashfall", SystemId = "srd5e", OwnerId = DmUser, Description = "The coast burns.",
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM } }, ActivePacks = { "srd5e" }
        });
        await _actors.SaveAsync(new ActorDocument { Id = "a-vera-" + id, CampaignId = id, Name = "Vera", Visibility = ActorVisibility.Known, Biography = "Keeps the ledgers." });
        await _actors.SaveAsync(new ActorDocument { Id = "a-oskar-" + id, CampaignId = id, Name = "Oskar", Visibility = ActorVisibility.Known, Biography = "Mends nets." });
        await _sessions.SaveAsync(new SessionDocument { Id = "s-one-" + id, CampaignId = id, Title = "The wreck", Status = SessionStatus.Completed, Recap = "The party found the wreck at low tide." });
        var dm = new CampaignAccess(id, DmUser, CampaignRole.DM);

        var asked = new List<string>();
        string WithSpot(IList<ChatMessage> messages, int n)
        {
            var user = messages.Last().Text;
            asked.Add(user);
            var items = Regex.Matches(user, @"^Slot (?<n>\d+) \([^)]*\): (?<label>.*)$", RegexOptions.Multiline).Select(m =>
            {
                var item = "{\"slot\":" + m.Groups["n"].Value + ",\"depicts\":\"" + m.Groups["label"].Value.Trim() + "\",\"visual\":\"a figure in a grey cloak on a wet stone quay at dusk\",\"mood\":\"tense\"";
                return m.Groups["label"].Value.Trim() == "Vera" ? item + ",\"spot\":{\"depicts\":\"The ledger\",\"visual\":\"an open ledger and a brass pen\",\"mood\":\"serene\"}}" : item + "}";
            });
            return "{\"slots\":[" + string.Join(",", items) + "]}";
        }

        var director = new BookArtDirection(_reader, _art, new FakeModels(new ScriptedChat(WithSpot)), _resolver.ResolveAsync, new BookGate());
        Assert.Equal(BookRunStatus.Done, (await Finish((await director.StartAsync(dm, "party", "en"))!)).Status);
        // A spot is never asked about as a place of its own: one line per entry, the header and the cover, and the scene's place for the session.
        Assert.Contains(asked, a => a.Contains("(a scene from this session; a small closing picture may be suggested): The wreck", StringComparison.Ordinal));

        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        var compiled = BookComposer.Compose(source, "en", out var entries);
        var places = BookSlots.For(source, compiled, entries);
        Assert.Equal(places.Count(x => x.Kind != BookSlots.Spot), asked.Sum(a => Regex.Matches(a, @"^Slot \d+ ", RegexOptions.Multiline).Count));
        Assert.Equal(entries.Count, places.Count(x => x.Kind == BookSlots.Spot));
        var veraEntry = entries.Single(e => e.Alt == "Vera");
        var spot = BookSlots.SpotKey(veraEntry);
        var stored = (await _art.GetAsync(dm))!;
        Assert.Equal("an open ledger and a brass pen", stored.Suggestions.Single(x => x.Slot == spot).Visual);
        Assert.Equal(BookArtDocument.Party, stored.Suggestions.Single(x => x.Slot == spot).BuiltFrom);

        var tray = (await new BookArtService(_reader, _art, _resolver).TrayAsync(dm, "party", "en"))!;
        var cast = tray.Chapters.Single(c => c.Id == "cast").Slots;
        Assert.Single(cast, r => r.Kind == BookSlots.Spot);
        Assert.Equal(spot, cast[cast.ToList().FindIndex(r => r.Key == veraEntry.SlotKey) + 1].Key);
        Assert.Contains(tray.Chapters.Single(c => c.Id == "chronicle").Slots, r => r.Kind == BookSlots.Entry && r.Label == "The wreck");

        var images = new FakeImages();
        Assert.Equal(BookRunStatus.Done, (await Finish((await Drawing(images).StartAsync(dm, "party", [new BookDrawPick(spot)]))!)).Status);
        Assert.Equal(ArtShape.Square, Assert.Single(images.Shapes));
        Assert.StartsWith(BookArtStyles.Framing["spot"] + ": an open ledger and a brass pen", Assert.Single(images.Prompts));

        var draft = BookDraftWriter.Generate((await _reader.ReadAsync(dm, BookAudience.Party))!, "en", await _art.GetAsync(dm));
        var castText = draft.Single(c => c.Section == "cast").Markdown;
        var spotAt = castText.IndexOf("{{spot\n![Vera](art:" + spot + ")\n}}", StringComparison.Ordinal);
        Assert.True(spotAt > castText.IndexOf("Keeps the ledgers.", StringComparison.Ordinal));
        var oskar = castText.IndexOf("## Oskar", StringComparison.Ordinal);
        Assert.True(oskar < castText.IndexOf("## Vera", StringComparison.Ordinal) ? spotAt > oskar : spotAt < oskar);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(castText, @"\{\{spot"));
        var chronicle = draft.Single(c => c.Section == "chronicle").Markdown;
        Assert.Contains("{{entry,#e-" + BookSlots.KeyFor(BookSlots.SessionSubject("s-one-" + id)) + "}}\n## The wreck", chronicle, StringComparison.Ordinal);
    }

    // Regenerate (28.3, item 3): drawing a slot again keeps the picture it puts back, up to three versions; the third draw says first that the
    // next one drops the oldest earlier version, which was paid for, and the fourth does drop it.
    [Fact]
    public async Task DrawingAgain_KeepsTheEarlierPicture_AndThePreflightSaysWhenANewOneDropsAPaidVersion()
    {
        var (dm, _) = await SeedAsync();
        var vera = await SlotOf(dm, "party", "Vera");
        var drawing = Drawing(new FakeImages(n => new GeneratedImage(BookArtTests.Png(300 + n), "image/png")));

        for (var drawn = 1; drawn <= 4; drawn++)
        {
            var item = Assert.Single((await drawing.PreviewAsync(dm, "party", [new BookDrawPick(vera)]))!.Items);
            Assert.Equal(drawn > 1, item.Replaces);
            Assert.Equal(drawn > BookArtDocument.MaxVersions, item.Drops);
            Assert.Equal(BookRunStatus.Done, (await Finish((await drawing.StartAsync(dm, "party", [new BookDrawPick(vera)]))!)).Status);
        }

        var art = (await _art.GetAsync(dm))!;
        Assert.Equal(304, Assert.Single(art.Pictures).Width);
        Assert.Equal(new[] { 302, 303 }, art.Kept.Select(p => p.Width).Order());
        Assert.All(art.Kept, p => Assert.Equal((vera, BookArtDocument.Party, "drawn"), (p.Slot, p.BuiltFrom, p.Origin)));
        Assert.Equal(4, art.DrawnPictures);

        // The version list marks every drawn version older once the style changes.
        var trays = new BookArtService(_reader, _art, _resolver, media: _media);
        Assert.All((await trays.VersionsAsync(dm, "party", vera))!, v => Assert.False(v.Stale));
        Assert.True(await drawing.SetStyleAsync(dm, new BookArtStyle("woodcut", "sepia", "bold", "dusk")));
        var versions = (await trays.VersionsAsync(dm, "party", vera))!;
        Assert.Equal(3, versions.Count);
        Assert.All(versions, v => Assert.True(v.Stale));
    }

    [Fact]
    public async Task APickIsDrawnOnlyFromThisEditionsCurrentSuggestion_WithAVisualThatPassesTheChecks()
    {
        var (dm, id) = await SeedAsync();
        var veraParty = await SlotOf(dm, "party", "Vera");
        var morrow = await SlotOf(dm, "dm", "Morrow");
        var images = new FakeImages();
        var drawing = Drawing(images);

        var preview = (await drawing.PreviewAsync(dm, "party",
        [
            new BookDrawPick(morrow), // the GM edition's: no such slot in the party's book
            new BookDrawPick("s000000000000000000000000"),
            new BookDrawPick(veraParty, "Vera on the quay at dusk"), // a name the edition holds
            new BookDrawPick(veraParty)
        ]))!;
        Assert.Equal(["slot", "slot", "visual", "repeat"], preview.Items.Select(i => i.Problem));
        Assert.Equal(0, preview.Pictures);
        Assert.Contains(preview.Items[2].VisualProblems, p => p.Contains("Vera", StringComparison.Ordinal));
        var fine = (await drawing.PreviewAsync(dm, "party", [new BookDrawPick(veraParty)]))!;
        Assert.Null(fine.Items.Single().Problem);
        Assert.Equal(1, fine.Pictures);

        // Both editions have a suggestion for Vera's slot; once the GM edition's is gone, the GM tray cannot draw from the party's.
        Assert.Equal(["dm", "party"], (await _art.GetAsync(dm))!.Suggestions.Where(s => s.Slot == veraParty).Select(s => s.BuiltFrom).Order());
        Assert.True(await _art.SaveSuggestionsAsync(dm, "dm", [], present: new HashSet<string>()));
        Assert.Equal("suggestion", (await drawing.PreviewAsync(dm, "dm", [new BookDrawPick(veraParty)]))!.Items.Single().Problem);

        // Text changed since the suggestion was made: it may hold words since taken out.
        Assert.NotNull(await _actors.UpdateAsync("a-vera-" + id, a => a.Biography = "Keeps the ledgers, and now the keys."));
        Assert.Equal("stale", (await drawing.PreviewAsync(dm, "party", [new BookDrawPick(veraParty)]))!.Items.Single().Problem);

        // Nothing drawable: nothing is opened, and the gate is free again.
        Assert.Null(await drawing.StartAsync(dm, "party", [new BookDrawPick(veraParty), new BookDrawPick(morrow)]));
        Assert.Empty(images.Opened);
        Assert.Empty((await _art.GetAsync(dm))!.Pictures);
    }

    [Fact]
    public async Task ARefusedPicture_IsNotStored_AndAKeyRefusalStopsTheRestOfTheRun()
    {
        var (dm, _) = await SeedAsync();
        var art = (await _art.GetAsync(dm))!;
        var slots = art.Suggestions.Where(s => s.BuiltFrom == "dm").Select(s => s.Slot).Take(3).ToList();
        Assert.Equal(3, slots.Count);

        // The first picture is not an image at all; the second call is refused by the provider.
        var images = new FakeImages(n => n == 1
            ? new GeneratedImage(System.Text.Encoding.ASCII.GetBytes("<svg onload=alert(1)>"), "image/png")
            : throw new HttpRequestException("unauthorised", null, System.Net.HttpStatusCode.Unauthorized));
        var snapshot = await Finish((await Drawing(images).StartAsync(dm, "dm", slots.Select(s => new BookDrawPick(s)).ToList()))!);

        Assert.Equal([BookRowStatus.Kept, BookRowStatus.Kept, BookRowStatus.Skipped], snapshot.Rows.Select(r => r.Status));
        Assert.Equal(["image", "auth", "auth"], snapshot.Rows.Select(r => r.Reason));
        Assert.Equal(2, images.Prompts.Count);
        var after = (await _art.GetAsync(dm))!;
        Assert.Empty(after.Pictures);
        // The refused image was drawn, and so paid for; the refused call was not.
        Assert.Equal((1, 1), (after.DrawnPictures, after.DrawUnpricedPictures));
    }

    [Fact]
    public async Task ADrawingRun_TakesTheGatesOneSlot_SharedWithChapterBuildsAndTheDirector()
    {
        var (dm, _) = await SeedAsync();
        var gate = new BookGate();
        Assert.True(gate.TryEnter(dm.UserId));
        var images = new FakeImages();

        var vera = await SlotOf(dm, "party", "Vera");
        await Assert.ThrowsAsync<BookBuildBusyException>(() => Drawing(images, gate).StartAsync(dm, "party", [new BookDrawPick(vera)]));
        Assert.Empty(images.Opened);

        gate.Exit(dm.UserId);
        var run = (await Drawing(images, gate).StartAsync(dm, "party", [new BookDrawPick(vera)]))!;
        await Finish(run);
        Assert.True(gate.TryEnter(dm.UserId), "the run gives the gate back when it ends");
    }

    /// <summary>
    /// The GM's own picture style (docs/BOOK_EXPORT.md 41.23) is used as written, in place of the book's style choices, with whatever names it
    /// gives (it is the GM's text for their own model); the picture's description is still held to the checks. A picture drawn with it is
    /// current in the tray until the guide changes.
    /// </summary>
    [Fact]
    public async Task TheGmsOwnPictureStyle_ReplacesTheBooksChoices_AsWritten_AndAChangedStyleMakesAPictureOlder()
    {
        var (dm, _) = await SeedAsync();
        var vera = await SlotOf(dm, "party", "Vera");
        const string guide = "Stylized hand-painted World of Warcraft concept art,   chunky shapes, saturated colours.";
        var images = new FakeImages(guide: guide);
        var drawing = Drawing(images);
        Assert.True(await drawing.SetStyleAsync(dm, new BookArtStyle("ink", "sepia", "fine", "candle")));

        Assert.Equal(BookRunStatus.Done, (await Finish((await drawing.StartAsync(dm, "party", [new BookDrawPick(vera)]))!)).Status);

        var prompt = Assert.Single(images.Prompts);
        Assert.Contains(" Stylized hand-painted World of Warcraft concept art, chunky shapes, saturated colours. ", prompt);
        Assert.DoesNotContain(BookArtStyles.Find(BookArtStyles.Media, "ink")!.Phrase, prompt);
        Assert.DoesNotContain(BookArtStyles.Find(BookArtStyles.Palettes, "sepia")!.Phrase, prompt);
        Assert.EndsWith(BookArtStyles.Negative, prompt);
        // A visual that names someone is still refused: the guide is the GM's, the description is the campaign's.
        var named = (await drawing.PreviewAsync(dm, "party", [new BookDrawPick(vera, "a portrait of Vera by the sea")]))!;
        Assert.Contains("capital:Vera", Assert.Single(named.Items).VisualProblems);

        var trays = new BookArtService(_reader, _art, _resolver, images);
        BookTraySlot Vera(BookTray tray) => tray.Chapters.SelectMany(c => c.Slots).Single(x => x.Key == vera);
        var tray = (await trays.TrayAsync(dm, "party", "en"))!;
        Assert.Equal(guide, tray.Guide);
        Assert.False(Vera(tray).Drawn!.Stale);
        // The book's own choices do not touch a picture drawn from the guide; another guide does.
        Assert.True(await drawing.SetStyleAsync(dm, new BookArtStyle("woodcut", "sepia", "bold", "dusk")));
        Assert.False(Vera((await trays.TrayAsync(dm, "party", "en"))!).Drawn!.Stale);
        Assert.True(Vera((await new BookArtService(_reader, _art, _resolver, new FakeImages(guide: "Watercolour wash.")).TrayAsync(dm, "party", "en"))!).Drawn!.Stale);
    }
}
