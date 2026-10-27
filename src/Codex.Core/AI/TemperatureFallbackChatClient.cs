using System.ClientModel;
using Microsoft.Extensions.AI;

namespace Codex.Core.AI;

/// <summary>
/// Sends <see cref="ChatOptions.Temperature"/> as asked, and stops sending it once the server
/// refuses it. Newer models (Claude's among them) reject the parameter outright with a 400
/// instead of ignoring it; callers keep their per-feature temperatures and this decides, per
/// endpoint, whether they can be honoured. The verdict is kept for the life of the client, so
/// only the first request pays for the failed attempt.
/// </summary>
internal sealed class TemperatureFallbackChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private volatile bool rejected;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Materialised once: a lazy sequence must not be enumerated by two requests.
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        if (rejected || options?.Temperature is null)
        {
            return await base.GetResponseAsync(history, Without(options), cancellationToken);
        }

        try
        {
            return await base.GetResponseAsync(history, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && RefusesTemperature(ex))
        {
            rejected = true;
            return await base.GetResponseAsync(history, Without(options), cancellationToken);
        }
    }

    // A stream cannot be replayed once it has started, so it only benefits from a verdict reached earlier.
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, rejected ? Without(options) : options, cancellationToken);

    private ChatOptions? Without(ChatOptions? options)
    {
        if (!rejected || options?.Temperature is null)
        {
            return options;
        }

        var clone = options.Clone();
        clone.Temperature = null;
        return clone;
    }

    /// <summary>True for a service error that names <c>temperature</c> ("HTTP 400 ... `temperature` is deprecated for this model").
    /// The message, not the status, is what tells it apart: servers differ in how they word it, but they all say which parameter.</summary>
    internal static bool RefusesTemperature(Exception ex) =>
        ex is ClientResultException && ex.Message.Contains("temperature", StringComparison.OrdinalIgnoreCase);
}
