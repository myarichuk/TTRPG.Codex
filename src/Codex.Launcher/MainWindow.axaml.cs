using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Codex.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherViewModel model;
    private bool loading = true;

    public MainWindow() : this(null!) { }

    internal MainWindow(LauncherViewModel model)
    {
        this.model = model;
        InitializeComponent();
        if (model == null) return; // the XAML previewer

        TryLoadBackdrop();
        LanBox.IsChecked = model.Settings.AllowLan;
        SetLanText();
        loading = false;
        model.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void TryLoadBackdrop()
    {
        try
        {
            var uri = new Uri("avares://ttrpg-codex-tray/Assets/background.jpg");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                Backdrop.Source = new Bitmap(stream);
            }
            else
            {
                HeadingText.IsVisible = true; // no artwork: the window names itself
            }
        }
        catch (Exception)
        {
            // No artwork is fine: the window keeps its plain dark background.
        }
    }

    internal void Refresh()
    {
        StatusText.Text = model.StatusText;
        StatusDot.Fill = new SolidColorBrush(Color.Parse(model.StatusColor));
        ToggleButton.Content = model.ToggleText == "Stop" ? "Stop server" : "Start server";
        ToggleButton.IsEnabled = !model.Controller.IsBusy;
        OpenButton.IsEnabled = model.CanOpen;
        ProblemText.Text = model.Controller.Problem;
        ProblemText.IsVisible = !string.IsNullOrEmpty(model.Controller.Problem);
        var join = model.JoinUrl;
        JoinPanel.IsVisible = join != null;
        JoinText.Text = join;
        LanHint.IsVisible = model.Settings.AllowLan;
    }

    private void OnToggle(object? sender, RoutedEventArgs e) => model.Toggle();
    private void OnOpen(object? sender, RoutedEventArgs e) => model.OpenBrowser();
    private void OnOpenLog(object? sender, RoutedEventArgs e) => model.OpenLog();
    private void OnQuit(object? sender, RoutedEventArgs e) => model.Quit();
    private async void OnCopyJoin(object? sender, RoutedEventArgs e) => await CopyJoinLinkAsync();

    private void OnLanChanged(object? sender, RoutedEventArgs e)
    {
        if (loading) return;
        model.SetAllowLan(LanBox.IsChecked == true);
        SetLanText();
    }

    private void SetLanText() =>
        LanBox.Content = (LanBox.IsChecked == true ? "◆  " : "◇  ") + "Let players on my network join";

    internal async Task CopyJoinLinkAsync()
    {
        var text = model.JoinUrl ?? model.Settings.LocalUrl;
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    /// <summary>The window is the launcher's face but the tray is its home: closing it hides it. Where
    /// there may be no tray (GNOME), it is minimised instead so it can always be found again.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!model.Quitting)
        {
            e.Cancel = true;
            if (model.TrayLikely) Hide(); else WindowState = WindowState.Minimized;
        }

        base.OnClosing(e);
    }
}
