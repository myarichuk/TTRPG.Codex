using Codex.Core.Book;

namespace Codex.Tests;

/// <summary>
/// The faces a book prints in, held to the languages it declares (docs/BOOK_EXPORT.md 41.21): each file's character map is read, and every
/// letter of a declared language must be drawn by a bundled face in every weight and style the theme declares, never left to a system font.
/// </summary>
public class BookFontCoverageTests
{
    private static string FontPath(string file) => Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "fonts", file);

    private static string Fixture(string file) => Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Fixtures", "Fonts", file);

    private static readonly Dictionary<string, IReadOnlySet<int>> Maps = new(StringComparer.Ordinal);

    private static IReadOnlySet<int> Map(string file)
    {
        lock (Maps)
        {
            if (!Maps.TryGetValue(file, out var map))
            {
                map = BookFontCoverage.Read(File.ReadAllBytes(FontPath(file))) ?? throw new InvalidOperationException(file + " was not read");
                Maps[file] = map;
            }

            return map;
        }
    }

    /// <summary>What a book in each language must be able to print: its letters, its own punctuation, digits and the common marks.</summary>
    public static string Needed(string language)
    {
        const string common = "0123456789.,;:!?()%&-–—…'\"";
        return language switch
        {
            "ru" => common + string.Concat(Enumerable.Range(0x410, 0x40).Select(c => (char)c)) + "Ёё«»„“№",
            "he" => common + string.Concat(Enumerable.Range(0x5D0, 0x1B).Select(c => (char)c)) + "־׳״₪",
            _ => common + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz“”‘’"
        };
    }

    public static TheoryData<string, string> ThemesAndLanguages()
    {
        var data = new TheoryData<string, string>();
        foreach (var (route, theme) in Themes())
        {
            foreach (var language in theme.Languages)
            {
                data.Add(route + ":" + theme.Id, language);
            }
        }

        return data;
    }

    private static IEnumerable<(string Route, BookTheme Theme)> Themes() =>
        BookThemes.All.Select(t => ("book", t)).Concat(BookDraftThemes.All.Select(t => ("draft", t))).Where(t => t.Item2.Fonts.Count > 0);

    private static BookTheme Theme(string id)
    {
        var (route, name) = (id[..id.IndexOf(':', StringComparison.Ordinal)], id[(id.IndexOf(':', StringComparison.Ordinal) + 1)..]);
        return route == "book" ? BookThemes.Find(name)! : BookDraftThemes.Find(name)!;
    }

    [Theory]
    [MemberData(nameof(ThemesAndLanguages))]
    public void EveryLetterOfADeclaredLanguage_IsInABundledFace_InEveryWeightAndStyleTheThemeDeclares(string themeId, string language)
    {
        var faces = Theme(themeId).FontsFor(language);
        // A family that draws text is one with a Latin face; a family for one character (Ashen Vellum's obelisk) is not held to a language.
        var textFamilies = faces.Where(f => f.Language is null && f.UnicodeRange == BookThemes.Latin).Select(f => f.Family).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var slot in faces.Where(f => textFamilies.Contains(f.Family)).GroupBy(f => (f.Family, f.Weight, f.Style)))
        {
            var lacking = Needed(language).EnumerateRunes().Select(r => r.Value)
                .Where(c => !slot.Any(f => BookFontCoverage.In(BookFontCoverage.Ranges(f.UnicodeRange)!, c) && Map(f.File).Contains(c)))
                .Select(char.ConvertFromUtf32).ToList();
            if (lacking.Count > 0) missing.Add($"{slot.Key.Family} {slot.Key.Weight} {slot.Key.Style}: {string.Concat(lacking)}");
        }

        Assert.True(missing.Count == 0, $"{themeId} ({language}) leaves to a system font:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void AScriptsStandIn_TakesOnlyASlotTheFamilysLatinFacesHave()
    {
        // A weight or style only the stand-in had would win the match for it and leave that weight's Latin letters to a system font.
        foreach (var (_, theme) in Themes())
        {
            var latin = theme.Fonts.Where(f => f.Language is null).Select(f => (f.Family, f.Weight, f.Style)).ToHashSet();
            Assert.All(theme.Fonts.Where(f => f.Language is not null), f => Assert.Contains((f.Family, f.Weight, f.Style), latin));
        }
    }

    [Fact]
    public void ABookEmbedsItsOwnScriptsFaces_AndNoOthers()
    {
        var hebrew = BookDraftThemes.AshenVellum.FontsFor("he-IL");
        var english = BookDraftThemes.AshenVellum.FontsFor("en");

        Assert.Contains(hebrew, f => f.Language == "he");
        Assert.DoesNotContain(hebrew, f => f.Language == "ru");
        Assert.All(english, f => Assert.Null(f.Language));
        Assert.Equal(BookDraftThemes.AshenVellum.Fonts.Count(f => f.Language is null), english.Count);
    }

    [Theory]
    [InlineData("ruslan-display/ruslan-display-cyrillic-400-normal.woff2", 99, 109594)]
    [InlineData("im-fell-english/im-fell-english-latin-400-normal.woff2", 213, 163102)]
    [InlineData("assistant/assistant-hebrew-wght-normal.woff2", 92, 1982969)]
    [InlineData("frank-ruhl-libre/frank-ruhl-libre-hebrew-wght-normal.woff2", 109, 2943837)]
    [InlineData("cinzel/cinzel-latin-wght-normal.woff2", 220, 175938)]
    public void AWoff2sCharacterMap_IsReadAsAFontLibraryReadsIt(string file, int count, long sum)
    {
        // Count and sum of the code points as fontTools' getBestCmap() gives them for the same file.
        var map = Map(file);

        Assert.Equal(count, map.Count);
        Assert.Equal(sum, map.Sum(c => (long)c));
    }

    [Theory]
    [InlineData("ruslan-display-cyrillic.ttf")]
    [InlineData("ruslan-display-cyrillic.woff")]
    public void TrueTypeAndWoffFiles_AreReadToo(string file)
    {
        // The same face as the WOFF2 above, written out by fontTools as a plain TrueType file and as a WOFF.
        var map = BookFontCoverage.Read(File.ReadAllBytes(Fixture(file)));

        Assert.NotNull(map);
        Assert.Equal(Map("ruslan-display/ruslan-display-cyrillic-400-normal.woff2").Order(), map!.Order());
    }

    [Fact]
    public void AFormat4Segment_ThroughItsGlyphArray_TakesItsDeltaAndSkipsGlyphZero()
    {
        // A TrueType file with one cmap, format 4, two segments: 0x41-0x43 through the glyph array with idDelta 5, the glyphs 0, 7 and 65531
        // (65531 + 5 wraps to 0: missing too); then the closing 0xFFFF segment. Only 0x42 is in the font.
        static byte[] U16(params int[] v) => v.SelectMany(x => new[] { (byte)(x >> 8), (byte)x }).ToArray();
        var segments = 2;
        // endCode, pad, startCode, idDelta, idRangeOffset (first: from its own place to the glyph array, 4 bytes on), glyphs.
        var sub = U16(4, 0, 0, segments * 2, 0, 0, 0).Concat(U16(0x43, 0xFFFF)).Concat(U16(0)).Concat(U16(0x41, 0xFFFF)).Concat(U16(5, 1))
            .Concat(U16(4, 0)).Concat(U16(0, 7, 65531)).ToArray();
        sub[2] = (byte)(sub.Length >> 8);
        sub[3] = (byte)sub.Length;
        var cmap = U16(0, 1, 3, 1).Concat(new byte[] { 0, 0, 0, 12 }).Concat(sub).ToArray();
        var font = new byte[] { 0, 1, 0, 0 }.Concat(U16(1, 16, 0, 0)).Concat("cmap"u8.ToArray()).Concat(new byte[4])
            .Concat(new byte[] { 0, 0, 0, 28 }).Concat(new[] { (byte)(cmap.Length >> 24), (byte)(cmap.Length >> 16), (byte)(cmap.Length >> 8), (byte)cmap.Length }).Concat(cmap).ToArray();

        var map = BookFontCoverage.Read(font);

        Assert.Equal([0x42], map!.Order());
    }

    [Fact]
    public void AnythingElse_IsNull_NeverAThrow()
    {
        var woff2 = File.ReadAllBytes(FontPath("ruslan-display/ruslan-display-cyrillic-400-normal.woff2"));
        var random = new Random(7);
        Assert.Null(BookFontCoverage.Read([]));
        Assert.Null(BookFontCoverage.Read("<html>not a font</html>"u8));
        Assert.Null(BookFontCoverage.Read(woff2.AsSpan(0, 60)));
        for (var i = 0; i < 300; i++)
        {
            var broken = woff2.ToArray();
            for (var k = 0; k < 4; k++) broken[random.Next(12, 80)] = (byte)random.Next(256);
            _ = BookFontCoverage.Read(broken);
        }
    }

    [Theory]
    [InlineData("U+0400-045F, U+2116", 0x416, true)]
    [InlineData("U+0400-045F, U+2116", 0x2116, true)]
    [InlineData("U+0400-045F, U+2116", 0x5D0, false)]
    [InlineData("U+4??", 0x4FF, true)]
    [InlineData("U+4??", 0x500, false)]
    public void AUnicodeRange_IsReadAsCssReadsIt(string range, int codePoint, bool inside)
    {
        Assert.Equal(inside, BookFontCoverage.In(BookFontCoverage.Ranges(range)!, codePoint));
    }

    [Fact]
    public void ADraftsCharactersNoFaceDraws_AreNamedByChapter_AndNothingElse()
    {
        var faces = BookDraftThemes.Plain.FontsFor("he");
        var chapters = new[]
        {
            ("cast", "## ורה הולוויי\n\nשומרת על המגדלור, 1311 ₪ — \"Vera\" **bold** {{note\n| a | b |\n}}\u200d\u00a0"),
            ("places", "Гавань Ω and ☃ and 🐉, again Ω and ☃.")
        };

        var uncovered = BookFontCoverage.Uncovered(chapters, faces, Map);

        // Russian is not this book's language: its faces are not embedded, so its letters are named too.
        var places = Assert.Single(uncovered);
        Assert.Equal("places", places.Section);
        // Each once, in the order first seen.
        Assert.Equal("ГавньΩ☃🐉", places.Characters);
    }

    [Fact]
    public void ACharacterAFacesRangeNames_ButItsFileLacks_IsNamed()
    {
        // The bundled Latin files are declared for U+2000-206F, but none has the obelisk; the browser would draw it in a system font.
        var uncovered = BookFontCoverage.Uncovered([("a", "Vera † 1311")], BookDraftThemes.Plain.FontsFor("en"), Map);

        Assert.Equal("†", Assert.Single(uncovered).Characters);
    }

    [Fact]
    public void AChaptersList_IsCapped()
    {
        var greek = string.Concat(Enumerable.Range(0x3B1, 24).Select(c => (char)c));

        var uncovered = BookFontCoverage.Uncovered([("a", greek)], BookDraftThemes.Plain.FontsFor("en"), Map, perChapter: 5);

        var (_, characters, more) = Assert.Single(uncovered);
        Assert.Equal(greek[..5], characters);
        // The rest are counted, not dropped: a chapter's first alphabet does not hide an emoji further on.
        Assert.Equal(19, more);
    }
}
