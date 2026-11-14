namespace Codex.Core.Systems;

/// <summary>
/// Watches the content roots and reloads the systems when a file under them changes (overhaul step 10,
/// <c>Codex:Systems:Watch</c>): an author editing a system.yaml, its scripts or a pack sees the change without pressing
/// <i>Reload systems</i>. Changes are gathered until the roots have been quiet for <see cref="Quiet"/> (a copy of a whole folder
/// is many events), then one reload runs; changes made during it start the next wait. Hidden folders (the installer's
/// <c>.staging</c> and <c>.trash</c>, a <c>.git</c>) and build output (<c>bin</c>, <c>obj</c>) are ignored, as the loader
/// ignores them. A watcher that loses events (its buffer overflowed) reloads too: it cannot tell what it missed.
/// <para>
/// Events are only a hint that something may have changed. Windows raises several <c>Changed</c> events for one write (data, then
/// size or attributes) and an indexer or antivirus scan can touch a file seconds later, so before a reload the watcher compares
/// the roots (path, size, last write of every file the loader could read) with what the last reload saw, and skips the reload
/// when nothing differs. One edit is one reload on every platform.
/// </para>
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
    private bool _forced;
    private string _applied = string.Empty;
    private readonly List<string> _roots = new();

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
            watcher.Error += (_, _) =>
            {
                lock (_gate) _forced = true;
                Schedule();
            };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
            _roots.Add(root);
        }

        _applied = Snapshot();
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

    /// <summary>What the loader could read now: every matching file's path, size and last write, as one comparable string.</summary>
    private string Snapshot()
    {
        var entries = new List<string>();
        foreach (var root in _roots)
        {
            try
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (!Matters(root, file.FullName)) continue;
                    try
                    {
                        entries.Add($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
                    }
                    catch (IOException)
                    {
                        // gone since it was listed: the next event settles it
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries.Add($"{root}|unreadable");
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return string.Join('\n', entries);
    }

    private async Task FireAsync()
    {
        bool force;
        lock (_gate)
        {
            if (_running || _stop.IsCancellationRequested) return;
            _running = true;
            force = _forced;
            _forced = false;
        }

        var reloaded = false;
        try
        {
            var now = Snapshot();
            if (force || now != _applied)
            {
                _applied = now;
                reloaded = true;
                await _reload();
            }
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
                if (reloaded) Reloads++;
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
