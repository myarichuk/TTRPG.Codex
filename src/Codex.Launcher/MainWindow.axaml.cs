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
        RebuildLinks();
        LanHint.IsVisible = model.Settings.AllowLan;
    }

    private string linksKey = "";

    /// <summary>One row per address worth sharing, each with a Copy button.</summary>
    private void RebuildLinks()
    {
        var links = model.Links;
        var key = string.Join("|", links.Select(l => l.Url));
        if (key == linksKey) return; // keep a "Copied" confirmation on screen across refreshes
        linksKey = key;
        LinksPanel.Children.Clear();
        foreach (var link in links)
        {
            var copy = new Button { Content = "Copy link", Padding = new Thickness(12, 4), FontSize = 13 };
            copy.Classes.Add("etched");
            copy.Click += async (_, _) =>
            {
                await CopyAsync(link.Url);
                copy.Content = "Copied ✓";
                await Task.Delay(1500);
                copy.Content = "Copy link";
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,Auto") };
            row.Children.Add(new TextBlock { Text = link.Label, Foreground = new SolidColorBrush(Color.Parse("#cbbfa5")), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var url = new SelectableTextBlock { Text = link.Url, Foreground = new SolidColorBrush(Color.Parse("#ffe9ae")), FontWeight = FontWeight.Bold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(url, 1);
            Grid.SetColumn(copy, 2);
            row.Children.Add(url);
            row.Children.Add(copy);
            LinksPanel.Children.Add(row);
        }
    }

    private void OnOpenData(object? sender, RoutedEventArgs e) => model.OpenDataFolder();
    private void OnOpenBackups(object? sender, RoutedEventArgs e) => model.OpenBackupsFolder();

    private void OnToggle(object? sender, RoutedEventArgs e) => model.Toggle();
    private void OnOpen(object? sender, RoutedEventArgs e) => model.OpenBrowser();
    private void OnOpenLog(object? sender, RoutedEventArgs e) => model.OpenLog();
    private void OnQuit(object? sender, RoutedEventArgs e) => model.Quit();

    private void OnLanChanged(object? sender, RoutedEventArgs e)
    {
        if (loading) return;
        model.SetAllowLan(LanBox.IsChecked == true);
        SetLanText();
    }

    private void SetLanText() =>
        LanBox.Content = (LanBox.IsChecked == true ? "◆  " : "◇  ") + "Let players on my network join";

    internal async Task CopyAsync(string text)
    {
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
