using System.Globalization;
using System.Text;

namespace Codex.Core.Book;

/// <summary>One bundled font file a theme draws with. <paramref name="File"/> is relative to <c>wwwroot/fonts</c>.</summary>
public sealed record BookFontFace(string Family, int Weight, string Style, string File, string UnicodeRange);

/// <summary>Paper, writing direction and the number of text columns (1 or 2), which the server writes into the CSS: <c>@page size</c> cannot read a custom property.</summary>
public sealed record BookLayout(string PageSize, bool Rtl, int Columns = 1)
{
    /// <summary>Anything but two columns is one: a book has no use for a third.</summary>
    public static BookLayout For(string language, string pageSize = "A4", int columns = 1) =>
        new(string.Equals(pageSize, "Letter", StringComparison.OrdinalIgnoreCase) ? "Letter" : "A4", BookThemes.IsRtl(language), columns == 2 ? 2 : 1);
}

/// <summary>
/// A look for a book, and the languages it can honestly print. The language gate is the point: a theme names the languages whose
/// letters its fonts all have, and a book in another language is refused for that theme instead of printing half in whatever system
/// font has the glyph. What a theme declares is data, and <c>BookThemeTests</c> holds it to the fonts it ships.
/// </summary>
public sealed class BookTheme
{
    public BookTheme(string id, string name, IReadOnlyCollection<string> languages, IReadOnlyList<BookFontFace> fonts, Func<BookLayout, string> css)
    {
        Id = id;
        Name = name;
        Languages = languages.Select(BookThemes.Primary).ToHashSet(StringComparer.Ordinal);
        Fonts = fonts;
        Css = css;
    }

    public string Id { get; }
    public string Name { get; }

    /// <summary>Two-letter language codes (<c>en</c>, <c>ru</c>, <c>he</c>) this theme prints.</summary>
    public IReadOnlySet<string> Languages { get; }

    public IReadOnlyList<BookFontFace> Fonts { get; }

    /// <summary>The theme's stylesheet for a layout. Fonts are not in it: they are added as inline faces by the builder.</summary>
    public Func<BookLayout, string> Css { get; }

    /// <summary>The gate: whether a book in <paramref name="language"/> (<c>ru</c>, <c>he-IL</c>) may use this theme.</summary>
    public bool Supports(string language) => Languages.Contains(BookThemes.Primary(language));
}

/// <summary>The themes a book can choose from.</summary>
public static class BookThemes
{
    // Latin, Latin-1 and the punctuation a book uses: what the bundled Latin files cover.
    private const string Latin = "U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+2000-206F, U+2074, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD";

    /// <summary>
    /// The plain theme: white paper, Alegreya Sans for text and Cinzel for headings, nothing to print that costs ink. It declares
    /// English only: those two faces are the ones whose coverage is proven. Russian and Hebrew follow when their faces (the bundled
    /// Forum, Assistant and Frank Ruhl Libre have no bold, no italic and one weight) are proven to cover a whole book by the
    /// preflight, not before.
    /// </summary>
    public static BookTheme Plain { get; } = new(
        "plain",
        "Plain",
        ["en"],
        [
            new("Alegreya Sans", 400, "normal", "alegreya-sans/alegreya-sans-latin-400-normal.woff2", Latin),
            new("Alegreya Sans", 400, "italic", "alegreya-sans/alegreya-sans-latin-400-italic.woff2", Latin),
            new("Alegreya Sans", 700, "normal", "alegreya-sans/alegreya-sans-latin-700-normal.woff2", Latin),
            new("Cinzel", 400, "normal", "cinzel/cinzel-latin-wght-normal.woff2", Latin)
        ],
        PlainCss);

    /// <summary>
    /// The plain theme's type on a warm paper with torn page edges. The edge is a vector shape drawn on the page background (see
    /// <see cref="BookMasks"/>), a different one on left and right pages, so it costs a few kilobytes and no raster. English only,
    /// for the same reason as <see cref="Plain"/>.
    /// </summary>
    public static BookTheme Parchment { get; } = new(
        "parchment",
        "Parchment",
        Plain.Languages,
        Plain.Fonts,
        layout => PlainCss(layout) + ParchmentCss(layout));

    public static IReadOnlyList<BookTheme> All { get; } = [Plain, Parchment];

    /// <summary>The page in millimetres, for shapes drawn to scale.</summary>
    internal static (double Width, double Height) PageMm(BookLayout layout) =>
        string.Equals(layout.PageSize, "Letter", StringComparison.OrdinalIgnoreCase) ? (215.9, 279.4) : (210.0, 297.0);

    private const string Paper = "#f2e8cf";

    // Chromium places an @page background image from the corner of the page's content box, not of the page, so a page-sized
    // shape is pulled back by the margins (the numbers of PlainCss's @page margin: 2cm 1.9cm 2.2cm). Measured, not assumed.
    private const int MarginTopMm = 20;
    private const int MarginLeftMm = 19;

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ParchmentCss(BookLayout layout)
    {
        var (w, h) = PageMm(layout);
        string Edge(ulong seed) => BookMasks.DataUri(BookMasks.PageEdge(seed, w, h, "#ffffff", "#b8a77c", new BookMasks.EdgeStyle(Depth: 3.4, Roughness: 2.6, CornerChip: 9)));
        // Left and right pages get their own edge; the ids are fixed, so a book prints the same every time.
        return $$"""
            @page { background-color: {{Paper}}; background-size: {{Mm(w)}}mm {{Mm(h)}}mm; background-position: -{{MarginLeftMm}}mm -{{MarginTopMm}}mm; background-repeat: no-repeat; }
            @page :left { background-image: {{Edge(0x1EF7)}}; }
            @page :right { background-image: {{Edge(0x7A11)}}; }
            body { background: transparent; }
            .note, .descriptive { background: rgba(255, 255, 255, .35); }
            .secret { background: rgba(138, 28, 28, .08); }
            .chapter > h1 + p:not(:has(> em:only-child))::first-letter { font-family: "Cinzel", serif; float: {{(layout.Rtl ? "right" : "left")}}; font-size: 4.1em; line-height: .72; padding-inline-end: .06em; padding-top: .04em; }
            """;
    }

    public static BookTheme? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>"he-IL" and "HE" to "he".</summary>
    public static string Primary(string language)
    {
        var dash = language.IndexOfAny(['-', '_']);
        return (dash < 0 ? language : language[..dash]).Trim().ToLowerInvariant();
    }

    public static bool IsRtl(string language) => Primary(language) is "he" or "ar" or "fa" or "ur";

    // A chapter's text runs in two balanced columns; its title, the wide blocks and anything the writer marked ".wide" span both. The
    // title page and the contents stay one column. Boxes and pictures are never split between columns (their break-inside: avoid).
    private static string Columns(BookLayout layout) => layout.Columns != 2 ? string.Empty : """
        .chapter { column-count: 2; column-gap: 9mm; column-fill: balance; }
        .chapter > h1 { column-span: all; }
        table { font-size: 9pt; }
        """;

    // A portrait is a block and not a float: a float that does not fit the rest of a column moves on without its heading, and Chromium does
    // not keep a heading with a float (seen in a render). A block keeps with its heading (break-after: avoid on the heading) and is never split.
    private static string PlainCss(BookLayout layout)
    {
        var inline = layout.Rtl ? "right" : "left";
        var outside = layout.Rtl ? "left" : "right";
        var css = new StringBuilder();
        css.Append(CultureInfo.InvariantCulture, $$"""
            @page { size: {{layout.PageSize}}; margin: 2cm 1.9cm 2.2cm; @bottom-center { content: counter(page); font: 9pt "Alegreya Sans", sans-serif; color: #555; } }
            @page :first { @bottom-center { content: none; } }
            @page :{{inline}} { @bottom-{{inline}} { content: counter(page); font: 9pt "Alegreya Sans", sans-serif; color: #555; } @bottom-center { content: none; } }
            @page :{{outside}} { @bottom-{{outside}} { content: counter(page); font: 9pt "Alegreya Sans", sans-serif; color: #555; } @bottom-center { content: none; } }
            @page :first { @bottom-left { content: none; } @bottom-right { content: none; } }
            * { box-sizing: border-box; }
            html { font-size: 11pt; }
            body { margin: 0; font-family: "Alegreya Sans", sans-serif; line-height: 1.35; color: #1a1a1a; background: #fff; text-align: start; hyphens: manual; }
            h1, h2, h3, h4 { font-family: "Cinzel", "Alegreya Sans", serif; font-weight: 400; line-height: 1.15; break-after: avoid; color: #000; }
            h1 { font-size: 24pt; margin: 0 0 .5em; padding-bottom: .15em; border-bottom: 1.5pt solid #000; }
            h2 { font-size: 16pt; margin: 1.1em 0 .35em; }
            h3 { font-size: 12.5pt; margin: 1em 0 .3em; }
            h4 { font-size: 11pt; margin: .8em 0 .2em; }
            p { margin: 0 0 .6em; orphans: 3; widows: 3; }
            ul, ol { margin: 0 0 .6em; padding-inline-start: 1.4em; }
            li { margin: 0 0 .15em; }
            hr { border: 0; border-top: .75pt solid #888; margin: 1em 0; }
            blockquote { margin: .6em 0; padding-inline-start: .8em; border-inline-start: 2pt solid #999; font-style: italic; }
            table { width: 100%; border-collapse: collapse; margin: .6em 0; break-inside: avoid; font-size: 10pt; }
            th, td { padding: .2em .4em; border-bottom: .5pt solid #bbb; }
            th { text-align: start; border-bottom: 1pt solid #000; }
            pre { white-space: pre-wrap; font: 9pt monospace; border: .5pt solid #bbb; padding: .4em; }
            code { font: .9em monospace; }
            .page-break { break-after: page; }
            .column-break { break-after: column; }
            .chapter { break-before: page; }
            .chapter:first-of-type { break-before: auto; }
            .title-page { break-after: page; text-align: center; padding-top: 28%; }
            .title-page h1 { border: 0; font-size: 34pt; }
            .title-page .tagline { font-style: italic; font-size: 14pt; }
            .toc { break-after: page; }
            .toc ol { list-style: none; padding: 0; }
            .toc .toc-2 { padding-inline-start: 1.2em; font-size: 10pt; }
            .toc a { color: inherit; text-decoration: none; display: flex; align-items: baseline; gap: .4em; }
            .toc-text { min-width: 0; }
            .toc-leader { flex: 1; min-width: 1em; border-bottom: .75pt dotted #888; }
            .toc-page { min-width: 2.5ch; text-align: end; font-variant-numeric: tabular-nums; }
            .note, .descriptive { margin: .7em 0; padding: .5em .7em; border: .75pt solid #888; background: #f4f4f4; break-inside: avoid; }
            .descriptive { font-style: italic; }
            .wide { column-span: all; }
            {{Columns(layout)}}
            .secret { margin: .7em 0; padding: .5em .7em; border-inline-start: 3pt solid #8a1c1c; background: #f6eeee; break-inside: avoid; }
            .slot { display: block; margin: .6em 0; padding: 1.2em .8em; border: 1pt dashed #999; color: #666; text-align: center; font-size: 9pt; break-inside: avoid; }
            .art { display: block; max-width: 100%; max-height: 120mm; margin: .6em auto; break-inside: avoid; }
            .title-page.has-cover { padding-top: 6%; break-inside: avoid; }
            .cover-art { display: block; max-width: 78%; max-height: {{(layout.PageSize == "Letter" ? 115 : 150)}}mm; margin: 0 auto 1.2em; object-fit: contain; }
            .art-portrait { width: 34%; margin: .2em 0 .5em; break-inside: avoid; }
            .art-portrait, .art-place { break-before: avoid; }
            .art-portrait p { margin: 0; }
            .art-portrait .art { width: 100%; max-height: 52mm; margin: 0; object-fit: cover; }
            .art-place { margin: .4em 0 .6em; break-inside: avoid; }
            .art-place p { margin: 0; }
            .art-place .art { width: 100%; max-height: 48mm; margin: 0; object-fit: cover; }
            .mention, .wikilink { font-weight: 700; }
            """);
        return css.ToString();
    }
}
