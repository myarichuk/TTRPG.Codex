namespace Codex.Core.Book;

/// <summary>An image cleared for a book: re-assembled from its own pixel data with every metadata block left out, its type taken from
/// its bytes rather than from what was declared, and its size known.</summary>
public sealed record BookImage(byte[] Bytes, string ContentType, int Width, int Height)
{
    /// <summary>A <c>data:</c> URI the renderer accepts (no whitespace, quote or angle bracket can occur in base64).</summary>
    public string DataUri() => "data:" + ContentType + ";base64," + Convert.ToBase64String(Bytes);
}

/// <summary>Why an image was kept out of a book.</summary>
public enum BookImageProblem
{
    None,
    Unsupported,
    Malformed,
    TooLarge
}

/// <summary>
/// Makes a stored image fit to print and to hand to other people. Uploads are kept exactly as they came (no re-encoding), so a
/// photo can still carry the camera's GPS position, the owner's name and a thumbnail of what was cropped out, and a book is
/// the thing that gets passed around. No image library is involved on purpose: the container formats are read just far enough
/// to drop the metadata blocks and learn the size, and the pixel data is copied byte for byte, so nothing is decoded and a
/// hostile file never reaches a decoder here. The browser that prints it is the only decoder, with its network closed.
/// <para>JPEG, PNG and WebP are supported. GIF and AVIF are not (their metadata lives in places this does not rewrite) and
/// take a placeholder; so does anything that is not what it says it is.</para>
/// </summary>
public static class BookImageSanitizer
{
    /// <summary>The widest or tallest side accepted, and the most pixels: the browser decodes the whole image to print it.</summary>
    public const int MaxSide = 16_000;

    public const long MaxPixels = 50_000_000;

    public static BookImageProblem TrySanitize(byte[] bytes, out BookImage? image)
    {
        image = null;
        if (bytes.Length < 12)
        {
            return BookImageProblem.Malformed;
        }

        try
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return Jpeg(bytes, out image);
            if (bytes.AsSpan(0, 8).SequenceEqual(PngSignature)) return Png(bytes, out image);
            if (Is(bytes, 0, "RIFF") && Is(bytes, 8, "WEBP")) return WebP(bytes, out image);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // Every read below is bounds-checked, so this is a belt over the braces: a file that trips one is malformed, not fatal.
            return BookImageProblem.Malformed;
        }

        return Is(bytes, 0, "GIF8") || Is(bytes, 4, "ftyp") ? BookImageProblem.Unsupported : BookImageProblem.Malformed;
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool Is(byte[] b, int at, string tag)
    {
        if (at + tag.Length > b.Length) return false;
        for (var i = 0; i < tag.Length; i++) if (b[at + i] != tag[i]) return false;
        return true;
    }

    private static BookImageProblem Size(long width, long height) =>
        width < 1 || height < 1 ? BookImageProblem.Malformed
        : width > MaxSide || height > MaxSide || width * height > MaxPixels ? BookImageProblem.TooLarge
        : BookImageProblem.None;

    // JPEG: keep the structure and the colour (JFIF, an ICC profile, Adobe's colour transform), drop every other APPn and comment.
    private static BookImageProblem Jpeg(byte[] b, out BookImage? image)
    {
        image = null;
        var output = new MemoryStream(b.Length);
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);
        var at = 2;
        int width = 0, height = 0;
        while (true)
        {
            // Fill bytes: any number of 0xFF before a marker.
            if (at >= b.Length || b[at] != 0xFF) return BookImageProblem.Malformed;
            while (at < b.Length && b[at] == 0xFF) at++;
            if (at >= b.Length) return BookImageProblem.Malformed;
            var marker = b[at++];
            if (marker == 0xD9) return BookImageProblem.Malformed; // the image ended before its scan
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7) or 0xD8)
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                continue;
            }

            if (at + 2 > b.Length) return BookImageProblem.Malformed;
            var length = (b[at] << 8) | b[at + 1];
            if (length < 2 || at + length > b.Length) return BookImageProblem.Malformed;
            var body = at + 2;
            var bodyLength = length - 2;

            var keep = marker switch
            {
                0xE0 => true,
                0xE2 => bodyLength >= 12 && Is(b, body, "ICC_PROFILE\0"),
                0xEE => bodyLength >= 5 && Is(b, body, "Adobe"),
                (>= 0xE1 and <= 0xEF) or 0xFE => false,
                _ => true
            };

            if (marker is (>= 0xC0 and <= 0xCF) and not (0xC4 or 0xC8 or 0xCC))
            {
                if (bodyLength < 6) return BookImageProblem.Malformed;
                height = (b[body + 1] << 8) | b[body + 2];
                width = (b[body + 3] << 8) | b[body + 4];
            }

            if (keep)
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                output.Write(b, at, length);
            }

            at += length;
            if (marker == 0xDA)
            {
                // The scan: entropy-coded data up to the end of the file, copied as it is.
                if (width == 0) return BookImageProblem.Malformed;
                output.Write(b, at, b.Length - at);
                var problem = Size(width, height);
                if (problem != BookImageProblem.None) return problem;
                image = new BookImage(output.ToArray(), "image/jpeg", width, height);
                return BookImageProblem.None;
            }
        }
    }

    private static readonly HashSet<string> PngKept = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "hIST", "pHYs", "sPLT", "acTL", "fcTL", "fdAT", "cICP"
    };

    // PNG: an allow-list of chunks; text, EXIF, time and every unknown ancillary chunk go. Kept chunks are copied whole, CRC included.
    private static BookImageProblem Png(byte[] b, out BookImage? image)
    {
        image = null;
        var output = new MemoryStream(b.Length);
        output.Write(PngSignature);
        var at = 8;
        long width = 0, height = 0;
        var first = true;
        var sawData = false;
        while (at + 12 <= b.Length)
        {
            var length = ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3];
            if (length > int.MaxValue || at + 12L + length > b.Length) return BookImageProblem.Malformed;
            var type = System.Text.Encoding.ASCII.GetString(b, at + 4, 4);
            if (first)
            {
                if (type != "IHDR" || length != 13) return BookImageProblem.Malformed;
                width = ((long)b[at + 8] << 24) | ((long)b[at + 9] << 16) | ((long)b[at + 10] << 8) | b[at + 11];
                height = ((long)b[at + 12] << 24) | ((long)b[at + 13] << 16) | ((long)b[at + 14] << 8) | b[at + 15];
                first = false;
            }

            if (type == "IDAT") sawData = true;
            if (PngKept.Contains(type)) output.Write(b, at, (int)(12 + length));
            at += (int)(12 + length);
            if (type == "IEND")
            {
                if (!sawData) return BookImageProblem.Malformed;
                var problem = Size(width, height);
                if (problem != BookImageProblem.None) return problem;
                image = new BookImage(output.ToArray(), "image/png", (int)width, (int)height);
                return BookImageProblem.None;
            }
        }

        return BookImageProblem.Malformed;
    }

    // WebP: drop the EXIF and XMP chunks, clear their flags in VP8X, and make the RIFF size say what is left.
    private static BookImageProblem WebP(byte[] b, out BookImage? image)
    {
        image = null;
        var declared = (long)b[4] | ((long)b[5] << 8) | ((long)b[6] << 16) | ((long)b[7] << 24);
        if (declared + 8 > b.Length || declared < 4) return BookImageProblem.Malformed;
        var end = (int)(declared + 8);
        var output = new MemoryStream(b.Length);
        output.Write(b, 0, 12);
        var at = 12;
        long width = 0, height = 0;
        var sawImage = false;
        while (at + 8 <= end)
        {
            var size = (long)b[at + 4] | ((long)b[at + 5] << 8) | ((long)b[at + 6] << 16) | ((long)b[at + 7] << 24);
            var padded = size + (size & 1);
            if (at + 8 + size > end) return BookImageProblem.Malformed;
            var body = at + 8;
            var whole = (int)Math.Min(8 + padded, end - at);

            if (Is(b, at, "VP8X"))
            {
                if (size < 10) return BookImageProblem.Malformed;
                width = 1 + (b[body + 4] | (b[body + 5] << 8) | (b[body + 6] << 16));
                height = 1 + (b[body + 7] | (b[body + 8] << 8) | (b[body + 9] << 16));
                var chunk = b.AsSpan(at, whole).ToArray();
                chunk[8] &= unchecked((byte)~0x0C); // no EXIF, no XMP
                output.Write(chunk);
            }
            else if (Is(b, at, "EXIF") || Is(b, at, "XMP "))
            {
                // dropped
            }
            else
            {
                if (Is(b, at, "VP8 ") && width == 0)
                {
                    if (size < 10 || b[body + 3] != 0x9D || b[body + 4] != 0x01 || b[body + 5] != 0x2A) return BookImageProblem.Malformed;
                    width = (b[body + 6] | (b[body + 7] << 8)) & 0x3FFF;
                    height = (b[body + 8] | (b[body + 9] << 8)) & 0x3FFF;
                }
                else if (Is(b, at, "VP8L") && width == 0)
                {
                    if (size < 5 || b[body] != 0x2F) return BookImageProblem.Malformed;
                    var bits = b[body + 1] | (b[body + 2] << 8) | (b[body + 3] << 16) | ((uint)b[body + 4] << 24);
                    width = (bits & 0x3FFF) + 1;
                    height = ((bits >> 14) & 0x3FFF) + 1;
                }

                if (Is(b, at, "VP8 ") || Is(b, at, "VP8L") || Is(b, at, "ANMF")) sawImage = true;
                output.Write(b, at, whole);
            }

            at += whole;
        }

        if (!sawImage) return BookImageProblem.Malformed;
        var problem = Size(width, height);
        if (problem != BookImageProblem.None) return problem;
        var result = output.ToArray();
        var riff = result.Length - 8;
        result[4] = (byte)riff;
        result[5] = (byte)(riff >> 8);
        result[6] = (byte)(riff >> 16);
        result[7] = (byte)(riff >> 24);
        image = new BookImage(result, "image/webp", (int)width, (int)height);
        return BookImageProblem.None;
    }
}
