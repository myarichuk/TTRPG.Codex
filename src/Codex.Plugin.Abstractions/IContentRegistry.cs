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
    List<string> ImagePaths { get; }
    Dictionary<string, int> Resources { get; }
    List<string> Abilities { get; }
    Dictionary<string, object> Metadata { get; }
}

public record LocationEdge(string TargetId, string RelationType, float Distance = 0, string TravelType = "Foot");

public interface ILocationDefinition
{
    string Id { get; }
    string SystemId { get; }
    string PackId { get; }
    string Name { get; }
    string? Description { get; }
    string? ParentId { get; } // For "Contains" relationship
    List<string> ImagePaths { get; }
    List<LocationEdge> Connections { get; }
    List<string> Tags { get; }
    Dictionary<string, object> Metadata { get; }
}