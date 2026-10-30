using System.Net;
using System.Net.Sockets;
using System.ClientModel;
using System.Text.Json;
using Anthropic.Exceptions;

namespace Codex.Core.AI;

public enum AiFailureKind
{
    /// <summary>Nothing recognisable; <see cref="AiFailure.Detail"/> carries what the provider said.</summary>
    Other,
    /// <summary>The model did not answer in time.</summary>
    Timeout,
    /// <summary>The key was refused (401/403).</summary>
    Auth,
    /// <summary>The address or model is unknown (404).</summary>
    NotFound,
    /// <summary>Too many requests or no credit left (429/402).</summary>
    RateLimit,
    /// <summary>The provider itself failed (5xx, overloaded).</summary>
    Server,
    /// <summary>The provider could not be reached at all.</summary>
    Network
}

/// <summary>
/// What went wrong with an AI call, in terms a person can act on. The SDKs wrap the same failure differently ("Retry failed
/// after 4 tries" around four copies of one timeout, a status buried in a message, a JSON body beside it), so this reads the
/// whole exception chain once and says which of a few things it was. The page turns the kind into words in the reader's language.
/// </summary>
public sealed record AiFailure(AiFailureKind Kind, string Detail)
{
    private const int MaxDetail = 240;

    /// <summary>Classifies a failure. Call it for errors only: a stop the user asked for is not one.</summary>
    public static AiFailure From(Exception ex)
    {
        var chain = Flatten(ex).ToList();

        var status = chain.Select(Status).FirstOrDefault(s => s != 0);
        switch (status)
        {
            case 401 or 403:
                return new AiFailure(AiFailureKind.Auth, DetailOf(chain));
            case 408:
                return new AiFailure(AiFailureKind.Timeout, DetailOf(chain));
            case 404:
                return new AiFailure(AiFailureKind.NotFound, DetailOf(chain));
            case 402 or 429:
                return new AiFailure(AiFailureKind.RateLimit, DetailOf(chain));
            case >= 500:
                return new AiFailure(AiFailureKind.Server, DetailOf(chain));
        }

        // The provider answered, so it was reachable and in time (the SDKs wrap their API errors around an HttpRequestException,
        // which must not read as a network failure): what is left is its own message, a 400 for instance.
        if (status != 0)
        {
            return new AiFailure(AiFailureKind.Other, DetailOf(chain));
        }

        if (chain.Any(e => e is OperationCanceledException or TimeoutException))
        {
            return new AiFailure(AiFailureKind.Timeout, DetailOf(chain));
        }

        if (chain.Any(e => e is HttpRequestException or SocketException or IOException))
        {
            return new AiFailure(AiFailureKind.Network, DetailOf(chain));
        }

        return new AiFailure(AiFailureKind.Other, DetailOf(chain));
    }

    // The exception, what it wraps, and every attempt an AggregateException (a retry policy's) gathered.
    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        var pending = new Stack<Exception>();
        pending.Push(ex);
        var seen = 0;
        while (pending.Count > 0 && seen++ < 32)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Reverse()) pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    private static int Status(Exception ex) => ex switch
    {
        ClientResultException c => c.Status,
        AnthropicApiException a => (int)a.StatusCode,
        HttpRequestException { StatusCode: { } code } => (int)code,
        _ => 0
    };

    // Prefer what the provider said (its JSON error message) over the SDK's wrapping of it, and keep it to one short line.
    private static string DetailOf(List<Exception> chain)
    {
        foreach (var ex in chain)
        {
            var body = ex is AnthropicApiException a ? ProviderMessage(a.ResponseBody) : null;
            if (!string.IsNullOrWhiteSpace(body)) return Shorten(body!);
        }

        // The innermost message is the cause; the outer ones are "Retry failed after 4 tries" and the like.
        var message = chain.Where(e => e is not AggregateException).Select(e => e.Message).LastOrDefault(m => !string.IsNullOrWhiteSpace(m));
        return Shorten(message ?? chain[0].GetType().Name);
    }

    private static string? ProviderMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    // The OpenAI SDK's message is "HTTP 400 (invalid_request_error: )" with the provider's reason on a line of its own below it;
    // the reason is the part a person can act on, so it leads and the status follows.
    private static readonly System.Text.RegularExpressions.Regex StatusLine = new(@"^(HTTP \d{3})\b");

    private static string Shorten(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.Count switch
        {
            0 => text.Trim(),
            > 1 when StatusLine.Match(lines[0]) is { Success: true } status => $"{lines[1]} ({status.Groups[1].Value})",
            _ => lines[0]
        };
        return line.Length <= MaxDetail ? line : line[..MaxDetail].TrimEnd() + "…";
    }
}
