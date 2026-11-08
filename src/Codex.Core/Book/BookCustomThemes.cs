using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>The languages a book prints, and what each must be able to print (docs/BOOK_EXPORT.md 41.21, 41.22).</summary>
public static class BookScripts
{
    public static IReadOnlyList<string> Languages { get; } = ["en", "ru", "he"];

    /// <summary>A language's letters: what a face must have to be said to print it.</summary>
    public static string Letters(string language) => BookThemes.Primary(language) switch
    {
        "ru" => string.Concat(Enumerable.Range(0x410, 0x40).Select(c => (char)c)) + "Ёё",
        "he" => string.Concat(Enumerable.Range(0x5D0, 0x1B).Select(c => (char)c)),
        _ => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
    };

    /// <summary>Its letters, its own punctuation, digits and the common marks: what a bundled theme is held to (<c>BookFontCoverageTests</c>).</summary>
    public static string Needed(string language)
    {
        const string common = "0123456789.,;:!?()%&-–—…'\"";
        return common + Letters(language) + BookThemes.Primary(language) switch
        {
            "ru" => "«»„“№",
            "he" => "־׳״₪",
            _ => "“”‘’"
        };
    }

    /// <summary>The <c>unicode-range</c> a stand-in for this language is declared for; null for English, which needs none.</summary>
    public static string? Range(string language) => BookThemes.Primary(language) switch
    {
        "ru" => BookThemes.Cyrillic,
        "he" => BookThemes.Hebrew,
        _ => null
    };
}

/// <summary>What a face looks like, for choosing a stand-in of the same kind.</summary>
public enum BookFaceKind
{
    Serif,
    Sans,
    Display,
    Mono
}

/// <summary>
/// A bundled face that can draw one script's letters for a theme whose own faces lack them: declared under the theme's family name, for that
/// script's range only (docs/BOOK_EXPORT.md 41.21). <paramref name="Bold"/> and <paramref name="Italic"/> are used for slots of that kind when
/// present; a variable file (<paramref name="Variable"/>) draws every weight.
/// </summary>
public sealed record BookStandIn(string Id, string Language, BookFaceKind Kind, string Name, string Regular, string? Italic = null, string? Bold = null, bool Variable = false)
{
    /// <summary>The file for a slot: its italic when the slot is italic and there is one, its bold when the slot is bold, else the regular.</summary>
    public string FileFor(string weight, string style)
    {
        var italic = style.Contains("italic", StringComparison.OrdinalIgnoreCase) || style.Contains("oblique", StringComparison.OrdinalIgnoreCase);
        if (italic && Italic != null) return Italic;
        return Bold != null && !Variable && BookStandIns.IsBold(weight) ? Bold : Regular;
    }
}

/// <summary>The stand-ins the wizard offers, by language (all bundled, all under open licences; LEGAL.md).</summary>
public static class BookStandIns
{
    public static IReadOnlyList<BookStandIn> All { get; } =
    [
        new("ru-old-standard", "ru", BookFaceKind.Serif, "Old Standard TT", "old-standard-tt/old-standard-tt-cyrillic-400-normal.woff2",
            "old-standard-tt/old-standard-tt-cyrillic-400-italic.woff2", "old-standard-tt/old-standard-tt-cyrillic-700-normal.woff2"),
        new("ru-alegreya-sans", "ru", BookFaceKind.Sans, "Alegreya Sans", "alegreya-sans/alegreya-sans-cyrillic-400-normal.woff2",
            "alegreya-sans/alegreya-sans-cyrillic-400-italic.woff2", "alegreya-sans/alegreya-sans-cyrillic-700-normal.woff2"),
        new("ru-ruslan", "ru", BookFaceKind.Display, "Ruslan Display", "ruslan-display/ruslan-display-cyrillic-400-normal.woff2"),
        new("ru-forum", "ru", BookFaceKind.Display, "Forum", "forum/forum-cyrillic-400-normal.woff2"),
        new("ru-cousine", "ru", BookFaceKind.Mono, "Cousine", "cousine/cousine-cyrillic-400-normal.woff2", Bold: "cousine/cousine-cyrillic-700-normal.woff2"),
        new("he-david", "he", BookFaceKind.Serif, "David Libre", "david-libre/david-libre-hebrew-400-normal.woff2", Bold: "david-libre/david-libre-hebrew-700-normal.woff2"),
        new("he-frank-ruhl", "he", BookFaceKind.Serif, "Frank Ruhl Libre", "frank-ruhl-libre/frank-ruhl-libre-hebrew-wght-normal.woff2", Variable: true),
        new("he-assistant", "he", BookFaceKind.Sans, "Assistant", "assistant/assistant-hebrew-wght-normal.woff2", Variable: true),
        new("he-frank-ruhl-black", "he", BookFaceKind.Display, "Frank Ruhl Libre Black", "frank-ruhl-libre/frank-ruhl-libre-hebrew-900-normal.woff2")
    ];

    public static BookStandIn? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

    public static IEnumerable<BookStandIn> For(string language) => All.Where(s => s.Language == BookThemes.Primary(language));

    /// <summary>
    /// The stand-in the wizard proposes for a family, from its name: a typewriter or code face takes the monospace one, a sans its sans, a
    /// capitals or titling face (Homebrewery's Nodesto, Solbera, a blackletter) the display one, anything else the serif. Hebrew has no monospace
    /// stand-in: narrow Hebrew letters in wide cells read as gaps inside words, so a Hebrew typewriter face is drawn in the sans.
    /// </summary>
    public static BookStandIn? Propose(string family, string language)
    {
        var name = family.ToLowerInvariant();
        var kind = Regex.IsMatch(name, "mono|code|courier|typewriter|elite|console") ? BookFaceKind.Mono
            : Regex.IsMatch(name, "sans|overpass|eaves|turncoat|grotesk|helvetica|arial") ? BookFaceKind.Sans
            : Regex.IsMatch(name, "caps|nodesto|solbera|pirata|display|title|fraktur|blackletter|gothic|uncial") ? BookFaceKind.Display
            : BookFaceKind.Serif;
        var offered = For(language).ToList();
        if (kind == BookFaceKind.Mono && offered.All(s => s.Kind != BookFaceKind.Mono)) kind = BookFaceKind.Sans;
        return offered.FirstOrDefault(s => s.Kind == kind) ?? offered.FirstOrDefault();
    }

    /// <summary>Whether a <c>font-weight</c> descriptor names a bold weight (600 or more, "bold"); a range is not.</summary>
    public static bool IsBold(string weight)
    {
        var w = weight.Trim().ToLowerInvariant();
        if (w is "bold" or "bolder") return true;
        return !w.Contains(' ', StringComparison.Ordinal) && int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 600;
    }
}

/// <summary>One <c>@font-face</c> a theme declares: its family, weight and style as written, its range, and the letters its file has (null when
/// the file could not be read: a face with no data source, a collection, an EOT).</summary>
public sealed record BookThemeFace(string Family, string Weight, string Style, IReadOnlyList<(int From, int To)>? Range, IReadOnlySet<int>? Map);

/// <summary>How a theme's text faces stand for one language: those that draw its letters, and those that do not (each needs a stand-in).</summary>
public sealed record BookThemeLanguage(string Language, IReadOnlyList<string> Covered, IReadOnlyList<string> Lacking);

/// <summary>
/// A theme the GM brought in (docs/BOOK_EXPORT.md 41.22): its stylesheet, cleaned, and the faces it declares measured. Built per book language,
/// with the stand-ins the GM chose for that language declared after the theme's own faces, under the same family names and slots.
/// </summary>
public static class BookCustomTheme
{
    /// <summary>The prefix of a campaign theme's id in a draft's settings; a built-in theme's id never has it.</summary>
    public const string Prefix = "custom:";

    private static readonly Regex FontFace = new(@"@font-face\s*\{(?<body>[^{}]*)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex Descriptor = new(@"(?<name>font-family|font-weight|font-style|unicode-range|src)\s*:\s*(?<value>(?:url\([^)]*\)|""[^""]*""|'[^']*'|[^;])*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex DataFont = new(@"url\(\s*[""']?data:[^;,]*;base64,(?<data>[A-Za-z0-9+/=]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <summary>The faces a cleaned stylesheet declares (only data sources remain after cleaning: the web's are written in as copies).</summary>
    public static IReadOnlyList<BookThemeFace> Faces(string cleanedCss)
    {
        var faces = new List<BookThemeFace>();
        foreach (Match face in FontFace.Matches(cleanedCss))
        {
            string family = string.Empty, weight = "normal", style = "normal";
            IReadOnlyList<(int, int)>? range = null;
            IReadOnlySet<int>? map = null;
            foreach (Match d in Descriptor.Matches(face.Groups["body"].Value))
            {
                var value = d.Groups["value"].Value.Trim();
                switch (d.Groups["name"].Value.ToLowerInvariant())
                {
                    case "font-family":
                        family = value.Trim('"', '\'').Trim();
                        break;
                    case "font-weight":
                        weight = value;
                        break;
                    case "font-style":
                        style = value;
                        break;
                    case "unicode-range":
                        range = BookFontCoverage.Ranges(value);
                        break;
                    case "src":
                        foreach (Match data in DataFont.Matches(value))
                        {
                            try
                            {
                                map = BookFontCoverage.Read(Convert.FromBase64String(data.Groups["data"].Value));
                            }
                            catch (FormatException)
                            {
                                map = null;
                            }

                            if (map != null) break;
                        }

                        break;
                }
            }

            if (family.Length > 0) faces.Add(new BookThemeFace(family, weight, style, range, map));
        }

        return faces;
    }

    private static readonly Regex FamilyUse = new(@"font-family\s*:\s*(?<value>[^;{}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex FontShorthand = new(@"(?<![-\w])font\s*:\s*(?<value>[^;{}]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace", "ui-rounded", "math", "emoji",
        "fangsong", "inherit", "initial", "unset", "revert", "revert-layer"
    };

    /// <summary>The families a stylesheet's rules name for text (outside its <c>@font-face</c> rules), generic ones and variables left out:
    /// with <see cref="Faces"/>, which of them it does not bring.</summary>
    public static IReadOnlyList<string> Named(string cleanedCss)
    {
        var rules = FontFace.Replace(cleanedCss, string.Empty);
        var names = new List<string>();
        foreach (var value in FamilyUse.Matches(rules).Select(m => m.Groups["value"].Value)
                     .Concat(FontShorthand.Matches(rules).Select(m => m.Groups["value"].Value).Where(v => v.Contains(',') || v.Contains('"') || v.Contains('\'')).Select(v =>
                     {
                         // In the shorthand the family list is what follows the size: from the first quote, or the last word before the first comma.
                         var at = v.IndexOfAny(['"', '\'']);
                         return at >= 0 ? v[at..] : v[(v[..v.IndexOf(',')].LastIndexOf(' ') + 1)..];
                     })))
        {
            foreach (var raw in value.Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Split(','))
            {
                var name = raw.Trim().Trim('"', '\'').Trim();
                if (name.Length == 0 || name.Contains('(') || Generic.Contains(name) || names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>The faces of Homebrewery's layer, measured from the bundled files, for a theme that sits on it.</summary>
    public static IReadOnlyList<BookThemeFace> LayerFaces(Func<string, byte[]?> font) =>
        BookDraftThemes.LayerFaces.Select(f => new BookThemeFace(f.Family, f.Weight, f.Style, null, font(f.File) is { } b ? BookFontCoverage.Read(b) : null)).ToList();

    private static bool Draws(IEnumerable<BookThemeFace> family, int c) =>
        family.Any(f => f.Map != null && f.Map.Contains(c) && (f.Range == null || BookFontCoverage.In(f.Range, c)));

    /// <summary>The families that draw text: those whose faces have the Latin letters. A family of pictures (dice, icons) is not held to a language.</summary>
    public static IReadOnlyList<string> TextFamilies(IReadOnlyList<BookThemeFace> faces) =>
        faces.GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .Where(g => BookScripts.Letters("en").All(c => Draws(g, c)))
            .Select(g => g.First().Family).ToList();

    /// <summary>For each language, which text families draw its letters and which do not.</summary>
    public static IReadOnlyList<BookThemeLanguage> Measure(IReadOnlyList<BookThemeFace> faces)
    {
        var text = TextFamilies(faces);
        return BookScripts.Languages.Select(language =>
        {
            var letters = BookScripts.Letters(language);
            var covered = text.Where(f => letters.All(c => Draws(faces.Where(x => string.Equals(x.Family, f, StringComparison.OrdinalIgnoreCase)), c))).ToList();
            return new BookThemeLanguage(language, covered, text.Except(covered, StringComparer.OrdinalIgnoreCase).ToList());
        }).ToList();
    }

    /// <summary>
    /// The stand-in faces for a language: for each family that lacks its letters and has a chosen stand-in, one <c>@font-face</c> per slot the
    /// family declares (its weight and style as written, so a weight range or keyword is kept), for the script's range only. Written after the
    /// theme's own sheet, so for those letters the stand-in is tried first.
    /// </summary>
    public static string StandInCss(IReadOnlyList<BookThemeFace> faces, string language, IReadOnlyDictionary<string, string> chosen, Func<string, byte[]?> font)
    {
        if (BookScripts.Range(language) is not { } range) return string.Empty;
        var css = new StringBuilder();
        var embedded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var family in faces.GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase))
        {
            var pick = chosen.FirstOrDefault(c => string.Equals(c.Key, family.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (BookStandIns.Find(pick) is not { } standIn || standIn.Language != BookThemes.Primary(language)) continue;
            foreach (var slot in family.Select(f => (f.Weight, f.Style)).Distinct())
            {
                var file = standIn.FileFor(slot.Weight, slot.Style);
                if (!embedded.TryGetValue(file, out var data))
                {
                    if (font(file) is not { } bytes) continue;
                    data = Convert.ToBase64String(bytes);
                    embedded[file] = data;
                }

                css.Append("@font-face{font-family:\"").Append(family.First().Family.Replace("\"", string.Empty, StringComparison.Ordinal)).Append("\";font-weight:")
                    .Append(Safe(slot.Weight)).Append(";font-style:").Append(Safe(slot.Style)).Append(";src:url(data:font/woff2;base64,").Append(data)
                    .Append(") format(\"woff2\");unicode-range:").Append(range).Append(";}\n");
            }
        }

        return css.ToString();
    }

    // A descriptor written back: only what a weight or style can be (keywords, numbers, a range), never anything that could close the rule.
    private static string Safe(string value) => Regex.IsMatch(value, @"^[a-zA-Z0-9 .%-]{1,40}$") ? value : "normal";

    /// <summary>What the stand-ins draw, for the editor's check: each stand-in face's letters inside the script's range.</summary>
    public static IEnumerable<BookThemeFace> StandInFaces(IReadOnlyList<BookThemeFace> faces, string language, IReadOnlyDictionary<string, string> chosen,
        Func<string, IReadOnlySet<int>?> map)
    {
        if (BookScripts.Range(language) is not { } range) yield break;
        var ranges = BookFontCoverage.Ranges(range);
        foreach (var family in faces.GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase))
        {
            var pick = chosen.FirstOrDefault(c => string.Equals(c.Key, family.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (BookStandIns.Find(pick) is not { } standIn || standIn.Language != BookThemes.Primary(language)) continue;
            yield return new BookThemeFace(family.Key, "normal", "normal", ranges, map(standIn.Regular));
        }
    }
}
