using Avalonia;

namespace Codex.Launcher;

internal static class Program
{
    private const string MutexName = "TTRPG.Codex.Launcher.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest();

        // Two launchers would fight over the port and the database's file lock: the second one just
        // opens the running one's address in the browser.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            Shell.Open(LauncherSettings.Load(LauncherPaths.SettingsFile(LauncherPaths.DataHome())).LocalUrl);
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();

    /// <summary>Checks that a release is laid out the way the launcher expects, without opening a window
    /// (CI machines have no display). Exit code 0 means the server and runtime were found.</summary>
    private static int SelfTest()
    {
        var serverDir = LauncherPaths.ServerDir();
        var problem = new ServerController(serverDir, LauncherPaths.DataHome()).CheckInstall();
        var host = LauncherPaths.DotnetHost(serverDir);
        Console.WriteLine($"server: {serverDir}");
        Console.WriteLine($"dotnet: {host}");
        if (host == "dotnet" && Environment.GetEnvironmentVariable("CODEX_SELF_TEST_ALLOW_PATH_DOTNET") == null)
        {
            problem ??= "The bundled .NET runtime is missing.";
        }

        if (!Directory.Exists(Path.Combine(serverDir, "content"))) problem ??= "The content packs folder is missing.";
        Console.WriteLine(problem ?? "ok");
        return problem == null ? 0 : 1;
    }
}
