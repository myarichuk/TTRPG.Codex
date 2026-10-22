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
