namespace Codex.Web;

/// <summary>
/// Brings data across from where older builds left it, once, before anything opens the new folders (<see cref="DataFolders"/>).
/// <para>
/// A relative data folder (<c>RavenData</c> by default) used to mean three places: Raven put the database under its own server
/// folder (<c>bin/.../RavenDBServer/RavenData</c>), while the key ring and the app's JSON backups landed under whatever folder
/// the server was started from. Here:
/// </para>
/// <list type="bullet">
/// <item>The database moves, only when the new folder has none; when both have one, nothing moves and the log names both.</item>
/// <item>Backups move, each one only if no backup of that name is there already.</item>
/// <item>Every key from every old ring is copied in, never overwriting. A ring reads anything any of its keys protected, and
/// nobody knows which old ring protected the stored AI keys, so all of them come along. Copies, so nothing is lost if this is
/// wrong; that also makes it safe to run on every start.</item>
/// </list>
/// </summary>
public static class LegacyDataMigration
{
    /// <summary>Where an older build may have left things.</summary>
    public sealed record Sources(string? Database, IReadOnlyList<string> KeyFolders, IReadOnlyList<string> BackupFolders);

    private const string MovedNote = "MOVED.txt";

    /// <param name="configuredData"><c>Codex:DataDirectory</c> as configured, possibly relative or unset.</param>
    /// <param name="folders">The folders resolved from it.</param>
    /// <param name="contentRoot">The app's content root.</param>
    /// <param name="workingDir">The folder the server was started from.</param>
    /// <param name="ravenServerDir">The folder holding <c>Raven.Server.dll</c>, which Raven took relative paths from.</param>
    public static Sources Find(string? configuredData, DataFolders folders, string contentRoot, string workingDir, string ravenServerDir)
    {
        // An absolute setting meant the same folder to everyone; only a relative one (or none: "RavenData") scattered things.
        var relative = string.IsNullOrWhiteSpace(configuredData) ? DataFolders.DataFolderName
            : Path.IsPathRooted(configuredData) ? null
            : configuredData;

        var keys = new List<string> { Path.Combine(folders.Data, DataFolders.KeysFolderName) };
        var backups = new List<string>();
        string? database = null;
        if (relative != null)
        {
            database = Path.GetFullPath(relative, ravenServerDir);
            foreach (var root in new[] { workingDir, contentRoot })
            {
                var old = Path.GetFullPath(relative, root);
                keys.Add(Path.Combine(old, DataFolders.KeysFolderName));
                backups.Add(Path.Combine(old, "Backups"));
            }
        }

        return new Sources(
            database is { } db && !SamePath(db, folders.Data) ? db : null,
            Distinct(keys.Where(k => !SamePath(k, folders.Keys))),
            Distinct(backups.Where(b => !SamePath(b, folders.Backups))));
    }

    /// <summary>Moves and copies what <paramref name="sources"/> holds into <paramref name="folders"/>; returns what it did, for the log.</summary>
    public static IReadOnlyList<string> Run(Sources sources, DataFolders folders)
    {
        var done = new List<string>();
        if (sources.Database is { } database) MoveDatabase(database, folders.Data, done);
        foreach (var backups in sources.BackupFolders) MoveBackups(backups, folders.Backups, done);
        foreach (var keys in sources.KeyFolders) CopyKeys(keys, folders.Keys, done);
        return done;
    }

    private static void MoveDatabase(string from, string to, List<string> done)
    {
        if (!Directory.Exists(Path.Combine(from, "Databases"))) return;
        // Raven keeps the database's record in System, so the two only ever travel together.
        if (Directory.Exists(Path.Combine(to, "Databases")) || Directory.Exists(Path.Combine(to, "System")))
        {
            done.Add($"Found an older database in '{from}' but '{to}' already has one; left both as they are.");
            return;
        }

        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.Name == MovedNote) continue;
            var target = Path.Combine(to, entry.Name);
            if (Path.Exists(target)) continue;
            Move(entry, target);
        }

        File.WriteAllText(Path.Combine(from, MovedNote), $"This database was moved to {to}{Environment.NewLine}");
        done.Add($"Moved the database from '{from}' to '{to}'.");
    }

    private static void MoveBackups(string from, string to, List<string> done)
    {
        if (!Directory.Exists(from)) return;
        var moved = 0;
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(to, entry.Name);
            if (Path.Exists(target)) continue;
            Directory.CreateDirectory(to);
            Move(entry, target);
            moved++;
        }

        if (moved > 0) done.Add($"Moved {moved} backup(s) from '{from}' to '{to}'.");
    }

    private static void CopyKeys(string from, string to, List<string> done)
    {
        if (!Directory.Exists(from)) return;
        var copied = 0;
        foreach (var key in Directory.EnumerateFiles(from, "key-*.xml"))
        {
            var target = Path.Combine(to, Path.GetFileName(key));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(to);
            File.Copy(key, target);
            copied++;
        }

        if (copied > 0) done.Add($"Copied {copied} Data Protection key(s) from '{from}' to '{to}'.");
    }

    /// <summary>A rename where it can be; across disks, a copy and then a delete.</summary>
    private static void Move(FileSystemInfo entry, string target)
    {
        try
        {
            if (entry is DirectoryInfo dir) dir.MoveTo(target);
            else ((FileInfo)entry).MoveTo(target);
        }
        catch (IOException) when (entry is DirectoryInfo dir && !Path.Exists(target))
        {
            CopyTree(dir, target);
            dir.Delete(recursive: true);
        }
        catch (IOException) when (entry is FileInfo file && !Path.Exists(target))
        {
            file.CopyTo(target);
            file.Delete();
        }
    }

    private static void CopyTree(DirectoryInfo from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in from.EnumerateFiles()) file.CopyTo(Path.Combine(to, file.Name));
        foreach (var dir in from.EnumerateDirectories()) CopyTree(dir, Path.Combine(to, dir.Name));
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(PathComparer).ToList();

    private static bool SamePath(string a, string b) =>
        PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)));

    // macOS and Windows file systems ignore case by default.
    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
