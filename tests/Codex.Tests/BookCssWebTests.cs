using System.Net;
using System.Net.Sockets;
using System.Text;
using Codex.Core.Book;
using Codex.Web.Book;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests;

/// <summary>
/// The server's copies of the web addresses a book's stylesheet names (docs/BOOK_EXPORT.md 41.14): the cleaner writes a copy in place of its
/// address and an imported sheet in place of its rule, cleaned the same way and unable to reach past itself; the fetcher goes only to public
/// addresses and keeps only what is what the rule asked for.
/// </summary>
public class BookCssWebTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Woff2 = "wOF2\0\u0001\0\0"u8.ToArray();

    private static BookCssWebFile Sheet(string url, string css) => new(url, BookCssWebKind.Sheet, "text/css", Encoding.UTF8.GetBytes(css));
    private static BookCssWebFile Picture(string url) => new(url, BookCssWebKind.Picture, "image/png", Png);
    private static BookCssWebFile Font(string url) => new(url, BookCssWebKind.Font, "font/woff2", Woff2);

    private static string PngData => "data:image/png;base64," + Convert.ToBase64String(Png);
    private static string FontData => "data:font/woff2;base64," + Convert.ToBase64String(Woff2);

    [Theory]
    [InlineData("https://cdn.example/a.css", null, "https://cdn.example/a.css")]
    [InlineData("  https://cdn.example/f.eot?#iefix ", null, "https://cdn.example/f.eot?")]
    [InlineData("tex.png", "https://cdn.example/theme/style.css", "https://cdn.example/theme/tex.png")]
    [InlineData("../img/tex.png", "https://cdn.example/theme/style.css", "https://cdn.example/img/tex.png")]
    [InlineData("//other.example/x.png", "https://cdn.example/theme/style.css", "https://other.example/x.png")]
    [InlineData("tex.png", null, null)]
    [InlineData("/themes/V3/Blank/style.css", null, null)]
    [InlineData("http://cdn.example/a.css", null, null)]
    [InlineData("ftp://cdn.example/a.css", null, null)]
    [InlineData("file:///etc/passwd", null, null)]
    [InlineData("https://user:pass@cdn.example/a.css", null, null)]
    [InlineData("data:image/png;base64,AA", null, null)]
    [InlineData("#filter", null, null)]
    [InlineData("tex.png", "http://cdn.example/style.css", null)]
    public void AnAddress_IsHttps_AndAbsoluteOrRelativeToAnImportedSheet(string url, string? relativeTo, string? expected) =>
        Assert.Equal(expected, BookCssWeb.Address(url, relativeTo));

    [Fact]
    public void AnImportedSheet_IsWrittenWhereItsRuleWas_WithItsOwnAddressesAsCopies()
    {
        var web = new BookCssWeb([
            Sheet("https://cdn.example/fonts.css", "@font-face{font-family:'Web Face';src:url(face.woff2) format('woff2')}\n.page{background:url('img/tex.png')}"),
            Font("https://cdn.example/face.woff2"),
            Picture("https://cdn.example/img/tex.png"),
            Picture("https://i.example/frame.png")
        ]);
        var css = "@import url('https://cdn.example/fonts.css');\n.note{border-image:url(https://i.example/frame.png) 14 stretch}";

        var cleaned = BookCss.Clean(css, out _, web);

        Assert.Contains("font-family:\"Web Face\"", cleaned);
        Assert.Contains("url(\"" + FontData + "\")", cleaned);
        Assert.Contains(".page{background:url(\"" + PngData + "\")}", cleaned);
        Assert.Contains(".note{border-image:url(\"" + PngData + "\") 14 stretch}", cleaned);
        Assert.DoesNotContain("@import", cleaned);
        Assert.DoesNotContain("example", cleaned);
        Assert.Empty(web.Missing);
        Assert.Equal(4, web.Used.Count);
        // The imported sheet's rules come first, where the rule was.
        Assert.True(cleaned.IndexOf("Web Face", StringComparison.Ordinal) < cleaned.IndexOf(".note", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatHasNoCopy_IsDropped_AndNotedForTheServerToFetch()
    {
        var web = new BookCssWeb();
        var css = "@import 'https://cdn.example/a.css';\n@font-face{font-family:x;src:url(https://cdn.example/f.woff2)}\np{background:url(https://cdn.example/p.png)}";

        var cleaned = BookCss.Clean(css, out var warnings, web);

        Assert.DoesNotContain("cdn.example", cleaned);
        Assert.Contains("css-at-rule-dropped", warnings);
        Assert.Equal(
            new[] { ("https://cdn.example/a.css", BookCssWebKind.Sheet), ("https://cdn.example/f.woff2", BookCssWebKind.Font), ("https://cdn.example/p.png", BookCssWebKind.Picture) },
            web.Missing.Select(a => (a.Url, a.Kind)).OrderBy(a => a.Url).ToArray());
        // Without copies at all it is the cleaner as it always was.
        Assert.Equal(BookCss.Clean(css, out _), cleaned);
    }

    [Fact]
    public void ACopyOfTheWrongKind_IsNotUsed()
    {
        // A picture is not a face's source, and a sheet is not a picture.
        var web = new BookCssWeb([Picture("https://cdn.example/f.woff2"), Sheet("https://cdn.example/p.png", "p{}")]);

        var cleaned = BookCss.Clean("@font-face{font-family:x;src:url(https://cdn.example/f.woff2)}p{background:url(https://cdn.example/p.png)}", out _, web);

        Assert.DoesNotContain("data:", cleaned);
        Assert.Equal(2, web.Missing.Count);
    }

    [Theory]
    // A media list wraps the sheet in @media.
    [InlineData("@import url('https://cdn.example/a.css') print;", "@media print{")]
    [InlineData("@import 'https://cdn.example/a.css' screen and (min-width: 600px);", "@media screen and (min-width: 600px){")]
    public void AnImportWithAMediaList_IsWrappedInIt(string css, string wrapper)
    {
        var web = new BookCssWeb([Sheet("https://cdn.example/a.css", "p{color:red}")]);

        var cleaned = BookCss.Clean(css, out _, web);

        Assert.Contains(wrapper + "\np{color:red}\n}", cleaned);
    }

    [Theory]
    // Conditions the cleaner does not write, and an import the browser would not read (after a rule): dropped, not applied unconditionally.
    [InlineData("@import url('https://cdn.example/a.css') layer(base);")]
    [InlineData("@import url('https://cdn.example/a.css') layer;")]
    [InlineData("@import url('https://cdn.example/a.css') supports(display: grid);")]
    [InlineData("p{color:blue}\n@import url('https://cdn.example/a.css');")]
    [InlineData("@media print{@import url('https://cdn.example/a.css');}")]
    public void AnImportTheCleanerCannotWriteAsTheBrowserReadsIt_IsDropped(string css)
    {
        var web = new BookCssWeb([Sheet("https://cdn.example/a.css", "p{color:red}")]);

        Assert.DoesNotContain("red", BookCss.Clean(css, out _, web));
    }

    [Fact]
    public void ImportsStop_AtACycle_AndAtTheirDepth()
    {
        var files = new List<BookCssWebFile> { Sheet("https://cdn.example/self.css", "@import 'self.css'; .self{color:red}") };
        for (var n = 0; n < 8; n++) files.Add(Sheet($"https://cdn.example/{n}.css", $"@import '{n + 1}.css'; .d{n}{{color:red}}"));
        var web = new BookCssWeb(files);

        var cycle = BookCss.Clean("@import 'https://cdn.example/self.css';", out _, web);
        var deep = BookCss.Clean("@import 'https://cdn.example/0.css';", out _, web);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cycle, @"\.self"));
        Assert.Contains($".d{BookCssWeb.MaxImportDepth - 1}{{", deep);
        Assert.DoesNotContain($".d{BookCssWeb.MaxImportDepth}{{", deep);
    }

    [Theory]
    // A closer it has no opener for is not written: the GM's rules after it, and a media wrapper around it, stay as they were.
    [InlineData("} } p{color:red}")]
    [InlineData("p{color:red}}} .after{x:y}")]
    // What it leaves open is closed at its end.
    [InlineData("p{color:red")]
    [InlineData("@media print{p{color:red")]
    [InlineData("p{background:linear-gradient(red")]
    // It cannot close the style element: strings are escaped, '<' is not written.
    [InlineData("p{content:'</style><script>alert(1)</script>'}")]
    [InlineData("</style><script>alert(1)</script>")]
    public void AnImportedSheet_CannotReachPastItself(string imported)
    {
        var web = new BookCssWeb([Sheet("https://cdn.example/a.css", imported)]);
        const string after = "\n.gm{color:blue}";

        var cleaned = BookCss.Clean("@import 'https://cdn.example/a.css' print;" + after, out _, web);

        Assert.DoesNotContain("</style", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("}\n" + after.TrimStart('\n'), cleaned.Replace("\n\n", "\n"));
        Assert.Equal(0, Balance(cleaned));
    }

    // Braces, brackets and parentheses outside strings (the cleaner writes strings double-quoted and escaped).
    private static int Balance(string css)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < css.Length; i++)
        {
            var c = css[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c is '{' or '(' or '[') depth++;
            else if (c is '}' or ')' or ']') depth--;
            if (depth < 0) return depth;
        }

        return depth;
    }

    [Fact]
    public void Sniff_TakesWhatTheBytesSay_NotWhatTheSiteSays()
    {
        Assert.Equal("image/png", BookCssWeb.Sniff(BookCssWebKind.Picture, Png));
        Assert.Equal("image/jpeg", BookCssWeb.Sniff(BookCssWebKind.Picture, [0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/webp", BookCssWeb.Sniff(BookCssWebKind.Picture, "RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal("font/woff2", BookCssWeb.Sniff(BookCssWebKind.Font, Woff2));
        Assert.Equal("font/ttf", BookCssWeb.Sniff(BookCssWebKind.Font, [0, 1, 0, 0, 0, 12]));
        Assert.Equal("text/css", BookCssWeb.Sniff(BookCssWebKind.Sheet, "﻿@font-face{}"u8));
        // A page in place of a picture, a picture in place of a font, SVG, a page in place of a sheet, a binary in place of a sheet.
        Assert.Null(BookCssWeb.Sniff(BookCssWebKind.Picture, "<!doctype html><title>Not found</title>"u8));
        Assert.Null(BookCssWeb.Sniff(BookCssWebKind.Font, Png));
        Assert.Null(BookCssWeb.Sniff(BookCssWebKind.Picture, "<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        Assert.Null(BookCssWeb.Sniff(BookCssWebKind.Sheet, "  <html><body>Sign in</body></html>"u8));
        Assert.Null(BookCssWeb.Sniff(BookCssWebKind.Sheet, Woff2));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.8.8")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("2001:0:4136:e378::1")]
    [InlineData("2001:db8::1")]
    public void AnAddressNotOnThePublicInternet_IsRefused(string address) => Assert.False(BookWebFetcher.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("151.101.1.140")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::6810:84e5")]
    [InlineData("::ffff:8.8.8.8")]
    public void APublicAddress_IsTaken(string address) => Assert.True(BookWebFetcher.IsPublic(IPAddress.Parse(address)));

    /// <summary>The fetcher the server registers goes nowhere on this computer, by name or by number, whatever the address says.</summary>
    [Theory]
    [InlineData("https://localhost:{0}/a.png")]
    [InlineData("https://127.0.0.1:{0}/a.png")]
    [InlineData("https://[::1]:{0}/a.png")]
    [InlineData("https://127.0.0.1.nip.io:{0}/a.png")]
    public async Task TheServersFetcher_GoesNowhereOnThisComputer(string address)
    {
        using var site = new Site();
        site.Serve("/a.png", "image/png", Png);
        var services = new ServiceCollection();
        BookWebFetcher.Register(services);
        using var provider = services.BuildServiceProvider();
        var fetcher = provider.GetRequiredService<BookWebFetcher>();

        var (file, failure) = await fetcher.FetchAsync(string.Format(address, site.Port), BookCssWebKind.Picture, BookCssWeb.MaxTotalBytes, CancellationToken.None);

        Assert.Null(file);
        // nip.io answers 127.0.0.1 for that name; offline, the name does not resolve at all. Either way, nothing reached the site.
        Assert.True(failure!.Problem is BookWebProblem.NotPublic || address.Contains("nip.io") && failure.Problem == BookWebProblem.Unreachable, failure.ToString());
        Assert.Equal(0, site.Requests);
    }

    private static BookWebFetcher Local() => BookWebFetcher.Over(new HttpClient(BookWebFetcher.Handler(_ => true)), httpsOnly: false);

    [Fact]
    public async Task TheFetcher_FollowsRedirects_AndKeepsOnlyWhatWasAskedFor()
    {
        using var site = new Site();
        site.Serve("/tex.png", "text/plain", Png);
        site.Redirect("/old.png", "/tex.png");
        site.Serve("/missing.png", "text/html", "<!doctype html><p>Not found"u8.ToArray());
        site.Status("/gone.png", 404);
        var fetcher = Local();

        var (file, _) = await fetcher.FetchAsync(site.Url("/old.png"), BookCssWebKind.Picture, BookCssWeb.MaxTotalBytes, CancellationToken.None);
        var (_, page) = await fetcher.FetchAsync(site.Url("/missing.png"), BookCssWebKind.Picture, BookCssWeb.MaxTotalBytes, CancellationToken.None);
        var (_, gone) = await fetcher.FetchAsync(site.Url("/gone.png"), BookCssWebKind.Picture, BookCssWeb.MaxTotalBytes, CancellationToken.None);

        Assert.Equal("image/png", file!.ContentType);
        Assert.Equal(site.Url("/old.png"), file.Url);
        Assert.Equal(BookWebProblem.NotWhatWasNamed, page!.Problem);
        Assert.Equal((BookWebProblem.Refused, 404), (gone!.Problem, gone.Status));
    }

    [Fact]
    public async Task TheFetcher_StopsAtItsBounds_WhateverTheSiteSays()
    {
        using var site = new Site();
        for (var n = 0; n < BookWebFetcher.MaxRedirects + 2; n++) site.Redirect($"/r{n}", $"/r{n + 1}");
        var big = new byte[BookCssWeb.MaxBytes(BookCssWebKind.Picture) + 1];
        Png.CopyTo(big, 0);
        // No length given (chunked): the bound is kept while reading.
        site.Serve("/big.png", "image/png", big, chunked: true);
        site.Serve("/small.png", "image/png", Png);
        var fetcher = Local();

        var (_, loop) = await fetcher.FetchAsync(site.Url("/r0"), BookCssWebKind.Picture, BookCssWeb.MaxTotalBytes, CancellationToken.None);
        var (_, large) = await fetcher.FetchAsync(site.Url("/big.png"), BookCssWebKind.Picture, BookCssWeb.MaxTotalBytes, CancellationToken.None);
        var (_, full) = await fetcher.FetchAsync(site.Url("/small.png"), BookCssWebKind.Picture, Png.Length - 1, CancellationToken.None);

        Assert.Equal(BookWebProblem.TooManyRedirects, loop!.Problem);
        Assert.Equal(BookWebProblem.TooLarge, large!.Problem);
        Assert.Equal(BookWebProblem.Full, full!.Problem);
    }

    /// <summary>A site on this computer, over plain http, for the fetcher made to reach it.</summary>
    internal sealed class Site : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Dictionary<string, Action<HttpListenerResponse>> routes = new(StringComparer.Ordinal);
        private int requests;

        public int Port { get; }
        public int Requests => requests;

        public Site()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

        public void Serve(string path, string type, byte[] body, bool chunked = false) => routes[path] = r =>
        {
            r.ContentType = type;
            if (chunked) r.SendChunked = true;
            else r.ContentLength64 = body.Length;
            r.OutputStream.Write(body);
        };

        public void Redirect(string path, string to) => routes[path] = r => r.Redirect(Url(to));

        public void Status(string path, int status) => routes[path] = r => r.StatusCode = status;

        private async Task LoopAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                Interlocked.Increment(ref requests);
                try
                {
                    if (routes.TryGetValue(context.Request.Url!.AbsolutePath, out var route)) route(context.Response);
                    else context.Response.StatusCode = 404;
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
        }
    }
}
