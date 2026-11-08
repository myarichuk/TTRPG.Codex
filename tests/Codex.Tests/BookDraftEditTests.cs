using Codex.Core.Book;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

/// <summary>
/// AI edits on a passage of a draft (docs/BOOK_EXPORT.md 41.4, phase 4), without a database: what the model is sent (never a secret, a picture,
/// a marker or a break, whichever way the selection cuts), what a reply must keep, and the book's shape taken from the game system.
/// </summary>
public class BookDraftEditTests
{
    private const string Chapter =
        "{{chapter,#ch-cast\n# Cast\n}}\n\n" +
        "{{entry,#e-vera}}\n## PUBLIC_Vera\n\n" +
        "{{portrait\n![PUBLIC_Vera](art:actor-vera)\n}}\n\n" +
        "PUBLIC_BIO first paragraph about the docks.\n\n" +
        "{{secret\nCANARY_SECRET_ONE is the smuggler.\n}}\n\n" +
        "PUBLIC_AFTER the secret, a second paragraph.\n\n" +
        "\\page {fitted}\n\n" +
        "{{entry,#e-oren}}\n## PUBLIC_Oren\n\n" +
        "PUBLIC_OREN text.\n";

    private static int At(string text, string what) => text.IndexOf(what, StringComparison.Ordinal) is var i and >= 0 ? i : throw new InvalidOperationException(what);

    private static string Prompt(BookEditPassage passage, string? shape = null)
    {
        var (system, user) = BookEditPrompts.Build(new BookEditRequest("Cast", "en", "Test game", shape, "Shorter.", passage));
        return system + "\n" + user;
    }

    [Fact]
    public void AReply_HasRoomToGrowIntoTheBooksProse_AndNeverPastTheClientsCeiling()
    {
        var shortOne = new BookEditRequest("Cast", "en", null, null, "Shorter.", BookDraftEdits.Cut(Chapter, 0, Chapter.Length)!);
        Assert.Equal(BookEditWorker.MinOutputTokens, BookEditWorker.MaxOutputFor(shortOne));

        var prose = string.Join("\n\n", Enumerable.Repeat("A paragraph of the keeper's notes about the reef and the boats.", 60));
        var longOne = new BookEditRequest("Cast", "en", null, null, "Shorter.", BookDraftEdits.Cut(prose, 0, prose.Length)!);
        Assert.Equal(BookTokens.Estimate(longOne.Passage.Text) * 4 + 1200, BookEditWorker.MaxOutputFor(longOne));
        Assert.True(BookEditWorker.MaxOutputFor(longOne) < Codex.Core.AI.AIClientFactory.AnthropicMaxOutputTokens);

        var huge = string.Join("\n\n", Enumerable.Repeat("A paragraph of the keeper's notes about the reef and the boats.", 600));
        var hugeOne = new BookEditRequest("Cast", "en", null, null, "Shorter.", BookDraftEdits.Cut(huge, 0, huge.Length)!);
        Assert.Equal(Codex.Core.AI.AIClientFactory.AnthropicMaxOutputTokens, BookEditWorker.MaxOutputFor(hugeOne));
    }

    [Fact]
    public void ABlockOpenedWithWordsAfterItsName_IsRefused_BecauseTheBookWouldPrintItsBraces()
    {
        var passage = BookDraftEdits.Cut(Chapter, At(Chapter, "PUBLIC_AFTER"), At(Chapter, "\\page"))!;
        // What a real model wrote for "As the book": the title on the opening line.
        Assert.Contains(BookDraftEdits.Check(passage, "Text.\n\n{{note Running Vera Holloway\nVera is not a suspect.\n}}\n"), p => p.Contains("#####"));
        Assert.NotEmpty(BookDraftEdits.Check(passage, "Text.\n\n  {{descriptive The lamp room\nWind.\n}}\n"));
        // The forms the renderer reads: the name alone, with classes, with a title heading inside, and an inline span closed on its line.
        foreach (var fine in new[]
                 {
                     "Text.\n\n{{note\n##### Running Vera Holloway\nVera is not a suspect.\n}}\n", "{{note,wide\nx\n}}", "{{descriptive\nWind.\n}}",
                     "A {{pageNumber 3}} span.", "{{wide Across both columns.}}"
                 })
        {
            Assert.Empty(BookDraftEdits.Check(passage, fine));
        }

        Assert.Contains("##### heading", Prompt(passage));
        Assert.Contains("leave that part out rather than make it up", Codex.Web.Book.BookDraftAi.AskText("shape"));
        // A real model's read-aloud box came with a clue, a motive and a DC the notes never had.
        Assert.Contains("from the passage only: no new facts, motives, events, clues or checks", Codex.Web.Book.BookDraftAi.AskText("readaloud"));
        Assert.Contains("checks and difficulty classes", Prompt(passage));
    }

    [Fact]
    public void ASelectionThatStartsInsideASecret_TakesTheWholeSecret_AndItsTextIsNeverSent()
    {
        var from = At(Chapter, "smuggler");
        var to = At(Chapter, "second paragraph");
        var passage = BookDraftEdits.Cut(Chapter, from, to)!;
        Assert.Equal(At(Chapter, "{{secret"), passage.From);
        Assert.Equal(to, passage.To);
        Assert.DoesNotContain("CANARY", Prompt(passage));
        Assert.Contains("CANARY_SECRET_ONE", Assert.Single(passage.Pieces).Text);
        Assert.Equal("a GM secret", passage.Pieces[0].Label);
        Assert.Equal(Chapter[passage.From..passage.To], passage.Original);
    }

    [Fact]
    public void ASelectionThatEndsInsideASecret_TakesTheWholeSecret()
    {
        var passage = BookDraftEdits.Cut(Chapter, At(Chapter, "PUBLIC_BIO"), At(Chapter, "the smuggler"))!;
        // The secret's block ends with its own line end.
        Assert.Equal(At(Chapter, "}}\n\nPUBLIC_AFTER") + 3, passage.To);
        Assert.DoesNotContain("CANARY", Prompt(passage));
    }

    [Fact]
    public void ASecretBesideThePassage_IsLeftOutOfTheTextAroundIt()
    {
        var passage = BookDraftEdits.Cut(Chapter, At(Chapter, "PUBLIC_AFTER"), At(Chapter, "\\page"))!;
        Assert.Empty(passage.Pieces);
        Assert.Contains("PUBLIC_BIO", passage.Before);
        Assert.Contains("PUBLIC_OREN", passage.After);
        Assert.DoesNotContain("CANARY", Prompt(passage));
        // Nor a picture, a marker or a break: the text around the passage is for reading.
        Assert.DoesNotContain("art:", passage.Before);
        Assert.DoesNotContain("#e-", passage.Before + passage.After);
        Assert.DoesNotContain("\\page", passage.After);
    }

    [Theory]
    [InlineData("Before.\n\n{{note\n## Rumours\n{{secret\nCANARY_NESTED\n}}\nPUBLIC_NOTE\n}}\n\nAfter.\n")]
    [InlineData("Before {{secret CANARY_INLINE}} after.\n")]
    [InlineData("Before.\n\n{{note,secret\nCANARY_CLASSES\n}}\n\nAfter.\n")]
    [InlineData("Before.\n\n{{wide,secret,#x\nCANARY_CLASSES_TWO\n}}\n\nAfter.\n")]
    [InlineData("Before.\n\n{{secret\nCANARY_NEVER_CLOSED\n\nAfter, still inside it.\n")]
    [InlineData("Before.\n\n<div class=\"note secret\">CANARY_HTML <b>bold</b></div>\n\nAfter.\n")]
    [InlineData("Before.\n\n<div class='secret'>CANARY_HTML_UNCLOSED\n\nAfter.\n")]
    [InlineData("Before.\n\n{{SECRET\nCANARY_UPPER\n}}\n\nAfter.\n")]
    public void NoSecretReachesTheModel_WhateverItsSpelling_AndWhereverTheSelectionIs(string chapter)
    {
        for (var from = 0; from <= chapter.Length; from += 3)
        {
            for (var to = from; to <= chapter.Length; to += 5)
            {
                var passage = BookDraftEdits.Cut(chapter, from, to)!;
                Assert.DoesNotContain("CANARY", Prompt(passage));
                Assert.Equal(chapter, chapter[..passage.From] + BookDraftEdits.Restore(passage, passage.Text) + chapter[passage.To..]);
            }
        }
    }

    [Fact]
    public void PicturesMarkersAndBreaks_AreKeptBack_AndAnEchoPutsTheChapterBackAsItWas()
    {
        var passage = BookDraftEdits.Cut(Chapter, 0, Chapter.Length)!;
        Assert.Equal(["the chapter's title", "the next entry's marker", "a picture", "a GM secret", "a page or column break", "the next entry's marker"], passage.Pieces.Select(p => p.Label));
        Assert.All(passage.Pieces, p => Assert.True(p.Line));
        // What the model reads of the chapter (the rules themselves name \page, to forbid it).
        var sent = passage.Text + passage.Before + passage.After;
        foreach (var gone in new[] { "art:", "#e-vera", "#ch-cast", "\\page", "CANARY" }) Assert.DoesNotContain(gone, sent);
        Assert.DoesNotContain("CANARY", Prompt(passage));
        Assert.Contains("PUBLIC_BIO first paragraph", Prompt(passage));
        Assert.Empty(BookDraftEdits.Check(passage, passage.Text));
        Assert.Equal(Chapter, BookDraftEdits.Restore(passage, passage.Text));
    }

    [Fact]
    public void AReply_MustKeepEveryBlockOnce_OnItsOwnLine_AndCarryNothingTheBookForbids()
    {
        var passage = BookDraftEdits.Cut(Chapter, At(Chapter, "PUBLIC_BIO"), At(Chapter, "\\page"))!;
        Assert.Single(passage.Pieces);
        Assert.NotEmpty(BookDraftEdits.Check(passage, "Shorter text, the secret gone."));
        Assert.NotEmpty(BookDraftEdits.Check(passage, "One.\n\n{{block:1}}\n\nTwo.\n\n{{block:1}}\n"));
        Assert.NotEmpty(BookDraftEdits.Check(passage, "One. {{block:1}} Two."));
        Assert.NotEmpty(BookDraftEdits.Check(passage, "One.\n\n{{block:1}}\n\n{{block:2}}\n"));
        foreach (var bad in new[]
                 {
                     "{{secret\nnew\n}}", "{{note,secret\nnew\n}}", "{{entry,#e-x}}", "{{portrait\nx\n}}", "![x](art:actor-vera)", "![x](https://example.com/a.png)",
                     "see https://example.com", "<b>bold</b>", "[[actor:x]]", "\\page", "\\column", ":::dm\nx\n:::"
                 })
        {
            Assert.NotEmpty(BookDraftEdits.Check(passage, "One.\n\n{{block:1}}\n\n" + bad + "\n"));
        }

        Assert.Empty(BookDraftEdits.Check(passage, "One \\<b>escaped\\</b>.\n\n{{block:1}}\n\n{{descriptive\nThe docks reek of tar.\n}}\n\n{{note\n##### Art of the deal\nA sidebar.\n}}\n"));
    }

    private sealed class Replies(Func<int, string, (string Text, ChatFinishReason? Finish)> reply) : IChatClient
    {
        public List<string> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var text = string.Join("\n", messages.Select(m => m.Text));
            Requests.Add(text);
            var (answer, finish) = reply(Requests.Count, text);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
            {
                FinishReason = finish, Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 }
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static BookEditRequest Request(BookEditPassage passage) => new("Cast", "en", "Test game", null, "Shorter.", passage);

    private static BookBudget Budget(BookEditRequest request) => new(Codex.Web.Book.BookDraftAi.CapFor(request));

    [Fact]
    public async Task AReplyCutOffByTheLengthLimit_IsNeverOffered_EvenIfItPassesTheChecks()
    {
        var request = Request(BookDraftEdits.Cut(Chapter, 0, Chapter.Length)!);
        // The start of the chapter, every marker in place, and then nothing: a whole-chapter edit with its end missing.
        var chat = new Replies((_, _) => (request.Passage.Text[..(request.Passage.Text.LastIndexOf("{{block:6}}", StringComparison.Ordinal) + 11)], ChatFinishReason.Length));
        var result = await new BookEditWorker(chat, Budget(request)).RunAsync(request);
        Assert.Null(result.Replacement);
        Assert.Equal("cutoff", result.Reason);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("cut off", chat.Requests[1]);
    }

    [Fact]
    public async Task AReplyThatDropsABlock_IsAskedAgain_WithTheProblem_AndTheSecondIsRestored()
    {
        var request = Request(BookDraftEdits.Cut(Chapter, At(Chapter, "PUBLIC_BIO"), At(Chapter, "\\page"))!);
        var chat = new Replies((n, _) => n == 1 ? ("Short.", null) : ("Short.\n\n{{block:1}}\n\nPUBLIC_AFTER, shorter.\n", ChatFinishReason.Stop));
        var result = await new BookEditWorker(chat, Budget(request)).RunAsync(request);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("{{block:1}} is missing", chat.Requests[1]);
        Assert.Equal("Short.\n\n{{secret\nCANARY_SECRET_ONE is the smuggler.\n}}\n\nPUBLIC_AFTER, shorter.\n\n", result.Replacement);
        Assert.All(chat.Requests, r => Assert.DoesNotContain("CANARY", r));
    }

    [Fact]
    public void ThePromptTakesTheBooksShapeFromTheGameSystem_AndAGameThatSaysNothingGetsNeutralWords()
    {
        var srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        var d6 = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "examples", "simple-d6", "system.yaml"));
        var passage = BookDraftEdits.Cut(Chapter, 0, Chapter.Length)!;
        Assert.False(string.IsNullOrWhiteSpace(srd.Ai.BookShape));
        Assert.Null(d6.Ai.BookShape);
        var fifth = Prompt(passage, srd.Ai.BookShape);
        Assert.Contains("fifth-edition adventures", fifth);
        Assert.Contains("read-aloud", fifth);
        var other = Prompt(passage, d6.Ai.BookShape);
        Assert.Contains(BookEditPrompts.NeutralShape, other);
        Assert.DoesNotContain("fifth-edition", other);
        // A shape to take after, never a name to use: no trademark or product in the system's own words.
        foreach (var name in new[] { "Dungeons", "D&D", "Wizards", "Handbook", "Dungeon Master", "Forgotten Realms", "Monster Manual" })
        {
            Assert.DoesNotContain(name, srd.Ai.BookShape!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ASelectionOutsideTheChapter_IsRefused()
    {
        Assert.Null(BookDraftEdits.Cut(Chapter, -1, 3));
        Assert.Null(BookDraftEdits.Cut(Chapter, 5, Chapter.Length + 1));
        Assert.Null(BookDraftEdits.Cut(Chapter, 9, 4));
    }
}
