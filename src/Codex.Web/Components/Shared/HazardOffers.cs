using Codex.Core;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>
/// The complex hazards the quick-add panel offers (HAZARDS_DESIGN.md 3.3): those the encounter's place holds first, then the Library's. A hazard is read only through
/// <see cref="HazardNumbers.For"/>, and its actor is made by <see cref="YamlContentPackLoader.ToActorBlueprint(ContentEntry, ContentTypeDefinition, HazardDefinition, ResourceDefinition?)"/>,
/// the same blueprint the registry holds for it.
/// </summary>
public static class HazardOffers
{
    /// <summary>A Library hazard that is a registry actor, with no place.</summary>
    public static HazardOffer FromLibrary(IActorDefinition definition, ContentTypeDefinition type)
    {
        // The actor's properties are the entry's, so the bonus is read through the same overlay as every hazard number (the base numbers: no scaled row).
        var entry = new ContentEntry { Id = definition.Id, Type = type.Type, Properties = new Dictionary<string, object>(definition.Properties) };
        return new HazardOffer(definition, new HazardActorLink { Entry = PlacedHazard.Reference(type.Type, definition.Id) },
            type.Hazard == null ? 0 : HazardNumbers.InitiativeOf(HazardNumbers.For(entry, type, null), type.Hazard));
    }

    /// <summary>The complex hazards standing on <paramref name="placeId"/> or a place inside it, each as the actor it would become with the link to its placement. Hazards
    /// already disabled, and those that already have an actor at the table, are left out. <paramref name="read"/> reads a placement's Library entry (null when it is gone).</summary>
    public static async Task<IReadOnlyList<HazardOffer>> ForPlaceAsync(IEnumerable<RegionDocument> regions, string placeId, SystemDefinition system, ContentTypeDefinition type,
        IEnumerable<ActorDocument> actors, Func<PlacedHazard, Task<ContentEntry?>> read)
    {
        var offers = new List<HazardOffer>();
        if (string.IsNullOrEmpty(placeId) || type.Hazard is not { } hazard) return offers;
        var taken = actors.Where(a => !a.Retired && a.Hazard is { IsPlaced: true }).Select(a => (a.Hazard!.RegionId, a.Hazard.LocationId)).ToHashSet();
        foreach (var region in regions)
        {
            foreach (var place in region.Locations.Where(l => l.Feature?.Hazard != null))
            {
                var stored = place.Feature!.Hazard!;
                if (stored.State == HazardState.Disabled || taken.Contains((region.Id, place.Id)) || !Chain(region, place).Any(c => c.Id == placeId)) continue;
                var entry = await read(stored);
                if (entry == null || !HazardNumbers.IsComplex(entry, type)) continue;
                var numbers = HazardNumbers.For(entry, type, stored.Levels);
                var withLevel = new ContentEntry { Id = entry.Id, SystemId = entry.SystemId, PackId = entry.PackId, Type = entry.Type, Name = entry.Name, Description = entry.Description, Tags = entry.Tags, Properties = new Dictionary<string, object>(numbers) };
                var link = new HazardActorLink { RegionId = region.Id, LocationId = place.Id, Entry = stored.Entry, IsHomebrew = stored.IsHomebrew, Levels = stored.Levels };
                offers.Add(new HazardOffer(YamlContentPackLoader.ToActorBlueprint(withLevel, type, hazard, system.PrimaryResource), link, HazardNumbers.InitiativeOf(numbers, hazard), place.Name));
            }
        }

        return offers;
    }

    private static IEnumerable<LocationDocument> Chain(RegionDocument region, LocationDocument place)
    {
        var seen = new HashSet<string>();
        for (var at = place; at != null && seen.Add(at.Id); at = string.IsNullOrEmpty(at.ParentId) ? null : region.Locations.FirstOrDefault(l => l.Id == at.ParentId))
        {
            yield return at;
        }
    }
}
