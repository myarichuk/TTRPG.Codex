using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Codex.Launcher;

public partial class WelcomeWindow : Window
{
    public const string Host = "host";
    public const string Solo = "solo";

    public WelcomeWindow() => InitializeComponent();

    private void OnHost(object? sender, RoutedEventArgs e) => Close(Host);
    private void OnSolo(object? sender, RoutedEventArgs e) => Close(Solo);
}
