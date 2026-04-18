using Microsoft.Extensions.AI;
using OpenAI;
using System;
using System.ClientModel;

namespace Codex.Core.AI;

public static class AIClientFactory
{
    public static IChatClient? CreateClient(AIConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Provider))
        {
            return null;
        }

        switch (config.Provider.ToLowerInvariant())
        {
            case "openai":
                if (!string.IsNullOrEmpty(config.ApiKey) && !string.IsNullOrEmpty(config.ModelId))
                {
                    var openAiClient = new OpenAIClient(new ApiKeyCredential(config.ApiKey));
                    return openAiClient.GetChatClient(config.ModelId).AsIChatClient();
                }
                break;
            case "ollama":
                // For a locally hosted model compatible with standard endpoint
                if (!string.IsNullOrEmpty(config.Endpoint) && !string.IsNullOrEmpty(config.ModelId))
                {
                    var ollamaClient = new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions { Endpoint = new Uri(config.Endpoint) });
                    return ollamaClient.GetChatClient(config.ModelId).AsIChatClient();
                }
                break;
        }

        return null;
    }
}
