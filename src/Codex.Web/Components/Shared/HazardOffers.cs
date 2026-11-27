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

    /// <summary>The actor a complex hazard becomes when it takes its turn (HAZARDS_DESIGN.md 3.3): <b>Hidden</b>, so no player ever sees it or its row in the order, with the link to
    /// its placement, seeded from the blueprint. The one place that makes it: the console's quick-add and a prepared encounter's start both call it, so they cannot differ. The
    /// caller saves it (<c>CampaignRuntimeManager.TryCreateActorAsync</c>) and joins it with <c>JoinInitiativeCommand</c>.</summary>
    public static ActorDocument ToActor(string campaignId, HazardOffer offer, SystemDefinition? system, Codex.Plugin.Abstractions.ComponentRegistry components)
    {
        var definition = offer.Definition;
        var actor = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            Kind = ActorKind.Monster,
            BlueprintId = $"{definition.SystemId}:{definition.Id}",
            Name = definition.Name,
            Visibility = ActorVisibility.Hidden,
            Hazard = offer.Link
        };
        ActorSheetSync.SeedFromBlueprint(actor, definition, system, components);

        // A hazard's `conditions` are what its effect puts on the creatures it catches (the Rolling Stone's Prone), not conditions on the hazard. The
        // seed copies every entry field onto the sheet, where that key is the actor's own condition list, so the row showed a condition made of the
        // effect's raw data. Hazard rules read the entry, never this sheet, so the key is dropped and the state worked out again.
        if (actor.Sheet.Remove(Codex.Core.Rules.SheetModel.ConditionsKey)) ActorSheetSync.ApplySheetToState(actor, system, components);
        return actor;
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
                if (stored.State == HazardState.Disabled || taken.Contains((region.Id, place.Id)) || !PlaceTree.Chain(region, place).Any(c => c.Id == placeId)) continue;
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
}
