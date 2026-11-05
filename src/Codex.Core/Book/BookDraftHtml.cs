using System.Globalization;
using System.Net;
using System.Text;

namespace Codex.Core.Book;

/// <summary>
/// The themes of the editable book (docs/BOOK_EXPORT.md 41.2). Each sits on Homebrewery's Blank layout (<c>Book/Themes/blank.css</c>, built from
/// <c>tools/book-editor/themes/blank.less</c>), the layout every Homebrewery theme sits on, so a page has Homebrewery's structure and defaults
/// (<c>.page</c> with its two columns, <c>.columnWrapper</c>, <c>.columnSplit</c>, <c>.block</c>, <c>.pageNumber</c>, <c>.toc</c>, <c>.wide</c>) and
/// CSS written for Homebrewery lays out here as it does there.
/// <para>On top of it, every theme styles every block of the generator's contract: Homebrewery's <c>.frontCover</c> page and <c>.toc</c>, and our
/// <c>.cover-art</c>, <c>.chapter</c>, <c>.chapter-art</c>, <c>.entry</c>, <c>.portrait</c>, <c>.place</c> and <c>.secret</c>, with Homebrewery's
/// <c>.note</c>, <c>.descriptive</c> and <c>.monster</c> and tables. A theme that styles only some of them is not a theme: the same Markdown under
/// another theme's rule for "every picture" put a portrait over the text (section 40). No theme copies a published book's look.</para>
/// </summary>
public static class BookDraftThemes
{
    /// <summary>White pages, Alegreya Sans and Cinzel (the plain book's faces, so the same language gate: English only until the others are proven).</summary>
    public static BookTheme Plain { get; } = new("plain", "Plain", BookThemes.Plain.Languages, BookThemes.Plain.Fonts, PlainCss);

    public static IReadOnlyList<BookTheme> All { get; } = [Plain];

    public static BookTheme? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Homebrewery's Blank layout, as built (MIT; its notice is in <c>wwwroot/lib/book/THIRD-PARTY-NOTICES.txt</c>).</summary>
    public static string Blank { get; } = Resource("blank.css");

    internal static string Resource(string name)
    {
        using var stream = typeof(BookDraftThemes).Assembly.GetManifestResourceStream("Codex.Core.Book.Themes." + name)
            ?? throw new InvalidOperationException($"The theme sheet {name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// What every theme starts from: Blank, then the few rules that make it this book's page. The page takes the book's paper size (Blank's is
    /// Letter); every page is laid out, not only those on screen (Blank leaves pages off screen unrendered, and the renderer measures them all);
    /// and the screen around the pages, and printing them page for page.
    /// </summary>
    public static string PageCss(BookLayout layout)
    {
        var (w, h) = BookThemes.PageMm(layout);
        return Blank + $$"""

            /* This book's page, on Blank's. */
            @page { size: {{Mm(w)}}mm {{Mm(h)}}mm; margin: 0; }
            html, body { margin: 0; padding: 0; }
            body { background: #5b5b5b; }
            .pages { display: flex; flex-direction: column; align-items: center; gap: 6mm; padding: 6mm 0; }
            .page { width: {{Mm(w)}}mm; height: {{Mm(h)}}mm; content-visibility: visible; break-after: page; break-inside: avoid; box-shadow: 0 1px 6px rgba(0, 0, 0, .45); }
            /* The generator's blocks that span both columns, in every theme (Blank makes a block inline, which cannot span). */
            .page .block.chapter, .page .block.chapter-art { display: block; column-span: all; }
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

            /* Plain */
            .page { column-gap: 8mm; background: #fff; color: #1f1f1f; font: 9.5pt/1.34 "Alegreya Sans", sans-serif; text-align: start; }
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
            .page table { border-collapse: collapse; margin: 0 0 3mm; font-size: 8.8pt; }
            .page th { text-align: start; border-bottom: 1px solid #5a1414; padding: .6mm 1mm; }
            .page td { padding: .6mm 1mm; vertical-align: top; }
            .page tbody tr:nth-child(odd) { background: #f3eee4; }
            .page .pageNumber { bottom: 8mm; font-size: 8pt; color: #555; }

            .page:has(.frontCover) { columns: 1; }
            .page:has(.frontCover) .columnWrapper { height: 100%; display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center; }
            .page:has(.frontCover) h1 { border: 0; font-size: 30pt; margin-bottom: 4mm; }
            .page:has(.frontCover) p { font-size: 12pt; font-style: italic; }
            .block.cover-art { margin-top: 6mm; }
            .block.cover-art img, .block.cover-art .art-missing { display: block; width: 100%; max-height: 150mm; object-fit: cover; }
            .block.toc ul { list-style: none; padding: 0; margin: 0; }
            .block.toc li { margin: 0 0 1.6mm; font-size: 11pt; }

            .block.chapter-art { column-span: all; margin: 0 0 4mm; }
            .block.chapter-art img, .block.chapter-art .art-missing { display: block; width: 100%; height: 58mm; object-fit: cover; }
            .block.portrait { float: inline-end; width: 38%; max-width: 32mm; margin: 0 0 2mm; margin-inline-start: 3mm; }
            .block.portrait img, .block.portrait .art-missing { display: block; width: 100%; aspect-ratio: 3 / 4; height: auto; object-fit: cover; object-position: 50% 20%; }
            .block.place { margin: 0 0 2.5mm; }
            .block.place img, .block.place .art-missing { display: block; width: 100%; max-height: 62mm; object-fit: cover; }
            .block.note, .block.descriptive { margin: 0 0 3mm; padding: 2.5mm 3mm; background: #f1ebdd; border-inline-start: 2px solid #5a1414; }
            .block.descriptive { font-style: italic; background: #ece6da; border-inline-start-color: #8b7a5a; }
            .block.secret { margin: 0 0 3mm; padding: 2.5mm 3mm; background: #faf0f0; border: 1.2px dashed #8a1c1c; }
            .block.monster { margin: 0 0 3mm; padding: 2.5mm 3mm; background: #fbf6ec; border-top: 2px solid #5a1414; border-bottom: 2px solid #5a1414; font-size: 8.8pt; }
            .block.monster h4, .block.monster h3 { margin-top: 0; }
            .block > :last-child { margin-bottom: 0; }
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
