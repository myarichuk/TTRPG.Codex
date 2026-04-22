using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Codex.Authoring;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
