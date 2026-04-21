using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public interface IContentRegistry
{
    // Abilities
    void RegisterAbility(IAbilityDefinition ability, int priority);
    IAbilityDefinition? GetAbility(string fullId);
    IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId);

    // NPCs
    void RegisterNpc(INpcDefinition npc, int priority);
    INpcDefinition? GetNpc(string fullId);
    IEnumerable<INpcDefinition> GetNpcsBySystem(string systemId);

    // Locations
    void RegisterLocation(ILocationDefinition location, int priority);
    ILocationDefinition? GetLocation(string fullId);
    IEnumerable<ILocationDefinition> GetLocationsBySystem(string systemId);

    IEnumerable<string> GetLoadedPacks();
    System.Threading.Tasks.Task ExecuteAbilityAsync(string fullId, dynamic context);
}

public interface INpcDefinition
{
    string Id { get; }
    string SystemId { get; }
    string PackId { get; }
    string Name { get; }
    string? Description { get; }
    Dictionary<string, int> Resources { get; }
    List<string> Abilities { get; }
    Dictionary<string, object> Metadata { get; }
}

public interface ILocationDefinition
{
    string Id { get; }
    string SystemId { get; }
    string PackId { get; }
    string Name { get; }
    string? Description { get; }
    List<string> Tags { get; }
    Dictionary<string, object> Metadata { get; }
}