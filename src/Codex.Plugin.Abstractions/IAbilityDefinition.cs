using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public class TypedComponent
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, object>? Params { get; set; }

    public TypedComponent() { }

    public TypedComponent(string type, Dictionary<string, object>? parameters = null) { Type = type; Params = parameters; }

    /// <summary>
    /// A deep-enough copy: a fresh instance with its own Params dictionary, so mutating a child
    /// ability/actor's inherited components can never mutate the base definition they came from
    /// (B9 remediation - the base's TypedComponent instances used to be shared by reference).
    /// </summary>
    public TypedComponent Clone() => new(Type, Params == null ? null : new Dictionary<string, object>(Params));
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