using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Codex.Authoring.Views;
using FluentAvalonia.UI.Controls;

namespace Codex.Authoring;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var navView = this.FindControl<NavigationView>("NavView");
        if (navView != null)
        {
            navView.SelectionChanged += OnSelectionChanged;
            navView.SelectedItem = navView.MenuItems.Cast<object>().FirstOrDefault();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is NavigationViewItem item && sender is NavigationView navView)
        {
            var tag = item.Tag?.ToString();

            if (tag == "ThemeToggle")
            {
                ToggleTheme();
            }
            else if (tag == "Dashboard")
            {
                navView.Content = new DashboardView();
            }
            else if (tag == "Graph")
            {
                navView.Content = new GraphEditorView();
            }
            // Other sections will be added as implemented
        }
    }

    private void ToggleTheme()
    {
        var app = Application.Current;
        if (app != null)
        {
            app.RequestedThemeVariant = app.RequestedThemeVariant == ThemeVariant.Dark
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
    }
}
