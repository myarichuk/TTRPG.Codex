using System.Net;
using System.Net.Sockets;
using Codex.Persistence;

namespace Codex.Web;

/// <summary>What changes while the launcher has the server open to the whole internet (Tailscale Funnel,
/// <c>Codex:PublicExposure</c>). Anyone with the address can then reach it, so a stranger may not make an
/// account without an invite, and may not spend the server's AI key.</summary>
public static class ExposurePolicy
{
    public const string ConfigKey = "Codex:PublicExposure";
    public const string ServerAdminRole = "ServerAdmin";
    private const string JoinPrefix = "/campaigns/join/";

    public const int GuessesPerMinute = 30;

    /// <summary>The pages and endpoints where a visitor can learn whether a password or an invite code is right.</summary>
    public static bool IsGuessable(PathString path) =>
        path.StartsWithSegments("/login") || path.StartsWithSegments("/register") || path.StartsWithSegments("/campaigns/join");

    /// <summary>The rate-limit bucket for a client: its address, or for IPv6 its whole /64, which one subscriber can rotate through freely.</summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address == null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString();
    }

    public static bool IsExposed(IConfiguration? configuration) => configuration?.GetValue<bool>(ConfigKey) == true;

    /// <summary>The invite code in a return address that points at a join link, or <c>null</c>. Accepts the
    /// absolute form a browser reports as well as the local path.</summary>
    public static string? InviteCodeFrom(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return null;
        string path;
        if (UrlHelper.IsLocalUrl(returnUrl)) path = returnUrl.Split('?', '#')[0];
        else if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") path = absolute.AbsolutePath;
        else return null;

        if (!path.StartsWith(JoinPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var code = path[JoinPrefix.Length..].TrimEnd('/');
        return code.Length > 0 && !code.Contains('/') ? Uri.UnescapeDataString(code) : null;
    }

    /// <summary>Open servers take a new account only from someone arriving with a real campaign invite.</summary>
    public static async Task<bool> MaySignUpAsync(bool exposed, string? returnUrl, ICampaignRepository campaigns) =>
        !exposed || (InviteCodeFrom(returnUrl) is { } code && await campaigns.InviteCodeExistsAsync(code));

    /// <summary>The key in the server's own configuration belongs to whoever runs the server.</summary>
    public static bool MayUseServerAi(bool exposed, UserDocument? user) =>
        !exposed || user?.Roles.Contains(ServerAdminRole) == true;

    /// <summary>Whether an address a user typed in points at this computer or its private network: a name with no dots,
    /// <c>localhost</c>, <c>*.local</c>, or a loopback, link-local, private or tailnet address. While the server is open to
    /// the internet a stranger's own AI provider may not aim at those, or it would reach the DM's local model and database.</summary>
    public static bool IsInternalEndpoint(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(host, out var ip)) return IsInternalAddress(ip);
        return !host.Contains('.')
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInternalAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] == 0;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
    }

    /// <summary>The one client used for a stranger's own AI provider while the server is open: it connects only to public
    /// addresses, redirects included, so no hostname or answer from their server can lead to this computer. Requests are
    /// timed by the AI clients themselves.</summary>
    public static readonly HttpClient PublicOnlyClient = new(new SocketsHttpHandler { ConnectCallback = ConnectPublicOnlyAsync, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Connects like a normal client, but only to public addresses, judged after the name is resolved so a
    /// public-looking name or a redirect cannot lead inside.</summary>
    public static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(a => !IsInternalAddress(a)).ToArray();
        if (allowed.Length == 0) throw new HttpRequestException("That address is not public.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
