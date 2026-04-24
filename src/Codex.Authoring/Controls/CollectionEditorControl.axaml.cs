using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Codex.Plugin.Abstractions;

namespace Codex.Authoring.Controls;

public partial class CollectionEditorControl : UserControl
{
    public static readonly DirectProperty<CollectionEditorControl, IEnumerable?> ItemsProperty =
        AvaloniaProperty.RegisterDirect<CollectionEditorControl, IEnumerable?>(
            nameof(Items),
            o => o.Items,
            (o, v) => o.Items = v);

    private IEnumerable? _items;

    public IEnumerable? Items
    {
        get => _items;
        set
        {
            SetAndRaise(ItemsProperty, ref _items, value);
            InitializeCollectionIfNeeded();
        }
    }

    public static readonly DirectProperty<CollectionEditorControl, string?> TargetTypeProperty =
        AvaloniaProperty.RegisterDirect<CollectionEditorControl, string?>(
            nameof(TargetType),
            o => o.TargetType,
            (o, v) => o.TargetType = v);

    private string? _targetType;

    public string? TargetType
    {
        get => _targetType;
        set => SetAndRaise(TargetTypeProperty, ref _targetType, value);
    }

    public CollectionEditorControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void InitializeCollectionIfNeeded()
    {
        if (_items == null)
        {
            if (TargetType == "Effect" || TargetType == "Cost" || TargetType == "Trigger" || TargetType == "Requirement")
            {
                 Items = new ObservableCollection<TypedComponent>();
            }
            else
            {
                 Items = new ObservableCollection<string>();
            }
        }
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        InitializeCollectionIfNeeded();

        if (Items is ObservableCollection<string> stringCollection)
        {
            stringCollection.Add(string.Empty);
        }
        else if (Items is ObservableCollection<TypedComponent> typedCompCollection)
        {
            typedCompCollection.Add(new TypedComponent("New" + (TargetType ?? "Component")));
        }
        else if (Items is IList list && !list.IsReadOnly && !list.IsFixedSize)
        {
            try
            {
                var itemType = list.GetType().GetGenericArguments().FirstOrDefault() ?? typeof(object);
                if (itemType == typeof(string))
                {
                     list.Add(string.Empty);
                }
                else if (itemType == typeof(TypedComponent))
                {
                     list.Add(new TypedComponent("New" + (TargetType ?? "Component")));
                }
                else
                {
                    list.Add(Activator.CreateInstance(itemType));
                }
            }
            catch
            {
                // Ignore if type doesn't match or cannot be instantiated
            }
        }
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext != null && Items != null)
        {
            var item = btn.DataContext;
            if (Items is ObservableCollection<string> stringCollection && item is string strItem)
            {
                stringCollection.Remove(strItem);
            }
            else if (Items is ObservableCollection<TypedComponent> typedCompCollection && item is TypedComponent compItem)
            {
                typedCompCollection.Remove(compItem);
            }
            else if (Items is IList list && !list.IsReadOnly && !list.IsFixedSize)
            {
                list.Remove(item);
            }
        }
    }
}
