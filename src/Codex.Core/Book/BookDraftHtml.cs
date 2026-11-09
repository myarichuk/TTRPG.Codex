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
/// <c>.cover-art</c>, <c>.chapter</c>, <c>.chapter-art</c>, <c>.entry</c>, <c>.portrait</c>, <c>.place</c>, <c>.spot</c>, <c>.secret</c> and <c>.properties</c> (a homebrew entry's table of numbers), with Homebrewery's
/// <c>.note</c>, <c>.descriptive</c> and <c>.monster</c> and tables. A theme that styles only some of them is not a theme: the same Markdown under
/// another theme's rule for "every picture" put a portrait over the text (section 40). No theme copies a published book's look.</para>
/// </summary>
public static class BookDraftThemes
{
    /// <summary>White pages, Alegreya Sans and Cinzel (the plain book's faces and languages, Russian and Hebrew by stand-ins, <see cref="BookThemes.Plain"/>).</summary>
    public static BookTheme Plain { get; } = new("plain", "Plain", BookThemes.Plain.Languages, BookThemes.Plain.Fonts, _ => PlainSheet);

    /// <summary>
    /// Homebrewery's 5e layout and nothing over it but black ink on white and frames to see the boxes by (docs/BOOK_EXPORT.md 41.12): the
    /// theme to pick when the GM's CSS was written for a Homebrewery brew, which then lays out as it does there. Its faces are the layer's.
    /// </summary>
    public static BookTheme HomebreweryLayout { get; } = new("homebrewery", "Homebrewery layout", ["en"], [], _ => HomebrewerySheet);

    /// <summary>
    /// The dark-fantasy theme: the "Ashen Vellum" sheet the GM supplied, ported onto Blank (<c>Book/Themes/ashen-vellum.css</c>, whose header
    /// lists what changed). Ash-grey vellum, IM Fell, Pirata One for titles, Special Elite for the GM's secrets; for Russian and Hebrew, stand-ins under those names (below, and <see cref="BookFontFace"/>).
    /// </summary>
    public static BookTheme AshenVellum { get; } = new("ashen-vellum", "Ashen Vellum", ["en", "ru", "he"],
        [
            new("IM Fell English", 400, "normal", "im-fell-english/im-fell-english-latin-400-normal.woff2", BookThemes.Latin),
            new("IM Fell English", 400, "italic", "im-fell-english/im-fell-english-latin-400-italic.woff2", BookThemes.Latin),
            new("IM Fell English SC", 400, "normal", "im-fell-english-sc/im-fell-english-sc-latin-400-normal.woff2", BookThemes.Latin),
            new("IM Fell DW Pica", 400, "normal", "im-fell-dw-pica/im-fell-dw-pica-latin-400-normal.woff2", BookThemes.Latin),
            new("IM Fell DW Pica", 400, "italic", "im-fell-dw-pica/im-fell-dw-pica-latin-400-italic.woff2", BookThemes.Latin),
            new("IM Fell DW Pica SC", 400, "normal", "im-fell-dw-pica-sc/im-fell-dw-pica-sc-latin-400-normal.woff2", BookThemes.Latin),
            new("Pirata One", 400, "normal", "pirata-one/pirata-one-latin-400-normal.woff2", BookThemes.Latin),
            new("Special Elite", 400, "normal", "special-elite/special-elite-latin-400-normal.woff2", BookThemes.Latin),
            // The obelisk of the list markers and page numbers, which no IM Fell file has (the sheet's header).
            new("Ashen Obelisk", 400, "normal", "old-standard-tt/old-standard-tt-latin-ext-400-normal.woff2", "U+2020"),
            // Russian: Old Standard TT for the IM Fell faces (an old-print Cyrillic serif), Ruslan Display for Pirata One's titles, Cousine
            // for the typewriter. The SC families have no Cyrillic small caps: Old Standard's letters stand in, in their own case.
            new("IM Fell English", 400, "normal", "old-standard-tt/old-standard-tt-cyrillic-400-normal.woff2", BookThemes.Cyrillic, "ru"),
            new("IM Fell English", 400, "italic", "old-standard-tt/old-standard-tt-cyrillic-400-italic.woff2", BookThemes.Cyrillic, "ru"),
            new("IM Fell English SC", 400, "normal", "old-standard-tt/old-standard-tt-cyrillic-400-normal.woff2", BookThemes.Cyrillic, "ru"),
            new("IM Fell DW Pica", 400, "normal", "old-standard-tt/old-standard-tt-cyrillic-400-normal.woff2", BookThemes.Cyrillic, "ru"),
            new("IM Fell DW Pica", 400, "italic", "old-standard-tt/old-standard-tt-cyrillic-400-italic.woff2", BookThemes.Cyrillic, "ru"),
            new("IM Fell DW Pica SC", 400, "normal", "old-standard-tt/old-standard-tt-cyrillic-400-normal.woff2", BookThemes.Cyrillic, "ru"),
            new("Pirata One", 400, "normal", "ruslan-display/ruslan-display-cyrillic-400-normal.woff2", BookThemes.Cyrillic, "ru"),
            new("Special Elite", 400, "normal", "cousine/cousine-cyrillic-400-normal.woff2", BookThemes.Cyrillic, "ru"),
            // Hebrew: David Libre for the IM Fell faces (upright in the italic slots, see BookFontFace), Frank Ruhl Libre Black for the titles,
            // Assistant for the typewriter: in a monospaced Hebrew face (Cousine) the narrow letters stand in wide cells and read as gaps in a word.
            new("IM Fell English", 400, "normal", "david-libre/david-libre-hebrew-400-normal.woff2", BookThemes.Hebrew, "he"),
            new("IM Fell English", 400, "italic", "david-libre/david-libre-hebrew-400-normal.woff2", BookThemes.Hebrew, "he"),
            new("IM Fell English SC", 400, "normal", "david-libre/david-libre-hebrew-400-normal.woff2", BookThemes.Hebrew, "he"),
            new("IM Fell DW Pica", 400, "normal", "david-libre/david-libre-hebrew-400-normal.woff2", BookThemes.Hebrew, "he"),
            new("IM Fell DW Pica", 400, "italic", "david-libre/david-libre-hebrew-400-normal.woff2", BookThemes.Hebrew, "he"),
            new("IM Fell DW Pica SC", 400, "normal", "david-libre/david-libre-hebrew-400-normal.woff2", BookThemes.Hebrew, "he"),
            new("Pirata One", 400, "normal", "frank-ruhl-libre/frank-ruhl-libre-hebrew-900-normal.woff2", BookThemes.Hebrew, "he"),
            new("Special Elite", 400, "normal", "assistant/assistant-hebrew-wght-normal.woff2", BookThemes.Hebrew, "he")
        ],
        _ => AshenVellumSheet);

    public static IReadOnlyList<BookTheme> All { get; } = [Plain, HomebreweryLayout, AshenVellum];

    public static BookTheme? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Homebrewery's Blank layout, as built (MIT; its notice is in <c>wwwroot/lib/book/THIRD-PARTY-NOTICES.txt</c>).</summary>
    public static string Blank { get; } = Resource("blank.css");

    /// <summary>
    /// The Homebrewery compatibility layer: Homebrewery's 5e PHB theme with no pictures, colours, shadows or gradients, built by
    /// <c>tools/book-editor/build.mjs</c> (MIT). Its layout only: columns, sizes, spacing, the boxes' geometry, the faces it names.
    /// </summary>
    public static string Layer { get; } = Resource("homebrewery-5e.css");

    /// <summary>The faces the layer names, as Homebrewery declares them (<c>themes/fonts/5e/fonts.less</c>), from <c>wwwroot/fonts/homebrewery-5e</c>
    /// (CC BY-SA 4.0, OFL and Apache 2.0; see its README). Latin only, and not part of any theme's language gate: a theme names its own faces.</summary>
    public static IReadOnlyList<(string Family, string Weight, string Style, string File)> LayerFaces { get; } =
    [
        ("BookInsanityRemake", "normal", "normal", "homebrewery-5e/bookinsanity.woff2"),
        ("BookInsanityRemake", "bold", "normal", "homebrewery-5e/bookinsanity-bold.woff2"),
        ("BookInsanityRemake", "normal", "italic", "homebrewery-5e/bookinsanity-italic.woff2"),
        ("BookInsanityRemake", "bold", "italic", "homebrewery-5e/bookinsanity-bold-italic.woff2"),
        ("ScalySansRemake", "normal", "normal", "homebrewery-5e/scaly-sans.woff2"),
        ("ScalySansRemake", "bold", "normal", "homebrewery-5e/scaly-sans-bold.woff2"),
        ("ScalySansRemake", "normal", "italic", "homebrewery-5e/scaly-sans-italic.woff2"),
        ("ScalySansRemake", "bold", "italic", "homebrewery-5e/scaly-sans-bold-italic.woff2"),
        ("ScalySansSmallCapsRemake", "normal", "normal", "homebrewery-5e/scaly-sans-caps.woff2"),
        ("WalterTurncoat", "normal", "normal", "homebrewery-5e/walterturncoat-regular.woff2"),
        ("MrEavesRemake", "normal", "normal", "homebrewery-5e/mr-eaves-small-caps.woff2"),
        ("SolberaImitationRemake", "100 1000", "italic", "homebrewery-5e/solbera-imitation.woff2"),
        ("NodestoCapsCondensed", "normal", "normal", "homebrewery-5e/nodesto-caps-condensed.woff2"),
        ("NodestoCapsCondensed", "bold", "normal", "homebrewery-5e/nodesto-caps-condensed-bold.woff2"),
        ("NodestoCapsCondensed", "normal", "italic", "homebrewery-5e/nodesto-caps-condensed-italic.woff2"),
        ("NodestoCapsCondensed", "bold", "italic", "homebrewery-5e/nodesto-caps-condensed-bold-italic.woff2"),
        ("NodestoCapsWide", "normal", "normal", "homebrewery-5e/nodesto-caps-wide.woff2"),
        ("Overpass", "500", "normal", "homebrewery-5e/overpass-medium.woff2")
    ];

    // The sheet with its paper textures in place: each {{tile:NAME}} is that embedded picture as a data URI, so a book stores it once.
    private static readonly string AshenVellumSheet = System.Text.RegularExpressions.Regex.Replace(Resource("ashen-vellum.css"), @"\{\{tile:([\w.-]+)\}\}",
        m => (m.Groups[1].Value.EndsWith(".jpg", StringComparison.Ordinal) ? "data:image/jpeg;base64," : "data:image/webp;base64,") + Convert.ToBase64String(Bytes(m.Groups[1].Value)));

    internal static string Resource(string name) => Encoding.UTF8.GetString(Bytes(name));

    private static byte[] Bytes(string name)
    {
        using var stream = typeof(BookDraftThemes).Assembly.GetManifestResourceStream("Codex.Core.Book.Themes." + name)
            ?? throw new InvalidOperationException($"The theme file {name} is not embedded.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// What every theme starts from: Blank, the layer when it is on, then the few rules that make it this book's page. The page takes the book's
    /// paper size (Blank's is Letter); every page is laid out, not only those on screen (Blank leaves pages off screen unrendered, and the
    /// renderer measures them all); and the screen around the pages, and printing them page for page.
    /// </summary>
    public static string PageCss(BookLayout layout, bool homebrewery)
    {
        var (w, h) = BookThemes.PageMm(layout);
        return Blank + (homebrewery ? "\n" + Layer : string.Empty) + $$"""

            /* This book's page, on Blank's. */
            @page { size: {{Mm(w)}}mm {{Mm(h)}}mm; margin: 0; }
            html, body { margin: 0; padding: 0; }
            body { background: #5b5b5b; }
            .pages { display: flex; flex-direction: column; align-items: center; gap: 6mm; padding: 6mm 0; }
            .page { width: {{Mm(w)}}mm; height: {{Mm(h)}}mm; content-visibility: visible; break-after: page; break-inside: avoid; box-shadow: 0 1px 6px rgba(0, 0, 0, .45); }
            /* The generator's blocks that span both columns, in every theme (Blank makes a block inline, which cannot span). */
            .page .block.chapter, .page .block.chapter-art { display: block; column-span: all; }
            /* The tagline the generator writes under the cover's title is not a chapter's first paragraph: no drop cap, under any theme or layer. */
            .page:has(.frontCover) h1 + p::first-letter, .page:has(.frontCover) h1 + p::first-line { all: unset; }
            {{(layout.Rtl ? Unspaced : string.Empty)}}
            .art-missing { display: block; box-sizing: border-box; min-height: 18mm; border: 1px dashed #9a9a9a; color: #777; font-size: 7.5pt; padding: 2mm; text-align: center; }
            @media print {
              body { background: none; }
              .pages { display: block; padding: 0; gap: 0; }
              .page { box-shadow: none; }
              .art-missing { visibility: hidden; }
            }
            """;
    }

    // Hebrew is never letter-spaced: a theme's tracked small caps and title lines pull its letters apart into what reads as broken words (seen on
    // Ashen Vellum's cover). Above the GM's stylesheet only by !important; the GM's own !important still wins, being later.
    private const string Unspaced = ".page, .page *, .page *::first-line, .page *::first-letter { letter-spacing: normal !important; }";

    private const string PlainSheet = """

            /* Plain */
            /* Plain's colours, through Homebrewery's colour variables: the layer's rules then draw in them, and a brew's CSS that sets one is obeyed. */
            :root {
                --HB_Color_Background: #fff; --HB_Color_Accent: #f3eee4; --HB_Color_HeaderUnderline: #b9a98a; --HB_Color_HorizontalRule: #b9a98a;
                --HB_Color_HeaderText: #5a1414; --HB_Color_MonsterStatBackground: #fbf6ec; --HB_Color_CaptionText: #555; --HB_Color_WatercolorStain: #b9a98a;
                --HB_Color_Footnotes: #555;
            }
            .page { column-gap: 8mm; background: var(--HB_Color_Background); color: #1f1f1f; font: 9.5pt/1.34 "Alegreya Sans", sans-serif; text-align: start; }
            .page p { margin: 0 0 2.2mm; orphans: 2; widows: 2; }
            .page h1, .page h2, .page h3, .page h4, .page h5, .page h6 { font-family: "Cinzel", serif; font-weight: 400; color: var(--HB_Color_HeaderText); break-after: avoid; margin: 0 0 2mm; line-height: 1.15; }
            .page h1 { column-span: all; font-size: 21pt; border-bottom: 1.5px solid var(--HB_Color_HeaderText); padding-bottom: 1.5mm; margin-bottom: 4mm; }
            .page h2 { font-size: 13pt; margin-top: 3mm; }
            .page h3 { font-size: 11pt; margin-top: 2.5mm; }
            .page h4, .page h5, .page h6 { font-size: 9.5pt; }
            .page ul, .page ol { margin: 0 0 2.2mm; padding-inline-start: 5mm; }
            .page blockquote { margin: 0 0 2.2mm; padding-inline-start: 3mm; border-inline-start: 2px solid var(--HB_Color_HorizontalRule); font-style: italic; }
            .page hr { border: 0; border-top: 1px solid var(--HB_Color_HorizontalRule); margin: 2.5mm 0; }
            .page a { color: inherit; text-decoration: none; }
            .page img { max-width: 100%; }
            .page table { border-collapse: collapse; margin: 0 0 3mm; font-size: 8.8pt; }
            .page th { text-align: start; border-bottom: 1px solid var(--HB_Color_HeaderText); padding: .6mm 1mm; }
            .page td { padding: .6mm 1mm; vertical-align: top; }
            .page tbody tr:nth-child(odd) { background: var(--HB_Color_Accent); }
            .page .pageNumber { bottom: 8mm; font-size: 8pt; color: var(--HB_Color_Footnotes); }
            /* The running footer: the chapter's name at the page's foot, beside the page number (Blank places that). */
            .page .footnote { position: absolute; right: 80px; bottom: 8mm; width: 60%; font-size: 7.5pt; letter-spacing: .06em; text-transform: uppercase; text-align: right; color: var(--HB_Color_Footnotes); }
            .page:nth-child(even) .footnote { right: auto; left: 80px; text-align: left; }
            /* Plain prints in its own faces wherever the Homebrewery layer names one of its own (those are for a brew's CSS). */
            .page :is(table, .note, .descriptive, .monster, .spellList, .artist, .runeTable th, .toc ul span:last-child, .footnote, .banner, .logo p),
            .page:is(:has(.frontCover), :has(.insideCover), :has(.backCover), :has(.partCover)) :is(p, h2) { font-family: "Alegreya Sans", sans-serif; }
            .page :is(h1, h2, h3, h4, h5, h6, .monster h3, .artist h5),
            .page:is(:has(.frontCover), :has(.insideCover), :has(.backCover), :has(.partCover)) h1,
            .page :is(p.first-letter, p.drop-cap, h1 + p)::first-letter { font-family: "Cinzel", serif; }

            .page:has(.frontCover) { columns: 1; }
            .page:has(.frontCover) .columnWrapper { height: 100%; display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center; }
            .page:has(.frontCover) h1 { border: 0; font-size: 30pt; margin-bottom: 4mm; text-transform: none; }
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
            .block.spot { clear: both; width: 30mm; margin: 1mm auto 3mm; }
            .block.spot img, .block.spot .art-missing { display: block; width: 100%; aspect-ratio: 1; height: auto; object-fit: cover; }
            .block.note, .block.descriptive { margin: 0 0 3mm; padding: 2.5mm 3mm; background: #f1ebdd; border-inline-start: 2px solid #5a1414; }
            .block.descriptive { font-style: italic; background: #ece6da; border-inline-start-color: #8b7a5a; }
            .block.secret { margin: 0 0 3mm; padding: 2.5mm 3mm; background: #faf0f0; border: 1.2px dashed #8a1c1c; }
            .block.monster { margin: 0 0 3mm; padding: 2.5mm 3mm; background: var(--HB_Color_MonsterStatBackground); border-top: 2px solid var(--HB_Color_HeaderText); border-bottom: 2px solid var(--HB_Color_HeaderText); font-size: 8.8pt; }
            .block.monster h4, .block.monster h3 { margin-top: 0; }
            .block.properties { clear: both; margin: 0 0 3mm; break-inside: avoid; }
            .block.properties table { width: 100%; margin: 0 0 1.5mm; }
            .block.properties h5 { margin: 1.5mm 0 1mm; }
            .block > :last-child { margin-bottom: 0; }
            """;

    // Black on white over the layer, with the frames its pictures drew as thin lines; and the generator's own blocks, which Homebrewery has not.
    // Nothing here sets a size, a margin or a face of the layer's: that is what makes it the theme a Homebrewery brew's CSS matches.
    private const string HomebrewerySheet = """

            /* Homebrewery layout. Colours through Homebrewery's own variables, so a brew's CSS that sets them is obeyed. */
            :root { --HB_Color_Background: #FFFFFF; --HB_Color_HeaderText: #000000; }
            .page .note { border-color: #8c8c8c; }
            /* The thick frames the layer keeps (their width is layout) are drawn as a thin line inside them, which takes no room. */
            .page .descriptive, .page .monster.frame { outline: 1px solid #8c8c8c; outline-offset: -4px; }
            .page:has(.frontCover) :is(h1, h2, .banner, .footnote) { color: #000; }
            .page .pageNumber, .page .footnote { color: #444; }
            .block.cover-art { margin-top: 1cm; }
            .block.cover-art img, .block.cover-art .art-missing { display: block; width: 100%; max-height: 13cm; object-fit: cover; }
            .block.chapter-art { margin: 0 0 0.325cm; }
            .block.chapter-art img, .block.chapter-art .art-missing { display: block; width: 100%; height: 5.6cm; object-fit: cover; }
            .block.portrait { float: right; width: 38%; max-width: 3.2cm; margin: 0 0 0.2cm 0.3cm; }
            .block.portrait img, .block.portrait .art-missing { display: block; width: 100%; aspect-ratio: 3 / 4; height: auto; object-fit: cover; object-position: 50% 20%; }
            .block.place img, .block.place .art-missing { display: block; width: 100%; max-height: 6cm; object-fit: cover; }
            .block.spot { clear: both; width: 3cm; margin: 0.1cm auto 0.3cm; }
            .block.spot img, .block.spot .art-missing { display: block; width: 100%; aspect-ratio: 1; height: auto; object-fit: cover; }
            .block.secret { padding: 0.13cm 0.16cm; border: 1px dashed #000; }
            .block.properties { clear: both; margin: 0 0 0.3cm; break-inside: avoid; }
            .block.properties table { width: 100%; }
            """;
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

    public static BookHtmlResult Shell(string title, string language, BookTheme theme, string pageSize, string? gmCss, bool homebrewery,
        Func<string, byte[]?> font, BookCssWeb? web = null)
    {
        if (!theme.Supports(language))
        {
            return new BookHtmlResult(null, $"The theme \"{theme.Name}\" does not support the language \"{BookThemes.Primary(language)}\".", [], [], []);
        }

        var layout = BookLayout.For(language, pageSize, 2);
        var css = new StringBuilder();
        var missing = new List<string>();
        foreach (var face in theme.FontsFor(language))
        {
            if (font(face.File) is not { } bytes)
            {
                missing.Add(face.File);
                continue;
            }

            css.Append("@font-face{font-family:\"").Append(face.Family).Append("\";font-weight:").Append(face.Weight).Append(";font-style:").Append(face.Style)
                .Append(";src:url(data:font/woff2;base64,").Append(Convert.ToBase64String(bytes)).Append(") format(\"woff2\");unicode-range:").Append(face.UnicodeRange).Append(";}\n");
        }

        if (homebrewery)
        {
            foreach (var face in BookDraftThemes.LayerFaces)
            {
                if (font(face.File) is not { } bytes)
                {
                    missing.Add(face.File);
                    continue;
                }

                css.Append("@font-face{font-family:\"").Append(face.Family).Append("\";font-weight:").Append(face.Weight).Append(";font-style:").Append(face.Style)
                    .Append(";src:url(data:font/woff2;base64,").Append(Convert.ToBase64String(bytes)).Append(") format(\"woff2\");}\n");
            }
        }

        // Blank, the layer and the themes are written for left-to-right pages; a right-to-left book gets them mirrored (BookRtl), the GM's CSS as written.
        var sheets = BookDraftThemes.PageCss(layout, homebrewery) + theme.Css(layout);
        css.Append(layout.Rtl ? BookRtl.Mirror(sheets) : sheets);
        IReadOnlyList<string> warnings = [];
        var custom = string.IsNullOrWhiteSpace(gmCss) ? string.Empty : BookCss.Clean(gmCss, out warnings, web);
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"").Append(Enc(BookThemes.Primary(language))).Append("\" dir=\"").Append(layout.Rtl ? "rtl" : "ltr").Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">\n")
            .Append("<title>").Append(Enc(title)).Append("</title>\n<style id=\"theme\">").Append(css).Append("</style>\n<style id=\"gm-css\">").Append(custom).Append("</style>\n")
            .Append("</head>\n<body class=\"book-draft theme-").Append(Enc(theme.Id)).Append(homebrewery ? " homebrewery-layer" : string.Empty).Append("\">\n<div id=\"brew\"></div>\n</body>\n</html>\n");
        return new BookHtmlResult(html.ToString(), null, [], warnings, missing);
    }

    private static string Enc(string text) => WebUtility.HtmlEncode(text);
}
