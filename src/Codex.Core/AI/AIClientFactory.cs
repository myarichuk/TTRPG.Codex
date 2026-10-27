using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Codex.Core.AI;

/// <summary>
/// Builds an <see cref="IChatClient"/> for any endpoint that speaks the OpenAI chat-completions
/// protocol. Presets only fill in defaults (OpenRouter's base URL, Ollama's dummy key); the wire
/// format is the same for all of them.
/// </summary>
public static class AIClientFactory
{
    public const string OpenRouterEndpoint = "https://openrouter.ai/api/v1";
    public const string OpenAIEndpoint = "https://api.openai.com/v1";
    public const string OllamaEndpoint = "http://localhost:11434/v1";

    /// <summary>The providers the settings page offers, with the defaults each one needs.</summary>
    public static readonly IReadOnlyList<AiProviderPreset> Presets = new[]
    {
        new AiProviderPreset("openrouter", "OpenRouter", OpenRouterEndpoint, "openai/gpt-4o-mini", true, "One key, hundreds of models. Get a key at openrouter.ai/keys."),
        new AiProviderPreset("openai", "OpenAI", OpenAIEndpoint, "gpt-4o-mini", true, "Direct OpenAI access with your own key."),
        new AiProviderPreset("ollama", "Ollama (local)", OllamaEndpoint, "llama3.1", false, "A model running on your own machine. No key needed."),
        new AiProviderPreset("lmstudio", "LM Studio (local)", "http://localhost:1234/v1", "local-model", false, "LM Studio's local server. No key needed."),
        new AiProviderPreset("groq", "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true, "Fast hosted open models."),
        new AiProviderPreset("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", true, "Mistral's hosted models."),
        new AiProviderPreset("custom", "Custom (OpenAI-compatible)", string.Empty, string.Empty, false, "Any server that implements /v1/chat/completions.")
    };

    public static AiProviderPreset? Preset(string? kind) =>
        Presets.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase));

    public static IChatClient? CreateClient(AIConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Provider) || string.IsNullOrWhiteSpace(config.ModelId))
        {
            return null;
        }

        var kind = config.Provider.Trim().ToLowerInvariant();
        var preset = Preset(kind);
        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? preset?.DefaultEndpoint : config.Endpoint.Trim();
        var key = config.ApiKey?.Trim();

        switch (kind)
        {
            case "openai":
                if (string.IsNullOrEmpty(key)) return null;
                break;
            case "ollama":
                if (string.IsNullOrEmpty(endpoint)) return null;
                key = string.IsNullOrEmpty(key) ? "ollama" : key;
                break;
            default:
                if (string.IsNullOrEmpty(endpoint)) return null;
                if (preset is { RequiresKey: true } && string.IsNullOrEmpty(key)) return null;
                key = string.IsNullOrEmpty(key) ? "none" : key;
                break;
        }

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return null;
            }

            options.Endpoint = uri;
        }

        if (kind == "openrouter")
        {
            // OpenRouter asks callers to identify the app; both headers are optional but polite.
            options.AddPolicy(new HeaderPolicy(("HTTP-Referer", "https://github.com/myarichuk/TTRPG.Codex"), ("X-Title", "TTRPG.Codex")), PipelinePosition.PerCall);
        }

        var client = new OpenAIClient(new ApiKeyCredential(key!), options);
        return new TemperatureFallbackChatClient(client.GetChatClient(config.ModelId.Trim()).AsIChatClient());
    }

    private sealed class HeaderPolicy(params (string Name, string Value)[] headers) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void Apply(PipelineMessage message)
        {
            foreach (var (name, value) in headers)
            {
                message.Request.Headers.Set(name, value);
            }
        }
    }
}

/// <summary>A provider the settings page can set up with one click.</summary>
public sealed record AiProviderPreset(string Kind, string Label, string DefaultEndpoint, string DefaultModel, bool RequiresKey, string Hint);
