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
            SyncFunnel();
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
    public bool IsRunning => planning || Controller.State is ServerState.Running or ServerState.Starting;

    public string ToggleLabel => IsRunning ? L["Launcher.StopServer"] : L["Launcher.StartServer"];
    public bool CanOpen => Controller.State == ServerState.Running;

    public IReadOnlyList<LauncherLink> Links => Settings.Links(LanAddress.Best(), LanAddress.Tailnet(), InternetUrl);

    // ---- Open to the internet (Tailscale Funnel) ----

    private FunnelSession? funnel;
    private string? funnelExe;
    private volatile bool planning;
    private volatile bool replan;
    private string? carriedNote;
    private bool approvalOpened;

    /// <summary>What the DM should know about the internet link right now: why it is not on, what it is waiting for, or <c>null</c>.</summary>
    public string? FunnelNote { get; private set; }

    /// <summary>The Tailscale page where the owner of the tailnet approves Funnel, while it is waiting for that.</summary>
    public string? FunnelApprovalUrl { get; private set; }

    /// <summary>The public address, once Tailscale has confirmed it is serving it.</summary>
    public string? InternetUrl { get; private set; }

    private readonly object planGate = new();
    private bool startedExposed;

    private sealed record FunnelPlan(string? InternetUrl, bool Strict, string? Exe);

    public void StartServer(bool openBrowser)
    {
        openWhenReady = openBrowser;
        lock (planGate)
        {
            if (planning)
            {
                replan = true; // a setting changed during the check: look again before starting
                return;
            }

            planning = true;
        }

        Changed?.Invoke();
        _ = StartAfterPlanAsync();
    }

    private async Task StartAfterPlanAsync()
    {
        FunnelPlan plan;
        try
        {
            while (true)
            {
                lock (planGate) replan = false;
                plan = await PlanFunnelAsync();
                lock (planGate)
                {
                    if (replan && !Quitting) continue;
                    break;
                }
            }
        }
        catch
        {
            lock (planGate) planning = false;
            throw;
        }

        // The plan travels in locals, and the funnel's own settings are fixed in the same step as the start, so a
        // second check begun in the gap can neither lose the strict mode nor attach a funnel to a server that is not ready for it.
        lock (planGate)
        {
            planning = false;
            if (Quitting) return;
            funnelExe = plan.Exe;
            if (Controller.State is ServerState.Stopped or ServerState.Failed)
            {
                startedExposed = plan.InternetUrl != null;
                Controller.Start(Settings, plan.InternetUrl, plan.Strict);
            }
        }

        if (Controller.State == ServerState.Stopped) Changed?.Invoke();
    }

    /// <summary>Works out, before the server starts, whether it can be opened to the internet and at what address. The
    /// address is known in advance (this computer's Tailscale name), so the server can start already knowing it.</summary>
    private async Task<FunnelPlan> PlanFunnelAsync()
    {
        InternetUrl = null;
        FunnelApprovalUrl = null;
        approvalOpened = false;
        FunnelNote = null;
        var carried = carriedNote;
        carriedNote = null;
        var exe = TailscaleCli.Find();

        if (!Settings.OpenToInternet)
        {
            // A funnel left running by a launcher that was killed would keep this port on the internet, so such a run
            // starts in the stricter mode. The funnel is a foreground command: ending that command ends the funnel.
            var strict = await ReapStrayFunnelAsync(exe);
            if (strict) FunnelNote = L["Launcher.Funnel.Stray", Isolate($"tailscale funnel {Settings.EffectivePort} off")];
            FunnelNote ??= carried;
            return new FunnelPlan(null, strict, null);
        }

        if (exe == null) return Refuse(L["Launcher.Funnel.NotInstalled"]);
        var status = await TailscaleCli.StatusAsync(exe);
        if (status == null) return Refuse(L["Launcher.Funnel.NoStatus"]);
        if (!status.SignedIn) return Refuse(L["Launcher.Funnel.SignedOut"]);
        if (status.DnsName == null) return Refuse(L["Launcher.Funnel.NoName"]);
        if (status.HttpsEnabled == false) return Refuse(L["Launcher.Funnel.NoHttps"]);

        return new FunnelPlan("https://" + status.DnsName, false, exe);
    }

    /// <summary>Ends a funnel an earlier launcher left behind, if the marker it wrote names one that is still alive.
    /// Returns true when the server must still start in the stricter mode: the funnel could not be ended, or Tailscale
    /// itself lists one for this port.</summary>
    private async Task<bool> ReapStrayFunnelAsync(string? exe)
    {
        var strict = false;
        if (File.Exists(FunnelMarker))
        {
            var ended = false;
            try
            {
                var text = File.ReadAllText(FunnelMarker).Trim();
                if (text.Length == 0) ended = true;
                else if (int.TryParse(text, out var pid)) ended = EndStrayProcess(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            if (ended) MarkFunnel(false);
            else strict = true;
        }

        if (!strict && exe != null && TailscaleCli.FunnelPointsAtPort(await TailscaleCli.FunnelStatusAsync(exe), Settings.EffectivePort))
        {
            strict = true;
        }

        return strict;
    }

    /// <summary>True when the process is gone: it already was, or it was a tailscale command and has now been ended.</summary>
    private static bool EndStrayProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Contains("tailscale", StringComparison.OrdinalIgnoreCase)) return true; // the number was reused by something else
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(3000);
        }
        catch (ArgumentException)
        {
            return true; // no such process
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private FunnelPlan Refuse(string note)
    {
        Settings.OpenToInternet = false;
        FunnelNote = note;
        return new FunnelPlan(null, false, null);
    }

    /// <summary>Starts Funnel once the server is answering, and takes it down as soon as the server stops.</summary>
    private void SyncFunnel()
    {
        if (Controller.State == ServerState.Running && funnelExe != null && startedExposed && funnel == null && Settings.OpenToInternet && !Quitting)
        {
            FunnelNote = L["Launcher.Funnel.Waiting"];
            funnel = new FunnelSession(funnelExe, Settings.EffectivePort, OnFunnelLine, Controller.Note, OnFunnelExit);
            try
            {
                funnel.Start();
                MarkFunnel(true, funnel.ProcessId);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Controller.Note("could not start tailscale: " + ex.Message);
                StopFunnel();
                GiveUpOnInternet(L["Launcher.Funnel.Stopped"]);
            }
        }
        else if (Controller.State is ServerState.Stopping or ServerState.Stopped or ServerState.Failed)
        {
            StopFunnel();
        }
    }

    private void StopFunnel()
    {
        var running = funnel;
        funnel = null;
        running?.Stop();
        if (running != null) MarkFunnel(false);
        InternetUrl = null;
        FunnelApprovalUrl = null;
        if (FunnelNote == L["Launcher.Funnel.Waiting"]) FunnelNote = null;
    }

    private void OnFunnelLine(FunnelLine line)
    {
        switch (line.Kind)
        {
            case FunnelLineKind.Public:
                InternetUrl = line.Url;
                FunnelApprovalUrl = null;
                FunnelNote = null;
                break;
            case FunnelLineKind.Approval:
                FunnelApprovalUrl = line.Url;
                FunnelNote = L["Launcher.Funnel.Approve"];
                if (!approvalOpened && line.Url != null)
                {
                    approvalOpened = true;
                    Shell.Open(line.Url);
                }

                break;
            case FunnelLineKind.NeedsPermission:
                FunnelNote = L["Launcher.Funnel.Permission", Isolate("sudo tailscale set --operator=$USER")];
                break;
            default:
                return;
        }

        Changed?.Invoke();
    }

    private string FunnelMarker => Path.Combine(dataHome, "funnel-active");

    /// <summary>A file that exists while a funnel may be running, holding its process number, so a launcher that dies
    /// without stopping it can end it next time.</summary>
    private void MarkFunnel(bool on, int? processId = null)
    {
        try
        {
            if (on) File.WriteAllText(FunnelMarker, processId?.ToString() ?? "");
            else File.Delete(FunnelMarker);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnFunnelExit(int? code)
    {
        Controller.Note($"tailscale funnel ended ({(code is { } c ? "code " + c : "code unknown")})");
        funnel = null;
        MarkFunnel(false);
        InternetUrl = null;
        FunnelApprovalUrl = null;
        if (FunnelNote == L["Launcher.Funnel.Waiting"]) FunnelNote = null;
        FunnelNote ??= L["Launcher.Funnel.Stopped"];
        Changed?.Invoke();
        GiveUpOnInternet(FunnelNote);
    }

    /// <summary>The server was told it was on the internet at an address that no longer answers: start it again as an
    /// ordinary one, so its invite links work for the people who are actually there.</summary>
    private void GiveUpOnInternet(string note)
    {
        FunnelNote = note;
        if (!Settings.OpenToInternet) return;
        Settings.OpenToInternet = false;
        carriedNote = note;
        if (Controller.State == ServerState.Running) _ = RestartAsync();
        Changed?.Invoke();
    }

    public void OpenFunnelApproval()
    {
        if (FunnelApprovalUrl is { } url) Shell.Open(url);
    }

    public void SetOpenToInternet(bool on)
    {
        if (Settings.OpenToInternet == on) return;
        Settings.OpenToInternet = on;
        if (!on)
        {
            FunnelNote = null;
            FunnelApprovalUrl = null;
        }

        Changed?.Invoke();
        if (IsRunning) _ = RestartAsync();
    }

    public void MarkWelcomeShown()
    {
        Settings.WelcomeShown = true;
        Settings.Save(LauncherPaths.SettingsFile(dataHome));
    }

    public void Toggle()
    {
        if (planning) return;
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
        StopFunnel(); // never leave the internet link up behind a closed launcher
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
