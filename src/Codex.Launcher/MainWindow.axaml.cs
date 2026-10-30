using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using static Codex.Launcher.Strings;

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
        model.Changed += () => Dispatcher.UIThread.Post(Refresh);
        WindowText.Bind(this, Localize);
        loading = false;
    }

    /// <summary>Sets every label in the current language; runs at start and whenever the language changes.</summary>
    private void Localize()
    {
        var wasLoading = loading;
        loading = true; // selecting the language below must not count as the DM choosing it
        LanguagePicker.Sync(LanguageBox);
        loading = wasLoading;

        ToolTip.SetTip(LanguageBox, L["Launcher.Language"]);
        AutomationProperties.SetName(LanguageBox, L["Launcher.Language"]);
        ToolTip.SetTip(HelpButton, L["Launcher.Help.Title"]);
        AutomationProperties.SetName(HelpButton, L["Launcher.Help.Automation"]);
        OpenButton.Content = L["Launcher.OpenTable"];
        QuitButton.Content = L["Launcher.Quit"];
        LanHint.Text = L["Launcher.Lan.Hint"];
        OpenLabel.Text = L["Launcher.OpenLabel"];
        LogButton.Content = L["Launcher.Log"];
        DataButton.Content = L["Launcher.CampaignData"];
        BackupsButton.Content = L["Launcher.Backups"];
        ContentButton.Content = L["Launcher.MyContent"];
        CheckButton.Content = L["Launcher.CantJoin"];
        FunnelWarning.Text = L["Launcher.Funnel.Hint"];
        FunnelConfirmButton.Content = L["Launcher.Funnel.Confirm"];
        FunnelCancelButton.Content = L["Launcher.Funnel.Cancel"];
        FunnelApproveButton.Content = L["Launcher.Funnel.ApproveButton"];
        SetLanText();
        SetFunnelText();
        linksKey = ""; // the link rows carry text too: rebuild them
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
        ToggleButton.Content = model.ToggleLabel;
        ToggleButton.IsEnabled = !model.Controller.IsBusy;
        OpenButton.IsEnabled = model.CanOpen;
        ProblemText.Text = model.Controller.Problem;
        ProblemText.IsVisible = !string.IsNullOrEmpty(model.Controller.Problem);
        RebuildLinks();
        LanHint.IsVisible = model.Settings.AllowLan;
        if (!FunnelConfirmPanel.IsVisible) FunnelBox.IsChecked = model.Settings.OpenToInternet; // a refusal switches it back off
        SetFunnelText();
        FunnelNoteText.Text = model.FunnelNote;
        FunnelNoteText.IsVisible = !string.IsNullOrEmpty(model.FunnelNote);
        FunnelApproveButton.IsVisible = model.FunnelApprovalUrl != null;
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
            var copy = new Button { Content = L["Launcher.CopyLink"], Padding = new Thickness(12, 4), FontSize = 13 };
            copy.Classes.Add("etched");
            copy.Click += async (_, _) =>
            {
                await CopyAsync(link.Url);
                copy.Content = L["Launcher.Copied"];
                await Task.Delay(1500);
                copy.Content = L["Launcher.CopyLink"];
            };
            var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            if (link.Kind != LinkKind.This)
            {
                var qr = new Button { Content = L["Launcher.QrCode"], Padding = new Thickness(12, 4), FontSize = 13 };
                qr.Classes.Add("etched");
                qr.Click += (_, _) => ShowQr(link.Kind, link.Url);
                buttons.Children.Add(qr);
            }

            buttons.Children.Add(copy);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var text = new StackPanel { Spacing = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            text.Children.Add(new TextBlock { Text = link.Label, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#cbbfa5")) });
            text.Children.Add(new SelectableTextBlock { Text = link.Url, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#ffe9ae")), FontWeight = FontWeight.Bold });
            Grid.SetColumn(buttons, 1);
            buttons.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            row.Children.Add(text);
            row.Children.Add(buttons);
            LinksPanel.Children.Add(row);
        }
    }

    internal void ShowQr(LinkKind kind, string url)
    {
        if (!IsVisible) Show();
        _ = new QrWindow(kind, url).ShowDialog(this);
    }

    internal void ShowCheck()
    {
        if (!IsVisible) Show();
        _ = new CheckWindow(model).ShowDialog(this);
    }

    private void OnCheck(object? sender, RoutedEventArgs e) => ShowCheck();

    internal void ShowHelp()
    {
        if (!IsVisible) Show();
        new HelpWindow().ShowDialog(this);
    }

    private void OnHelp(object? sender, RoutedEventArgs e) => ShowHelp();
    private void OnOpenData(object? sender, RoutedEventArgs e) => model.OpenDataFolder();
    private void OnOpenContent(object? sender, RoutedEventArgs e) => model.OpenMyContentFolder();
    private void OnOpenBackups(object? sender, RoutedEventArgs e) => model.OpenBackupsFolder();

    private void OnToggle(object? sender, RoutedEventArgs e) => model.Toggle();
    private void OnOpen(object? sender, RoutedEventArgs e) => model.OpenBrowser();
    private void OnOpenLog(object? sender, RoutedEventArgs e) => model.OpenLog();
    private void OnQuit(object? sender, RoutedEventArgs e) => model.Quit();

    private void OnLanguageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || model == null) return;
        if (LanguagePicker.TagAt(LanguageBox) is { } tag && tag != Strings.Tag) model.SetLanguage(tag);
    }

    private void OnLanChanged(object? sender, RoutedEventArgs e)
    {
        if (loading) return;
        model.SetAllowLan(LanBox.IsChecked == true);
        SetLanText();
    }

    private void OnFunnelChanged(object? sender, RoutedEventArgs e)
    {
        if (loading) return;
        if (FunnelBox.IsChecked == true)
        {
            FunnelConfirmPanel.IsVisible = true; // nothing happens until the DM confirms
        }
        else
        {
            FunnelConfirmPanel.IsVisible = false;
            model.SetOpenToInternet(false);
        }

        SetFunnelText();
    }

    private void OnFunnelConfirm(object? sender, RoutedEventArgs e)
    {
        FunnelConfirmPanel.IsVisible = false;
        model.SetOpenToInternet(true);
    }

    private void OnFunnelCancel(object? sender, RoutedEventArgs e)
    {
        FunnelConfirmPanel.IsVisible = false;
        FunnelBox.IsChecked = false;
        SetFunnelText();
    }

    private void OnFunnelApprove(object? sender, RoutedEventArgs e) => model.OpenFunnelApproval();

    private void SetFunnelText() =>
        FunnelBox.Content = (FunnelBox.IsChecked == true ? "◆  " : "◇  ") + L["Launcher.Funnel.Toggle"];

    private void SetLanText() =>
        LanBox.Content = (LanBox.IsChecked == true ? "◆  " : "◇  ") + L["Launcher.Lan.Toggle"];

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
