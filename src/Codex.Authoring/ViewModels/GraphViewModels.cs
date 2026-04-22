using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Data.Converters;

namespace Codex.Authoring.ViewModels;

public partial class NodeViewModel : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString();
    [ObservableProperty] private string _name = "New Node";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private string _type = "Location";
    public object? Data { get; set; }
}

public partial class EdgeViewModel : ObservableObject
{
    [ObservableProperty] private NodeViewModel _source;
    [ObservableProperty] private NodeViewModel _target;
    [ObservableProperty] private string _relationType = "Adjacent";

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

    public void AddNode(string name, string type, double x, double y)
    {
        Nodes.Add(new NodeViewModel { Name = name, Type = type, X = x, Y = y });
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
