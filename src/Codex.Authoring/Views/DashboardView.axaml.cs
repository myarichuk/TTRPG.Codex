using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FluentAvalonia.UI.Controls;

namespace Codex.Authoring.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnCreateNewPackClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "New Pack Wizard",
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new NewPackDialogView { DataContext = new ViewModels.NewPackDialogViewModel() }
        };

        var vm = (ViewModels.NewPackDialogViewModel)((NewPackDialogView)dialog.Content).DataContext!;
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var manifest = vm.CreatePackManifest();
            if (manifest != null)
            {
                // In a real app we'd save this or set it as active state.
                // Navigate to Graph Editor
                if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
                {
                    mainWindow.NavView.SelectedItem = mainWindow.NavView.MenuItems.ElementAt(1); // Select Graph Editor
                }
            }
        }
    }

    private async void OnOpenPackClicked(object? sender, RoutedEventArgs e)
    {
        // In a real application, this would open a file picker to select a .codexpack or directory,
        // load the PackManifest from the chosen location, set the active app state.
        // For this task, we will mock loading an existing manifest.
        var mockManifest = new Plugin.Abstractions.PackManifest(
            Id: System.Guid.NewGuid().ToString(),
            Name: "My Awesome Homebrew",
            Version: "1.0.0",
            SystemId: "DnD5e",
            Description: "A mock pack to demonstrate the edit flow.",
            ThumbnailPath: "mock_thumb.png"
        );

        var dialog = new ContentDialog
        {
            Title = "Edit Pack",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new EditPackDialogView { DataContext = new ViewModels.EditPackDialogViewModel(mockManifest) }
        };

        var vm = (ViewModels.EditPackDialogViewModel)((EditPackDialogView)dialog.Content).DataContext!;
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var updatedManifest = vm.UpdateManifest();
            // In a real app we'd save updatedManifest back to disk here.

            if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
            {
                mainWindow.NavView.SelectedItem = mainWindow.NavView.MenuItems.ElementAt(1); // Select Graph Editor
            }
        }
    }
}
