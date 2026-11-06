using System.Net;
using System.Net.Sockets;
using Codex.Core.Book;

namespace Codex.Web.Book;

/// <summary>Why an address the GM's stylesheet names has no copy.</summary>
public enum BookWebProblem
{
    /// <summary>The name is this computer, its network, or an address that is not on the public internet.</summary>
    NotPublic,
    Unreachable,
    TimedOut,

    /// <summary>The site answered with an error (<see cref="BookWebFailure.Status"/>).</summary>
    Refused,
    TooLarge,

    /// <summary>What came back is not a font, a picture or a stylesheet, as the rule that names it asks for (a page saying "not found").</summary>
    NotWhatWasNamed,
    TooManyRedirects,

    /// <summary>The draft already keeps as many copies, or as many bytes, as it may.</summary>
    Full
}

public sealed record BookWebFailure(string Url, BookWebProblem Problem, int Status = 0);

/// <summary>
/// Fetches one web address a draft's stylesheet names (docs/BOOK_EXPORT.md 41.14), for a copy the page can hold. The GM names the address, so
/// the server is careful where it goes: https only (Codex.Core's <see cref="BookCssWeb.Address"/>, and again at every redirect), only to
/// addresses on the public internet, checked when the connection is made (so a name that answers differently the second time gains nothing) and
/// never through a proxy, which would be checked instead of the site; each redirect is followed by hand and checked again; what is read stops
/// at the copy's bound whatever the site says its length is; and what came back is kept only if its bytes are what was asked for.
/// </summary>
public sealed class BookWebFetcher
{
    public const string ClientName = "book-web";
    public const int MaxRedirects = 4;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // A browser's, and nothing about the server or who runs it: a font service sends a browser the woff2 faces it would send one, and an older
    // client the much larger TrueType.
    public const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private readonly HttpClient client;
    private readonly bool httpsOnly;

    public BookWebFetcher(IHttpClientFactory clients) : this(clients.CreateClient(ClientName), httpsOnly: true)
    {
    }

    /// <summary>A fetcher over a client whose handler the caller made: for tests, one that may reach a server on this computer over plain http.
    /// The server's own is <see cref="Register"/>'s.</summary>
    public static BookWebFetcher Over(HttpClient client, bool httpsOnly) => new(client, httpsOnly);

    private BookWebFetcher(HttpClient client, bool httpsOnly)
    {
        this.client = client;
        this.httpsOnly = httpsOnly;
    }

    /// <summary>The fetcher's own client: no proxy, no cookies, no redirects of its own, and a connection only to a public address.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddHttpClient(ClientName, c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => Handler(IsPublic));
        services.AddSingleton(sp => new BookWebFetcher(sp.GetRequiredService<IHttpClientFactory>()));
    }

    public static SocketsHttpHandler Handler(Func<IPAddress, bool> allowed) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxResponseHeadersLength = 64,
        ConnectCallback = async (context, cancel) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancel);
            // Every address the name gives must be public: a name that gives a public and a private one is refused, not tried in turn.
            if (addresses.Length == 0 || !addresses.All(allowed)) throw new NotPublicException(host);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    /// <summary>Whether an address is on the public internet: not this computer, not a private or shared network (Tailscale's 100.64/10 among
    /// them), not link-local (a cloud's metadata service), and not one of the IPv6 forms that carry an IPv4 address inside them.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] is 0 or 10 or 127 or >= 224
                || b[0] == 100 && (b[1] & 0xC0) == 64
                || b[0] == 169 && b[1] == 254
                || b[0] == 172 && (b[1] & 0xF0) == 16
                || b[0] == 192 && b[1] == 168
                || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2
                || b[0] == 192 && b[1] == 88 && b[2] == 99
                || b[0] == 198 && (b[1] & 0xFE) == 18
                || b[0] == 198 && b[1] == 51 && b[2] == 100
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        // Global unicast only (2000::/3), less the parts of it that are not one host on the internet.
        if ((b[0] & 0xE0) != 0x20) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false; // Teredo: an IPv4 address inside
        if (b[0] == 0x20 && b[1] == 0x02) return false; // 6to4: an IPv4 address inside
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] < 0x20) return false; // other protocol assignments
        return true;
    }

    private sealed class NotPublicException(string host) : IOException($"{host} is not a public address.");

    /// <summary>One address, as a copy of the kind its rule names, at most <paramref name="room"/> bytes; or why not.</summary>
    public async Task<(BookCssWebFile? File, BookWebFailure? Failure)> FetchAsync(string url, BookCssWebKind kind, long room, CancellationToken cancel)
    {
        var limit = Math.Min(BookCssWeb.MaxBytes(kind), room);
        if (limit <= 0) return (null, new BookWebFailure(url, BookWebProblem.Full));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Timeout);
        var at = new Uri(url);
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (at.Scheme != Uri.UriSchemeHttps && (httpsOnly || at.Scheme != Uri.UriSchemeHttp)) return (null, new BookWebFailure(url, BookWebProblem.NotPublic));
                using var request = new HttpRequestMessage(HttpMethod.Get, at);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.ParseAdd(kind switch
                {
                    BookCssWebKind.Sheet => "text/css,*/*;q=0.1",
                    BookCssWebKind.Picture => "image/avif,image/webp,image/png,image/*;q=0.8,*/*;q=0.5",
                    _ => "*/*"
                });
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) return (null, new BookWebFailure(url, BookWebProblem.TooManyRedirects));
                    at = new Uri(at, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode) return (null, new BookWebFailure(url, BookWebProblem.Refused, status));
                // Too large for any copy, or for the room the draft has left.
                var over = new BookWebFailure(url, limit < BookCssWeb.MaxBytes(kind) ? BookWebProblem.Full : BookWebProblem.TooLarge);
                if (response.Content.Headers.ContentLength > limit) return (null, over);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = await ReadAsync(stream, limit, timeout.Token);
                if (bytes == null) return (null, over);
                if (BookCssWeb.Sniff(kind, bytes) is not { } type) return (null, new BookWebFailure(url, BookWebProblem.NotWhatWasNamed));
                return (new BookCssWebFile(url, kind, type, bytes), null);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return (null, new BookWebFailure(url, BookWebProblem.TimedOut));
        }
        catch (HttpRequestException ex)
        {
            return (null, new BookWebFailure(url, ex.InnerException is NotPublicException || ex.InnerException?.InnerException is NotPublicException
                ? BookWebProblem.NotPublic
                : BookWebProblem.Unreachable));
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or InvalidOperationException)
        {
            return (null, new BookWebFailure(url, ex is NotPublicException ? BookWebProblem.NotPublic : BookWebProblem.Unreachable));
        }
    }

    // At most limit bytes; null if there are more.
    private static async Task<byte[]?> ReadAsync(Stream stream, long limit, CancellationToken cancel)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var n = await stream.ReadAsync(chunk, cancel);
            if (n == 0) return buffer.ToArray();
            if (buffer.Length + n > limit) return null;
            buffer.Write(chunk, 0, n);
        }
    }
}
