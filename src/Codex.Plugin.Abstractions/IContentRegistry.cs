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

    /// <summary>Remembers flavor (<see cref="ContentEntry.FlavorKeys"/>) for the entry of this type and id, whether
    /// or not a loaded pack defines it, so entries made later (a campaign's homebrew) can pick it up.</summary>
    void RegisterFlavor(string systemId, string type, string id, string packId, IReadOnlyDictionary<string, object> properties);

    /// <summary>The flavor registered for this entry (empty when none), with <see cref="ContentEntry.FlavorSourceKey"/> set.</summary>
    IReadOnlyDictionary<string, object> GetFlavor(string systemId, string type, string id);

    /// <summary>Remembers the translated display fields of one entry for one language. Translations never replace
    /// the entry: rules and logic keep running on the original, and <see cref="Localize"/> overlays the text at display time.</summary>
    void RegisterTranslation(string systemId, string type, string id, string locale, string packId, IReadOnlyDictionary<string, object> fields);

    /// <summary>The entry as it should be shown in <paramref name="culture"/> ("ru", "he-IL"): a copy with translated
    /// <see cref="ContentEntry.Name"/>, <see cref="ContentEntry.Description"/> and text properties where a translation pack
    /// provides them, falling back from the exact culture to its language and then to the entry itself. Never mutates the shared entry.</summary>
    ContentEntry Localize(ContentEntry entry, string? culture);

    /// <summary>Every entry of <paramref name="type"/> for a system, optionally restricted to the given pack ids.</summary>
    IEnumerable<ContentEntry> GetEntries(string systemId, string type, IReadOnlySet<string>? packIds = null);

    /// <summary>Distinct entry types registered for a system, in first-seen order.</summary>
    IEnumerable<string> GetEntryTypes(string systemId);

    /// <summary>
    /// Case-insensitive search over name, tags and description. Name matches rank first (prefix
    /// before substring), then tag matches, then description matches. An empty query returns
    /// everything of the given type (or everything, when type is null).
    /// </summary>
    /// <remarks>With a <paramref name="culture"/> other than English the results are localized copies (<see cref="Localize"/>), and an
    /// entry matches on either its translated or its original name, so English names still work in a translated UI.</remarks>
    IEnumerable<ContentEntry> Search(string systemId, string query, string? type = null, IReadOnlySet<string>? packIds = null, int limit = 200, string? culture = null);

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
