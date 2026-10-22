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
        copyItem = new NativeMenuItem("Copy join link");
        copyItem.Click += async (_, _) => await window!.CopyJoinLinkAsync();
        var show = new NativeMenuItem("Show window");
        show.Click += (_, _) => ShowWindow();
        var log = new NativeMenuItem("Open log");
        log.Click += (_, _) => model!.OpenLog();
        var data = new NativeMenuItem("Open data folder");
        data.Click += (_, _) => model!.OpenDataFolder();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => model!.Quit();

        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[] { openItem, show, new NativeMenuItemSeparator(), toggleItem, copyItem, new NativeMenuItemSeparator(), log, data, new NativeMenuItemSeparator(), quit })
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

    private async Task RenderAndExitAsync(IClassicDesktopStyleApplicationLifetime desktop, string path)
    {
        await Task.Delay(1500);
        var size = new PixelSize((int)window!.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        bitmap.Render(window);
        bitmap.Save(path);
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
        copyItem.IsEnabled = model.JoinUrl != null;
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        window?.Close();
        desktop.Shutdown();
    }
}
