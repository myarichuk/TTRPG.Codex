using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public record TypedComponent(string Type, Dictionary<string, object>? Params = null);

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