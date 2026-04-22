using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public interface IAbilityDefinition
{
    string Id { get; }
    string SystemId { get; }
    string PackId { get; }
    string Name { get; }
    string? Description { get; }
    string? IconPath { get; }
    TriggerType Trigger { get; }
    string? Inherits { get; }
    Dictionary<string, int>? Costs { get; }
    List<IAbilityEffect>? Effects { get; }
    Dictionary<string, object>? Metadata { get; }
}