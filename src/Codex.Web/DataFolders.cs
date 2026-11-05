namespace Codex.Web;

/// <summary>
/// Where the server keeps what it must not lose, worked out once at startup as absolute paths. <see cref="Data"/> holds the
/// database and backups; <see cref="Keys"/>, the Data Protection key ring that sign-in cookies and stored AI keys need, sits
/// beside it by default (<c>RavenData</c> and <c>Keys</c> under one folder, next to <c>RavenServer</c>).
/// <para>
/// With no <c>Codex:DataDirectory</c> the data goes to the per-user folder the launchers use, so a build run from the IDE,
/// <c>dotnet run</c> from any folder, and the installed app all find the same campaigns and the same keys. A relative setting
/// is taken from the app's content root, never the working folder: before this, the database followed Raven's own server
/// folder under <c>bin/</c> and the key ring followed whatever folder the server was started from, so the two drifted apart
/// and stored AI keys stopped decrypting (<see cref="LegacyDataMigration"/> brings old data across).
/// </para>
/// </summary>
public sealed record DataFolders(string Data, string Keys)
{
    public const string DataFolderName = "RavenData";
    public const string KeysFolderName = "Keys";

    public string Backups => Path.Combine(Data, "Backups");

    /// <param name="configuredData"><c>Codex:DataDirectory</c>, if set.</param>
    /// <param name="configuredKeys"><c>Codex:KeysDirectory</c>, if set.</param>
    /// <param name="contentRoot">What relative settings are taken from.</param>
    /// <param name="userHome">The per-user folder; <see cref="UserHome"/> when null.</param>
    public static DataFolders Resolve(string? configuredData, string? configuredKeys, string contentRoot, string? userHome = null)
    {
        var data = string.IsNullOrWhiteSpace(configuredData)
            ? Path.Combine(userHome ?? UserHome(), DataFolderName)
            : Path.GetFullPath(configuredData, contentRoot);
        data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(data));

        var keys = string.IsNullOrWhiteSpace(configuredKeys)
            ? Path.Combine(Path.GetDirectoryName(data) ?? data, KeysFolderName)
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredKeys, contentRoot));
        return new DataFolders(data, keys);
    }

    /// <summary>
    /// The per-user folder: <c>CODEX_DATA_HOME</c> if set, else Application Support (macOS), LocalAppData (Windows) or the XDG
    /// data folder. Kept in step with the launcher's <c>LauncherPaths.DataHome</c> and the shell launchers.
    /// </summary>
    public static string UserHome()
    {
        var overridden = Environment.GetEnvironmentVariable("CODEX_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TTRPG.Codex");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "TTRPG.Codex");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg, "ttrpg-codex");
    }
}
