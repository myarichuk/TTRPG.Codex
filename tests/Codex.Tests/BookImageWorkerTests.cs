using Codex.Core.Book;
using Codex.Persistence;
using SkiaSharp;

namespace Codex.Tests;

/// <summary>The picture decoder runs in a worker process, and nothing a file does to that process reaches the server.</summary>
public class BookImageWorkerTests
{
    private static BookImage Picture(int width, int height)
    {
        var random = new Random(5);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(i / 4 % width / 8 + random.Next(16)); pixels[i + 1] = (byte)random.Next(60); pixels[i + 2] = 90; pixels[i + 3] = 255; }
        var bytes = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 90).ToArray();
        Assert.Equal(BookImageProblem.None, BookImageSanitizer.TrySanitize(bytes, out var image));
        return image!;
    }

    private static BookImageWorkerLaunch Real()
    {
        var launch = BookImageWorkerLaunch.Beside(AppContext.BaseDirectory);
        Assert.NotNull(launch); // the worker is built beside anything that references the server
        return launch!;
    }

    private static BookImageWorkerLaunch Shell(string script) => new("sh", ["-c", script]);

    [Fact]
    public async Task TheRealWorkerScalesAPicture_LikeTheInProcessEngine_AndIsReused()
    {
        using var pool = new BookImageWorkerPool(Real());
        var original = Picture(1800, 2400);

        var first = await pool.ScaleAsync(original, 700, 933, default);
        var second = await pool.ScaleAsync(original, 700, 933, default);

        Assert.NotNull(first);
        Assert.NotNull(second);
        var local = BookImageEngine.Scale(original, 700, 933);
        Assert.Equal((local.Width, local.Height), (first!.Width, first.Height));
        Assert.Equal(local.ContentType, first.ContentType);
        Assert.Equal(1, pool.Started); // one process, two pictures
        Assert.Equal(0, pool.Failures);
    }

    [Fact]
    public async Task APictureTheEngineKeeps_ComesBackAsNothing()
    {
        using var pool = new BookImageWorkerPool(Real());

        // Already smaller than asked for: the engine hands the picture back, and the worker says "keep it".
        Assert.Null(await pool.ScaleAsync(Picture(200, 200), 1000, 1000, default));
        Assert.Equal(0, pool.Failures);
    }

    [Fact]
    public async Task AWorkerThatDiesAtOnce_CostsTheSavingNotThePicture_AndNotTheServer()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pool = new BookImageWorkerPool(Shell("exit 139"));

        Assert.Null(await pool.ScaleAsync(Picture(900, 900), 300, 300, default));
        Assert.Equal(1, pool.Failures);
    }

    [Fact]
    public async Task AWorkerThatNeverAnswers_IsKilledAtTheTimeout()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pool = new BookImageWorkerPool(Shell("sleep 60"), timeout: TimeSpan.FromMilliseconds(700));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(await pool.ScaleAsync(Picture(900, 900), 300, 300, default));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        Assert.Equal(1, pool.Failures);
        Assert.Equal(0, pool.Alive); // killed, not left sleeping
    }

    [Fact]
    public async Task AfterAWorkerDies_TheNextPictureIsServedByAFreshOne()
    {
        if (!OperatingSystem.IsLinux()) return;
        var real = Real();
        var marker = Path.Combine(Path.GetTempPath(), "codex-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The first start dies; every later one is the real worker.
            using var pool = new BookImageWorkerPool(new BookImageWorkerLaunch("sh", ["-c", $"if [ -e '{marker}' ]; then exec \"$0\" \"$@\"; else touch '{marker}'; exit 1; fi", real.FileName, .. real.Arguments]));
            var picture = Picture(1800, 2400);

            Assert.Null(await pool.ScaleAsync(picture, 700, 933, default));
            var again = await pool.ScaleAsync(picture, 700, 933, default);

            Assert.NotNull(again);
            Assert.Equal(1, pool.Failures);
            Assert.Equal(2, pool.Started);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task ACancelledRequest_StopsTheWorkerAndSaysSo_AndThePoolStillWorks()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pool = new BookImageWorkerPool(Shell("sleep 60"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.ScaleAsync(Picture(900, 900), 300, 300, cancel.Token));

        Assert.Equal(0, pool.Failures); // a cancelled request is not a failure of the worker
        Assert.Equal(0, pool.Alive);
        using var other = new BookImageWorkerPool(Real());
        Assert.NotNull(await other.ScaleAsync(Picture(1800, 2400), 700, 933, default));
    }

    [Fact]
    public async Task WithNoWorkerFile_PicturesAreKept_AndTheScalerSaysSo()
    {
        using var pool = new BookImageWorkerPool(null);
        var original = Picture(1800, 2400);

        Assert.Same(original, await new BookImageScaler(pool).ScaleAsync(original, "portrait", BookArtQuality.Print));
        Assert.Null(BookImageWorkerLaunch.Beside(Path.GetTempPath()));
    }

    private sealed class Counting(IBookImageDecoder inner) : IBookImageDecoder
    {
        public int Calls;
        public Task<BookImage?> ScaleAsync(BookImage image, int width, int height, CancellationToken ct) { Calls++; return inner.ScaleAsync(image, width, height, ct); }
    }

    [Fact]
    public async Task APictureThatKilledAWorker_IsNotSentToAnotherOne()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var pool = new BookImageWorkerPool(Shell("exit 139"));
        var counting = new Counting(pool);
        var scaler = new BookImageScaler(counting);
        var original = Picture(1800, 2400);

        var first = await scaler.ScaleAsync(original, "portrait", BookArtQuality.Print);
        var second = await scaler.ScaleAsync(original, "portrait", BookArtQuality.Print);

        Assert.Same(original, first);
        Assert.Same(original, second);
        Assert.Equal(1, counting.Calls); // remembered as "keep", so a poison file costs one worker, not one per request
    }
}
