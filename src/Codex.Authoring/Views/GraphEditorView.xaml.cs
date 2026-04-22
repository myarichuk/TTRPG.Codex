using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Codex.Authoring.ViewModels;

namespace Codex.Authoring.Views;

public partial class GraphEditorView : UserControl
{
    public GraphEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = new GraphViewModel();
        
        var addLocBtn = this.FindControl<Button>("AddLocationButton");
        if (addLocBtn != null) addLocBtn.Click += OnAddLocation;

        var addActorBtn = this.FindControl<Button>("AddActorButton");
        if (addActorBtn != null) addActorBtn.Click += OnAddActor;
    }

    private void OnAddLocation(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphViewModel vm) vm.AddNode("New Location", "Location", 100, 100);
    }

    private void OnAddActor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphViewModel vm) vm.AddNode("New Actor", "Actor", 100, 100);
    }
}
