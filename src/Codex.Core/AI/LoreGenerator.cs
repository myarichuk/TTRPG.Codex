using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Codex.Core.AI;

public class LoreGenerator
{
    private readonly IChatClient? _chatClient;

    public LoreGenerator(IChatClient? chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<string> GenerateLoreAsync(string sessionRecap, string entityName, CancellationToken cancellationToken = default)
    {
        if (_chatClient == null)
        {
            return "AI integration is not configured.";
        }

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "Based on the provided session recap and entity name, generate a short descriptive lore entry for the specified entity. Provide only the lore entry."),
            new ChatMessage(ChatRole.User, $"Entity Name:\n{entityName}\n\nSession Recap:\n{sessionRecap}")
        };

        var response = await _chatClient.GetResponseAsync(messages, null, cancellationToken);
        return response.ToString() ?? "Failed to generate lore.";
    }

    public async Task<string> SummarizeSessionAsync(string sessionEvents, CancellationToken cancellationToken = default)
    {
        if (_chatClient == null)
        {
            return "AI integration is not configured.";
        }

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "Summarize the following session events into a brief recap suitable for players. Provide only the summary."),
            new ChatMessage(ChatRole.User, $"Session Events:\n{sessionEvents}")
        };

        var response = await _chatClient.GetResponseAsync(messages, null, cancellationToken);
        return response.ToString() ?? "Failed to summarize session.";
    }
}
