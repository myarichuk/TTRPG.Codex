namespace Codex.Launcher;

/// <summary>Where the launcher finds the server it starts and where it keeps its own files. The data
/// folders match the shell launchers (<c>ttrpg-codex</c>, <c>TTRPG.Codex.cmd</c>) so a campaign made
/// with either is found by both.</summary>
internal static class LauncherPaths
{
    /// <summary>The folder holding <c>Codex.Web.dll</c>: next to the launcher, or one level up when the
    /// launcher is published into a <c>launcher/</c> subfolder. <c>CODEX_SERVER_DIR</c> overrides both.</summary>
    public static string ServerDir()
    {
        var overridden = Environment.GetEnvironmentVariable("CODEX_SERVER_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var candidate in new[] { here, Path.GetDirectoryName(here), Path.GetDirectoryName(Path.GetDirectoryName(here)) })
        {
            if (candidate != null && File.Exists(Path.Combine(candidate, "Codex.Web.dll"))) return candidate;
        }

        return here;
    }

    /// <summary>The bundled <c>dotnet</c> host, or plain <c>dotnet</c> from the PATH when running from a build tree.</summary>
    public static string DotnetHost(string serverDir)
    {
        var bundled = Path.Combine(serverDir, "runtime", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(bundled) ? bundled : "dotnet";
    }

    /// <summary>The per-user folder that holds <c>RavenData</c>, the launcher's settings and its logs.</summary>
    public static string DataHome()
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

    /// <summary>The DM's own systems and packs: survives upgrades, unlike the content folder inside the app.</summary>
    public static string MyContent(string dataHome) => Path.Combine(dataHome, "content");

    public static string RavenData(string dataHome) => Path.Combine(dataHome, "RavenData");
    public static string SettingsFile(string dataHome) => Path.Combine(dataHome, "launcher.json");
    public static string LogFile(string dataHome) => Path.Combine(dataHome, "logs", "server.log");
}
