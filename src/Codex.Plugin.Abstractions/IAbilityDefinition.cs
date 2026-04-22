using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public class TypedComponent
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, object>? Params { get; set; }

    public TypedComponent() { }

    public TypedComponent(string type, Dictionary<string, object>? parameters = null) { Type = type; Params = parameters; }
}

public interface IAbilityDefinition
{
    string Id { get; }
    string SystemId { get; }
    string PackId { get; }
    string Name { get; }
    string? Description { get; }
    string? IconPath { get; }
    string? Inherits { get; }

    List<TypedComponent>? Triggers { get; }
    List<TypedComponent>? Requires { get; }
    List<TypedComponent>? Costs { get; }
    List<TypedComponent>? Effects { get; }

    Dictionary<string, object>? Metadata { get; }
}