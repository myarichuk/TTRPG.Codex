using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Codex.Core.AI;
using Codex.Localization;
using Codex.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;

namespace Codex.Web.Services;

/// <summary>Thrown by AI features when the user has no working provider; the UI turns it into a link to the settings page.</summary>
public sealed class AiNotConfiguredException() : LocalizedException("Service.Ai.NotConfigured");

/// <summary>Thrown by art generation when the user has no image model; chat providers don't count.</summary>
public sealed class AiImageNotConfiguredException() : LocalizedException("Service.Ai.ImageNotConfigured");

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
    AIImageConfiguration serverImageConfig,
    IHttpClientFactory httpClients,
    ILogger<AiProviderService> logger,
    IConfiguration configuration,
    ServerAiUsage usage)
{
    // While the server is open to the internet only its admin may use the key in the server's own configuration.
    private bool ServerKeyAllowedFor(UserDocument? user) => ExposurePolicy.MayUseServerAi(ExposurePolicy.IsExposed(configuration), user);

    // A stranger's own provider must not aim at this computer or its network while the server is on the internet.
    // The server's admin owns this computer and may use whatever runs on it.
    private bool Confined(UserDocument? user) =>
        ExposurePolicy.IsExposed(configuration) && user?.Roles.Contains(ExposurePolicy.ServerAdminRole) != true;

    private bool Blocked(AiProviderConfig provider, UserDocument? user) =>
        Confined(user) && ExposurePolicy.IsInternalEndpoint(provider.BaseUrl);

    private static bool IsAdmin(UserDocument? user) => user?.Roles.Contains(ExposurePolicy.ServerAdminRole) == true;

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Codex.AiProviderKeys.v1");
    private readonly Lazy<IChatClient?> _serverClient = new(() => AIClientFactory.CreateClient(serverConfig));

    // One client per provider setup, kept for the life of the server: a client learns things about its model (that it refuses a
    // temperature, say) which a fresh one would have to find out again with a failed request. The key's hash is part of what
    // names a setup, so a changed key, address or model gets a client of its own and the old one is never used again.
    private const int MaxClients = 128;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();

    private IChatClient? CachedClient(AIConfiguration config, bool confined)
    {
        var name = string.Join('\n', config.Provider, config.Endpoint?.Trim(), config.ModelId?.Trim(), confined,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.ApiKey ?? string.Empty))));
        if (_clients.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var client = AIClientFactory.CreateClient(config);
        if (client == null)
        {
            return null;
        }

        // Setups people tried and left behind pile up; starting over now and then costs one relearned refusal each.
        if (_clients.Count >= MaxClients)
        {
            _clients.Clear();
        }

        return _clients.GetOrAdd(name, client);
    }

    public bool ServerConfigured => _serverClient.Value != null;

    public string ServerDescription => ServerConfigured
        ? $"{AIClientFactory.Preset(serverConfig.Provider)?.Label ?? serverConfig.Provider} · {serverConfig.ModelId}"
        : "not configured";

    // Each user's keys are protected under a purpose of their own, so a blob copied onto another account
    // (an export, a hand-edited document) does not decrypt there. Keys stored before this was so need entering again.
    private IDataProtector ProtectorFor(string userId) => _protector.CreateProtector(userId);

    public string Protect(string userId, string apiKey) => string.IsNullOrEmpty(apiKey) ? string.Empty : ProtectorFor(userId).Protect(apiKey);

    public string Unprotect(string userId, string protectedKey)
    {
        if (TryUnprotect(userId, protectedKey, out var plain, out var error))
        {
            return plain;
        }

        if (error != null)
        {
            // A key from another keyring (data restored elsewhere), another account, or from before keys were bound to
            // their user can't be read; treat it as missing.
            logger.LogWarning(error, "Could not unprotect a stored AI key; the provider needs its key entered again.");
        }

        return string.Empty;
    }

    /// <summary>True when <paramref name="protectedKey"/> is stored but cannot be read for this user any more, so the
    /// settings page can ask for it again instead of saying "key stored".</summary>
    public bool NeedsReentry(string userId, string protectedKey) =>
        !string.IsNullOrEmpty(protectedKey) && !TryUnprotect(userId, protectedKey, out _, out _);

    private bool TryUnprotect(string userId, string protectedKey, out string plain, out Exception? error)
    {
        plain = string.Empty;
        error = null;
        if (string.IsNullOrEmpty(protectedKey))
        {
            return false;
        }

        try
        {
            plain = ProtectorFor(userId).Unprotect(protectedKey);
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    public AIConfiguration ToConfiguration(AiProviderConfig provider, UserDocument? user, string? plainKey = null, bool confined = false) => new()
    {
        Http = confined ? ExposurePolicy.PublicOnlyClient : null,
        Provider = provider.Kind,
        Endpoint = provider.BaseUrl,
        ModelId = provider.ModelId,
        ApiKey = plainKey ?? (user == null ? string.Empty : Unprotect(user.Id, provider.ProtectedApiKey))
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

    public IChatClient? ClientFor(UserDocument? user, ServerAiUsage.Reservation? prepaid = null)
    {
        // The server's key is counted per account, so it is never handed to a session whose account is gone.
        if (user == null) return null;

        var provider = DefaultProvider(user);
        if (provider != null && !Blocked(provider, user))
        {
            var setup = ToConfiguration(provider, user, null, Confined(user));
            var client = CachedClient(setup, Confined(user));
            if (client != null)
            {
                return client;
            }

            // A key stored but no longer readable (from before keys were bound to their user, or another keyring) is the
            // user's to enter again, as AI settings says; spending the server's shared allowance meanwhile would hide that.
            if (!string.IsNullOrEmpty(provider.ProtectedApiKey) && string.IsNullOrEmpty(setup.ApiKey))
            {
                logger.LogWarning("User {UserId}'s AI key for {Provider} can no longer be read; they need to enter it again.", user.Id, provider.Name);
                return null;
            }

            logger.LogWarning("User {UserId}'s default AI provider {Provider} is incomplete; falling back to the server configuration.", user.Id, provider.Name);
        }

        return ServerKeyAllowedFor(user) && _serverClient.Value is { } server ? new MeteredClient(server, usage, user.Id, IsAdmin(user), prepaid) : null;
    }

    /// <summary>True when this user's AI calls go to the server's own key (and so count against their daily allowance).</summary>
    public bool UsesServerKey(UserDocument? user) => ClientFor(user) is MeteredClient;

    public string? Describe(UserDocument? user)
    {
        var provider = DefaultProvider(user);
        if (provider != null)
        {
            return $"{provider.Name} · {provider.ModelId}";
        }

        return ServerConfigured && ServerKeyAllowedFor(user) ? "Server default · " + serverConfig.ModelId : null;
    }

    public async Task<AiAssistant> AssistantForAsync(string userId)
    {
        var user = await users.GetUserByIdAsync(userId);
        var client = ClientFor(user) ?? throw new AiNotConfiguredException();
        return new AiAssistant(client);
    }

    /// <summary>The user's text model itself, for a conversation that calls tools (the world-building chat).</summary>
    public async Task<IChatClient> ChatClientForAsync(string userId) =>
        ClientFor(await users.GetUserByIdAsync(userId)) ?? throw new AiNotConfiguredException();

    /// <summary>Sets aside <paramref name="requests"/> on the server's key for a job that will make many calls, or returns null when
    /// the user's calls go to their own provider (nothing of the server's to spend). Throws when they do not fit in today's
    /// allowance. Hand the result to <see cref="ClientFor"/> and dispose it when the job ends.</summary>
    public async Task<ServerAiUsage.Reservation?> ReserveRequestsAsync(string userId, int requests)
    {
        var user = await users.GetUserByIdAsync(userId) ?? throw new AiNotConfiguredException();
        var provider = DefaultProvider(user);
        if (provider != null && !Blocked(provider, user) && ClientFor(user) is { } own && own is not MeteredClient)
        {
            return null;
        }

        if (!ServerKeyAllowedFor(user) || !ServerConfigured)
        {
            throw new AiNotConfiguredException();
        }

        return usage.Reserve(user.Id, ServerAiUsage.Kind.Text, requests, IsAdmin(user));
    }

    /// <summary>The prices of the model <see cref="ClientFor"/> would use for this user: their own provider's, or the optional
    /// <c>AI:InputPricePerMTok</c>, <c>AI:OutputPricePerMTok</c> and <c>AI:PriceLabel</c> of the server's key. Null when none are set.</summary>
    public async Task<AiPriceQuote?> PriceForAsync(string userId)
    {
        var user = await users.GetUserByIdAsync(userId);
        var provider = DefaultProvider(user);
        if (provider != null && !Blocked(provider, user) && ToConfigurationSafe(provider, user))
        {
            return provider.InputPricePerMTok == null && provider.OutputPricePerMTok == null ? null
                : new AiPriceQuote(provider.InputPricePerMTok, provider.OutputPricePerMTok, provider.PriceLabel);
        }

        var input = configuration.GetValue<decimal?>("AI:InputPricePerMTok");
        var output = configuration.GetValue<decimal?>("AI:OutputPricePerMTok");
        return input == null && output == null ? null : new AiPriceQuote(input, output, configuration["AI:PriceLabel"] ?? string.Empty);
    }

    // The same test ClientFor applies before it uses the user's own provider: a client can be made from it.
    private bool ToConfigurationSafe(AiProviderConfig provider, UserDocument? user) =>
        user != null && CachedClient(ToConfiguration(provider, user, null, Confined(user)), Confined(user)) is not null;

    public async Task<bool> IsConfiguredForAsync(string userId) => ClientFor(await users.GetUserByIdAsync(userId)) != null;

    public bool ServerImageConfigured => AiImageGenerator.Problem(serverImageConfig) == null;

    public string ServerImageDescription => ServerImageConfigured
        ? $"{AiImageGenerator.Preset(serverImageConfig.Provider)?.Label ?? serverImageConfig.Provider} · {serverImageConfig.ModelId}"
        : "not configured";

    /// <summary>The image model a user's "Generate art" buttons use, or null to fall back to the server.</summary>
    public AiProviderConfig? DefaultImageProvider(UserDocument? user)
    {
        if (user == null || user.ImageProviders.Count == 0)
        {
            return null;
        }

        return user.ImageProviders.FirstOrDefault(p => p.Id == user.DefaultImageProviderId) ?? user.ImageProviders[0];
    }

    private AIConfiguration? ImageConfigFor(UserDocument? user)
    {
        // As for text (ClientFor): no server key for an account that is gone, nor in place of a key that can't be read.
        if (user == null) return null;

        if (DefaultImageProvider(user) is { } provider && !Blocked(provider, user))
        {
            var config = ToConfiguration(provider, user, null, Confined(user));
            if (AiImageGenerator.Problem(config) == null)
            {
                return config;
            }

            if (!string.IsNullOrEmpty(provider.ProtectedApiKey) && string.IsNullOrEmpty(config.ApiKey))
            {
                logger.LogWarning("User {UserId}'s image key for {Provider} can no longer be read; they need to enter it again.", user.Id, provider.Name);
                return null;
            }

            logger.LogWarning("User {UserId}'s image provider {Provider} is incomplete; falling back to the server configuration.", user!.Id, provider.Name);
        }

        return ServerImageConfigured && ServerKeyAllowedFor(user) ? serverImageConfig : null;
    }

    public async Task<bool> IsImageConfiguredForAsync(string userId) => ImageConfigFor(await users.GetUserByIdAsync(userId)) != null;

    public async Task<GeneratedImage> GenerateImageAsync(string userId, string prompt, ArtShape shape, CancellationToken ct = default)
    {
        var user = await users.GetUserByIdAsync(userId);
        var config = ImageConfigFor(user) ?? throw new AiImageNotConfiguredException();
        if (ReferenceEquals(config, serverImageConfig))
        {
            usage.Take(userId, ServerAiUsage.Kind.Image, IsAdmin(user));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var http = config.Http ?? httpClients.CreateClient("ai-images");
        if (config.Http == null) http.Timeout = TimeSpan.FromMinutes(2);
        return await AiImageGenerator.GenerateAsync(config, prompt, shape, http, timeout.Token);
    }

    /// <summary>Sends one tiny prompt so the settings page can confirm a key and model work.</summary>
    /// <remarks>The message is a <see cref="LocalizedException"/> so the page shows it in the viewer's language; it is not thrown.</remarks>
    public async Task<(bool Ok, LocalizedException Message)> TestAsync(AiProviderConfig provider, string? plainKey, UserDocument? user = null, CancellationToken ct = default)
    {
        if (Blocked(provider, user)) return (false, new LocalizedException("Service.Ai.PrivateBlocked"));
        var client = AIClientFactory.CreateClient(ToConfiguration(provider, user, plainKey, Confined(user)));
        if (client == null)
        {
            return (false, new LocalizedException("Service.Ai.NeedsSetup"));
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
            return (true, text.Length == 0
                ? new LocalizedException("Service.Ai.ConnectedEmpty")
                : new LocalizedException("Service.Ai.Connected", text.Length > 40 ? text[..40] + "…" : text));
        }
        catch (Exception ex)
        {
            return (false, new LocalizedException("Service.Ai.Unreachable", ex.Message));
        }
    }

    /// <summary>The server's own chat client, as one account uses it: each request is counted against their daily allowance
    /// first (F14). The client underneath is shared, so this one never disposes it.</summary>
    private sealed class MeteredClient(IChatClient inner, ServerAiUsage usage, string userId, bool unlimited, ServerAiUsage.Reservation? prepaid = null) : DelegatingChatClient(inner)
    {
        // A request that was set aside beforehand is spent from there; one beyond it is counted like anyone's.
        private void Count()
        {
            if (prepaid?.TryUse() != true) usage.Take(userId, ServerAiUsage.Kind.Text, unlimited);
        }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Count();
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Count();
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
