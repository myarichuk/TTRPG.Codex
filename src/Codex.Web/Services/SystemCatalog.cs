using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Codex.Core.Systems;

namespace Codex.Web.Services;

/// <summary>The systems a release lists, and whether the list is the app's own release's or the latest one's.</summary>
public sealed record SystemCatalogListing(IReadOnlyList<SystemCatalogEntry> Systems, bool FromLatest);

/// <summary>Why a download was not accepted, for the page to say in the reader's language.</summary>
public enum SystemDownloadFailure
{
    None,
    /// <summary>The server could not be reached, answered with an error, or was too slow.</summary>
    Unreachable,
    /// <summary>The file is not the one the catalog described (size or checksum differ), or too big, or not an allowed file name.</summary>
    Refused
}

/// <summary>A downloaded archive, held in memory for <see cref="SystemInstaller.CheckAsync"/>, or why there is none.</summary>
public sealed record SystemDownload(byte[]? Bytes, SystemDownloadFailure Failure)
{
    public static SystemDownload Failed(SystemDownloadFailure failure) => new(null, failure);
}

/// <summary>
/// The list of game systems released for this version of the app (<c>systems.json</c> of its release) and their archives.
/// <list type="bullet">
/// <item><b>Where from.</b> <c>Codex:Systems:CatalogUrl</c>, by default the release of this app's own version on GitHub (a build
/// with no release version reads the latest release instead). An empty setting turns the list off.</item>
/// <item><b>Only beside the list.</b> An archive is fetched only as <c>&lt;the list's folder&gt;/&lt;file&gt;</c>, and the file name
/// must be <c>system-&lt;id&gt;-&lt;version&gt;.zip</c>: the list cannot point anywhere else. Redirects go only to https
/// github.com, *.githubusercontent.com and the host of a configured list.</item>
/// <item><b>Checked.</b> What is downloaded must be as big as the list says (never more than <see cref="SystemArchive.MaxBytes"/>,
/// counted as it arrives) and have the list's SHA-256. It then goes through the same check as an upload.</item>
/// <item><b>Private.</b> Requests carry a generic user agent and nothing about the server, its users or its campaigns.</item>
/// </list>
/// </summary>
public sealed partial class SystemCatalog
{
    public const string CatalogUrlKey = "Codex:Systems:CatalogUrl";
    public const string ClientName = "system-catalog";
    public const string DefaultUrl = "https://github.com/myarichuk/TTRPG.Codex/releases/download/v{version}/systems.json";
    public const string LatestUrl = "https://github.com/myarichuk/TTRPG.Codex/releases/latest/download/systems.json";

    private const int MaxRedirects = 5;
    private const int MaxCatalogBytes = 1024 * 1024;
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"^system-[a-z0-9-]+-[0-9A-Za-z.+-]+\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRule();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Rule();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionRule();

    private readonly IHttpClientFactory _http;
    private readonly ILogger<SystemCatalog> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SystemCatalogListing? _cached;
    private DateTimeOffset _cachedUntil;

    public SystemCatalog(IHttpClientFactory http, IConfiguration configuration, ILogger<SystemCatalog> logger, string appVersion, TimeProvider? clock = null)
    {
        _http = http;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        AppVersion = StripMetadata(appVersion);
        UserAgent = $"TTRPG.Codex/{AppVersion}";

        var configured = configuration[CatalogUrlKey];
        if (configured == null)
        {
            FromLatest = !IsReleaseVersion(AppVersion);
            configured = FromLatest ? LatestUrl : DefaultUrl.Replace("{version}", AppVersion, StringComparison.Ordinal);
        }
        else
        {
            configured = configured.Trim().Replace("{version}", AppVersion, StringComparison.Ordinal);
        }

        if (Uri.TryCreate(configured, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" && url.Segments.Length > 1)
        {
            CatalogUri = url;
            Base = new Uri(url, ".");
            IsDefaultHost = configuration[CatalogUrlKey] == null;
        }
    }

    /// <summary>The app's own version, without build metadata.</summary>
    public string AppVersion { get; }

    public string UserAgent { get; }

    /// <summary>Whether the list is the latest release's, because this build has no release version of its own.</summary>
    public bool FromLatest { get; }

    /// <summary>Whether there is a list to read (the setting is not empty).</summary>
    public bool Enabled => CatalogUri != null;

    public Uri? CatalogUri { get; }

    private Uri? Base { get; }
    private bool IsDefaultHost { get; }

    /// <summary>The version this running app was built as (<c>InformationalVersion</c>, which the release build sets).</summary>
    public static string CurrentAppVersion() =>
        StripMetadata(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>A release version (1.2.3 or 1.2.3-beta.1) that is not the SDK's default, 1.0.0, which every local build has.</summary>
    public static bool IsReleaseVersion(string version) => version != "1.0.0" && ReleaseVersionRule().IsMatch(version);

    private static string StripMetadata(string version) => version.Split('+')[0].Trim();

    /// <summary>Whether <paramref name="file"/> may be fetched: <c>system-&lt;id&gt;-&lt;version&gt;.zip</c> and nothing else.</summary>
    public static bool IsArchiveName(string? file) => file != null && FileNameRule().IsMatch(file);

    /// <summary>The systems the release lists, or null when the list cannot be had (off, offline, an error, a list this build does not
    /// read). Kept for a few minutes.</summary>
    public async Task<SystemCatalogListing?> ListAsync(CancellationToken ct = default)
    {
        if (!Enabled) return null;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached != null && _clock.GetUtcNow() < _cachedUntil) return _cached;
            var listing = await FetchListAsync(ct);
            if (listing != null)
            {
                _cached = listing;
                _cachedUntil = _clock.GetUtcNow() + CacheFor;
            }

            return listing;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SystemCatalogListing?> FetchListAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CatalogTimeout);
            using var response = await SendAsync(CatalogUri!, timeout.Token);
            if (response == null) return null;

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var read = await ReadAtMostAsync(stream, MaxCatalogBytes, timeout.Token);
            if (read == null) return null;

            if (SystemCatalogFile.TryRead(System.Text.Encoding.UTF8.GetString(read)) is not { } file)
            {
                _logger.LogWarning("The systems list at {Url} is not one this version reads.", CatalogUri);
                return null;
            }

            var usable = file.Systems.Where(IsUsable).ToList();
            return new SystemCatalogListing(usable, FromLatest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            _logger.LogInformation("The systems list could not be read from {Url}: {Reason}", CatalogUri, ex.Message);
            return null;
        }
    }

    private bool IsUsable(SystemCatalogEntry entry)
    {
        var usable = !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.Version)
                     && IsArchiveName(entry.File) && entry.Size is > 0 and <= SystemArchive.MaxBytes
                     && entry.Sha256 != null && Sha256Rule().IsMatch(entry.Sha256);
        if (!usable) _logger.LogInformation("The systems list has an entry that is not usable and was left out: {Id} {File}", entry.Id, entry.File);
        return usable;
    }

    /// <summary>Downloads a listed system's archive and checks it is the one the list described. The bytes are for
    /// <see cref="SystemInstaller.CheckAsync"/>: nothing is unpacked or installed here.</summary>
    public async Task<SystemDownload> DownloadAsync(SystemCatalogEntry entry, CancellationToken ct = default)
    {
        if (!Enabled || !IsUsable(entry)) return SystemDownload.Failed(SystemDownloadFailure.Refused);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DownloadTimeout);
            using var response = await SendAsync(new Uri(Base!, entry.File), timeout.Token);
            if (response == null) return SystemDownload.Failed(SystemDownloadFailure.Unreachable);

            // The size the list gives is the most it may be; Content-Length is a hint, never the limit.
            if (response.Content.Headers.ContentLength is { } announced && announced != entry.Size)
            {
                return SystemDownload.Failed(SystemDownloadFailure.Refused);
            }

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadAtMostAsync(stream, entry.Size, timeout.Token);
            if (bytes == null || bytes.Length != entry.Size) return SystemDownload.Failed(SystemDownloadFailure.Refused);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("The download of {File} does not have the checksum the systems list gives; it was refused.", entry.File);
                return SystemDownload.Failed(SystemDownloadFailure.Refused);
            }

            return new SystemDownload(bytes, SystemDownloadFailure.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            _logger.LogInformation("{File} could not be downloaded: {Reason}", entry.File, ex.Message);
            return SystemDownload.Failed(SystemDownloadFailure.Unreachable);
        }
    }

    /// <summary>Reads the stream, stopping as soon as it holds more than <paramref name="limit"/> bytes; null then.</summary>
    private static async Task<byte[]?> ReadAtMostAsync(Stream stream, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>A GET that follows redirects itself, and only to the hosts a release is served from.</summary>
    private async Task<HttpResponseMessage?> SendAsync(Uri start, CancellationToken ct)
    {
        var client = _http.CreateClient(ClientName);
        var url = start;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) return null;
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (!MayFollow(next))
                {
                    _logger.LogWarning("A redirect to {Host} was not followed.", next.Host);
                    return null;
                }

                url = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return null;
            }

            return response;
        }

        return null;
    }

    /// <summary>https github.com and *.githubusercontent.com; and the host of a configured list, never downgrading its scheme.</summary>
    private bool MayFollow(Uri next)
    {
        var host = next.Host;
        if (next.Scheme == Uri.UriSchemeHttps && (host == "github.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal))) return true;
        return !IsDefaultHost && host == CatalogUri!.Host && (next.Scheme == Uri.UriSchemeHttps || next.Scheme == CatalogUri.Scheme);
    }
}
