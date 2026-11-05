using System.Globalization;
using System.Net;
using System.Text;

namespace Codex.Core.Book;

/// <summary>
/// The themes of the editable book (docs/BOOK_EXPORT.md 41.2). Their CSS styles Homebrewery's page structure (<c>.page</c>, <c>.columnWrapper</c>,
/// <c>.columnSplit</c>, <c>.block</c>) and every block of the generator's contract: <c>.cover</c>, <c>.toc</c>, <c>.chapter-art</c>, <c>.portrait</c>,
/// <c>.place</c>, <c>.note</c>, <c>.descriptive</c>, <c>.secret</c>, <c>.monster</c>, tables. A theme that styles only some of them is not a theme:
/// the same Markdown under another theme's rules for "every picture" put a portrait over the text (section 40).
/// <para>The layout is our own, modelled on Homebrewery's page (a fixed page, two columns that fill from the left, a column break), not copied
/// from its stylesheets; no theme copies a published book's look.</para>
/// </summary>
public static class BookDraftThemes
{
    /// <summary>White pages, Alegreya Sans and Cinzel (the plain book's faces, so the same language gate: English only until the others are proven).</summary>
    public static BookTheme Plain { get; } = new("plain", "Plain", BookThemes.Plain.Languages, BookThemes.Plain.Fonts, PlainCss);

    public static IReadOnlyList<BookTheme> All { get; } = [Plain];

    public static BookTheme? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Rules every theme starts from: the page box, its columns, the screen around it and printing it page for page.</summary>
    public static string PageCss(BookLayout layout)
    {
        var (w, h) = BookThemes.PageMm(layout);
        return $$"""
            @page { size: {{Mm(w)}}mm {{Mm(h)}}mm; margin: 0; }
            html, body { margin: 0; padding: 0; }
            body { background: #5b5b5b; }
            .pages { display: flex; flex-direction: column; align-items: center; gap: 6mm; padding: 6mm 0; counter-reset: page-number; }
            .page { position: relative; box-sizing: border-box; width: {{Mm(w)}}mm; height: {{Mm(h)}}mm; overflow: hidden; counter-increment: page-number;
              padding: 14mm 17mm 18mm; break-after: page; break-inside: avoid; box-shadow: 0 1px 6px rgba(0, 0, 0, .45); }
            .columnWrapper { box-sizing: border-box; height: 100%; width: 100%; column-count: 2; column-fill: auto; column-gap: 8mm; }
            .columnSplit { visibility: hidden; break-after: column; height: 0; margin: 0; }
            .page::after { content: counter(page-number); position: absolute; bottom: 8mm; inset-inline: 0; text-align: center; font-size: 8pt; }
            .page:first-child::after, .page:has(.block.cover)::after { content: none; }
            .art-missing { display: block; box-sizing: border-box; min-height: 18mm; border: 1px dashed #9a9a9a; color: #777; font-size: 7.5pt; padding: 2mm; text-align: center; }
            @media print {
              body { background: none; }
              .pages { display: block; padding: 0; gap: 0; }
              .page { box-shadow: none; }
              .art-missing { visibility: hidden; }
            }
            """;
    }

    private static string PlainCss(BookLayout layout)
    {
        return PageCss(layout) + """
            .page { background: #fff; color: #1f1f1f; font: 9.5pt/1.34 "Alegreya Sans", sans-serif; text-align: start; }
            .page p { margin: 0 0 2.2mm; orphans: 2; widows: 2; }
            .page h1, .page h2, .page h3, .page h4, .page h5, .page h6 { font-family: "Cinzel", serif; font-weight: 400; color: #5a1414; break-after: avoid; margin: 0 0 2mm; line-height: 1.15; }
            .page h1 { column-span: all; font-size: 21pt; border-bottom: 1.5px solid #5a1414; padding-bottom: 1.5mm; margin-bottom: 4mm; }
            .page h2 { font-size: 13pt; margin-top: 3mm; }
            .page h3 { font-size: 11pt; margin-top: 2.5mm; }
            .page h4, .page h5, .page h6 { font-size: 9.5pt; }
            .page ul, .page ol { margin: 0 0 2.2mm; padding-inline-start: 5mm; }
            .page blockquote { margin: 0 0 2.2mm; padding-inline-start: 3mm; border-inline-start: 2px solid #b9a98a; font-style: italic; }
            .page hr { border: 0; border-top: 1px solid #b9a98a; margin: 2.5mm 0; }
            .page a { color: inherit; text-decoration: none; }
            .page img { max-width: 100%; }
            .page table { width: 100%; border-collapse: collapse; margin: 0 0 3mm; font-size: 8.8pt; break-inside: avoid; }
            .page th { text-align: start; border-bottom: 1px solid #5a1414; padding: .6mm 1mm; }
            .page td { padding: .6mm 1mm; vertical-align: top; }
            .page tbody tr:nth-child(odd) { background: #f3eee4; }
            .page .wide { column-span: all; }

            .block.cover { column-span: all; height: 247mm; display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center; }
            .block.cover h1 { border: 0; font-size: 30pt; margin-bottom: 4mm; }
            .block.cover p { font-size: 12pt; font-style: italic; }
            .block.cover img { display: block; width: 100%; max-height: 150mm; object-fit: cover; margin-top: 6mm; }
            .block.toc { column-span: all; }
            .block.toc ul { list-style: none; padding: 0; margin: 0; }
            .block.toc li { margin: 0 0 1.6mm; font-size: 11pt; }
            .block.toc a { display: flex; gap: 2mm; align-items: baseline; }
            .block.toc a::after { content: ""; order: 1; flex: 1; border-bottom: 1px dotted #9a8f7a; }
            .block.toc .toc-page { order: 2; }

            .block.chapter-art { column-span: all; margin: 0 0 4mm; }
            .block.chapter-art img, .block.chapter-art .art-missing { display: block; width: 100%; height: 58mm; object-fit: cover; }
            .block.portrait { float: inline-end; width: 38%; max-width: 32mm; margin: 0 0 2mm; margin-inline-start: 3mm; }
            .block.portrait img, .block.portrait .art-missing { display: block; width: 100%; aspect-ratio: 3 / 4; height: auto; object-fit: cover; object-position: 50% 20%; }
            .block.place { margin: 0 0 2.5mm; break-inside: avoid; }
            .block.place img, .block.place .art-missing { display: block; width: 100%; max-height: 62mm; object-fit: cover; }
            .block.note, .block.descriptive { break-inside: avoid; margin: 0 0 3mm; padding: 2.5mm 3mm; background: #f1ebdd; border-inline-start: 2px solid #5a1414; }
            .block.descriptive { font-style: italic; background: #ece6da; border-inline-start-color: #8b7a5a; }
            .block.secret { break-inside: avoid; margin: 0 0 3mm; padding: 2.5mm 3mm; background: #faf0f0; border: 1.2px dashed #8a1c1c; }
            .block.monster { break-inside: avoid; margin: 0 0 3mm; padding: 2.5mm 3mm; background: #fbf6ec; border-top: 2px solid #5a1414; border-bottom: 2px solid #5a1414; font-size: 8.8pt; }
            .block.monster h4, .block.monster h3 { margin-top: 0; }
            .block > :last-child { margin-bottom: 0; }
            .page:has(.block.cover) .columnWrapper { column-count: 1; }
            """;
    }
}

/// <summary>
/// The page an editable book is drawn into, before its pages are: the theme's faces and stylesheet, the GM's stylesheet (cleaned), a policy that
/// lets nothing be fetched or run, and an empty <c>#brew</c>. The editor's preview frame and the print browser both start from it, and the
/// renderer (<c>wwwroot/lib/book/book-render.js</c>) fills it.
/// </summary>
public static class BookDraftHtml
{
    /// <summary>As the printed book's: no script, no network; inline style, data images and data fonts only.</summary>
    public const string ContentSecurityPolicy = BookHtml.ContentSecurityPolicy;

    public static BookHtmlResult Shell(string title, string language, BookTheme theme, string pageSize, string? gmCss, Func<string, byte[]?> font)
    {
        if (!theme.Supports(language))
        {
            return new BookHtmlResult(null, $"The theme \"{theme.Name}\" does not support the language \"{BookThemes.Primary(language)}\".", [], [], []);
        }

        var layout = BookLayout.For(language, pageSize, 2);
        var css = new StringBuilder();
        var missing = new List<string>();
        foreach (var face in theme.Fonts)
        {
            if (font(face.File) is not { } bytes)
            {
                missing.Add(face.File);
                continue;
            }

            css.Append("@font-face{font-family:\"").Append(face.Family).Append("\";font-weight:").Append(face.Weight).Append(";font-style:").Append(face.Style)
                .Append(";src:url(data:font/woff2;base64,").Append(Convert.ToBase64String(bytes)).Append(") format(\"woff2\");unicode-range:").Append(face.UnicodeRange).Append(";}\n");
        }

        css.Append(theme.Css(layout));
        IReadOnlyList<string> warnings = [];
        var custom = string.IsNullOrWhiteSpace(gmCss) ? string.Empty : BookCss.Clean(gmCss, out warnings);
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"").Append(Enc(BookThemes.Primary(language))).Append("\" dir=\"").Append(layout.Rtl ? "rtl" : "ltr").Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">\n")
            .Append("<title>").Append(Enc(title)).Append("</title>\n<style id=\"theme\">").Append(css).Append("</style>\n<style id=\"gm-css\">").Append(custom).Append("</style>\n")
            .Append("</head>\n<body class=\"book-draft theme-").Append(Enc(theme.Id)).Append("\">\n<div id=\"brew\"></div>\n</body>\n</html>\n");
        return new BookHtmlResult(html.ToString(), null, [], warnings, missing);
    }

    private static string Enc(string text) => WebUtility.HtmlEncode(text);
}
