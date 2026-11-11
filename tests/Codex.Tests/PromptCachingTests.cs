using System.Net;
using System.Text;
using Codex.Core.AI;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

/// <summary>
/// Anthropic's prompt cache, asked for by the chats that loop over tools (<see cref="PromptCachingChatClient"/>): where the
/// breakpoints go, that nothing the caller holds is marked, and that the numbers the cost is worked out from mean what the
/// accounting takes them to mean.
/// </summary>
public class PromptCachingTests
{
    // What the Anthropic client reads off a tool to put a breakpoint after it (nameof(Tool.CacheControl) in its own namespace).
    private const string ToolBreakpointKey = "CacheControl";

    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "x", name, "a tool");

    // Where the Anthropic client's AIContent extension keeps a block's breakpoint (its reader is not public).
    private const string BlockBreakpointKey = "anthropic:cache_control";

    private static bool Marked(ChatMessage message) => message.Contents.Any(c => c.AdditionalProperties?.ContainsKey(BlockBreakpointKey) == true);

    private sealed class Recording : IChatClient
    {
        public List<ChatMessage> Messages { get; private set; } = new();
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            Options = options;
            return AsyncEnumerable.Empty<ChatResponseUpdate>();
        }

        public object? GetService(System.Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static List<ChatMessage> Conversation(out ChatMessage toolResult)
    {
        toolResult = new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("c1", "RESULT") });
        return new List<ChatMessage>
        {
            new(ChatRole.System, "SYSTEM"),
            new(ChatRole.User, "first question"),
            new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", "lookup", new Dictionary<string, object?>()) }),
            toolResult,
            new(ChatRole.Assistant, "an answer"),
            new(ChatRole.User, "second question")
        };
    }

    [Fact]
    public async Task AskedFor_MarksTheLastToolTheSystemPromptTheLastPlainUserTurnAndTheFinalMessage_OnCopies()
    {
        var inner = new Recording();
        var history = Conversation(out var toolResult);
        var first = Tool("first");
        var last = Tool("last");
        var options = PromptCachingChatClient.WithPromptCaching(new ChatOptions { Tools = [first, last], Temperature = 0.5f });

        await new PromptCachingChatClient(inner).GetResponseAsync(history, options);

        var sent = inner.Messages;
        Assert.Equal(history.Count, sent.Count);
        // System, the earlier user turn (where the last request left its breakpoint) and the final message; nothing in between.
        Assert.Equal(new[] { true, true, false, false, false, true }, sent.Select(Marked).ToArray());
        // Only the final block of a marked message carries it, and what was not marked is the very object that was passed.
        Assert.Same(toolResult, sent[3]);
        // The history the chat keeps is untouched: a marker left on it would pile up, a request at a time, past the four a request may carry.
        Assert.All(history, m => Assert.False(Marked(m)));
        Assert.NotSame(history[5], sent[5]);

        // Tools: the last one carries the breakpoint, as a copy; the caller's own functions are as they were.
        Assert.Same(first, inner.Options!.Tools![0]);
        Assert.NotSame(last, inner.Options.Tools[1]);
        Assert.True(inner.Options.Tools[1].AdditionalProperties.ContainsKey(ToolBreakpointKey));
        Assert.False(last.AdditionalProperties.ContainsKey(ToolBreakpointKey));
        Assert.Equal("last", inner.Options.Tools[1].Name);

        // The request's own settings go on, and the switch does not.
        Assert.Equal(0.5f, inner.Options.Temperature);
        Assert.Null(inner.Options.AdditionalProperties);
        Assert.True(options.AdditionalProperties!.ContainsKey(PromptCachingChatClient.OptInKey));
    }

    [Fact]
    public async Task WhenAToolResultEndsTheConversation_ThatResultCarriesTheFinalBreakpoint()
    {
        var inner = new Recording();
        var history = Conversation(out var toolResult).Take(4).ToList();

        await new PromptCachingChatClient(inner).GetResponseAsync(history, PromptCachingChatClient.WithPromptCaching(new ChatOptions { Tools = [Tool("lookup")] }));

        // The only plain user turn is the final-but-one: it is the anchor, and the tool result after it is the end.
        Assert.Equal(new[] { true, true, false, true }, inner.Messages.Select(Marked).ToArray());
        Assert.False(Marked(toolResult));
        Assert.IsType<FunctionResultContent>(inner.Messages[3].Contents.Single());
    }

    [Fact]
    public async Task TheToolLoopsLastRound_WhichTakesTheToolsAway_GetsNoBreakpoints()
    {
        // At its round limit the loop sends the same conversation without the tools, to make the model answer. Tools come first in the
        // cached prefix, so that request matches nothing written before it: a marker would only pay the write premium.
        var inner = new Recording();
        var history = Conversation(out _);

        await new PromptCachingChatClient(inner).GetResponseAsync(history, PromptCachingChatClient.WithPromptCaching(new ChatOptions()));

        Assert.All(inner.Messages, m => Assert.False(Marked(m)));
        Assert.Null(inner.Options!.AdditionalProperties);
    }

    [Fact]
    public async Task BreakpointsTheCallerPlacedItself_AreLeftToTheCaller()
    {
        var inner = new Recording();
        var own = new TextContent("pinned");
        own.AdditionalProperties = new AdditionalPropertiesDictionary { [BlockBreakpointKey] = new object() };
        var history = new List<ChatMessage> { new(ChatRole.System, new List<AIContent> { own }), new(ChatRole.User, "hi") };

        await new PromptCachingChatClient(inner).GetResponseAsync(history, PromptCachingChatClient.WithPromptCaching(new ChatOptions { Tools = [Tool("lookup")] }));

        // Nothing added: two more on top of unknown ones could pass the four a request may carry.
        Assert.False(Marked(inner.Messages[1]));
        Assert.False(inner.Options!.Tools![0].AdditionalProperties.ContainsKey(ToolBreakpointKey));
    }

    [Fact]
    public async Task BlankText_IsNeverGivenABreakpoint_BecauseTheClientDropsBlankText()
    {
        var inner = new Recording();
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "SYSTEM"), new(ChatRole.User, "a real question"), new(ChatRole.Assistant, "answer"), new(ChatRole.User, "  \n")
        };

        await new PromptCachingChatClient(inner).GetResponseAsync(history, PromptCachingChatClient.WithPromptCaching(new ChatOptions { Tools = [Tool("lookup")] }));

        // The blank final message cannot carry one; the real question before it is the anchor.
        Assert.Equal(new[] { true, true, false, false }, inner.Messages.Select(Marked).ToArray());
    }

    [Fact]
    public async Task ACopyDoesNotKeepTheRawBlockItCameFrom_WhichTheClientWouldSendInsteadOfTheMarker()
    {
        var inner = new Recording();
        var question = new TextContent("hi") { RawRepresentation = new object() };
        var history = new List<ChatMessage> { new(ChatRole.System, "SYSTEM"), new(ChatRole.User, new List<AIContent> { question }) };

        await new PromptCachingChatClient(inner).GetResponseAsync(history, PromptCachingChatClient.WithPromptCaching(new ChatOptions { Tools = [Tool("lookup")] }));

        Assert.Null(inner.Messages[1].Contents.Single().RawRepresentation);
        Assert.NotNull(question.RawRepresentation);
    }

    [Fact]
    public async Task NotAskedFor_PassesTheRequestOnAsItIs()
    {
        var inner = new Recording();
        var history = Conversation(out _);
        var options = new ChatOptions { Tools = [Tool("last")], AdditionalProperties = new AdditionalPropertiesDictionary { ["x"] = 1 } };

        await new PromptCachingChatClient(inner).GetResponseAsync(history, options);

        Assert.Same(options, inner.Options);
        Assert.All(inner.Messages, m => Assert.False(Marked(m)));
        Assert.False(inner.Options!.Tools![0].AdditionalProperties.ContainsKey(ToolBreakpointKey));
    }

    [Fact]
    public async Task ASwitchSetToFalse_IsRemovedAndMarksNothing()
    {
        var inner = new Recording();
        var options = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [PromptCachingChatClient.OptInKey] = false, ["x"] = 1 } };

        await new PromptCachingChatClient(inner).GetResponseAsync(Conversation(out _), options);

        Assert.All(inner.Messages, m => Assert.False(Marked(m)));
        Assert.Equal(new[] { "x" }, inner.Options!.AdditionalProperties!.Keys.ToArray());
    }

    // ---- Through the real Anthropic client, and a real tool loop ----

    private sealed class Server(params string[] replies) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = replies[Math.Min(Requests.Count, replies.Length) - 1];
            var streaming = Requests[^1].Contains("\"stream\":true", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }

    private static IChatClient Anthropic(Server server) => AIClientFactory.CreateClient(new AIConfiguration
    {
        Provider = "anthropic",
        ApiKey = "sk-ant-test",
        ModelId = "claude-sonnet-5-5",
        Endpoint = "https://example.invalid",
        Http = new HttpClient(server)
    })!;

    private const string ToolUseReply = """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{"type":"tool_use","id":"toolu_1","name":"lookup","input":{}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":2000,"cache_read_input_tokens":0}}""";
    private const string AnswerReply = """{"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":20,"output_tokens":7,"cache_creation_input_tokens":100,"cache_read_input_tokens":2000}}""";

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public async Task ATwoRoundToolLoop_SendsFourBreakpointsAtMost_NeverTheSwitch_AndSumsTheCacheNumbers()
    {
        var server = new Server(ToolUseReply, AnswerReply);
        var client = new ChatClientBuilder(Anthropic(server)).UseFunctionInvocation().Build();
        var options = PromptCachingChatClient.WithPromptCaching(new ChatOptions { Tools = [Tool("lookup")] });

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "SYSTEM"), new ChatMessage(ChatRole.User, "look it up")], options);

        Assert.Equal(2, server.Requests.Count);
        // Round one: tools, system, and the question that ends it. Round two adds the tool's result, with the question now the anchor.
        Assert.Equal(3, Count(server.Requests[0], "\"cache_control\""));
        Assert.Equal(4, Count(server.Requests[1], "\"cache_control\""));
        Assert.Contains("\"tool_result\"", server.Requests[1]);
        Assert.All(server.Requests, body => Assert.DoesNotContain(PromptCachingChatClient.OptInKey, body));

        // The input the client reports is everything sent: what was read from the cache and what was written to it are inside it, not extra.
        Assert.Equal(10 + 2000 + 0 + 20 + 100 + 2000, response.Usage!.InputTokenCount);
        Assert.Equal(2000, response.Usage.CachedInputTokenCount);
        Assert.Equal(2100, response.Usage.AdditionalCounts!["CacheCreationInputTokens"]);
    }

    [Fact]
    public async Task AChatThatDidNotAsk_SendsNoBreakpoint()
    {
        var server = new Server(AnswerReply);

        await Anthropic(server).GetResponseAsync([new ChatMessage(ChatRole.System, "SYSTEM"), new ChatMessage(ChatRole.User, "hi")], new ChatOptions { Tools = [Tool("lookup")] });

        Assert.DoesNotContain("cache_control", server.Requests.Single());
    }

    [Fact]
    public async Task AStreamedTurn_ReportsTheSameCacheNumbers_OnceEach()
    {
        const string stream =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5-5\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":100,\"output_tokens\":1,\"cache_creation_input_tokens\":2000,\"cache_read_input_tokens\":30000}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":7}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        var client = Anthropic(new Server(stream));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], PromptCachingChatClient.WithPromptCaching(new ChatOptions()))) updates.Add(update);
        var usage = updates.ToChatResponse().Usage!;

        Assert.Equal(32_100, usage.InputTokenCount);
        Assert.Equal(30_000, usage.CachedInputTokenCount);
        Assert.Equal(2_000, usage.AdditionalCounts!["CacheCreationInputTokens"]);
        Assert.Equal(7, usage.OutputTokenCount);
    }
}
