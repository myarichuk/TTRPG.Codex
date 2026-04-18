using Microsoft.Extensions.AI;
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

        string prompt = $"Based on the following session recap, generate a short descriptive lore entry for the entity named '{entityName}'.\n\nSession Recap:\n{sessionRecap}\n\nLore Entry:";

        var response = await _chatClient.GetResponseAsync(prompt, null, cancellationToken);
        return response.ToString() ?? "Failed to generate lore.";
    }

    public async Task<string> SummarizeSessionAsync(string sessionEvents, CancellationToken cancellationToken = default)
    {
        if (_chatClient == null)
        {
            return "AI integration is not configured.";
        }

        string prompt = $"Summarize the following session events into a brief recap suitable for players.\n\nSession Events:\n{sessionEvents}\n\nSession Recap:";

        var response = await _chatClient.GetResponseAsync(prompt, null, cancellationToken);
        return response.ToString() ?? "Failed to summarize session.";
    }
}
