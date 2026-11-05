using System.Text;
using Codex.Core.Book;

namespace Codex.Tests;

public class BookImageSanitizerTests
{
    public static IEnumerable<object[]> Files() =>
    [
        ["jpeg", ImageFixtures.Jpeg, "image/jpeg"],
        ["png", ImageFixtures.Png, "image/png"],
        ["webp", ImageFixtures.WebP, "image/webp"],
        ["webp-lossless", ImageFixtures.WebPLossless, "image/webp"]
    ];

    [Theory]
    [MemberData(nameof(Files))]
    public void Metadata_IsGoneAndTheSizeAndTypeComeFromTheBytes(string name, byte[] file, string type)
    {
        Assert.Contains("CANARY", Encoding.Latin1.GetString(file), StringComparison.Ordinal); // the fixture really carries it
        Assert.Equal(BookImageProblem.None, BookImageSanitizer.TrySanitize(file, out var image));
        Assert.Equal((type, 8, 6), (image!.ContentType, image.Width, image.Height));
        Assert.DoesNotContain("CANARY", Encoding.Latin1.GetString(image.Bytes), StringComparison.Ordinal);
        Assert.True(image.Bytes.Length < file.Length, name);
        Assert.StartsWith("data:" + type + ";base64,", image.DataUri(), StringComparison.Ordinal);
    }

    [Fact]
    public void WebP_ClearsTheMetadataFlags_AndTheRiffSizeStaysTrue()
    {
        BookImageSanitizer.TrySanitize(ImageFixtures.WebP, out var image);
        var b = image!.Bytes;
        Assert.Equal("VP8X", Encoding.ASCII.GetString(b, 12, 4));
        Assert.Equal(0, b[20] & 0x0C);
        Assert.Equal(b.Length - 8, b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
    }

    [Fact]
    public void Jpeg_KeepsItsPixelData_ByteForByte()
    {
        BookImageSanitizer.TrySanitize(ImageFixtures.Jpeg, out var image);
        var scan = ImageFixtures.Jpeg.AsSpan(IndexOfScan(ImageFixtures.Jpeg));
        Assert.True(image!.Bytes.AsSpan().EndsWith(scan));
    }

    private static int IndexOfScan(byte[] jpeg)
    {
        for (var i = 2; i + 1 < jpeg.Length;)
        {
            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker == 0xDA) return i;
            i += 2 + length;
        }

        throw new InvalidOperationException();
    }

    [Fact]
    public void GifAndAvif_AreNotSupported_AndGarbageIsMalformed()
    {
        Assert.Equal(BookImageProblem.Unsupported, BookImageSanitizer.TrySanitize(ImageFixtures.Gif, out var gif));
        Assert.Null(gif);
        var avif = new byte[32];
        Encoding.ASCII.GetBytes("ftypavif").CopyTo(avif, 4);
        Assert.Equal(BookImageProblem.Unsupported, BookImageSanitizer.TrySanitize(avif, out _));
        Assert.Equal(BookImageProblem.Malformed, BookImageSanitizer.TrySanitize(new byte[100], out _));
        Assert.Equal(BookImageProblem.Malformed, BookImageSanitizer.TrySanitize([], out _));
        Assert.Equal(BookImageProblem.Malformed, BookImageSanitizer.TrySanitize(Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"), out _));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ATruncatedFile_IsNeverAcceptedNorThrows(string name, byte[] file, string type)
    {
        _ = type;
        for (var length = 0; length < file.Length - 1; length++)
        {
            var problem = BookImageSanitizer.TrySanitize(file.AsSpan(0, length).ToArray(), out var image);
            // A prefix that happens to still parse is fine only if what comes out carries no metadata and has a size.
            if (problem == BookImageProblem.None)
            {
                Assert.True(image!.Width > 0 && image.Height > 0, $"{name} cut at {length}");
                Assert.DoesNotContain("CANARY", Encoding.Latin1.GetString(image.Bytes), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ARandomlyDamagedFile_NeverThrows()
    {
        var random = new Random(1234);
        foreach (var file in new[] { ImageFixtures.Jpeg, ImageFixtures.Png, ImageFixtures.WebP, ImageFixtures.WebPLossless })
        {
            for (var round = 0; round < 400; round++)
            {
                var copy = (byte[])file.Clone();
                for (var hit = 0; hit < 1 + round % 4; hit++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
                var problem = BookImageSanitizer.TrySanitize(copy, out var image);
                if (problem == BookImageProblem.None) Assert.NotNull(image);
            }
        }
    }

    [Fact]
    public void AnImageOfAbsurdSize_IsRefused_BeforeAnyBrowserDecodesIt()
    {
        var png = (byte[])ImageFixtures.Png.Clone();
        // IHDR width and height sit at 16..23.
        png[16] = 0; png[17] = 0; png[18] = 0x4E; png[19] = 0x20; // 20000
        png[20] = 0; png[21] = 0; png[22] = 0x4E; png[23] = 0x20;
        Assert.Equal(BookImageProblem.TooLarge, BookImageSanitizer.TrySanitize(png, out _));

        png[16] = 0; png[17] = 0; png[18] = 0; png[19] = 0; // a width of nought
        Assert.Equal(BookImageProblem.Malformed, BookImageSanitizer.TrySanitize(png, out _));
    }
}

public class BookImageDecodeTests
{
    /// <summary>The proof that matters: what comes out of the sanitizer is still a picture a browser decodes, at the size we said.</summary>
    [ChromiumFact]
    public async Task SanitizedImages_StillDecode_InARealBrowser()
    {
        var path = Codex.Web.Book.BookBrowser.Find(null, out _);
        await using var browser = await PuppeteerSharp.Puppeteer.LaunchAsync(new PuppeteerSharp.LaunchOptions
        {
            ExecutablePath = path, Headless = true, Args = ["--no-sandbox"]
        });
        await using var page = await browser.NewPageAsync();
        foreach (var (name, file) in new[] { ("jpeg", ImageFixtures.Jpeg), ("png", ImageFixtures.Png), ("webp", ImageFixtures.WebP), ("webp-lossless", ImageFixtures.WebPLossless) })
        {
            BookImageSanitizer.TrySanitize(file, out var image);
            var size = await page.EvaluateFunctionAsync<int[]>(
                "async uri => { const i = new Image(); i.src = uri; await i.decode(); return [i.naturalWidth, i.naturalHeight]; }", image!.DataUri());
            Assert.True(size is [8, 6], $"{name} decoded as {string.Join("x", size)}");
        }
    }
}
