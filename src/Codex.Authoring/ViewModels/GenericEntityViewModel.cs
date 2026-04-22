using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Codex.Plugin.Abstractions;

namespace Codex.Authoring.ViewModels;

public partial class FieldViewModel : ObservableObject
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private object? _value;
    [ObservableProperty] private FieldDefinition _definition;

    public FieldViewModel(FieldDefinition definition, object? initialValue)
    {
        _definition = definition;
        _label = definition.Label;
        _value = initialValue ?? definition.DefaultValue;
    }
}

public partial class GenericEntityViewModel : ObservableObject
{
    public UISchema Schema { get; }
    public ObservableCollection<FieldViewModel> Fields { get; } = new();
    
    [ObservableProperty] private string _entityId;
    [ObservableProperty] private string _name;

    public GenericEntityViewModel(UISchema schema, string id, string name, Dictionary<string, object> properties)
    {
        Schema = schema;
        _entityId = id;
        _name = name;

        foreach (var fieldDef in schema.Fields)
        {
            properties.TryGetValue(fieldDef.Key, out var val);
            Fields.Add(new FieldViewModel(fieldDef, val));
        }
    }

    public Dictionary<string, object> GetProperties()
    {
        var props = new Dictionary<string, object>();
        foreach (var field in Fields)
        {
            if (field.Value != null)
                props[field.Definition.Key] = field.Value;
        }
        return props;
    }
}