using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FluentAvalonia.UI.Controls;

namespace Codex.Authoring;

public partial class MainWindow : Window
{
    public NavigationView NavView => this.FindControl<NavigationView>("NavView")!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void NavView_SelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is NavigationViewItem nvi)
        {
            if (nvi.Tag?.ToString() == "Dashboard")
            {
                NavView.Content = new Views.DashboardView();
            }
            else if (nvi.Tag?.ToString() == "Graph")
            {
                NavView.Content = new Views.GraphEditorView();
            }
        }
    }
}
