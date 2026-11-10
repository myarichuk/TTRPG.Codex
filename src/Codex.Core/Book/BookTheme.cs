namespace Codex.Core.Book;

/// <summary>
/// One bundled font file a theme draws with. <paramref name="File"/> is relative to <c>wwwroot/fonts</c>. A face with a
/// <paramref name="Language"/> is embedded only in a book in that language: it is the theme's letters for that script.
/// <para>Such a face may be a <b>stand-in</b>: another typeface's file declared under the theme's own family name, for that script's
/// <paramref name="UnicodeRange"/> only (Forum's Cyrillic as "Cinzel", Assistant's Hebrew as "Alegreya Sans"). Every rule of the theme's sheet,
/// and a GM's stylesheet that names the theme's faces, then draws that script in it with no rule of its own, the way a web font is split into
/// files by script. A stand-in mirrors the weights and styles the family's Latin faces declare, no more: a weight only the stand-in had would
/// win the match for that weight and leave the Latin letters of it to a system font. A Hebrew face in an italic slot is upright on purpose
/// (no Hebrew face here has italics; a slanted Hebrew is a browser's fake, see docs/BOOK_EXPORT.md 41.21).</para>
/// </summary>
public sealed record BookFontFace(string Family, int Weight, string Style, string File, string UnicodeRange, string? Language = null);

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

    /// <summary>The faces a book in <paramref name="language"/> embeds: those of every language, and that language's own; never another script's.</summary>
    public IReadOnlyList<BookFontFace> FontsFor(string language)
    {
        var primary = BookThemes.Primary(language);
        return Fonts.Where(f => f.Language is null || f.Language == primary).ToList();
    }
}

/// <summary>What every theme shares: the scripts the bundled font files are cut for, the page in millimetres, and a language's primary subtag
/// and direction. The themes themselves are <see cref="BookDraftThemes"/>.</summary>
public static class BookThemes
{
    // Latin, Latin-1 and the punctuation a book uses: what the bundled Latin files cover.
    internal const string Latin = "U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+2000-206F, U+2074, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD";

    // Russian letters and the numero sign, as the bundled Cyrillic files are cut (their punctuation, « » and the dashes, is in the Latin files).
    internal const string Cyrillic = "U+0301, U+0400-045F, U+0490-0491, U+04B0-04B1, U+2116";

    // Hebrew letters, points, maqaf, geresh and gershayim, the shekel sign and the presentation forms, as the bundled Hebrew files are cut.
    internal const string Hebrew = "U+0307-0308, U+0590-05FF, U+200C-2010, U+20AA, U+25CC, U+FB1D-FB4F";

    /// <summary>The page in millimetres, for shapes drawn to scale.</summary>
    internal static (double Width, double Height) PageMm(BookLayout layout) =>
        string.Equals(layout.PageSize, "Letter", StringComparison.OrdinalIgnoreCase) ? (215.9, 279.4) : (210.0, 297.0);

    /// <summary>"he-IL" and "HE" to "he".</summary>
    public static string Primary(string language)
    {
        var dash = language.IndexOfAny(['-', '_']);
        return (dash < 0 ? language : language[..dash]).Trim().ToLowerInvariant();
    }

    public static bool IsRtl(string language) => Primary(language) is "he" or "ar" or "fa" or "ur";
}
