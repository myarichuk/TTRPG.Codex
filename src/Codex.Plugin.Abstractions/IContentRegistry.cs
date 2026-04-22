using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public interface IContentRegistry
{
    // Abilities
    void RegisterAbility(IAbilityDefinition ability, int priority);
    IAbilityDefinition? GetAbility(string fullId);
    IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId);

    // Actors (PCs, NPCs, Monsters)
    void RegisterActor(IActorDefinition actor, int priority);
    IActorDefinition? GetActor(string fullId);
    IEnumerable<IActorDefinition> GetActorsBySystem(string systemId);

    // Locations
    void RegisterLocation(ILocationDefinition location, int priority);
    ILocationDefinition? GetLocation(string fullId);
    IEnumerable<ILocationDefinition> GetLocationsBySystem(string systemId);

    IEnumerable<string> GetLoadedPacks();
    System.Threading.Tasks.Task ExecuteAbilityAsync(string fullId, dynamic context);
}

public interface IActorDefinition
{
    string Id { get; }
    string SystemId { get; }
    string PackId { get; }
    string Name { get; }
    string? Description { get; }
    string? Inherits { get; }
    List<string> Tags { get; }
    List<string> ImagePaths { get; }
    Dictionary<string, int> Resources { get; }
    List<string> Abilities { get; }
    Dictionary<string, object> Properties { get; } // Generic bag for Attributes, Skills, etc.
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