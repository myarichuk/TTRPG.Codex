namespace Codex.Tests;

/// <summary>
/// Cleanup for the data embedded RavenDB leaves behind. A killed test run never reaches its fixture's
/// <c>Dispose</c>, so each fixture also sweeps leftovers from earlier runs. Leftovers are only swept
/// once nothing in them has been written for a while, so a run that is still going keeps its data.
/// </summary>
public static class TestTempData
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(4);

    /// <summary>
    /// Where the suite's database folders go: the temp directory, or <c>CODEX_TEST_TEMP</c> when set. The tests run RavenDB in
    /// memory, but it still pre-allocates scratch and compression files for every database and index, gigabytes over a full run.
    /// Pointing this at a RAM disk (<c>/dev/shm</c>) keeps them off a small disk, but tmpfs counts against a container's memory
    /// limit and the kernel will kill the Raven server when it is exceeded, so it is a choice for a machine with room, not the default.
    /// </summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("CODEX_TEST_TEMP") is { Length: > 0 } custom && Directory.Exists(custom)
        ? custom
        : Path.GetTempPath();

    /// <summary>A folder name that says which test process owns it: <c>{prefix}{pid}_{guid}</c>.</summary>
    public static string OwnedName(string prefix) => $"{prefix}{Environment.ProcessId}_{Guid.NewGuid():N}";

    /// <summary>
    /// Deletes the folders <see cref="OwnedName"/> made whose process is gone: a run that was killed never disposes its data, and
    /// on tmpfs that is memory held until reboot. A folder of a run still going is left alone, however long it has been quiet.
    /// </summary>
    public static void SweepOrphans(string parent, string prefix)
    {
        if (!Directory.Exists(parent)) return;
        foreach (var dir in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            var rest = Path.GetFileName(dir)[prefix.Length..];
            var pid = rest.Split('_', 2)[0];
            if (int.TryParse(pid, out var owner) && !IsRunning(owner)) DeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Deletes the scratch files RavenDB's server left in <paramref name="folder"/> (<c>ravendb-{pid}-…</c>, named after the server
    /// process) when that server is gone. Killed runs leave gigabytes of them; a server still running keeps its own.
    /// </summary>
    public static void SweepOrphanFiles(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var alive = new Dictionary<int, bool>();
        foreach (var file in Directory.EnumerateFiles(folder, "ravendb-*"))
        {
            var parts = Path.GetFileName(file).Split('-', 3);
            if (parts.Length < 3 || !int.TryParse(parts[1], out var pid)) continue;
            if (!alive.TryGetValue(pid, out var running)) alive[pid] = running = IsRunning(pid);
            if (running) continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held by something: the next run tries again.
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly List<string> ToDeleteOnExit = new();

    /// <summary>
    /// Deletes <paramref name="path"/> when the test process ends. RavenDB's embedded server is one per process, so only the first
    /// fixture's folder is really used, and that fixture disposes it while later classes still run; what is left would sit there
    /// (in RAM, on tmpfs) until the next run's sweep. Nothing needs the data once the process is going.
    /// </summary>
    public static void DeleteOnExit(string path)
    {
        lock (ToDeleteOnExit)
        {
            if (ToDeleteOnExit.Count == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    lock (ToDeleteOnExit)
                    {
                        foreach (var dir in ToDeleteOnExit) DeleteDirectory(dir);
                    }
                };
            }

            ToDeleteOnExit.Add(path);
        }
    }

    /// <summary>
    /// Deletes a directory, retrying while RavenDB still holds files open. A failure is reported in
    /// the test output rather than swallowed, so a leak is visible.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Could not delete test data at {path}: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Deletes the entries in <paramref name="parent"/> that match <paramref name="pattern"/> and have
    /// not been written for <see cref="StaleAfter"/>.
    /// </summary>
    public static void SweepStale(string parent, string pattern)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleAfter;
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent, pattern))
        {
            if (LastWriteUtc(entry) >= cutoff)
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                DeleteDirectory(entry);
            }
            else
            {
                try
                {
                    File.Delete(entry);
                }
                catch (IOException)
                {
                    // Still open by a live server; a later run will try again.
                }
            }
        }
    }

    /// <summary>Latest write anywhere under <paramref name="path"/>, so an active folder is never stale.</summary>
    private static DateTime LastWriteUtc(string path)
    {
        var latest = File.GetLastWriteTimeUtc(path);
        if (!Directory.Exists(path))
        {
            return latest;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            var written = File.GetLastWriteTimeUtc(child);
            if (written > latest)
            {
                latest = written;
            }
        }

        return latest;
    }
}
