using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Codex.Core.AI;

/// <summary>
/// Puts Anthropic prompt-cache breakpoints on a request, for a caller that asked for them with
/// <see cref="WithPromptCaching"/>. Only the multi-round, tool-using chats ask: each round sends the tools, the system prompt and
/// the whole history again, and the cache turns that repeat into a read at a fraction of the price. A one-shot call would only pay
/// the write premium for an entry nothing reads, so nothing is marked unless the caller opted in.
/// <para>Up to four breakpoints, the API's limit, each at the END of its block (the cache covers everything before it, in the order
/// tools, system, messages): the last tool; the last system block; the last plain user message before the final one (where the
/// previous turn's request left its own breakpoint, so a new turn can read the conversation so far even when the turn between ran
/// past the 20-block lookback); and the final message.</para>
/// <para>Nothing the caller passed in is touched. The history a chat keeps and stores is shared with the messages sent, and a
/// marker left on a stored message would pile up round after round until the request carries more than four and is refused, so a
/// message that gets a marker is copied first, with its own list and its own marked block.</para>
/// </summary>
public sealed class PromptCachingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <summary>The <see cref="ChatOptions.AdditionalProperties"/> key that turns caching on for one request. Removed before the request
    /// goes on, so it never reaches the wire.</summary>
    public const string OptInKey = "codex.promptCache";

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (sent, chosen) = Prepare(messages, options);
        return base.GetResponseAsync(sent, chosen, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (sent, chosen) = Prepare(messages, options);
        return base.GetStreamingResponseAsync(sent, chosen, cancellationToken);
    }

    /// <summary>Asks for the cache breakpoints on this request.</summary>
    public static ChatOptions WithPromptCaching(ChatOptions options)
    {
        (options.AdditionalProperties ??= new AdditionalPropertiesDictionary())[OptInKey] = true;
        return options;
    }

    private static (IEnumerable<ChatMessage> Messages, ChatOptions? Options) Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } props || !props.TryGetValue(OptInKey, out var asked))
        {
            return (messages, options);
        }

        // The key is ours: it goes no further, whether or not it was a yes.
        var chosen = options.Clone();
        var rest = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in props)
        {
            if (key != OptInKey) rest[key] = value;
        }

        chosen.AdditionalProperties = rest.Count == 0 ? null : rest;
        if (asked is not true)
        {
            return (messages, chosen);
        }

        var history = messages as IList<ChatMessage> ?? messages.ToList();
        if (!WorthMarking(history, options.Tools))
        {
            return (history, chosen);
        }

        chosen.Tools = MarkLastTool(options.Tools);
        return (MarkMessages(history), chosen);
    }

    // Not every asked-for request should get breakpoints.
    //  - The tool loop's last round takes the tools away (to make the model answer). Tools come first in the cached prefix, so
    //    a request without them matches nothing written before; marking it would only pay the write premium, on the largest
    //    request of the turn.
    //  - A caller that put breakpoints on something itself is managing them: adding ours could pass the four a request may carry.
    private static bool WorthMarking(IList<ChatMessage> messages, IList<AITool>? tools)
    {
        if (tools is not { Count: > 0 } && messages.Any(m => m.Contents.Any(c => c is FunctionCallContent or FunctionResultContent)))
        {
            return false;
        }

        return !(tools?.Any(t => t.AdditionalProperties.ContainsKey(nameof(Tool.CacheControl))) ?? false)
            && !messages.Any(m => m.Contents.Any(c => c.AdditionalProperties?.ContainsKey(BlockBreakpointKey) == true));
    }

    // Where the Anthropic client's AIContent extension keeps a block's breakpoint.
    private const string BlockBreakpointKey = "anthropic:cache_control";

    // The last tool that is a function: a breakpoint there caches every tool definition, which stay the same while the system prompt
    // and the history change.
    private static IList<AITool>? MarkLastTool(IList<AITool>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return tools;
        }

        var last = -1;
        for (var i = tools.Count - 1; i >= 0 && last < 0; i--)
        {
            if (tools[i] is AIFunction) last = i;
        }

        if (last < 0 || tools[last].AdditionalProperties.ContainsKey(nameof(Tool.CacheControl)))
        {
            return tools;
        }

        var marked = new List<AITool>(tools) { [last] = new CachedTool((AIFunction)tools[last]) };
        return marked;
    }

    private static IList<ChatMessage> MarkMessages(IList<ChatMessage> messages)
    {
        // The system prompt is the leading run of system messages; the breakpoint goes on the last of them.
        var lead = 0;
        while (lead < messages.Count && messages[lead].Role == ChatRole.System) lead++;

        var marks = new SortedSet<int>();
        if (lead > 0) marks.Add(lead - 1);
        var tail = messages.Count - 1;
        if (tail >= lead) marks.Add(tail);
        for (var i = tail - 1; i >= lead; i--)
        {
            if (IsPlainUser(messages[i]))
            {
                marks.Add(i);
                break;
            }
        }

        var sent = new List<ChatMessage>(messages);
        foreach (var index in marks)
        {
            sent[index] = Marked(messages[index]);
        }

        return sent;
    }

    // What the person typed, as opposed to the tool results that also travel as a user turn.
    private static bool IsPlainUser(ChatMessage message) =>
        message.Role == ChatRole.User && message.Contents.Any(c => c is TextContent { Text: var t } && !string.IsNullOrWhiteSpace(t)) && !message.Contents.Any(c => c is FunctionResultContent);

    // A copy of the message whose last block that can take a breakpoint carries one; the message itself when none can (thinking blocks
    // and blank text are not cacheable: the client drops blank text altogether). The copy leaves out RawRepresentation: the client
    // sends a block that carries its own SDK block as that block, which would ignore the marker.
    private static ChatMessage Marked(ChatMessage message)
    {
        for (var i = message.Contents.Count - 1; i >= 0; i--)
        {
            AIContent? copy = message.Contents[i] switch
            {
                TextContent { Text: var words } text when !string.IsNullOrWhiteSpace(words) => new TextContent(words) { Annotations = text.Annotations, AdditionalProperties = text.AdditionalProperties?.Clone() },
                FunctionResultContent result => new FunctionResultContent(result.CallId, result.Result) { Exception = result.Exception, Annotations = result.Annotations, AdditionalProperties = result.AdditionalProperties?.Clone() },
                _ => null
            };

            if (copy == null)
            {
                continue;
            }

            copy.WithCacheControl(new CacheControlEphemeral());
            var contents = new List<AIContent>(message.Contents) { [i] = copy };
            var marked = message.Clone();
            marked.Contents = contents;
            return marked;
        }

        return message;
    }

    // The same function, with the breakpoint the Anthropic client reads from a tool's additional properties.
    private sealed class CachedTool(AIFunction inner) : DelegatingAIFunction(inner)
    {
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(inner.AdditionalProperties) { [nameof(Tool.CacheControl)] = new CacheControlEphemeral() };
    }
}
