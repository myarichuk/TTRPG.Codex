using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>
/// Pictures in the text (docs/BOOK_EXPORT.md 53): what the editor's chips are told. A chip shows only its own edition's suggestion, the prompt
/// it shows is the one a drawing sends, its block is the one the generator writes, and dismissing or asking the art director about one chapter
/// touches nothing else.
/// </summary>
public partial class BookArtDrawingTests
{
    [Fact]
    public async Task AChip_ShowsThePromptADrawingSends_AndOnlyItsOwnEditionsSuggestion_AndNothingToAPlayer()
    {
        var (dm, id) = await SeedAsync();
        var vera = await SlotOf(dm, "party", "Vera");
        var morrow = await SlotOf(dm, "dm", "Morrow");
        var images = new FakeImages(price: new AiImagePrice(0.04m, "USD"));
        var drawing = Drawing(images);

        var party = (await drawing.ChipsAsync(dm, "party"))!;
        // The party's book has no place for the hidden character, under any key.
        Assert.DoesNotContain(party.Chips, c => c.Slot == morrow || c.Label.Contains("Morrow", StringComparison.Ordinal));
        var chip = party.Chips.Single(c => c.Slot == vera);
        Assert.Equal(("cast", "Vera", false), (chip.Section, chip.Label, chip.Pictured));
        Assert.False(chip.Idea!.Stale);
        Assert.NotNull(party.Model);
        Assert.Empty(images.Opened);

        // The prompt shown is, letter for letter, the one the drawing sends.
        Assert.Equal(BookRunStatus.Done, (await Finish((await drawing.StartAsync(dm, "party", [new BookDrawPick(vera)]))!)).Status);
        Assert.Equal(chip.Idea.Prompt, Assert.Single(images.Prompts));
        Assert.True((await drawing.ChipsAsync(dm, "party"))!.Chips.Single(c => c.Slot == vera).Pictured);
        // A party picture prints in the GM's book too; a GM picture never in the party's.
        Assert.True((await drawing.ChipsAsync(dm, "dm"))!.Chips.Single(c => c.Slot == vera).Pictured);
        Assert.Equal(BookRunStatus.Done, (await Finish((await drawing.StartAsync(dm, "dm", [new BookDrawPick(morrow)]))!)).Status);
        Assert.True((await drawing.ChipsAsync(dm, "dm"))!.Chips.Single(c => c.Slot == morrow).Pictured);
        Assert.DoesNotContain((await drawing.ChipsAsync(dm, "party"))!.Chips, c => c.Slot == morrow);

        // With the party's suggestions gone, the party chip shows none, though the GM edition has one for the very same slot.
        Assert.True(await _art.SaveSuggestionsAsync(dm, "party", [], present: new HashSet<string>()));
        Assert.Contains((await _art.GetAsync(dm))!.Suggestions, s => s.Slot == vera && s.BuiltFrom == "dm");
        Assert.Null((await drawing.ChipsAsync(dm, "party"))!.Chips.Single(c => c.Slot == vera).Idea);
        Assert.NotNull((await drawing.ChipsAsync(dm, "dm"))!.Chips.Single(c => c.Slot == vera).Idea);

        // Not the GM: no chips at all.
        Assert.Null(await drawing.ChipsAsync(new CampaignAccess(id, "someone_else", CampaignRole.Player), "party"));
        Assert.Null(await drawing.ChipsAsync(dm, "everyone"));
    }

    [Fact]
    public async Task AChip_WhoseTextMovedOn_OrWhoseDescriptionNamesSomeone_HasNoPrompt_SoItCannotBeDrawn()
    {
        var (dm, id) = await SeedAsync();
        var vera = await SlotOf(dm, "party", "Vera");
        var drawing = Drawing(new FakeImages());

        // A description that names a character fails the same check a drawing applies.
        var stored = (await _art.GetAsync(dm))!.Suggestions.Single(s => s.Slot == vera && s.BuiltFrom == "party");
        Assert.True(await _art.SaveSuggestionsAsync(dm, "party",
            [new BookArtSuggestion { Slot = vera, Inputs = stored.Inputs, Depicts = "Vera", Visual = "Vera on the quay at dusk", Mood = "tense", Model = "m" }]));
        var named = (await drawing.ChipsAsync(dm, "party"))!.Chips.Single(c => c.Slot == vera).Idea!;
        Assert.False(named.Stale);
        Assert.Null(named.Prompt);
        Assert.Contains(named.Problems, p => p.Contains("Vera", StringComparison.Ordinal));

        // The campaign changed since the suggestion was made: stale, and no prompt to show or send.
        Assert.NotNull(await _actors.UpdateAsync("a-vera-" + id, a => a.Biography = "Keeps the ledgers, and now the keys."));
        var stale = (await drawing.ChipsAsync(dm, "party"))!.Chips.Single(c => c.Slot == vera).Idea!;
        Assert.True(stale.Stale);
        Assert.Null(stale.Prompt);
    }

    [Fact]
    public async Task AChipsBlock_IsTheBlockTheGeneratorWritesUnderThatHeading()
    {
        var (dm, _) = await SeedAsync();
        var vera = await SlotOf(dm, "party", "Vera");
        var drawing = Drawing(new FakeImages());
        var chip = (await drawing.ChipsAsync(dm, "party"))!.Chips.Single(c => c.Slot == vera);
        Assert.Equal("{{portrait\n![Vera](art:" + vera + ")\n}}", chip.Block);

        var source = (await _reader.ReadAsync(dm, BookAudience.Party))!;
        string Cast() => BookDraftWriter.Generate(source, "en", _art.GetAsync(dm).GetAwaiter().GetResult()).Single(c => c.Section == "cast").Markdown;
        var before = Cast();
        Assert.DoesNotContain("art:" + vera, before);
        Assert.Equal(BookRunStatus.Done, (await Finish((await drawing.StartAsync(dm, "party", [new BookDrawPick(vera)]))!)).Status);
        // With the picture drawn, the generator writes exactly what the editor puts under the heading: the old text with the block and a blank line.
        var heading = before.IndexOf("## Vera\n", StringComparison.Ordinal) + "## Vera".Length;
        Assert.Equal(before[..heading] + "\n\n" + chip.Block + before[heading..], Cast());
    }

    [Fact]
    public async Task Dismiss_TakesOnlyThisEditionsSuggestionForThatPlace_AndOnlyForTheGm()
    {
        var (dm, id) = await SeedAsync();
        var vera = await SlotOf(dm, "party", "Vera");
        var drawing = Drawing(new FakeImages());
        var before = (await _art.GetAsync(dm))!.Suggestions.Count;

        Assert.False(await drawing.DismissAsync(new CampaignAccess(id, "someone_else", CampaignRole.Player), "party", vera));
        Assert.False(await drawing.DismissAsync(dm, "party", "not-a-key"));
        Assert.False(await drawing.DismissAsync(dm, "everyone", vera));
        Assert.Equal(before, (await _art.GetAsync(dm))!.Suggestions.Count);

        Assert.True(await drawing.DismissAsync(dm, "party", vera));
        var left = (await _art.GetAsync(dm))!.Suggestions;
        Assert.Equal(before - 1, left.Count);
        Assert.DoesNotContain(left, s => s.Slot == vera && s.BuiltFrom == "party");
        Assert.Contains(left, s => s.Slot == vera && s.BuiltFrom == "dm");
        // Nothing left to dismiss there.
        Assert.False(await drawing.DismissAsync(dm, "party", vera));
        // And nothing to draw from: the server refuses the pick whatever the page shows.
        Assert.Equal("suggestion", (await drawing.PreviewAsync(dm, "party", [new BookDrawPick(vera)]))!.Items.Single().Problem);
    }

    [Fact]
    public async Task TheArtDirector_AskedFromAChip_AsksAboutThatChapterOnly()
    {
        var (dm, _) = await SeedAsync();
        var chat = new ScriptedChat(Direct);
        var director = new BookArtDirection(_reader, _art, new FakeModels(chat), _resolver.ResolveAsync, new BookGate());
        var whole = (await director.PreviewAsync(dm, "dm", "en", all: true))!;
        var cast = (await director.PreviewAsync(dm, "dm", "en", all: true, section: "cast"))!;
        Assert.True(whole.Calls > cast.Calls && whole.Slots > cast.Slots, $"{whole.Calls} calls and {whole.Slots} slots for the book, {cast.Calls} and {cast.Slots} for the chapter");
        Assert.Equal(1, cast.Calls);
        // A chapter the edition does not have asks nothing.
        Assert.Equal(0, (await director.PreviewAsync(dm, "dm", "en", all: true, section: "no-such-chapter"))!.Calls);
        Assert.Null(await director.StartAsync(dm, "dm", "en", all: true, section: "no-such-chapter"));

        Assert.True(await _art.SaveSuggestionsAsync(dm, "dm", [], present: new HashSet<string>()));
        Assert.Equal(BookRunStatus.Done, (await Finish((await director.StartAsync(dm, "dm", "en", all: true, section: "cast"))!)).Status);
        Assert.Equal(1, chat.Calls);
        var chips = (await Drawing(new FakeImages()).ChipsAsync(dm, "dm"))!.Chips;
        var suggested = (await _art.GetAsync(dm))!.Suggestions.Where(s => s.BuiltFrom == "dm").Select(s => s.Slot).ToHashSet();
        Assert.NotEmpty(suggested);
        // Every suggestion made is for a place of that chapter (its entries, their spots, its header).
        Assert.All(chips.Where(c => c.Idea != null), c => Assert.Equal("cast", c.Section));
        Assert.Contains(chips, c => c.Section == "cast" && c.Idea != null);
    }
}
