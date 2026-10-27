using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using Anthropic.Exceptions;
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

    // A stream cannot be replayed once an update has gone out, but a refusal arrives before the first one: the request is
    // rejected as a whole, so that is the window in which to try again without the temperature.
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var (source, any) = await OpenAsync(history, options, cancellationToken);
        await using (source)
        {
            if (!any)
            {
                yield break;
            }

            yield return source.Current;
            while (await source.MoveNextAsync())
            {
                yield return source.Current;
            }
        }
    }

    private async Task<(IAsyncEnumerator<ChatResponseUpdate> Source, bool Any)> OpenAsync(IList<ChatMessage> history, ChatOptions? options, CancellationToken cancellationToken)
    {
        var source = base.GetStreamingResponseAsync(history, Without(options), cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            return (source, await source.MoveNextAsync());
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !rejected && options?.Temperature is not null && RefusesTemperature(ex))
        {
            await source.DisposeAsync();
            rejected = true;
            var again = base.GetStreamingResponseAsync(history, Without(options), cancellationToken).GetAsyncEnumerator(cancellationToken);
            return (again, await again.MoveNextAsync());
        }
    }

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
    /// The message, not the status, is what tells it apart on the OpenAI side: servers differ in how they word it, but they all
    /// say which parameter. Anthropic's SDK keeps the API's error body apart from the message, so that is read too.
    /// (Never <c>ex.ToString()</c>: the stack trace names this class.)</summary>
    internal static bool RefusesTemperature(Exception ex) => ex switch
    {
        ClientResultException => Mentions(ex.Message),
        AnthropicApiException api => api.StatusCode == HttpStatusCode.BadRequest && (Mentions(api.Message) || Mentions(api.ResponseBody)),
        _ => false
    };

    private static bool Mentions(string? text) => text?.Contains("temperature", StringComparison.OrdinalIgnoreCase) == true;
}
