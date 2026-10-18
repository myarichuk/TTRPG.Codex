using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

public interface IContentRegistry
{
    // Abilities (TRCE-shaped, executable)
    void RegisterAbility(IAbilityDefinition ability, int priority);
    IAbilityDefinition? GetAbility(string fullId);
    IEnumerable<IAbilityDefinition> GetAbilitiesBySystem(string systemId);

    // Actors (NPC/monster blueprints the DM instantiates into a campaign)
    void RegisterActor(IActorDefinition actor, int priority);
    IActorDefinition? GetActor(string fullId);
    IEnumerable<IActorDefinition> GetActorsBySystem(string systemId);

    // Locations
    void RegisterLocation(ILocationDefinition location, int priority);
    ILocationDefinition? GetLocation(string fullId);
    IEnumerable<ILocationDefinition> GetLocationsBySystem(string systemId);

    // Generic reference content (classes, feats, spells, items, lore, ...)
    void RegisterEntry(ContentEntry entry, int priority);
    ContentEntry? GetEntry(string systemId, string type, string id);

    /// <summary>Every entry of <paramref name="type"/> for a system, optionally restricted to the given pack ids.</summary>
    IEnumerable<ContentEntry> GetEntries(string systemId, string type, IReadOnlySet<string>? packIds = null);

    /// <summary>Distinct entry types registered for a system, in first-seen order.</summary>
    IEnumerable<string> GetEntryTypes(string systemId);

    /// <summary>
    /// Case-insensitive search over name, tags and description. Name matches rank first (prefix
    /// before substring), then tag matches, then description matches. An empty query returns
    /// everything of the given type (or everything, when type is null).
    /// </summary>
    IEnumerable<ContentEntry> Search(string systemId, string query, string? type = null, IReadOnlySet<string>? packIds = null, int limit = 200);

    // Packs
    void RegisterPack(PackManifest manifest);
    IEnumerable<PackManifest> GetPacks(string? systemId = null);
    PackManifest? GetPack(string packId);
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

/// <summary>A connection between two locations. A plain class so pack YAML can describe it.</summary>
public class LocationEdge
{
    public LocationEdge()
    {
    }

    public LocationEdge(string targetId, string relationType, float distance = 0, string travelType = "Foot")
    {
        TargetId = targetId;
        RelationType = relationType;
        Distance = distance;
        TravelType = travelType;
    }

    public string TargetId { get; set; } = string.Empty;
    public string RelationType { get; set; } = string.Empty;
    public float Distance { get; set; }
    public string TravelType { get; set; } = "Foot";
}

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
