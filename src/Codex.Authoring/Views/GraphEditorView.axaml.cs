using System;
using Avalonia.Controls;
using Avalonia.Controls.PanAndZoom;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Codex.Authoring.ViewModels;

namespace Codex.Authoring.Views;

public partial class GraphEditorView : UserControl
{
    private readonly Random _random = new();

    public GraphEditorView()
    {
        InitializeComponent();
        DataContext = new GraphViewModel();

        var addLocBtn = this.FindControl<Button>("AddLocationButton");
        if (addLocBtn != null) addLocBtn.Click += OnAddLocation;

        var addActorBtn = this.FindControl<Button>("AddActorButton");
        if (addActorBtn != null) addActorBtn.Click += OnAddActor;

        var resetZoomBtn = this.FindControl<Button>("ResetZoomButton");
        if (resetZoomBtn != null) resetZoomBtn.Click += OnResetZoom;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnAddLocation(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphViewModel vm)
            vm.AddNode("New Location", "Location", "MapPin", "#3498db", _random.Next(100, 800), _random.Next(100, 500));
    }

    private void OnAddActor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphViewModel vm)
            vm.AddNode("New Actor", "Actor", "User", "#e74c3c", _random.Next(100, 800), _random.Next(100, 500));
    }

    private void OnNodePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is NodeViewModel node && DataContext is GraphViewModel vm)
        {
            foreach (var n in vm.Nodes) n.IsSelected = false;
            node.IsSelected = true;
            e.Handled = true;
        }
    }

    private void OnResetZoom(object? sender, RoutedEventArgs e)
    {
        var zoomBorder = this.FindControl<ZoomBorder>("ZoomBorder");
        zoomBorder?.ResetMatrix();
    }
}
