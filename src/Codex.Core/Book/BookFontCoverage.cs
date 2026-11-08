using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Codex.Core.Book;

/// <summary>
/// Which characters a font file has: its <c>cmap</c>, read from a WOFF2, WOFF or plain TrueType/OpenType file (docs/BOOK_EXPORT.md 41.21). It is
/// what proves a theme prints a language (<c>BookFontCoverageTests</c>), what the editor's check names a draft's characters against, and what a
/// theme brought in by the GM is measured with. A file is untrusted: every offset and length is checked, decompression is bounded, and a file
/// that cannot be read is <c>null</c>, never an exception.
/// </summary>
public static class BookFontCoverage
{
    /// <summary>The most a font may unpack to. The bundled faces are under 1 MB unpacked; a CJK face is about 20.</summary>
    public const int MaxUnpackedBytes = 48 * 1024 * 1024;

    /// <summary>The code points the font maps to a glyph, or <c>null</c> when the file is not a font this reads (a collection, a broken file).</summary>
    public static IReadOnlySet<int>? Read(ReadOnlySpan<byte> font)
    {
        try
        {
            var cmap = Table(font, "cmap");
            return cmap is null ? null : Cmap(cmap);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The code points a CSS <c>unicode-range</c> names (<c>U+0400-045F, U+2116, U+4??</c>); <c>null</c> when it cannot be read.</summary>
    public static IReadOnlyList<(int From, int To)>? Ranges(string unicodeRange)
    {
        var list = new List<(int, int)>();
        foreach (var raw in unicodeRange.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length < 3 || (raw[0] != 'U' && raw[0] != 'u') || raw[1] != '+')
            {
                return null;
            }

            var body = raw[2..];
            var dash = body.IndexOf('-', StringComparison.Ordinal);
            if (dash >= 0)
            {
                if (!Hex(body[..dash], out var a) || !Hex(body[(dash + 1)..], out var b) || b < a) return null;
                list.Add((a, b));
            }
            else if (body.Contains('?', StringComparison.Ordinal))
            {
                if (!Hex(body.Replace('?', '0'), out var a) || !Hex(body.Replace('?', 'F'), out var b)) return null;
                list.Add((a, b));
            }
            else
            {
                if (!Hex(body, out var a)) return null;
                list.Add((a, a));
            }
        }

        return list;
    }

    /// <summary>Whether <paramref name="codePoint"/> is in <paramref name="ranges"/>.</summary>
    public static bool In(IReadOnlyList<(int From, int To)> ranges, int codePoint)
    {
        foreach (var (from, to) in ranges)
        {
            if (codePoint >= from && codePoint <= to) return true;
        }

        return false;
    }

    /// <summary>
    /// The characters of each chapter that no face in <paramref name="faces"/> draws (its <c>unicode-range</c> names them and its file has them),
    /// at most <paramref name="perChapter"/> a chapter, in the order they first appear, and how many more there are. Spaces, controls and joiners are not letters to draw and
    /// are not listed. This is the editor's check before printing (docs/BOOK_EXPORT.md 41.21): such a character prints in whatever font the
    /// print browser's system has, or as a box.
    /// </summary>
    public static IReadOnlyList<(string Section, string Characters, int More)> Uncovered(IEnumerable<(string Section, string Text)> chapters,
        IReadOnlyList<BookFontFace> faces, Func<string, IReadOnlySet<int>?> map, int perChapter = 24) =>
        Uncovered(chapters, faces.Select(f => new BookThemeFace(f.Family, f.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Style,
            Ranges(f.UnicodeRange) ?? [], map(f.File))).ToList(), perChapter);

    /// <summary>The same, for faces already measured (a theme the GM brought in, with its stand-ins). A face with no range draws all it has.</summary>
    public static IReadOnlyList<(string Section, string Characters, int More)> Uncovered(IEnumerable<(string Section, string Text)> chapters,
        IReadOnlyList<BookThemeFace> faces, int perChapter = 24)
    {
        // By family: a letter counts as drawn only when every family that draws text has it (a family whose faces have the Latin letters).
        // Which family an element uses is the stylesheet's business; a letter only some families have prints in a system font wherever the
        // others are used (seen: a campaign theme's text face fell back to Times for Cyrillic while the layer's faces, unused there, had it).
        var families = faces.Where(f => f.Map != null).GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Select(f => (Ranges: f.Range ?? [(0, 0x10FFFF)], Map: f.Map!)).ToList()).ToList();
        bool Has(List<(IReadOnlyList<(int From, int To)> Ranges, IReadOnlySet<int> Map)> family, int c) => family.Any(f => In(f.Ranges, c) && f.Map.Contains(c));
        var textFamilies = families.Where(f => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".All(c => Has(f, c))).ToList();
        if (textFamilies.Count == 0) textFamilies = families;
        var known = new Dictionary<int, bool>();
        var result = new List<(string, string, int)>();
        foreach (var (section, text) in chapters)
        {
            var found = new List<int>();
            var more = new HashSet<int>();
            foreach (var rune in text.EnumerateRunes())
            {
                var c = rune.Value;
                if (found.Contains(c) || more.Contains(c) || !Drawable(rune)) continue;
                if (!known.TryGetValue(c, out var covered))
                {
                    // Letters are held to every text family (a script falls back where any one lacks it); digits, punctuation and symbols to
                    // any face, as a display face may carry none and the Markdown's own marks ({, #, [) never print anyway.
                    covered = Rune.IsLetter(rune) ? textFamilies.Count > 0 && textFamilies.All(f => Has(f, c)) : families.Any(f => Has(f, c));
                    known[c] = covered;
                }

                if (covered) continue;
                if (found.Count < perChapter) found.Add(c);
                else more.Add(c);
            }

            if (found.Count > 0) result.Add((section, string.Concat(found.Select(char.ConvertFromUtf32)), more.Count));
        }

        return result;
    }

    private static bool Drawable(Rune rune) => Rune.GetUnicodeCategory(rune) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
        or System.Globalization.UnicodeCategory.SpaceSeparator or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
        or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned)
        && rune.Value is not (>= 0xFE00 and <= 0xFE0F);

    private static bool Hex(string text, out int value) =>
        int.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 0x10FFFF && text.Length <= 6;

    // ---- containers ----

    private static byte[]? Table(ReadOnlySpan<byte> font, string tag)
    {
        if (font.Length < 12) return null;
        var signature = Encoding.ASCII.GetString(font[..4]);
        return signature switch
        {
            "wOF2" => Woff2(font, tag),
            "wOFF" => Woff(font, tag),
            "ttcf" => null,
            _ => Sfnt(font, tag)
        };
    }

    private static byte[]? Sfnt(ReadOnlySpan<byte> font, string tag)
    {
        var version = BinaryPrimitives.ReadUInt32BigEndian(font);
        if (version is not (0x00010000 or 0x4F54544F or 0x74727565)) return null; // 1.0, 'OTTO', 'true'
        int count = BinaryPrimitives.ReadUInt16BigEndian(font[4..]);
        for (var i = 0; i < count; i++)
        {
            var record = font.Slice(12 + i * 16, 16);
            if (Encoding.ASCII.GetString(record[..4]) != tag) continue;
            var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]));
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
            return font.Slice(offset, length).ToArray();
        }

        return null;
    }

    private static byte[]? Woff(ReadOnlySpan<byte> font, string tag)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font[12..]);
        for (var i = 0; i < count; i++)
        {
            var entry = font.Slice(44 + i * 20, 20);
            if (Encoding.ASCII.GetString(entry[..4]) != tag) continue;
            var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]));
            var compressed = checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]));
            var original = checked((int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]));
            if (original > MaxUnpackedBytes) return null;
            var data = font.Slice(offset, compressed);
            if (compressed == original) return data.ToArray();
            using var input = new MemoryStream(data.ToArray());
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            return ReadExactly(z, original);
        }

        return null;
    }

    // The tags WOFF2 names by number (its "known tags" table); only cmap, glyf and loca matter here, the rest are counted past.
    private const int KnownCmap = 0, KnownGlyf = 10, KnownLoca = 11;

    private static byte[]? Woff2(ReadOnlySpan<byte> font, string tag)
    {
        if (Encoding.ASCII.GetString(font.Slice(4, 4)) == "ttcf") return null;
        int count = BinaryPrimitives.ReadUInt16BigEndian(font[12..]);
        var compressedSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(font[20..]));
        var at = 48;
        long offset = 0;
        long wanted = -1, wantedLength = 0;
        for (var i = 0; i < count; i++)
        {
            var flags = font[at++];
            var known = flags & 0x3F;
            var transform = flags >> 6;
            string name;
            if (known == 63)
            {
                name = Encoding.ASCII.GetString(font.Slice(at, 4));
                at += 4;
            }
            else
            {
                name = known == KnownCmap ? "cmap" : known == KnownGlyf ? "glyf" : known == KnownLoca ? "loca" : "#" + known;
            }

            var length = Base128(font, ref at);
            // glyf and loca are transformed unless their version is 3; any other table is transformed unless its version is 0.
            var transformed = name is "glyf" or "loca" ? transform != 3 : transform != 0;
            if (transformed)
            {
                length = Base128(font, ref at);
            }

            if (name == tag && !transformed)
            {
                wanted = offset;
                wantedLength = length;
            }

            offset += length;
        }

        if (wanted < 0 || offset > MaxUnpackedBytes) return null;
        var unpacked = new byte[offset];
        if (!BrotliDecoder.TryDecompress(font.Slice(at, compressedSize), unpacked, out var written) || written != offset) return null;
        return unpacked.AsSpan(checked((int)wanted), checked((int)wantedLength)).ToArray();
    }

    private static uint Base128(ReadOnlySpan<byte> data, ref int at)
    {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = data[at++];
            if (i == 0 && b == 0x80) throw new InvalidDataException("A leading zero in a WOFF2 number.");
            if ((value & 0xFE000000) != 0) throw new InvalidDataException("A WOFF2 number past 32 bits.");
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return value;
        }

        throw new InvalidDataException("A WOFF2 number longer than five bytes.");
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(buffer, read, length - read);
            if (n == 0) throw new InvalidDataException("A WOFF table shorter than it says.");
            read += n;
        }

        return buffer;
    }

    // ---- cmap ----

    private static HashSet<int>? Cmap(byte[] cmap)
    {
        var span = cmap.AsSpan();
        int count = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        int best = -1, bestRank = 0;
        for (var i = 0; i < count; i++)
        {
            var record = span.Slice(4 + i * 8, 8);
            int platform = BinaryPrimitives.ReadUInt16BigEndian(record), encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
            var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[4..]));
            int format = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            // A full-repertoire table first (format 12), then the Unicode BMP one (format 4), as fontTools' getBestCmap does.
            var rank = (format, platform, encoding) switch
            {
                (12, 3, 10) => 6,
                (12, 0, _) => 5,
                (4, 3, 1) => 4,
                (4, 0, _) => 3,
                _ => 0
            };
            if (rank > bestRank)
            {
                (best, bestRank) = (offset, rank);
            }
        }

        if (best < 0) return null;
        var table = span[best..];
        return BinaryPrimitives.ReadUInt16BigEndian(table) == 12 ? Format12(table) : Format4(table);
    }

    private static HashSet<int> Format4(ReadOnlySpan<byte> t)
    {
        var set = new HashSet<int>();
        int segments = BinaryPrimitives.ReadUInt16BigEndian(t[6..]) / 2;
        int ends = 14, starts = ends + segments * 2 + 2, deltas = starts + segments * 2, ranges = deltas + segments * 2;
        for (var s = 0; s < segments; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(t[(ends + s * 2)..]);
            int start = BinaryPrimitives.ReadUInt16BigEndian(t[(starts + s * 2)..]);
            int delta = BinaryPrimitives.ReadInt16BigEndian(t[(deltas + s * 2)..]);
            int rangeAt = ranges + s * 2;
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(t[rangeAt..]);
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(t[(rangeAt + rangeOffset + (c - start) * 2)..]);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }

                if (glyph != 0) set.Add(c);
            }
        }

        return set;
    }

    private static HashSet<int> Format12(ReadOnlySpan<byte> t)
    {
        var set = new HashSet<int>();
        var groups = checked((int)BinaryPrimitives.ReadUInt32BigEndian(t[12..]));
        for (var g = 0; g < groups; g++)
        {
            var group = t.Slice(16 + g * 12, 12);
            var start = BinaryPrimitives.ReadUInt32BigEndian(group);
            var end = BinaryPrimitives.ReadUInt32BigEndian(group[4..]);
            var glyph = BinaryPrimitives.ReadUInt32BigEndian(group[8..]);
            if (end < start || end > 0x10FFFF || end - start > 0x10FFFF || set.Count + (end - start) > 0x110000) throw new InvalidDataException("A cmap group out of range.");
            for (var c = start; c <= end; c++)
            {
                if (glyph + (c - start) != 0) set.Add((int)c);
            }
        }

        return set;
    }
}
