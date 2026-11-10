namespace Codex.Core.Systems;

/// <summary>
/// Watches the content roots and reloads the systems when a file under them changes (overhaul step 10,
/// <c>Codex:Systems:Watch</c>): an author editing a system.yaml, its scripts or a pack sees the change without pressing
/// <i>Reload systems</i>. Changes are gathered until the roots have been quiet for <see cref="Quiet"/> (a copy of a whole folder
/// is many events), then one reload runs; changes made during it start the next wait. Hidden folders (the installer's
/// <c>.staging</c> and <c>.trash</c>, a <c>.git</c>) and build output (<c>bin</c>, <c>obj</c>) are ignored, as the loader
/// ignores them. A watcher that loses events (its buffer overflowed) reloads too: it cannot tell what it missed.
/// </summary>
public sealed class SystemsWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Func<Task> _reload;
    private readonly Action<Exception>? _failed;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Timer? _timer;
    private bool _running;
    private bool _pending;

    /// <summary>How long the roots must be quiet before a reload.</summary>
    public TimeSpan Quiet { get; }

    /// <summary>How many reloads the watcher has run.</summary>
    public int Reloads { get; private set; }

    public SystemsWatcher(IEnumerable<string> roots, Func<Task> reload, TimeSpan quiet, Action<Exception>? failed = null)
    {
        _reload = reload;
        _failed = failed;
        Quiet = quiet;
        foreach (var root in roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct())
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            watcher.Changed += (_, e) => Seen(root, e.FullPath);
            watcher.Created += (_, e) => Seen(root, e.FullPath);
            watcher.Deleted += (_, e) => Seen(root, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Seen(root, e.OldFullPath);
                Seen(root, e.FullPath);
            };
            watcher.Error += (_, _) => Schedule();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>Whether a change at <paramref name="path"/> under <paramref name="root"/> can change what the loader reads.</summary>
    public static bool Matters(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.StartsWith('.') || part is "bin" or "obj");
    }

    private void Seen(string root, string path)
    {
        if (Matters(root, path)) Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            if (_running)
            {
                _pending = true;
                return;
            }

            _timer ??= new Timer(_ => _ = FireAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task FireAsync()
    {
        lock (_gate)
        {
            if (_running || _stop.IsCancellationRequested) return;
            _running = true;
        }

        try
        {
            await _reload();
        }
        catch (Exception ex)
        {
            _failed?.Invoke(ex);
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                Reloads++;
                _running = false;
                again = _pending;
                _pending = false;
            }

            if (again) Schedule();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop.Cancel();
            _timer?.Dispose();
        }

        foreach (var watcher in _watchers) watcher.Dispose();
    }
}
