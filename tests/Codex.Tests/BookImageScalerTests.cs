using Codex.Core.Book;
using Codex.Persistence;
using SkiaSharp;

namespace Codex.Tests;

/// <summary>Pictures are made no bigger than their box prints at (docs/BOOK_EXPORT.md, art step 2).</summary>
public class BookImageScalerTests
{
    /// <summary>A picture with a gradient and a little noise: not trivially compressible, so the sizes mean something.</summary>
    private static byte[] Make(int width, int height, SKEncodedImageFormat format, bool transparent = false)
    {
        var random = new Random(7);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                pixels[at] = (byte)(x * 255L / width + random.Next(24));
                pixels[at + 1] = (byte)(y * 255L / height + random.Next(24));
                pixels[at + 2] = (byte)(random.Next(256));
                pixels[at + 3] = transparent && x < width / 4 ? (byte)0 : (byte)255;
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 95).ToArray();
    }

    private static BookImage Clean(byte[] bytes)
    {
        Assert.Equal(BookImageProblem.None, BookImageSanitizer.TrySanitize(bytes, out var image));
        return image!;
    }

    [Fact]
    public void TheTargetIsTheBoxAtTheQualitysResolution_NeverLargerThanTheSource_AndAPreviewIsSmallerThanAPrint()
    {
        var portrait = BookImageScaler.BoxFor("portrait");
        var (pw, ph) = BookImageScaler.TargetPixels(portrait, 3000, 4000, BookArtQuality.Print);
        var (vw, vh) = BookImageScaler.TargetPixels(portrait, 3000, 4000, BookArtQuality.Preview);

        // A filled box needs both sides covered: 62 mm wide at 300 dpi is 733 px, and the height then follows the aspect.
        Assert.Equal(733, pw);
        Assert.True(ph >= 52 * 300 / 25.4);
        Assert.True(vw < pw && vh < ph);
        Assert.Equal((200, 300), BookImageScaler.TargetPixels(portrait, 200, 300, BookArtQuality.Print)); // never up

        // A cover sits inside its box, so the limiting side is the one that is reached first.
        var (cw, ch) = BookImageScaler.TargetPixels(BookImageScaler.BoxFor("cover"), 4000, 4000, BookArtQuality.Print);
        Assert.Equal(cw, ch);
        Assert.True(ch <= Math.Ceiling(165 * 300 / 25.4));
    }

    [Theory]
    [InlineData("portrait")]
    [InlineData("cover")]
    [InlineData("location")]
    public async Task ABigOpaquePictureBecomesAJpegThatIsMuchSmaller_AtTheTargetSize(string kind)
    {
        var original = Clean(Make(2400, 3200, SKEncodedImageFormat.Png));

        var scaled = await new BookImageScaler().ScaleAsync(original, kind, BookArtQuality.Print);

        var (w, h) = BookImageScaler.TargetPixels(BookImageScaler.BoxFor(kind), original.Width, original.Height, BookArtQuality.Print);
        Assert.Equal("image/jpeg", scaled.ContentType);
        Assert.InRange(scaled.Width, w - 1, w + 1);
        Assert.InRange(scaled.Height, h - 1, h + 1);
        Assert.True(scaled.Bytes.Length * 4 < original.Bytes.Length, $"{scaled.Bytes.Length} vs {original.Bytes.Length}");
        // What it hands on is itself a clean image: the sanitizer accepts it and finds the size it says.
        Assert.Equal(BookImageProblem.None, BookImageSanitizer.TrySanitize(scaled.Bytes, out var again));
        Assert.Equal((scaled.Width, scaled.Height), (again!.Width, again.Height));
    }

    [Fact]
    public async Task APictureThatIsAlreadySmallEnough_IsNotDecodedAtAll_AndComesBackAsTheSameObject()
    {
        var small = Clean(Make(300, 400, SKEncodedImageFormat.Png));
        var scaler = new BookImageScaler();

        Assert.Same(small, await scaler.ScaleAsync(small, "portrait", BookArtQuality.Print));
        // Not even one that is a few pixels over: a re-encode for a few percent is a loss of quality for nothing.
        var (w, h) = BookImageScaler.TargetPixels(BookImageScaler.BoxFor("portrait"), 800, 1000, BookArtQuality.Print);
        var near = Clean(Make(Math.Min(800, (int)(w * 1.1)), (int)(h * 1.1), SKEncodedImageFormat.Png));
        Assert.Same(near, await scaler.ScaleAsync(near, "portrait", BookArtQuality.Print));
    }

    [Fact]
    public async Task Transparency_IsKept_AsAPng()
    {
        var original = Clean(Make(1800, 1800, SKEncodedImageFormat.Png, transparent: true));

        var scaled = await new BookImageScaler().ScaleAsync(original, "portrait", BookArtQuality.Print);

        Assert.Equal("image/png", scaled.ContentType);
        Assert.True(scaled.Width < original.Width);
        using var decoded = SKBitmap.Decode(scaled.Bytes);
        Assert.Equal(0, decoded.GetPixel(2, decoded.Height / 2).Alpha);
        Assert.Equal(255, decoded.GetPixel(decoded.Width - 3, decoded.Height / 2).Alpha);
    }

    [Fact]
    public async Task ABigJpeg_IsReducedWhileItIsDecoded_AndComesOutAtTheTargetSize()
    {
        var original = Clean(Make(6000, 4000, SKEncodedImageFormat.Jpeg));

        var scaled = await new BookImageScaler().ScaleAsync(original, "location", BookArtQuality.Print);

        var (w, h) = BookImageScaler.TargetPixels(BookImageScaler.BoxFor("location"), 6000, 4000, BookArtQuality.Print);
        Assert.InRange(scaled.Width, w - 1, w + 1);
        Assert.InRange(scaled.Height, h - 1, h + 1);
        Assert.True(scaled.Bytes.Length < original.Bytes.Length / 8);
    }

    [Fact]
    public async Task APictureTheDecoderRefuses_IsKeptAsTheSanitizerLeftIt_NotLost()
    {
        var bytes = Make(2000, 2000, SKEncodedImageFormat.Png);
        // Structure intact (so the sanitizer accepts it), pixel data destroyed.
        var at = Array.IndexOf(bytes, (byte)'I', 40);
        while (!(bytes[at + 1] == 'D' && bytes[at + 2] == 'A' && bytes[at + 3] == 'T')) at = Array.IndexOf(bytes, (byte)'I', at + 1);
        for (var i = at + 10; i < at + 400; i++) bytes[i] = 0x5A;
        var original = Clean(bytes);

        var scaled = await new BookImageScaler().ScaleAsync(original, "portrait", BookArtQuality.Print);

        Assert.Same(original, scaled);
    }

    [Fact]
    public async Task TheSamePictureTwice_IsScaledOnce()
    {
        var original = Clean(Make(2000, 2000, SKEncodedImageFormat.Png));
        var scaler = new BookImageScaler();

        var first = await scaler.ScaleAsync(original, "portrait", BookArtQuality.Print);
        var second = await scaler.ScaleAsync(Clean(original.Bytes), "portrait", BookArtQuality.Print);

        Assert.Same(first, second);
        // A different quality is a different picture.
        Assert.NotSame(first, await scaler.ScaleAsync(original, "portrait", BookArtQuality.Preview));
    }

    [Fact]
    public async Task ColoursAreConvertedToSrgb_BecauseTheFileItWritesCarriesNoProfile()
    {
        // A wide-gamut profile on the source: a pure Display P3 red must not come out as sRGB red-as-is.
        var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        using var bitmap = new SKBitmap(new SKImageInfo(1800, 1800, SKColorType.Rgba8888, SKAlphaType.Unpremul, p3));
        bitmap.Erase(new SKColor(0, 200, 0));
        using var image = SKImage.FromBitmap(bitmap);
        var source = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        Assert.Equal(BookImageProblem.None, BookImageSanitizer.TrySanitize(source, out var clean));

        var scaled = await new BookImageScaler().ScaleAsync(clean!, "portrait", BookArtQuality.Print);

        using var decoded = SKBitmap.Decode(scaled.Bytes);
        var middle = decoded.GetPixel(decoded.Width / 2, decoded.Height / 2);
        // P3 (0,200,0) is a more saturated green than sRGB can hold; converted, the red and blue channels are clipped to zero and green is lower than 200 + something.
        Assert.True(middle.Green > 190, middle.ToString());
        Assert.True(middle.Red < 40 && middle.Blue < 60, middle.ToString());
    }
}
