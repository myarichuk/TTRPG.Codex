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
            CreatureActorId = string.IsNullOrWhiteSpace(feature.CreatureActorId) ? null : feature.CreatureActorId.Trim()
        };
        return tidy.IsEmpty ? null : tidy;

        static string? Note(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var t && t.Length > max ? t[..max] : text.Trim();
    }

    public const int MaxNote = 300;
    public const int MaxContents = 4000;

    public bool IsEmpty => !Container && string.IsNullOrWhiteSpace(Lock) && string.IsNullOrWhiteSpace(Trap) && string.IsNullOrWhiteSpace(Concealment)
                           && string.IsNullOrWhiteSpace(Contents) && string.IsNullOrWhiteSpace(Creature) && string.IsNullOrEmpty(CreatureActorId);
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
