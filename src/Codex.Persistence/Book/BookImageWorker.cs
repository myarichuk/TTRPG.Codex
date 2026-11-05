using System.Collections.Concurrent;
using System.Diagnostics;
using Codex.Core.Book;

namespace Codex.Persistence;

/// <summary>Scales a picture to a size, somewhere. A null answer means "keep the picture as it is".</summary>
public interface IBookImageDecoder
{
    Task<BookImage?> ScaleAsync(BookImage image, int width, int height, CancellationToken ct);
}

/// <summary>Decodes in the calling process, on a pool thread. For tests, and for a host that has no worker.</summary>
public sealed class InProcessBookImageDecoder : IBookImageDecoder
{
    public Task<BookImage?> ScaleAsync(BookImage image, int width, int height, CancellationToken ct) =>
        Task.Run<BookImage?>(() => BookImageEngine.Scale(image, width, height), ct);
}

/// <summary>The messages between the server and its image worker: length-prefixed, little endian, nothing else on the pipe.</summary>
internal static class BookImageWire
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private const int MaxText = 64;

    public static async Task WriteRequestAsync(Stream to, BookImage image, int width, int height, CancellationToken ct)
    {
        var buffer = new MemoryStream(image.Bytes.Length + 64);
        var w = new BinaryWriter(buffer);
        w.Write(width);
        w.Write(height);
        w.Write(image.ContentType);
        w.Write(image.Width);
        w.Write(image.Height);
        w.Write(image.Bytes.Length);
        w.Write(image.Bytes);
        w.Flush();
        await to.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct);
        await to.FlushAsync(ct);
    }

    /// <summary>Null at the end of the stream (the server closed it).</summary>
    public static async Task<(BookImage Image, int Width, int Height)?> ReadRequestAsync(Stream from, CancellationToken ct)
    {
        var head = new byte[8];
        if (!await ReadFullAsync(from, head, ct, allowEof: true)) return null;
        var width = BitConverter.ToInt32(head, 0);
        var height = BitConverter.ToInt32(head, 4);
        var image = await ReadImageAsync(from, ct);
        return (image, width, height);
    }

    /// <summary>The reply: nothing to say (keep the picture) or a picture.</summary>
    public static async Task WriteReplyAsync(Stream to, BookImage? image, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var w = new BinaryWriter(buffer);
        w.Write(image != null);
        if (image != null)
        {
            w.Write(image.ContentType);
            w.Write(image.Width);
            w.Write(image.Height);
            w.Write(image.Bytes.Length);
            w.Write(image.Bytes);
        }

        w.Flush();
        await to.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct);
        await to.FlushAsync(ct);
    }

    public static async Task<BookImage?> ReadReplyAsync(Stream from, CancellationToken ct)
    {
        var flag = new byte[1];
        if (!await ReadFullAsync(from, flag, ct, allowEof: false)) return null;
        return flag[0] == 0 ? null : await ReadImageAsync(from, ct);
    }

    private static async Task<BookImage> ReadImageAsync(Stream from, CancellationToken ct)
    {
        // 7-bit length-prefixed string (BinaryWriter): read byte by byte, since it is short.
        var length = 0;
        var shift = 0;
        var one = new byte[1];
        while (true)
        {
            await ReadFullAsync(from, one, ct, allowEof: false);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
            shift += 7;
            if (shift > 14) throw new InvalidDataException("bad length");
        }

        if (length is < 1 or > MaxText) throw new InvalidDataException("bad content type");
        var type = new byte[length];
        await ReadFullAsync(from, type, ct, allowEof: false);
        var numbers = new byte[12];
        await ReadFullAsync(from, numbers, ct, allowEof: false);
        var width = BitConverter.ToInt32(numbers, 0);
        var height = BitConverter.ToInt32(numbers, 4);
        var size = BitConverter.ToInt32(numbers, 8);
        if (size is < 1 or > MaxBytes || width < 1 || height < 1) throw new InvalidDataException("bad picture");
        var bytes = new byte[size];
        await ReadFullAsync(from, bytes, ct, allowEof: false);
        return new BookImage(bytes, System.Text.Encoding.UTF8.GetString(type), width, height);
    }

    private static async Task<bool> ReadFullAsync(Stream from, byte[] buffer, CancellationToken ct, bool allowEof)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await from.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (allowEof && read == 0) return false;
                throw new EndOfStreamException();
            }

            read += n;
        }

        return true;
    }
}

/// <summary>The loop of the worker process (<c>Codex.ImageWorker</c>): read a picture and a size, answer with the scaled picture or nothing.</summary>
public static class BookImageWorker
{
    public static async Task<int> RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        while (await BookImageWire.ReadRequestAsync(input, ct) is { } request)
        {
            var scaled = BookImageEngine.Scale(request.Image, request.Width, request.Height);
            await BookImageWire.WriteReplyAsync(output, ReferenceEquals(scaled, request.Image) ? null : scaled, ct);
        }

        return 0;
    }
}

/// <summary>How to start the image worker: a program and its arguments.</summary>
public sealed record BookImageWorkerLaunch(string FileName, IReadOnlyList<string> Arguments)
{
    public const string WorkerFile = "Codex.ImageWorker.dll";

    /// <summary>The worker built beside the server, run by the same dotnet host the server runs in (the launcher starts the server with the
    /// bundled one). Null when the worker is not there; the book then keeps pictures as they are.</summary>
    public static BookImageWorkerLaunch? Beside(string directory)
    {
        var worker = Path.Combine(directory, WorkerFile);
        if (!File.Exists(worker)) return null;
        var host = Environment.ProcessPath is { } own && Path.GetFileNameWithoutExtension(own).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? own
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } fromSdk && File.Exists(fromSdk) ? fromSdk : "dotnet";
        return new BookImageWorkerLaunch(host, [worker]);
    }
}

/// <summary>
/// Decodes pictures in worker processes, so that a file that crashes or exhausts the native decoder ends the worker and not the server.
/// At most <c>size</c> workers; each is started on first use, serves one picture at a time, and is killed and replaced when it dies, when
/// it does not answer within the timeout, or when the request is cancelled mid-flight. Whatever goes wrong, the answer is "keep the picture
/// as it is": a failure here costs bytes, never a picture and never the server. The scaler remembers that answer, so a file that kills a
/// worker is not tried again until the server restarts.
/// </summary>
public sealed class BookImageWorkerPool(BookImageWorkerLaunch? launch, int size = 2, TimeSpan? timeout = null) : IBookImageDecoder, IDisposable
{
    private readonly SemaphoreSlim _slots = new(size);
    private readonly ConcurrentStack<Worker> _idle = new();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);
    private readonly ConcurrentBag<Process> _all = new();
    private int _failures;
    private int _started;

    /// <summary>How many pictures were kept as they were because a worker died, timed out or could not start.</summary>
    public int Failures => Volatile.Read(ref _failures);

    /// <summary>Worker processes that are still running (a hung worker must not outlive its timeout).</summary>
    public int Alive => _all.Count(p => { try { return !p.HasExited; } catch { return false; } });

    /// <summary>How many worker processes were started (a replaced one counts again).</summary>
    public int Started => Volatile.Read(ref _started);

    public async Task<BookImage?> ScaleAsync(BookImage image, int width, int height, CancellationToken ct)
    {
        if (launch == null) return null;
        await _slots.WaitAsync(ct);
        try
        {
            if (!_idle.TryPop(out var worker)) worker = new Worker(launch, this);
            try
            {
                var result = await worker.CallAsync(image, width, height, _timeout, ct);
                _idle.Push(worker);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                worker.Kill(); // mid-message: its pipe cannot be reused
                throw;
            }
            catch (Exception)
            {
                worker.Kill();
                Interlocked.Increment(ref _failures);
                return null;
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose()
    {
        while (_idle.TryPop(out var worker)) worker.Kill();
        _slots.Dispose();
    }

    private sealed class Worker(BookImageWorkerLaunch launch, BookImageWorkerPool pool)
    {
        private Process? _process;

        public async Task<BookImage?> CallAsync(BookImage image, int width, int height, TimeSpan timeout, CancellationToken ct)
        {
            if (_process is not { HasExited: false })
            {
                var info = new ProcessStartInfo(launch.FileName)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in launch.Arguments) info.ArgumentList.Add(argument);
                _process = Process.Start(info) ?? throw new InvalidOperationException("The image worker did not start.");
                Interlocked.Increment(ref pool._started);
                pool._all.Add(_process);
                // Nothing is expected on stderr, but a full pipe would stop the worker: read it and drop it.
                var errors = _process.StandardError;
                _ = Task.Run(async () => { try { while (await errors.ReadLineAsync() != null) { } } catch { } });
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await BookImageWire.WriteRequestAsync(_process.StandardInput.BaseStream, image, width, height, limit.Token);
                return await BookImageWire.ReadReplyAsync(_process.StandardOutput.BaseStream, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The image worker did not answer in time.");
            }
        }

        public void Kill()
        {
            try { _process?.Kill(entireProcessTree: true); } catch { }
            try { _process?.Dispose(); } catch { }
            _process = null;
        }
    }
}
