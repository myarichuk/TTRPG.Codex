using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Codex.Core.AI;

/// <summary>
/// Builds an <see cref="IChatClient"/> for any endpoint that speaks the OpenAI chat-completions
/// protocol. Presets only fill in defaults (OpenRouter's base URL, Ollama's dummy key); the wire
/// format is the same for all of them. Anthropic is the exception: it gets its own Messages-API
/// client, picked by <see cref="IsAnthropic"/> from the preset, the address or the key, so a key
/// pasted under "Custom" still ends up on the right protocol.
/// </summary>
public static class AIClientFactory
{
    public const string OpenRouterEndpoint = "https://openrouter.ai/api/v1";
    public const string OpenAIEndpoint = "https://api.openai.com/v1";
    public const string OllamaEndpoint = "http://localhost:11434/v1";
    public const string AnthropicEndpoint = "https://api.anthropic.com";

    /// <summary>Output budget per reply; Anthropic's API insists on one. Room for a world-building proposal with several entries.</summary>
    public const int AnthropicMaxOutputTokens = 8192;

    /// <summary>How long one request may wait on a model. The SDK's 100 seconds is short for a local model reading a long prompt
    /// or a hosted one writing a big proposal; the replies are streamed, so this is the wait for the next piece, not for all of it.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The providers the settings page offers, with the defaults each one needs.</summary>
    public static readonly IReadOnlyList<AiProviderPreset> Presets = new[]
    {
        new AiProviderPreset("openrouter", "OpenRouter", OpenRouterEndpoint, "openai/gpt-4o-mini", true, "One key, hundreds of models. Get a key at openrouter.ai/keys."),
        new AiProviderPreset("openai", "OpenAI", OpenAIEndpoint, "gpt-4o-mini", true, "Direct OpenAI access with your own key."),
        new AiProviderPreset("anthropic", "Anthropic", AnthropicEndpoint, "claude-haiku-5-5", true, "Claude through Anthropic's own API. Get a key at console.anthropic.com."),
        new AiProviderPreset("ollama", "Ollama (local)", OllamaEndpoint, "llama3.1", false, "A model running on your own machine. No key needed."),
        new AiProviderPreset("lmstudio", "LM Studio (local)", "http://localhost:1234/v1", "local-model", false, "LM Studio's local server. No key needed."),
        new AiProviderPreset("groq", "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true, "Fast hosted open models."),
        new AiProviderPreset("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", true, "Mistral's hosted models."),
        new AiProviderPreset("custom", "Custom (OpenAI-compatible)", string.Empty, string.Empty, false, "Any server that implements /v1/chat/completions.")
    };

    public static AiProviderPreset? Preset(string? kind) =>
        Presets.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the settings point at Anthropic: the "anthropic" preset, an anthropic.com address, or, with no
    /// address at all, a key in Anthropic's <c>sk-ant-</c> format. An explicit address elsewhere wins over the key, since
    /// a gateway in front of Claude takes the same key but speaks the OpenAI protocol.</summary>
    public static bool IsAnthropic(string? kind, string? endpoint, string? apiKey)
    {
        if (string.Equals(kind?.Trim(), "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
                && (uri.Host.Equals("anthropic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".anthropic.com", StringComparison.OrdinalIgnoreCase));
        }

        return apiKey?.Trim().StartsWith("sk-ant-", StringComparison.Ordinal) == true;
    }

    /// <summary>The kind to store for what was typed: "anthropic" when <see cref="IsAnthropic"/> says so, else the kind as chosen.</summary>
    public static string DetectKind(string? kind, string? endpoint, string? apiKey) =>
        IsAnthropic(kind, endpoint, apiKey) ? "anthropic" : kind?.Trim() ?? string.Empty;

    public static IChatClient? CreateClient(AIConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Provider) || string.IsNullOrWhiteSpace(config.ModelId))
        {
            return null;
        }

        if (IsAnthropic(config.Provider, config.Endpoint, config.ApiKey))
        {
            return CreateAnthropicClient(config);
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

        var options = new OpenAIClientOptions
        {
            NetworkTimeout = RequestTimeout,
            RetryPolicy = new PatientRetryPolicy()
        };
        if (!string.IsNullOrEmpty(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return null;
            }

            options.Endpoint = uri;
        }

        if (config.Http != null) options.Transport = new HttpClientPipelineTransport(config.Http);

        if (kind == "openrouter")
        {
            // OpenRouter asks callers to identify the app; both headers are optional but polite.
            options.AddPolicy(new HeaderPolicy(("HTTP-Referer", "https://github.com/myarichuk/TTRPG.Codex"), ("X-Title", "TTRPG.Codex")), PipelinePosition.PerCall);
        }

        var client = new OpenAIClient(new ApiKeyCredential(key!), options);
        return new TemperatureFallbackChatClient(client.GetChatClient(config.ModelId.Trim()).AsIChatClient());
    }

    private static IChatClient? CreateAnthropicClient(AIConfiguration config)
    {
        var key = config.ApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? AnthropicEndpoint : config.Endpoint.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // The SDK adds /v1/messages itself; the address people paste from the OpenAI-compatible docs already ends in /v1.
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        var baseUrl = uri.GetLeftPart(UriPartial.Authority) + path;
        var client = config.Http == null
            ? new AnthropicClient { ApiKey = key, BaseUrl = baseUrl, Timeout = RequestTimeout, MaxRetries = 1 }
            : new AnthropicClient { ApiKey = key, BaseUrl = baseUrl, Timeout = RequestTimeout, MaxRetries = 1, HttpClient = config.Http };
        return new TemperatureFallbackChatClient(client.AsIChatClient(config.ModelId!.Trim(), AnthropicMaxOutputTokens));
    }

    /// <summary>The SDK's default retries a request that timed out as readily as one the server turned away, so a model that is
    /// simply slow keeps the person waiting for four timeouts in a row. Timeouts are reported at once; the rest retry as usual (twice).</summary>
    private sealed class PatientRetryPolicy() : ClientRetryPolicy(maxRetries: 2)
    {
        protected override bool ShouldRetry(PipelineMessage message, Exception? exception) =>
            exception is not (OperationCanceledException or TimeoutException) && base.ShouldRetry(message, exception);

        protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) =>
            exception is not (OperationCanceledException or TimeoutException) && await base.ShouldRetryAsync(message, exception);
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
