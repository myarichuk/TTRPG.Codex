using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Codex.Launcher;

public partial class HelpWindow : Window
{
    public HelpWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
