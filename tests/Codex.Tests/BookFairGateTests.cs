using Codex.Core.Book;
using Codex.Persistence;

namespace Codex.Tests;

/// <summary>The decoder's two places are shared fairly between users, and one user alone still has both (docs/BOOK_EXPORT.md 31.1, item 3).</summary>
public class BookFairGateTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OneUserAlone_HoldsEveryPlace()
    {
        var gate = new BookFairGate(2);
        using var first = await gate.EnterAsync("a").WaitAsync(Wait);
        using var second = await gate.EnterAsync("a").WaitAsync(Wait);

        var third = gate.EnterAsync("a");
        Assert.False(third.IsCompleted);
        first.Dispose();
        (await third.WaitAsync(Wait)).Dispose();
    }

    [Fact]
    public async Task AFreedPlace_GoesToTheWaitingUserWhoHoldsFewest_NotToTheNextOfAUsersLongLine()
    {
        var gate = new BookFairGate(2);
        var a1 = await gate.EnterAsync("a");
        var a2 = await gate.EnterAsync("a");
        // A has a long line behind its two places; B asks after all of it.
        var aLine = Enumerable.Range(0, 5).Select(_ => gate.EnterAsync("a")).ToList();
        var b = gate.EnterAsync("b");

        a1.Dispose();
        // B holds none and A holds one: B is next, though A asked first.
        var bPlace = await b.WaitAsync(Wait);
        Assert.All(aLine, t => Assert.False(t.IsCompleted));

        // Then A, in the order it asked.
        a2.Dispose();
        var next = await aLine[0].WaitAsync(Wait);
        Assert.All(aLine.Skip(1), t => Assert.False(t.IsCompleted));

        bPlace.Dispose();
        next.Dispose();
        foreach (var t in aLine.Skip(1)) (await t.WaitAsync(Wait)).Dispose();
        Assert.Equal(0, gate.Owners);
    }

    [Fact]
    public async Task ACancelledWait_LeavesTheLine_AndItsPlaceGoesToTheNext()
    {
        var gate = new BookFairGate(1);
        var held = await gate.EnterAsync("a");
        using var cancel = new CancellationTokenSource();
        var gone = gate.EnterAsync("b", cancel.Token);
        var next = gate.EnterAsync("c");

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gone.WaitAsync(Wait));
        held.Dispose();
        (await next.WaitAsync(Wait)).Dispose();
        Assert.Equal(0, gate.Owners);

        // A place given back twice is given back once.
        var once = await gate.EnterAsync("a");
        once.Dispose();
        once.Dispose();
        using var only = await gate.EnterAsync("a").WaitAsync(Wait);
        Assert.False(gate.EnterAsync("b").IsCompleted);
    }

    /// <summary>A decoder that answers only when told to, so a test can see who is decoding.</summary>
    private sealed class HeldDecoder : IBookImageDecoder
    {
        public readonly List<(BookImage Image, TaskCompletionSource<BookImage?> Done)> Started = [];

        public Task<BookImage?> ScaleAsync(BookImage image, int width, int height, CancellationToken ct)
        {
            var done = new TaskCompletionSource<BookImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Started) Started.Add((image, done));
            return done.Task;
        }

        public async Task<int> CountAsync(int n)
        {
            var until = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < until)
            {
                lock (Started) if (Started.Count >= n) return Started.Count;
                await Task.Delay(10);
            }

            lock (Started) return Started.Count;
        }
    }

    [Fact]
    public async Task TheScaler_SharesItsDecodersByOwner()
    {
        var decoder = new HeldDecoder();
        var scaler = new BookImageScaler(decoder);
        // Distinct large pictures (the cache keys on the bytes), each needing a decode.
        BookImage Picture(int n) => new([(byte)n, 1, 2, 3], "image/png", 4000, 4000);

        var a = Enumerable.Range(0, 4).Select(n => scaler.ScaleAsync(Picture(n), "portrait", BookArtQuality.Print, owner: "a")).ToList();
        Assert.Equal(2, await decoder.CountAsync(2));
        var b = scaler.ScaleAsync(Picture(9), "portrait", BookArtQuality.Print, owner: "b");

        decoder.Started[0].Done.SetResult(null);
        Assert.Equal(3, await decoder.CountAsync(3));
        Assert.Equal(9, decoder.Started[2].Image.Bytes[0]);

        var all = Task.WhenAll([.. a, b]);
        while (!all.IsCompleted)
        {
            lock (decoder.Started) foreach (var started in decoder.Started) started.Done.TrySetResult(null);
            await Task.WhenAny(all, Task.Delay(20));
        }

        await all.WaitAsync(Wait);
        Assert.Equal(5, decoder.Started.Count);
        Assert.Equal(0, scaler.DecodingOwners);
    }
}
