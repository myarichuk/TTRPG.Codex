using System.Collections.Concurrent;
using System.Security.Cryptography;
using Codex.Core.Book;
using SkiaSharp;

namespace Codex.Persistence;

/// <summary>How sharp a book's pictures need to be: the PDF prints at 300 dots per inch, the live preview shows a screen.</summary>
public enum BookArtQuality
{
    Preview = 120,
    Print = 300
}

/// <summary>Where a picture is printed, in millimetres, and whether it fills that box (cropped) or sits inside it.</summary>
public readonly record struct BookArtBox(double WidthMm, double HeightMm, bool Fill);

/// <summary>
/// Makes a picture no bigger than the page can show. A generated or photographed picture is often 2 to 5 times the pixels its box can
/// print, and a book of thirty of them would be larger than the book's size cap and slow to preview. The picture is scaled down (never
/// up) to the box's size at the quality's dots per inch, and written as JPEG (quality 85) when it has no transparency, PNG when it has.
/// <para>The decoding is done by an <see cref="IBookImageDecoder"/>: in the server, a separate process (<see cref="BookImageWorkerPool"/>); with none
/// given, in this process (tests).</para>
/// <para>This is the one place in the book that decodes a picture, so it is narrow on purpose. Only bytes that already passed
/// <see cref="BookImageSanitizer"/> arrive (a known container, bounded pixels, no metadata). A picture that is already small enough is
/// <b>not decoded at all</b> and goes on exactly as it was. A picture that fails to decode, is too big to decode safely, or would not
/// get smaller is kept as the sanitizer left it, so a refusal here can only cost bytes, never a picture. Decoding runs two at a time and
/// colours are converted to sRGB, since the re-encoded file carries no profile.</para>
/// </summary>
public sealed class BookImageScaler(IBookImageDecoder? decoder = null)
{
    private readonly IBookImageDecoder _decoder = decoder ?? new InProcessBookImageDecoder();

    /// <summary>The most pixels this decodes whole (RGBA, four bytes each). A larger JPEG is decoded at a reduced size by the codec; a
    /// larger PNG or WebP is kept as it is.</summary>
    public const long MaxDecodePixels = 30_000_000;

    public const int JpegQuality = 85;

    /// <summary>Bytes of results remembered; the whole memory is dropped when it is full, which is cheap and bounded.</summary>
    public const long CacheBytes = 96L * 1024 * 1024;

    private readonly SemaphoreSlim _decoding = new(2);
    private readonly ConcurrentDictionary<string, BookImage> _cache = new(StringComparer.Ordinal);
    private long _cached;

    /// <summary>The printed size of each kind of picture (see the theme's <c>.cover-art</c>, <c>.art-portrait</c>, <c>.art-place</c>, <c>.art-header</c>).</summary>
    public static BookArtBox BoxFor(string kind, int columns = 2) => kind switch
    {
        "cover" => new(140, 165, false),
        // A portrait is 3:4, at most 42 mm wide (34% of a column, see .art-portrait), and cropped from near the top: a face is high in a
        // portrait, and a wide box cut a tall one's head off (found on a real model's portrait;
        // an uploaded tall portrait was cut the same way).
        "portrait" or "homebrew" => new(42, 56, true),
        // A chapter header spans both columns (the text width) whatever the column count.
        "header" => new(172, 62, true),
        // A place picture is as wide as its column: 88 mm in two columns, the text width (172 mm) in one.
        _ => new(columns == 2 ? 88 : 172, 48, true)
    };

    public static (int Width, int Height) TargetPixels(BookArtBox box, int width, int height, BookArtQuality quality)
    {
        var dpi = (int)quality;
        var boxW = Math.Max(1, (int)Math.Ceiling(box.WidthMm * dpi / 25.4));
        var boxH = Math.Max(1, (int)Math.Ceiling(box.HeightMm * dpi / 25.4));
        var scale = box.Fill ? Math.Max((double)boxW / width, (double)boxH / height) : Math.Min((double)boxW / width, (double)boxH / height);
        if (scale >= 1) return (width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public async Task<BookImage> ScaleAsync(BookImage image, string kind, BookArtQuality quality, CancellationToken ct = default, int columns = 2)
    {
        var (w, h) = TargetPixels(BoxFor(kind, columns), image.Width, image.Height, quality);
        // Already as small as it needs to be (a sixth to spare, so a few pixels over does not cost a re-encode): untouched, undecoded.
        if (w * 1.15 >= image.Width && h * 1.15 >= image.Height) return image;

        var key = Convert.ToHexString(SHA256.HashData(image.Bytes)) + ":" + w + "x" + h;
        if (_cache.TryGetValue(key, out var hit)) return hit;

        await _decoding.WaitAsync(ct);
        try
        {
            // Another request may have made it while this one waited for a place.
            if (_cache.TryGetValue(key, out hit)) return hit;
            var made = await _decoder.ScaleAsync(image, w, h, ct) ?? image;
            // A picture that would not get smaller is remembered as itself, so it is not decoded again on every preview.
            if (made.Bytes.Length >= image.Bytes.Length) made = image;
            if (Interlocked.Add(ref _cached, made.Bytes.Length) > CacheBytes)
            {
                _cache.Clear();
                Interlocked.Exchange(ref _cached, made.Bytes.Length);
            }

            _cache[key] = made;
            return made;
        }
        finally
        {
            _decoding.Release();
        }
    }
}

/// <summary>
/// The decoder itself: SkiaSharp, and nothing else in the book path touches it. It runs in <see cref="BookImageWorker"/>'s process (see
/// <see cref="BookImageWorkerPool"/>), so that a file that crashes or exhausts a native decoder ends that process and not the server.
/// </summary>
public static class BookImageEngine
{
    /// <summary>The picture scaled to <paramref name="width"/> x <paramref name="height"/>, or <paramref name="image"/> itself when it cannot or
    /// need not be (not decodable, too big to decode safely, not smaller afterwards).</summary>
    public static BookImage Scale(BookImage image, int width, int height)
    {
        // Never up: a request for a size at or above the picture's own is nothing to do.
        if (width >= image.Width && height >= image.Height) return image;
        try
        {
            using var data = SKData.CreateCopy(image.Bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) return image;
            var info = codec.Info;
            var isJpeg = image.ContentType == "image/jpeg";
            var wholePixels = (long)info.Width * info.Height;
            if (wholePixels > BookImageScaler.MaxDecodePixels && !isJpeg) return image;

            // A JPEG decodes at 1/2, 1/4 or 1/8 straight from its blocks, which keeps a 40-megapixel photo from ever being whole in memory.
            var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
            if (isJpeg)
            {
                // The codec reduces by 1/2, 1/4 or 1/8 straight from the blocks. Take the biggest reduction that still covers the target,
                // asking for each in turn (GetScaledDimensions rounds to the nearest, which can be too small).
                var chosen = false;
                foreach (var denominator in new[] { 8, 4, 2 })
                {
                    var scaled = codec.GetScaledDimensions(1f / denominator);
                    if (scaled.Width >= width && scaled.Height >= height && (long)scaled.Width * scaled.Height <= BookImageScaler.MaxDecodePixels)
                    {
                        decodeInfo = decodeInfo.WithSize(scaled.Width, scaled.Height);
                        chosen = true;
                        break;
                    }
                }

                if (!chosen && wholePixels > BookImageScaler.MaxDecodePixels) return image;
            }

            using var source = new SKBitmap(decodeInfo);
            if (codec.GetPixels(decodeInfo, source.GetPixels()) != SKCodecResult.Success) return image;

            using var small = source.Width == width && source.Height == height
                ? source.Copy()
                : source.Resize(decodeInfo.WithSize(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (small == null) return image;

            var hasAlpha = false;
            foreach (var pixel in small.Pixels)
            {
                if (pixel.Alpha < 255) { hasAlpha = true; break; }
            }

            using var picture = SKImage.FromBitmap(small);
            using var encoded = hasAlpha ? picture.Encode(SKEncodedImageFormat.Png, 100) : picture.Encode(SKEncodedImageFormat.Jpeg, BookImageScaler.JpegQuality);
            if (encoded == null) return image;
            return new BookImage(encoded.ToArray(), hasAlpha ? "image/png" : "image/jpeg", small.Width, small.Height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A decoder that throws on a file the sanitizer let through costs the saving, not the picture.
            return image;
        }
    }
}
