using Codex.Core.AI;
using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Services;
using Microsoft.Extensions.AI;

namespace Codex.Web.Book;

/// <summary>The model a user's book build would run on, as the page shows it before anything is sent.</summary>
/// <param name="Label">"provider · model" as the AI settings name it.</param>
/// <param name="Price">The user's prices, or null when they set none (then no money is shown, never a guessed figure).</param>
/// <param name="ServerUsedToday">On the server's key: requests this account used today, and the daily allowance (0 for none); null on the user's own provider.</param>
public sealed record BookModelInfo(string Label, AiPriceQuote? Price, (int Used, int Limit)? ServerUsedToday);

/// <summary>How many calls a book's runs make at once: one for a model on this machine or this network (Ollama queues requests until they
/// time out), two for a hosted one.</summary>
public static class BookModelConcurrency
{
    /// <summary>1 for an address on this machine or a private network, 2 otherwise.</summary>
    public static int For(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return 2;
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') && !host.Contains(':'))
        {
            return 1;
        }

        if (!System.Net.IPAddress.TryParse(host, out var ip)) return 2;
        if (System.Net.IPAddress.IsLoopback(ip)) return 1;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC ? 1 : 2;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127 ? 1 : 2;
    }
}

/// <summary>A model opened for one build. Dispose it when the build ends: it hands back the server requests that were set aside and not used.</summary>
public sealed class BookModelHandle(IChatClient chat, string model, int concurrency, AiPriceQuote? price, IDisposable? reservation) : IDisposable
{
    public IChatClient Chat { get; } = chat;
    public string Model { get; } = model;
    public int Concurrency { get; } = concurrency;
    public AiPriceQuote? Price { get; } = price;

    /// <summary>True for a failure raised by the app before a request left (the server's allowance): nothing was sent, nothing was spent.</summary>
    public Func<Exception, bool> RefusedBeforeSending { get; } = ex => ex is LocalizedException;

    public void Dispose() => reservation?.Dispose();
}

/// <summary>The seam between the book build and the AI settings, so the build can be tested with a scripted model.</summary>
public interface IBookModels
{
    /// <summary>The model the user's AI buttons use, or null when they have none.</summary>
    Task<BookModelInfo?> InfoAsync(string userId);

    /// <summary>Sets aside <paramref name="requests"/> on the server's key when that is whose it is (all or nothing, refused up front
    /// with the allowance's own message), and returns the client. Throws <see cref="AiNotConfiguredException"/> when there is no model.</summary>
    Task<BookModelHandle> OpenAsync(string userId, int requests);
}

public sealed class AiBookModels(AiProviderService providers, IUserRepository users, ServerAiUsage usage, AIConfiguration serverConfig) : IBookModels
{
    public async Task<BookModelInfo?> InfoAsync(string userId)
    {
        var user = await users.GetUserByIdAsync(userId);
        if (providers.Describe(user) is not { } label || !await providers.IsConfiguredForAsync(userId)) return null;
        (int, int)? server = providers.UsesServerKey(user) ? (usage.UsedToday(userId, ServerAiUsage.Kind.Text), UnlimitedAdmin(user) ? 0 : usage.LimitOf(ServerAiUsage.Kind.Text)) : null;
        return new BookModelInfo(label, await providers.PriceForAsync(userId), server);
    }

    private static bool UnlimitedAdmin(UserDocument? user) => user?.Roles.Contains(ExposurePolicy.ServerAdminRole) == true;

    public async Task<BookModelHandle> OpenAsync(string userId, int requests)
    {
        var user = await users.GetUserByIdAsync(userId);
        var reservation = await providers.ReserveRequestsAsync(userId, requests);
        try
        {
            var client = providers.ClientFor(user, reservation) ?? throw new AiNotConfiguredException();
            var provider = providers.DefaultProvider(user);
            var endpoint = providers.UsesServerKey(user) ? serverConfig.Endpoint : provider?.BaseUrl;
            return new BookModelHandle(client, providers.Describe(user) ?? string.Empty, BookModelConcurrency.For(endpoint), await providers.PriceForAsync(userId), reservation);
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }
}
