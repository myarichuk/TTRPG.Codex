using System.Text;

namespace Codex.Core.Book;

public enum BookCssWebKind
{
    /// <summary>A stylesheet an <c>@import</c> names.</summary>
    Sheet,

    /// <summary>A face's source in <c>@font-face</c>.</summary>
    Font,

    /// <summary>A picture any other <c>url()</c> names (a page texture, a frame).</summary>
    Picture
}

/// <summary>A copy of one web address the GM's stylesheet names, as the server fetched it.</summary>
public sealed record BookCssWebFile(string Url, BookCssWebKind Kind, string ContentType, byte[] Bytes);

/// <summary>An address the stylesheet names that has no copy yet.</summary>
public sealed record BookCssWebAddress(string Url, BookCssWebKind Kind);

/// <summary>
/// The copies of web addresses a draft's stylesheet names (docs/BOOK_EXPORT.md 41.14), which <see cref="BookCss.Clean(string, out IReadOnlyList{string}, BookCssWeb?)"/>
/// writes into the sheet in place of the address: a font or a picture as a data URL, an imported sheet as its own rules, cleaned the same way.
/// The page still fetches nothing; the server fetched them once, when the GM saved the stylesheet.
/// <para>While it cleans, it notes every address it was asked for (<see cref="Used"/>) and the ones it had no copy of (<see cref="Missing"/>):
/// what the server should fetch next, read by the same code that writes the sheet, so the two never disagree.</para>
/// </summary>
public sealed class BookCssWeb
{
    /// <summary>A sheet imported by a sheet imported by …: deeper than this is not followed.</summary>
    public const int MaxImportDepth = 4;

    /// <summary>Bounds on what a draft keeps: every copy together, and how many. A page texture is a megabyte or two, a family of faces a few
    /// hundred kilobytes each; the whole goes into the page the editor and the print browser draw.</summary>
    public const long MaxTotalBytes = 16 * 1024 * 1024;
    public const int MaxFiles = 120;

    /// <summary>The largest copy of each kind (Tasha's imported font sheet is 0.9 MB of faces written into it).</summary>
    public static long MaxBytes(BookCssWebKind kind) => kind switch
    {
        BookCssWebKind.Sheet => 3 * 1024 * 1024,
        BookCssWebKind.Font => 4 * 1024 * 1024,
        _ => 6 * 1024 * 1024
    };

    /// <summary>The types a copy is stored as, by what its first bytes say it is (<see cref="Sniff"/>), never what the server said.</summary>
    public static readonly IReadOnlySet<string> ContentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "text/css", "font/woff2", "font/woff", "font/ttf", "font/otf", "font/collection", "application/vnd.ms-fontobject",
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif"
    };

    /// <summary>
    /// What a fetched file is, from its bytes: a font by its signature for a face's source, a picture by its signature for any other
    /// <c>url()</c>, and text with no control characters for an imported sheet. Null when it is not what was asked for (an error page served in
    /// place of a picture, a font given as a picture). SVG is not taken as a picture, as uploads do not take it.
    /// </summary>
    public static string? Sniff(BookCssWebKind kind, ReadOnlySpan<byte> b)
    {
        switch (kind)
        {
            case BookCssWebKind.Font:
                if (b.StartsWith("wOF2"u8)) return "font/woff2";
                if (b.StartsWith("wOFF"u8)) return "font/woff";
                if (b.StartsWith("OTTO"u8)) return "font/otf";
                if (b.StartsWith((byte[])[0, 1, 0, 0]) || b.StartsWith("true"u8)) return "font/ttf";
                if (b.StartsWith("ttcf"u8)) return "font/collection";
                // Embedded OpenType: "LP" at 34. The print browser does not read it, but old faces list it first and the face is kept whole.
                if (b.Length > 36 && b[34] == 'L' && b[35] == 'P') return "application/vnd.ms-fontobject";
                return null;
            case BookCssWebKind.Picture:
                if (b.StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
                if (b.StartsWith((byte[])[0xFF, 0xD8, 0xFF])) return "image/jpeg";
                if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8)) return "image/gif";
                if (b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
                if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8) && (b[8..12].SequenceEqual("avif"u8) || b[8..12].SequenceEqual("avis"u8))) return "image/avif";
                return null;
            default:
                if (b.StartsWith((byte[])[0xEF, 0xBB, 0xBF])) b = b[3..];
                foreach (var c in b)
                {
                    if (c < 0x20 && c is not (0x09 or 0x0A or 0x0C or 0x0D)) return null;
                }

                // An HTML page (a sign-in wall, a "not found") is not a stylesheet.
                var head = Encoding.UTF8.GetString(b[..Math.Min(b.Length, 512)]).TrimStart();
                return head.StartsWith('<') ? null : "text/css";
        }
    }

    private readonly Dictionary<string, BookCssWebFile> files;
    private readonly Dictionary<string, string> dataUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BookCssWebKind> used = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BookCssWebKind> missing = new(StringComparer.Ordinal);

    public BookCssWeb(IEnumerable<BookCssWebFile>? files = null)
    {
        this.files = new Dictionary<string, BookCssWebFile>(StringComparer.Ordinal);
        foreach (var f in files ?? []) this.files[f.Url] = f;
    }

    public IReadOnlyCollection<BookCssWebFile> Files => files.Values;

    /// <summary>Every address the last clean asked for, copy or not.</summary>
    public IReadOnlyCollection<BookCssWebAddress> Used => used.Select(kv => new BookCssWebAddress(kv.Key, kv.Value)).ToList();

    /// <summary>The addresses the last clean asked for and had no copy of.</summary>
    public IReadOnlyCollection<BookCssWebAddress> Missing => missing.Select(kv => new BookCssWebAddress(kv.Key, kv.Value)).ToList();

    /// <summary>Forgets what was asked for, before a clean.</summary>
    public void Reset()
    {
        used.Clear();
        missing.Clear();
    }

    /// <summary>
    /// The address a stylesheet's <c>url()</c> or <c>@import</c> names, absolute, or null when it names nothing on the web that may be fetched.
    /// Only https: an address of the GM's own sheet must be absolute (it has no address to be relative to); one in an imported sheet may be
    /// relative to that sheet. The fragment is not part of it (<c>font.eot?#iefix</c> is <c>font.eot?</c>).
    /// </summary>
    public static string? Address(string url, string? relativeTo)
    {
        var u = url.Trim();
        if (u.Length == 0 || u.Length > 2048 || u.StartsWith('#') || u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        Uri? uri;
        if (Uri.TryCreate(u, UriKind.Absolute, out var absolute) && !absolute.IsFile && !absolute.IsUnc && absolute.Scheme.Length > 1) uri = absolute;
        else if (relativeTo != null && Uri.TryCreate(relativeTo, UriKind.Absolute, out var root) && Uri.TryCreate(root, u, out var relative)) uri = relative;
        else return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || uri.Host.Length == 0) return null;
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    /// <summary>Whether there is a copy of this address, of any kind.</summary>
    public bool Has(string address) => files.ContainsKey(address);

    /// <summary>The copy of a picture an address names, as a data URL; null when there is none (or it is not a picture). Notes nothing: it is
    /// for the pictures a draft's chapters name (<see cref="BookWebImages"/>), not for what a stylesheet's clean asked for.</summary>
    public string? PictureUrl(string address) => files.TryGetValue(address, out var file) && file.Kind == BookCssWebKind.Picture ? DataUrl(address, file) : null;

    internal string? Data(string address, BookCssWebKind kind)
    {
        Note(address, kind);
        if (!files.TryGetValue(address, out var file) || file.Kind != kind) return Miss(address, kind);
        return DataUrl(address, file);
    }

    private string DataUrl(string address, BookCssWebFile file)
    {
        if (!dataUrls.TryGetValue(address, out var data))
        {
            data = "data:" + file.ContentType + ";base64," + Convert.ToBase64String(file.Bytes);
            dataUrls[address] = data;
        }

        return data;
    }

    internal string? Sheet(string address)
    {
        Note(address, BookCssWebKind.Sheet);
        if (!files.TryGetValue(address, out var file) || file.Kind != BookCssWebKind.Sheet) return Miss(address, BookCssWebKind.Sheet);
        if (!sheets.TryGetValue(address, out var text))
        {
            text = Encoding.UTF8.GetString(file.Bytes);
            sheets[address] = text;
        }

        return text;
    }

    private void Note(string address, BookCssWebKind kind) => used.TryAdd(address, kind);

    private string? Miss(string address, BookCssWebKind kind)
    {
        missing.TryAdd(address, kind);
        return null;
    }
}
