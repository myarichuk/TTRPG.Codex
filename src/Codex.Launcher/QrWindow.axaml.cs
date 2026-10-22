using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Codex.Launcher;

public partial class QrWindow : Window
{
    private readonly string url = "";

    public QrWindow() => InitializeComponent();

    internal QrWindow(string label, string url) : this()
    {
        this.url = url;
        Heading.Text = label;
        UrlText.Text = url;
        Code.Source = QrImage.Make(url);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
        CopyButton.Content = "Copied ✓";
        await Task.Delay(1500);
        CopyButton.Content = "Copy link";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
