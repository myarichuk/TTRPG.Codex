using System.Diagnostics;
using Codex.Localization;
using static Codex.Launcher.Strings;

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
        ServerState.Starting => L["Launcher.Status.Starting"],
        ServerState.Running => L["Launcher.Status.Running", Isolate(Settings.LocalUrl)],
        ServerState.Stopping => L["Launcher.Status.Stopping"],
        ServerState.Failed => L["Launcher.Status.Failed"],
        _ => L["Launcher.Status.Stopped"],
    };

    public string StatusColor => Controller.State switch
    {
        ServerState.Running => "#5fbf6a",
        ServerState.Starting or ServerState.Stopping => "#e0b64a",
        ServerState.Failed => "#d9604a",
        _ => "#8a8478",
    };

    /// <summary>True while the server is up or coming up, i.e. when the toggle would stop it.</summary>
    public bool IsRunning => Controller.State is ServerState.Running or ServerState.Starting;

    public string ToggleLabel => IsRunning ? L["Launcher.StopServer"] : L["Launcher.StartServer"];
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
        if (IsRunning) _ = Controller.StopAsync();
        else StartServer(openBrowser: true);
    }

    public void SetAllowLan(bool allow)
    {
        if (Settings.AllowLan == allow) return;
        Settings.AllowLan = allow;
        Settings.Save(LauncherPaths.SettingsFile(dataHome));
        Changed?.Invoke();
        if (IsRunning) _ = RestartAsync();
    }

    /// <summary>Saves the chosen language and relabels every open window and the tray menu. A running server keeps
    /// the default language it was started with until its next start.</summary>
    public void SetLanguage(string tag)
    {
        if (!SupportedCultures.IsSupported(tag)) return;
        Settings.Language = SupportedCultures.Normalize(tag);
        Settings.Save(LauncherPaths.SettingsFile(dataHome));
        Strings.Set(Settings.Language);
        Changed?.Invoke();
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

    /// <summary>The folder for the DM's own systems and packs (see the wiki's homebrew guide).</summary>
    public void OpenMyContentFolder()
    {
        Directory.CreateDirectory(LauncherPaths.MyContent(dataHome));
        Shell.Open(LauncherPaths.MyContent(dataHome));
    }

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
