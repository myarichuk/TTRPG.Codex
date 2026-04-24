using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Codex.Core;
using Codex.Plugin.Abstractions;
using FluentAvalonia.UI.Controls;

namespace Codex.Authoring.Controls;

public partial class EntityReferenceControl : UserControl
{
    public static readonly DirectProperty<EntityReferenceControl, string?> EntityIdProperty =
        AvaloniaProperty.RegisterDirect<EntityReferenceControl, string?>(
            nameof(EntityId),
            o => o.EntityId,
            (o, v) => o.EntityId = v);

    private string? _entityId;

    public string? EntityId
    {
        get => _entityId;
        set => SetAndRaise(EntityIdProperty, ref _entityId, value);
    }

    public static readonly DirectProperty<EntityReferenceControl, string?> TargetEntityTypeProperty =
        AvaloniaProperty.RegisterDirect<EntityReferenceControl, string?>(
            nameof(TargetEntityType),
            o => o.TargetEntityType,
            (o, v) => o.TargetEntityType = v);

    private string? _targetEntityType;

    public string? TargetEntityType
    {
        get => _targetEntityType;
        set => SetAndRaise(TargetEntityTypeProperty, ref _targetEntityType, value);
    }

    public EntityReferenceControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnSearchClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = $"Select {TargetEntityType ?? "Entity"}",
            PrimaryButtonText = "Select",
            CloseButtonText = "Cancel"
        };

        var searchBox = new TextBox { Watermark = "Enter entity ID..." };
        dialog.Content = new StackPanel
        {
            Spacing = 10,
            Children = { searchBox }
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(searchBox.Text))
        {
            EntityId = searchBox.Text;
        }
    }
}
