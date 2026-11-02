using System.Text.Json;
using Codex.Persistence;
using Microsoft.Extensions.AI;

namespace Codex.Web.Services;

// The world-building chat's history as the database keeps it, and back.
public sealed partial class WorldChat
{
    /// <summary>A message as the database keeps it: its words, its tool calls and its tool results. Reasoning is not kept (the
    /// model does not need it back), so a message that held only reasoning is not kept at all.</summary>
    internal static WorldChatMessage? ToStored(ChatMessage message)
    {
        var text = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
        var calls = message.Contents.OfType<FunctionCallContent>()
            .Select(c => new WorldChatCall { Id = c.CallId, Name = c.Name, Arguments = JsonSerializer.Serialize(c.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions) })
            .ToList();
        var results = message.Contents.OfType<FunctionResultContent>()
            .Select(r => new WorldChatResult { CallId = r.CallId, Result = ResultText(r.Result) })
            .ToList();
        if (text.Length == 0 && calls.Count == 0 && results.Count == 0) return null;
        return new WorldChatMessage
        {
            Role = message.Role.Value,
            Text = text.Length == 0 ? null : text,
            Calls = calls.Count == 0 ? null : calls,
            Results = results.Count == 0 ? null : results
        };
    }

    // A tool's text comes back from the function wrapped as a JSON string; stored as JSON it would reach the model again quoted and
    // escaped, not as the text it read the first time.
    private static string ResultText(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions)
    };

    internal static ChatMessage FromStored(WorldChatMessage stored)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(stored.Text)) contents.Add(new TextContent(stored.Text));
        foreach (var call in stored.Calls ?? new List<WorldChatCall>())
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments, AIJsonUtilities.DefaultOptions);
            contents.Add(new FunctionCallContent(call.Id, call.Name, arguments));
        }

        foreach (var result in stored.Results ?? new List<WorldChatResult>())
        {
            contents.Add(new FunctionResultContent(result.CallId, result.Result));
        }

        return new ChatMessage(new ChatRole(stored.Role), contents);
    }
}
