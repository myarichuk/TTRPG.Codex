using System;
using System.Collections.Generic;

namespace Codex.Persistence;


/// <summary>How big a place is, from the widest down: a region (<see cref="RegionDocument"/>) holds areas, an area holds
/// settlements, a settlement districts, and any of them sites (a tavern, a ship, a ruin). A site has sections (a deck, a dungeon
/// level), sections have rooms, and a room holds features (a chest, a safe behind a false wall). Only a guide: a site may sit
/// straight in a region, a room in a room, and any place may be nested as deep as the table needs.</summary>
public enum PlaceKind
{
    Area,
    Settlement,
    District,
    Site,
    Section,
    Room,
    Feature
}

/// <summary>What a region is, which says what goes straight inside it (see <see cref="PlaceTree.ChildKind"/>). Only a guide, like
/// <see cref="PlaceKind"/>.</summary>
public enum RegionKind
{
    General,
    Realm,
    Wilderness,
    City,
    Dungeon,
    Vessel,
    Plane
}

/// <summary>
/// The DM's notes on a thing in a room that the party can open, find or be bitten by: a chest, a safe behind a false wall, a
/// mimic. Each note is free text in the campaign's own terms ("DC 15, Maud has the key"), since systems differ; empty means
/// none. Players never get these (the repository strips them): what a player knows of a feature is its description.
/// </summary>
public class PlaceFeature
{
    /// <summary>It holds things: a chest, a safe, a hollow statue.</summary>
    public bool Container { get; set; }

    /// <summary>How it is locked and how it opens.</summary>
    public string? Lock { get; set; }

    /// <summary>What happens to whoever opens it carelessly, and how to spot or disarm it.</summary>
    public string? Trap { get; set; }

    /// <summary>How it is hidden (a false wall, an illusion) and how it is found.</summary>
    public string? Concealment { get; set; }

    /// <summary>What is inside. Text, not items: who takes what is settled at the table and given from the DM screen.</summary>
    public string? Contents { get; set; }

    /// <summary>What it really is, or what lives in it, when that is a creature: "a mimic".</summary>
    public string? Creature { get; set; }

    /// <summary>That creature on the cast, if the DM put it there.</summary>
    public string? CreatureActorId { get; set; }

    /// <summary>A hazard from the Library placed here, with what has happened to it at this place. The state belongs to the placement, so
    /// one Library hazard can be placed many times. <see cref="Trap"/> stays beside it, as free text for anything else.</summary>
    public PlacedHazard? Hazard { get; set; }

    /// <summary>The notes trimmed and capped, or null when nothing is left, so a place with no feature stores none.</summary>
    public static PlaceFeature? Tidy(PlaceFeature? feature)
    {
        if (feature == null) return null;
        var tidy = new PlaceFeature
        {
            Container = feature.Container,
            Lock = Note(feature.Lock, MaxNote),
            Trap = Note(feature.Trap, MaxNote),
            Concealment = Note(feature.Concealment, MaxNote),
            Contents = Note(feature.Contents, MaxContents),
            Creature = Note(feature.Creature, MaxNote),
            CreatureActorId = string.IsNullOrWhiteSpace(feature.CreatureActorId) ? null : feature.CreatureActorId.Trim(),
            Hazard = PlacedHazard.Tidy(feature.Hazard)
        };
        return tidy.IsEmpty ? null : tidy;

        static string? Note(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var t && t.Length > max ? t[..max] : text.Trim();
    }

    public const int MaxNote = 300;
    public const int MaxContents = 4000;

    public bool IsEmpty => !Container && string.IsNullOrWhiteSpace(Lock) && string.IsNullOrWhiteSpace(Trap) && string.IsNullOrWhiteSpace(Concealment)
                           && string.IsNullOrWhiteSpace(Contents) && string.IsNullOrWhiteSpace(Creature) && string.IsNullOrEmpty(CreatureActorId)
                           && Hazard == null;
}

/// <summary>Where a placed hazard stands. Plain words, in the order a hazard usually goes through them.</summary>
public enum HazardState
{
    /// <summary>Nobody has found it.</summary>
    Hidden,

    /// <summary>Someone has seen it (see <see cref="PlacedHazard.NoticedBy"/>).</summary>
    Noticed,

    /// <summary>It was made safe. It stays so until the game master sets it back.</summary>
    Disabled,

    /// <summary>It went off.</summary>
    Triggered
}

/// <summary>
/// A hazard of the system's Library on a place's feature (HAZARDS_DESIGN.md 4.1): which entry, and its state here. DM only, like the rest of the
/// feature. Not a copy of the entry: the numbers are read from the Library when the table needs them.
/// </summary>
public class PlacedHazard
{
    public const int MaxNoticedBy = 50;
    public const int MaxFound = 30;

    /// <summary>The Library entry as <c>type/id</c> (<c>hazard/spiked-pit</c>). Homebrew entries are named the same way.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>True when <see cref="Entry"/> is the campaign's own homebrew entry, not a pack's. A homebrew entry can keep the id of a pack
    /// entry it was copied from, so the reference alone does not say which is meant (as <c>BestiaryEntry.IsHomebrew</c>). Absent in old data: a pack entry.</summary>
    public bool IsHomebrew { get; set; }

    /// <summary>The <c>levels</c> of the entry's own scaled row this placement uses (<c>5-10</c>), chosen when it was placed; null is the entry's base
    /// numbers. The table reads the numbers through <c>HazardNumbers.For</c>, never the party's level at the moment.</summary>
    public string? Levels { get; set; }

    public HazardState State { get; set; } = HazardState.Hidden;

    /// <summary>The successes toward disabling it so far, for a hazard that needs more than one.</summary>
    public int Successes { get; set; }

    /// <summary>The cast members who have noticed it (<see cref="ActorDocument.Id"/>), before the game master reveals it to everyone.</summary>
    public List<string> NoticedBy { get; set; } = new();

    /// <summary>The ids of the detect routes (<c>HazardRoute.Id</c>) that have succeeded here, so a route that comes <c>after</c> another is offered once
    /// its route is found. A route with no id is not recorded.</summary>
    public List<string> Found { get; set; } = new();

    /// <summary>The Library type of <see cref="Entry"/>, or empty when it is not <c>type/id</c>.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public string EntryType => Split() is { } parts ? parts.Type : string.Empty;

    /// <summary>The Library id of <see cref="Entry"/>, or empty when it is not <c>type/id</c>.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public string EntryId => Split() is { } parts ? parts.Id : string.Empty;

    /// <summary>The reference for an entry of the given type and id.</summary>
    public static string Reference(string type, string id) => $"{type}/{id}";

    private (string Type, string Id)? Split()
    {
        var slash = Entry.IndexOf('/');
        return slash > 0 && slash < Entry.Length - 1 ? (Entry[..slash], Entry[(slash + 1)..]) : null;
    }

    /// <summary>The placement with a well-formed reference, a state that exists and a non-negative count, or null when it names no entry.</summary>
    public static PlacedHazard? Tidy(PlacedHazard? hazard)
    {
        if (hazard == null || string.IsNullOrWhiteSpace(hazard.Entry)) return null;
        var tidy = new PlacedHazard
        {
            Entry = hazard.Entry.Trim(),
            IsHomebrew = hazard.IsHomebrew,
            Levels = string.IsNullOrWhiteSpace(hazard.Levels) ? null : hazard.Levels.Trim(),
            State = Enum.IsDefined(hazard.State) ? hazard.State : HazardState.Hidden,
            Successes = Math.Max(0, hazard.Successes),
            NoticedBy = hazard.NoticedBy.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().Take(MaxNoticedBy).ToList(),
            Found = hazard.Found.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().Take(MaxFound).ToList()
        };
        return tidy.Split() == null ? null : tidy;
    }
}

/// <summary>
/// What ties a complex hazard's actor to the hazard it stands for (HAZARDS_DESIGN.md 3.3): the placement (<see cref="RegionId"/> and <see cref="LocationId"/>), or,
/// for a hazard taken straight from the Library, no place (both empty), in which case its live state belongs to the actor alone and is never written to a place.
/// </summary>
public class HazardActorLink
{
    public string RegionId { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;

    /// <summary>The Library entry as <c>type/id</c>.</summary>
    public string Entry { get; set; } = string.Empty;

    public bool IsHomebrew { get; set; }

    /// <summary>The entry's scaled row the placement uses (<see cref="PlacedHazard.Levels"/>); null: the base numbers.</summary>
    public string? Levels { get; set; }

    /// <summary>True when the hazard stands on a place; false for one taken from the Library alone.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool IsPlaced => RegionId.Length > 0 && LocationId.Length > 0;
}

public class LocationDocument
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public PlaceKind Kind { get; set; } = PlaceKind.Site;

    /// <summary>The place in the same region this one is inside, or empty for a place straight in the region. See <see cref="PlaceTree"/>.</summary>
    public string? ParentId { get; set; }

    /// <summary>For a feature: the DM's notes on its lock, trap, hiding place and contents. DM only.</summary>
    public PlaceFeature? Feature { get; set; }

    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>Fog-of-war state (2.5): Hidden by default, revealed to the party via a DM action.</summary>
    public ActorVisibility Visibility { get; set; } = ActorVisibility.Hidden;

    /// <summary>The wiki page about this place (<see cref="WikiPageDocument.Id"/>), if it has one.</summary>
    public string? WikiPageId { get; set; }

    /// <summary>True once art is stored on the region document (see <c>RavenMediaStore.LocationArtName</c>).</summary>
    public bool HasArt { get; set; }
    public int ArtVersion { get; set; }
}

/// <summary>
/// The spatial graph aggregate (CAMPAIGN_ARCHITECTURE.md 2): every location and connection for a
/// region lives in this one document, so loading the map - or routing across it - is a single
/// <c>Get</c> plus in-memory graph traversal, never a fan-out of per-location loads.
/// </summary>
public class RegionDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RegionKind Kind { get; set; } = RegionKind.General;
    public List<LocationDocument> Locations { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Places deleted with their art still on this document, and when: the art stays while Undo can bring the
    /// place back, and the next change to the region after that drops it.</summary>
    public Dictionary<string, DateTime> RemovedArt { get; set; } = new();
}
