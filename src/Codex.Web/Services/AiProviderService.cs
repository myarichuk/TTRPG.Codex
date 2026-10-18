using Codex.Core.AI;
using Codex.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;

namespace Codex.Web.Services;

/// <summary>Thrown by AI features when the user has no working provider; the UI turns it into a link to the settings page.</summary>
public sealed class AiNotConfiguredException() : InvalidOperationException("No AI provider is set up.");

/// <summary>
/// Which chat model a user's AI buttons talk to. Each user keeps their own providers (and keys,
/// protected with Data Protection) on their <see cref="UserDocument"/>; the server-wide
/// <c>AI</c> configuration section is the fallback when they have none. Nothing is ever sent to
/// a model unless someone clicks an AI button.
/// </summary>
public sealed class AiProviderService(
    IDataProtectionProvider dataProtection,
    IUserRepository users,
    AIConfiguration serverConfig,
    ILogger<AiProviderService> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Codex.AiProviderKeys.v1");
    private readonly Lazy<IChatClient?> _serverClient = new(() => AIClientFactory.CreateClient(serverConfig));

    public bool ServerConfigured => _serverClient.Value != null;

    public string ServerDescription => ServerConfigured
        ? $"{AIClientFactory.Preset(serverConfig.Provider)?.Label ?? serverConfig.Provider} · {serverConfig.ModelId}"
        : "not configured";

    public string Protect(string apiKey) => string.IsNullOrEmpty(apiKey) ? string.Empty : _protector.Protect(apiKey);

    public string Unprotect(string protectedKey)
    {
        if (string.IsNullOrEmpty(protectedKey))
        {
            return string.Empty;
        }

        try
        {
            return _protector.Unprotect(protectedKey);
        }
        catch (Exception ex)
        {
            // A key protected under another machine's keyring (restored data dir) can't be read; treat as missing.
            logger.LogWarning(ex, "Could not unprotect a stored AI key; the provider needs its key entered again.");
            return string.Empty;
        }
    }

    public AIConfiguration ToConfiguration(AiProviderConfig provider, string? plainKey = null) => new()
    {
        Provider = provider.Kind,
        Endpoint = provider.BaseUrl,
        ModelId = provider.ModelId,
        ApiKey = plainKey ?? Unprotect(provider.ProtectedApiKey)
    };

    /// <summary>The provider a user's AI actions run on, or null when neither they nor the server have one.</summary>
    public AiProviderConfig? DefaultProvider(UserDocument? user)
    {
        if (user == null || user.AiProviders.Count == 0)
        {
            return null;
        }

        return user.AiProviders.FirstOrDefault(p => p.Id == user.DefaultAiProviderId) ?? user.AiProviders[0];
    }

    public IChatClient? ClientFor(UserDocument? user)
    {
        var provider = DefaultProvider(user);
        if (provider != null)
        {
            var client = AIClientFactory.CreateClient(ToConfiguration(provider));
            if (client != null)
            {
                return client;
            }

            logger.LogWarning("User {UserId}'s default AI provider {Provider} is incomplete; falling back to the server configuration.", user!.Id, provider.Name);
        }

        return _serverClient.Value;
    }

    public string? Describe(UserDocument? user)
    {
        var provider = DefaultProvider(user);
        if (provider != null)
        {
            return $"{provider.Name} · {provider.ModelId}";
        }

        return ServerConfigured ? "Server default · " + serverConfig.ModelId : null;
    }

    public async Task<AiAssistant> AssistantForAsync(string userId)
    {
        var user = await users.GetUserByIdAsync(userId);
        var client = ClientFor(user) ?? throw new AiNotConfiguredException();
        return new AiAssistant(client);
    }

    public async Task<bool> IsConfiguredForAsync(string userId) => ClientFor(await users.GetUserByIdAsync(userId)) != null;

    /// <summary>Sends one tiny prompt so the settings page can confirm a key and model work.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(AiProviderConfig provider, string? plainKey, CancellationToken ct = default)
    {
        var client = AIClientFactory.CreateClient(ToConfiguration(provider, plainKey));
        if (client == null)
        {
            return (false, "The provider needs an endpoint, a model and (for hosted services) an API key.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word OK.")],
                new ChatOptions { MaxOutputTokens = 5 },
                timeout.Token);
            var text = response.Text?.Trim() ?? string.Empty;
            return (true, text.Length == 0 ? "Connected (empty reply)." : $"Connected. The model said: “{(text.Length > 40 ? text[..40] + "…" : text)}”");
        }
        catch (Exception ex)
        {
            return (false, "Could not reach the model: " + ex.Message);
        }
    }
}
