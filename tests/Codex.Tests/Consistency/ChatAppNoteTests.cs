using Codex.Persistence;
using Codex.Web.Services;
using Microsoft.Extensions.AI;

namespace Codex.Tests.Consistency;

/// <summary>The count of open issues is a line the app adds after each of the game master's messages. It must not count as one of "the DM's last few
/// messages" when old tool results are cut, or it would halve how long they are kept.</summary>
public class ChatAppNoteTests
{
    private const string Count = "(A note from the app, not from the game master: 2 open issues (0 errors, 2 warnings, 0 info); call list_issues for details.)";

    private static readonly string Page = new('x', 5_000);

    // n messages from the DM, each followed by a long tool result and an answer; a count note after each message when asked.
    private static List<ChatMessage> History(int n, Func<string, ChatMessage>? note)
    {
        var history = new List<ChatMessage>();
        for (var i = 1; i <= n; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"message {i}"));
            if (note != null) history.Add(note(Count));
            history.Add(new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent($"c{i}", "get_page", new Dictionary<string, object?> { ["page"] = "p" }) }));
            history.Add(new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent($"c{i}", Page) }));
            history.Add(new ChatMessage(ChatRole.Assistant, $"answer {i}"));
        }

        return history;
    }

    // Which results reach the model whole, by call id.
    private static string[] Whole(IReadOnlyList<ChatMessage> history) =>
        WorldChat.ForModel(new ChatMessage(ChatRole.System, "system"), history).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Where(r => r.Result as string == Page).Select(r => r.CallId).ToArray();

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(12)]
    public void TheWindowOfWholeResults_IsTheSameWithTheCountNoteAndWithout(int messages)
    {
        var without = Whole(History(messages, null));
        var with = Whole(History(messages, text => new AppNoteMessage(text)));

        Assert.Equal(without, with);
        Assert.True(without.Length >= 3 || messages < 3);
    }

    [Fact]
    public void ANoteThatIsNotMarked_WouldHaveShortenedTheWindow()
    {
        // The contrast: an unmarked line of the same text counts as a message of the DM's, so fewer results are kept whole.
        var plain = Whole(History(7, text => new ChatMessage(ChatRole.User, text)));
        var marked = Whole(History(7, text => new AppNoteMessage(text)));

        Assert.True(plain.Length < marked.Length, $"{plain.Length} vs {marked.Length}");
    }

    [Fact]
    public async Task TheMarkIsStoredWithTheHistory_SoAChatOpenedAgainIsCutWhereItWas()
    {
        var page = new WikiPageDocument { Id = "wikipages/1-A", Slug = "road", Title = "Road", Body = "Past [[The Mere of Dead Men]].", Visibility = WikiVisibility.Party };
        var saving = WorldBuilderTests.ServiceWithPages(new[] { page });
        var model = new WorldBuilderTests.ScriptedModel(_ => new ChatMessage(ChatRole.Assistant, "One."), _ => new ChatMessage(ChatRole.Assistant, "Two."), _ => new ChatMessage(ChatRole.Assistant, "Three."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);
        await chat.SendAsync("First.", _ => { }, CancellationToken.None);
        await chat.SendAsync("Second.", _ => { }, CancellationToken.None);

        var stored = chat.Document.History;
        var notes = stored.Where(m => m.AppNote == true).ToList();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.StartsWith("(A note from the app, not from the game master: 1 open issues", n.Text));
        Assert.All(stored.Where(m => m.AppNote != true), m => Assert.Null(m.AppNote)); // nothing else carries a mark

        // Opened again from the stored document, the notes are still the app's: a third message stores all three marked.
        var reopened = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm, chat.Document);
        await reopened.SendAsync("Third.", _ => { }, CancellationToken.None);
        Assert.Equal(3, reopened.Document.History.Count(m => m.AppNote == true));
    }
}
