using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using static Codex.Launcher.Strings;

namespace Codex.Launcher;

public partial class QrWindow : Window
{
    private readonly string url = "";
    private readonly LinkKind kind;

    public QrWindow() => InitializeComponent();

    internal QrWindow(LinkKind kind, string url) : this()
    {
        this.kind = kind;
        this.url = url;
        UrlText.Text = url;
        Code.Source = QrImage.Make(url);
        WindowText.Bind(this, Localize);
    }

    private void Localize()
    {
        Title = L["Launcher.Qr.Title"];
        Heading.Text = LinkLabel(kind);
        Instruction.Text = L["Launcher.Qr.Instruction"];
        SameWifi.Text = kind == LinkKind.Internet ? L["Launcher.Qr.Anywhere"] : L["Launcher.Qr.SameWifi"];
        CopyButton.Content = L["Launcher.CopyLink"];
        DoneButton.Content = L["Launcher.Done"];
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
        CopyButton.Content = L["Launcher.Copied"];
        await Task.Delay(1500);
        CopyButton.Content = L["Launcher.CopyLink"];
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
