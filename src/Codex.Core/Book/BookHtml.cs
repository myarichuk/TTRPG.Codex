using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>One chapter of a book: Markdown in the book's dialect. Its heading is part of the Markdown.</summary>
public sealed record BookSection(string Id, string Markdown, IReadOnlyDictionary<int, string>? Blocks = null);

/// <summary>The words a book prints that are not the campaign's own. Defaults are English; the app passes the reader's language.</summary>
public sealed record BookLabels(
    string Contents = "Contents",
    string Overview = "Overview",
    string Cast = "Cast",
    string Gazetteer = "Gazetteer",
    string Chronicle = "Chronicle",
    string Lore = "Lore",
    string Pages = "Pages",
    string Homebrew = "Homebrew");

/// <summary>A compiled book: what to print, in order, before any theme. <paramref name="CoverAsset"/> names the cover picture (an asset id the
/// <see cref="BookHtmlOptions.Image"/> delegate resolves), or null for a title page of type alone.</summary>
public sealed record BookDocument(string Title, string Tagline, string Language, IReadOnlyList<BookSection> Sections, BookLabels Labels, string? CoverAsset = null);

public sealed class BookHtmlOptions
{
    /// <summary>The DM edition boxes secrets; the party edition has none to box.</summary>
    public bool BoxSecrets { get; init; }

    public Func<string, string, string?>? Mention { get; init; }
    public Func<string, string?>? Image { get; init; }

    /// <summary>The bytes of a bundled font (<see cref="BookFontFace.File"/>), or null when it is not there. Fonts are written
    /// into the page as data URIs, so the page needs no font request: with no network at all, it prints the same.</summary>
    public Func<string, byte[]?>? Font { get; init; }

    /// <summary>The user's own CSS, applied after the theme. It is cleaned by <see cref="BookCss.Clean"/> first.</summary>
    public string? CustomCss { get; init; }

    public string PageSize { get; init; } = "A4";

    /// <summary>The text columns of a chapter: 2, or anything else for one.</summary>
    public int Columns { get; init; } = 1;

    /// <summary>Page numbers for the table of contents, by heading id. Null leaves the numbers out (the preview has no pages); an empty
    /// set reserves their column without filling it, which is how the first print of a book is laid out; a heading with no entry
    /// shows no number rather than a wrong one.</summary>
    public IReadOnlyDictionary<string, int>? PageNumbers { get; init; }
}

/// <summary>What a book rendered to. <paramref name="Html"/> is null when the theme does not support the language
/// (<paramref name="Refused"/> says so), which is the language gate.</summary>
public sealed record BookHtmlResult(string? Html, string? Refused, IReadOnlyList<BookHeading> Headings, IReadOnlyList<string> Warnings, IReadOnlyList<string> MissingFonts);

/// <summary>Puts a compiled book on a page: theme, chapters, a table of contents and a content security policy that allows nothing
/// to be fetched. One document serves the live preview (a sandboxed iframe) and the PDF (headless Chromium), so the preview shows
/// what the engine prints.</summary>
public static class BookHtml
{
    /// <summary>The policy of the page, also sent as a header by the route. No script, no network: only inline style, data images and data fonts.</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'";

    public static BookHtmlResult Build(BookDocument book, BookTheme theme, BookHtmlOptions options)
    {
        if (!theme.Supports(book.Language))
        {
            return new BookHtmlResult(null, $"The theme \"{theme.Name}\" does not support the language \"{BookThemes.Primary(book.Language)}\".", [], [], []);
        }

        var layout = BookLayout.For(book.Language, options.PageSize, options.Columns);
        var warnings = new List<string>();
        var headings = new List<BookHeading>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var markdown = new BookMarkdownOptions { BoxSecrets = options.BoxSecrets, Mention = options.Mention, Image = options.Image, HeadingIds = ids };

        var body = new StringBuilder();
        foreach (var section in book.Sections)
        {
            // A section rewritten by a model carries its template blocks (trusted HTML the code built); the others have none.
            var rendered = BookMarkdown.Render(section.Markdown, section.Blocks is { Count: > 0 } blocks
                ? new BookMarkdownOptions { BoxSecrets = markdown.BoxSecrets, Mention = markdown.Mention, Image = markdown.Image, HeadingIds = ids, Blocks = blocks }
                : markdown);
            headings.AddRange(rendered.Headings);
            warnings.AddRange(rendered.Warnings.Where(w => !warnings.Contains(w)));
            body.Append("<section class=\"chapter\" id=\"sec-").Append(Enc(section.Id)).Append("\">").Append(rendered.Html).Append("</section>\n");
        }

        var missing = new List<string>();
        var css = new StringBuilder();
        foreach (var face in theme.FontsFor(book.Language))
        {
            var bytes = options.Font?.Invoke(face.File);
            if (bytes == null)
            {
                missing.Add(face.File);
                continue;
            }

            css.Append("@font-face{font-family:\"").Append(face.Family).Append("\";font-weight:").Append(face.Weight).Append(";font-style:").Append(face.Style)
                .Append(";src:url(data:font/woff2;base64,").Append(Convert.ToBase64String(bytes)).Append(") format(\"woff2\");unicode-range:").Append(face.UnicodeRange).Append(";}\n");
        }

        css.Append(theme.Css(layout)).Append('\n');
        if (!string.IsNullOrWhiteSpace(options.CustomCss))
        {
            var custom = BookCss.Clean(options.CustomCss, out var customWarnings);
            warnings.AddRange(customWarnings.Where(w => !warnings.Contains(w)));
            css.Append(custom);
        }

        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"").Append(Enc(book.Language)).Append("\" dir=\"").Append(layout.Rtl ? "rtl" : "ltr").Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">\n")
            .Append("<title>").Append(Enc(book.Title)).Append("</title>\n<style>").Append(css).Append("</style>\n</head>\n<body class=\"book theme-").Append(Enc(theme.Id)).Append("\">\n");
        var cover = book.CoverAsset == null ? null : options.Image?.Invoke(book.CoverAsset);
        // The same test the renderer applies to every picture: a data image and nothing else is ever written into an attribute.
        if (cover != null && (!cover.StartsWith("data:image/", StringComparison.Ordinal) || cover.Any(ch => ch is '"' or '<' or '>' or '\'' || char.IsWhiteSpace(ch))))
        {
            cover = null;
        }

        html.Append(cover == null ? "<header class=\"title-page\">" : "<header class=\"title-page has-cover\"><img class=\"cover-art\" src=\"" + cover + "\" alt=\"\">")
            .Append("<h1>").Append(Enc(book.Title)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(book.Tagline))
        {
            html.Append("<p class=\"tagline\">").Append(Enc(book.Tagline)).Append("</p>");
        }

        html.Append("</header>\n");
        html.Append(Contents(book.Labels.Contents, headings, options.PageNumbers)).Append(body).Append("</body>\n</html>\n");
        return new BookHtmlResult(html.ToString(), null, headings, warnings, missing);
    }

    private static string Contents(string title, IReadOnlyList<BookHeading> headings, IReadOnlyDictionary<string, int>? numbers)
    {
        var entries = headings.Where(h => h.Level <= 2).ToList();
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<nav class=\"toc\"><h1>").Append(Enc(title)).Append("</h1><ol>");
        foreach (var heading in entries)
        {
            sb.Append("<li class=\"toc-").Append(heading.Level).Append("\"><a href=\"#").Append(Enc(heading.Id)).Append("\"><span class=\"toc-text\">").Append(Enc(heading.Text)).Append("</span>");
            if (numbers != null)
            {
                sb.Append("<span class=\"toc-leader\" aria-hidden=\"true\"></span><span class=\"toc-page\">").Append(numbers.TryGetValue(heading.Id, out var page) ? page.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty).Append("</span>");
            }

            sb.Append("</a></li>");
        }

        return sb.Append("</ol></nav>\n").ToString();
    }

    private static string Enc(string text) => WebUtility.HtmlEncode(text);
}
