using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Web.Book;

/// <summary>A theme as a book draws it: the theme, and when it is one the GM brought in, the faces it draws with (its own, the layer's, and
/// the stand-ins for the book's language), which the editor's check of characters reads.</summary>
public sealed record BookResolvedTheme(BookTheme Theme, IReadOnlyList<BookThemeFace>? Faces, bool? Homebrewery);

/// <summary>What the wizard found in a pasted stylesheet: each face it declares (and the layer's, when it sits on it), each language with the
/// text families that draw its letters and those that do not, and the families it names that nothing brings (a system font on the server).</summary>
public sealed record BookThemeMeasure(IReadOnlyList<BookThemeFace> Faces, IReadOnlyList<BookThemeLanguage> Languages, IReadOnlyList<string> Unbrought,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The themes a campaign's book can use (docs/BOOK_EXPORT.md 41.22): the built-in ones, and the ones the GM brought in with the theme wizard.
/// Every place that turns a draft's theme id into a theme goes through here (the editor's preview, its check of characters, the PDF and Fit
/// pages), so a campaign theme prints as it previews.
/// </summary>
public sealed class BookThemeCatalog(IBookDraftRepository store)
{
    /// <summary>The draft's theme for <paramref name="language"/>: a built-in one, a ready campaign theme, or Plain when the id names nothing
    /// (a campaign theme deleted since).</summary>
    public async Task<BookResolvedTheme> ResolveAsync(CampaignAccess dm, string themeId, string language, Func<string, byte[]?> font)
    {
        if (BookDraftThemes.Find(themeId) is { } builtIn) return new BookResolvedTheme(builtIn, null, null);
        if (themeId.StartsWith(BookCustomTheme.Prefix, StringComparison.Ordinal)
            && await store.ThemeAsync(dm, themeId[BookCustomTheme.Prefix.Length..]) is { Ready: true } doc)
        {
            var web = await store.ThemeWebAsync(dm, doc.Key);
            return Build(themeId, doc.Name, doc.Css, doc.Homebrewery, doc.Languages, doc.StandIns, web, language, font);
        }

        return new BookResolvedTheme(BookDraftThemes.Plain, null, null);
    }

    /// <summary>The faces of a stylesheet, cleaned with its copies, and what they print.</summary>
    public static BookThemeMeasure Measure(string css, bool homebrewery, BookCssWeb web, Func<string, byte[]?> font)
    {
        var cleaned = BookCss.Clean(css, out var warnings, web);
        var own = BookCustomTheme.Faces(cleaned);
        var faces = homebrewery ? own.Concat(BookCustomTheme.LayerFaces(font)).ToList() : own.ToList();
        var declared = faces.Select(f => f.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unbrought = BookCustomTheme.Named(cleaned).Where(f => !declared.Contains(f)).ToList();
        return new BookThemeMeasure(faces, BookCustomTheme.Measure(faces), unbrought, warnings);
    }

    /// <summary>A theme from its parts, for one language: the wizard's preview and a stored theme are built the same way.</summary>
    public static BookResolvedTheme Build(string id, string name, string css, bool homebrewery, IReadOnlyCollection<string> languages,
        IReadOnlyDictionary<string, Dictionary<string, string>> standIns, BookCssWeb web, string language, Func<string, byte[]?> font)
    {
        var cleaned = BookCss.Clean(css, out _, web);
        var own = BookCustomTheme.Faces(cleaned);
        var faces = homebrewery ? own.Concat(BookCustomTheme.LayerFaces(font)).ToList() : own.ToList();
        var chosen = standIns.TryGetValue(BookThemes.Primary(language), out var c) ? c : new Dictionary<string, string>();
        // The stand-ins after the theme's own faces: for their script's letters they are tried first.
        // A theme on the Homebrewery layer was written against Homebrewery's reset (an h1 has no margin above it there), so it gets it first.
        var sheet = (homebrewery ? BookDraftThemes.HomebreweryReset : string.Empty) + cleaned + "\n/* Stand-ins for this book's language (docs/BOOK_EXPORT.md 41.21). */\n" + BookCustomTheme.StandInCss(faces, language, chosen, font);
        var maps = new Dictionary<string, IReadOnlySet<int>?>(StringComparer.Ordinal);
        var drawn = faces.Concat(BookCustomTheme.StandInFaces(faces, language, chosen,
            file => maps.TryGetValue(file, out var m) ? m : maps[file] = font(file) is { } b ? BookFontCoverage.Read(b) : null)).ToList();
        return new BookResolvedTheme(new BookTheme(id, name, languages, [], _ => sheet), drawn, homebrewery);
    }
}
