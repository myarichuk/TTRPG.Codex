using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Data.Converters;
using Codex.Plugin.Abstractions;

namespace Codex.Authoring.ViewModels;

public partial class NodeViewModel : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString();
    [ObservableProperty] private string _name = "New Node";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private string _type = "General";
    [ObservableProperty] private string _icon = "Circle";
    [ObservableProperty] private string _color = "#FFFFFF";
    [ObservableProperty] private bool _isSelected;

    public List<GraphEdgeSchema> AllowedEdges { get; set; } = new();
    public object? Data { get; set; }
}

public partial class EdgeViewModel : ObservableObject
{
    [ObservableProperty] private NodeViewModel _source;
    [ObservableProperty] private NodeViewModel _target;
    [ObservableProperty] private string _relationType = "Default";
    [ObservableProperty] private string _color = "#666666";
    [ObservableProperty] private bool _isBidirectional;

    public EdgeViewModel(NodeViewModel source, NodeViewModel target)
    {
        _source = source;
        _target = target;
    }
}

public partial class GraphViewModel : ObservableObject
{
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<EdgeViewModel> Edges { get; } = new();

    public void AddNode(string name, string type, string icon, string color, double x, double y)
    {
        Nodes.Add(new NodeViewModel { Name = name, Type = type, Icon = icon, Color = color, X = x, Y = y });
    }

    public void Connect(NodeViewModel source, NodeViewModel target, string type)
    {
        Edges.Add(new EdgeViewModel(source, target) { RelationType = type });
    }
}

public class CenterConverter : IValueConverter
{
    public double Offset { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return d + Offset;
        return 0.0;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class FieldTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NodeToPointConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NodeViewModel node)
        {
            // Assuming node width 200 and some estimated height for center
            return new Avalonia.Point(node.X + 100, node.Y + 50);
        }
        return new Avalonia.Point(0, 0);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
