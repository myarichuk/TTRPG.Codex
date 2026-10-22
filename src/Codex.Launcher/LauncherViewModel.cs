using System.Diagnostics;

namespace Codex.Launcher;

/// <summary>Everything the window and the tray menu share: the server, the settings and what to say about them.</summary>
internal sealed class LauncherViewModel
{
    private readonly string dataHome;

    public LauncherViewModel(string serverDir, string dataHome)
    {
        this.dataHome = dataHome;
        Settings = LauncherSettings.Load(LauncherPaths.SettingsFile(dataHome));
        Controller = new ServerController(serverDir, dataHome);
        Controller.Changed += () =>
        {
            if (Controller.State == ServerState.Running && openWhenReady)
            {
                openWhenReady = false;
                if (Settings.OpenBrowserOnStart) OpenBrowser();
            }

            Changed?.Invoke();
        };
    }

    private bool openWhenReady;

    public LauncherSettings Settings { get; }
    public ServerController Controller { get; }
    public bool Quitting { get; private set; }

    /// <summary>False on GNOME, which has no tray unless an extension adds one.</summary>
    public bool TrayLikely { get; } = !(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase);

    public event Action? Changed;
    public Action? QuitRequested { get; set; }

    public string StatusText => Controller.State switch
    {
        ServerState.Starting => "Lighting the candles… (the first start takes a little longer)",
        ServerState.Running => $"The table is ready at {Settings.LocalUrl}",
        ServerState.Stopping => "Putting out the candles…",
        ServerState.Failed => "The server would not start",
        _ => "Server stopped",
    };

    public string StatusColor => Controller.State switch
    {
        ServerState.Running => "#5fbf6a",
        ServerState.Starting or ServerState.Stopping => "#e0b64a",
        ServerState.Failed => "#d9604a",
        _ => "#8a8478",
    };

    public string ToggleText => Controller.State is ServerState.Running or ServerState.Starting ? "Stop" : "Start";
    public bool CanOpen => Controller.State == ServerState.Running;

    public IReadOnlyList<LauncherLink> Links => Settings.Links(LanAddress.Best(), LanAddress.Tailnet());

    public void StartServer(bool openBrowser)
    {
        openWhenReady = openBrowser;
        Controller.Start(Settings);
    }

    public void MarkWelcomeShown()
    {
        Settings.WelcomeShown = true;
        Settings.Save(LauncherPaths.SettingsFile(dataHome));
    }

    public void Toggle()
    {
        if (Controller.State is ServerState.Running or ServerState.Starting) _ = Controller.StopAsync();
        else StartServer(openBrowser: true);
    }

    public void SetAllowLan(bool allow)
    {
        if (Settings.AllowLan == allow) return;
        Settings.AllowLan = allow;
        Settings.Save(LauncherPaths.SettingsFile(dataHome));
        Changed?.Invoke();
        if (Controller.State is ServerState.Running or ServerState.Starting) _ = RestartAsync();
    }

    private async Task RestartAsync()
    {
        await Controller.StopAsync();
        for (var i = 0; i < 100 && Controller.State == ServerState.Stopping; i++) await Task.Delay(100);
        StartServer(openBrowser: false);
    }

    public void OpenBrowser() => Shell.Open(Settings.LocalUrl);
    public void OpenLog() => Shell.Open(File.Exists(Controller.LogPath) ? Controller.LogPath : Path.GetDirectoryName(Controller.LogPath)!);
    public void OpenDataFolder() => Shell.Open(dataHome);

    /// <summary>The daily backups the server writes beside the campaign data.</summary>
    public void OpenBackupsFolder()
    {
        var backups = Path.Combine(LauncherPaths.RavenData(dataHome), "Backups");
        Shell.Open(Directory.Exists(backups) ? backups : dataHome);
    }

    public void Quit()
    {
        Quitting = true;
        QuitRequested?.Invoke();
    }
}

internal static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // No browser or file association: nothing useful to do from a tray app.
        }
    }
}
