using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Codex.Launcher;

public partial class App : Application
{
    private LauncherViewModel? model;
    private MainWindow? window;
    private NativeMenuItem? toggleItem;
    private NativeMenuItem? openItem;
    private NativeMenuItem? copyItem;
    private NativeMenuItem? copyLocalItem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var dataHome = LauncherPaths.DataHome();
            model = new LauncherViewModel(LauncherPaths.ServerDir(), dataHome);
            window = new MainWindow(model);
            model.QuitRequested = () => Dispatcher.UIThread.Post(() => Quit(desktop));
            model.Changed += () => Dispatcher.UIThread.Post(UpdateMenu);
            BuildTray(desktop);
            desktop.Exit += (_, _) => model.Controller.Dispose();

            // A logout, a shutdown or `kill` should stop the server cleanly, like Quit does.
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            {
                PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    model.Quit();
                });
            }

            window.Show();
            var args = Environment.GetCommandLineArgs();
            var shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                // A look at the window without a screen grab (used to review the design; the server is not started).
                _ = RenderAndExitAsync(desktop, args[shot + 1]);
                return;
            }

            if (!model.Settings.WelcomeShown)
            {
                _ = WelcomeAsync();
                return;
            }

            model.StartServer(openBrowser: model.Settings.OpenBrowserOnStart);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        toggleItem = new NativeMenuItem("Stop");
        toggleItem.Click += (_, _) => model!.Toggle();
        openItem = new NativeMenuItem("Open TTRPG.Codex");
        openItem.Click += (_, _) => model!.OpenBrowser();
        copyItem = new NativeMenuItem("Copy link for players");
        copyItem.Click += async (_, _) => await window!.CopyAsync(model!.Links.Count > 1 ? model.Links[1].Url : model.Settings.LocalUrl);
        copyLocalItem = new NativeMenuItem("Copy link for this computer");
        copyLocalItem.Click += async (_, _) => await window!.CopyAsync(model!.Settings.LocalUrl);
        var content = new NativeMenuItem("Open my content folder");
        content.Click += (_, _) => model!.OpenMyContentFolder();
        var backups = new NativeMenuItem("Open backups folder");
        backups.Click += (_, _) => model!.OpenBackupsFolder();
        var show = new NativeMenuItem("Show window");
        show.Click += (_, _) => ShowWindow();
        var help = new NativeMenuItem("How do players join?");
        help.Click += (_, _) => window!.ShowHelp();
        var check = new NativeMenuItem("Players can't join?");
        check.Click += (_, _) => window!.ShowCheck();
        var log = new NativeMenuItem("Open log");
        log.Click += (_, _) => model!.OpenLog();
        var data = new NativeMenuItem("Open data folder");
        data.Click += (_, _) => model!.OpenDataFolder();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => model!.Quit();

        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[] { openItem, show, new NativeMenuItemSeparator(), toggleItem, copyItem, copyLocalItem, help, check, new NativeMenuItemSeparator(), log, data, backups, content, new NativeMenuItemSeparator(), quit })
        {
            menu.Items.Add(item);
        }

        var icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ttrpg-codex-tray/Assets/tray.png"))),
            ToolTipText = "TTRPG.Codex",
            Menu = menu,
            IsVisible = true,
        };
        icon.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { icon });
        UpdateMenu();
    }

    /// <summary>The first run: ask who is playing, then set things up so the DM never has to find a setting.</summary>
    private async Task WelcomeAsync()
    {
        // CODEX_WELCOME_ANSWER=host|solo answers for an unattended first run (tests, managed installs).
        var scripted = Environment.GetEnvironmentVariable("CODEX_WELCOME_ANSWER");
        var choice = scripted is WelcomeWindow.Host or WelcomeWindow.Solo ? scripted : await new WelcomeWindow().ShowDialog<string?>(window!);
        model!.Controller.Note($"welcome answered: {choice ?? "closed"}");
        model!.MarkWelcomeShown();
        if (choice != WelcomeWindow.Host)
        {
            model.StartServer(openBrowser: true);
            return;
        }

        model.SetAllowLan(true);
        void ShowCodeWhenReady()
        {
            if (model.Controller.State != ServerState.Running) return;
            model.Changed -= ShowCodeWhenReady;
            Dispatcher.UIThread.Post(() =>
            {
                if (model.Links.Skip(1).FirstOrDefault() is { } link)
                {
                    model.Controller.Note($"showing the QR code for {link.Url}");
                    window!.ShowQr(link.Label, link.Url);
                }
            });
        }

        model.Changed += ShowCodeWhenReady;
        model.StartServer(openBrowser: true);
    }

    private async Task RenderAndExitAsync(IClassicDesktopStyleApplicationLifetime desktop, string path)
    {
        // Renders each window to <path>, <path>-help, -welcome and -qr so the design can be reviewed without a screen grab.
        async Task Shot(Window w, string file)
        {
            w.Show();
            await Task.Delay(1200);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)w.Bounds.Width * 2, (int)w.Bounds.Height * 2), new Vector(192, 192));
            bitmap.Render(w);
            bitmap.Save(file);
        }

        await Task.Delay(300);
        if (Environment.GetEnvironmentVariable("CODEX_SCREENSHOT_START") != null)
        {
            model!.StartServer(openBrowser: false);
            for (var i = 0; i < 120 && model.Controller.State != ServerState.Running; i++) await Task.Delay(500);
            Dispatcher.UIThread.Post(() => window!.Refresh());
            await Task.Delay(500);
        }

        var stem = Path.ChangeExtension(path, null);
        await Shot(window!, path);
        await Shot(new HelpWindow(), stem + "-help.png");
        await Shot(new WelcomeWindow(), stem + "-welcome.png");
        var check = new CheckWindow();
        check.Show(await JoinCheck.RunAsync(new LauncherSettings { AllowLan = true }, ServerState.Running, null, new[] { "192.168.1.20", "10.8.0.4" }, "100.101.1.2", _ => Task.FromResult(true), _ => true));
        await Shot(check, stem + "-check.png");
        var broken = new CheckWindow();
        broken.Show(await JoinCheck.RunAsync(new LauncherSettings(), ServerState.Stopped, null, new[] { "192.168.1.20" }, null, _ => Task.FromResult(false), _ => true));
        await Shot(broken, stem + "-check-problems.png");
        await Shot(new QrWindow("Players on your network", "http://192.168.1.20:5080"), stem + "-qr.png");
        model!.Quit();
    }

    private void ShowWindow()
    {
        if (window == null) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void UpdateMenu()
    {
        if (model == null || toggleItem == null || openItem == null || copyItem == null) return;
        toggleItem.Header = model.ToggleText == "Stop" ? "Stop server" : "Start server";
        toggleItem.IsEnabled = !model.Controller.IsBusy;
        openItem.IsEnabled = model.CanOpen;
        copyItem.IsEnabled = model.Links.Count > 1;
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        window?.Close();
        desktop.Shutdown();
    }
}
