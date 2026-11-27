namespace Codex.Persistence;

/// <summary>An encounter the GM prepared ahead of the session (ENCOUNTER_BUILDER_DESIGN.md 2). GM only, everywhere: its name can spoil a secret.
/// Starting it at the table makes the live <see cref="EncounterDocument"/>; it is a plan, never a fight, so it is stored apart from encounters.</summary>
public class PreparedEncounterDocument
{
    /// <summary>The start of every id this repository makes.</summary>
    public const string IdPrefix = "preparedencounters/";

    public string Id { get; set; } = string.Empty;              // preparedencounters/<guid>
    public string CampaignId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;            // "Ambush in the cellar"
    public string? RegionId { get; set; }                       // the place, as the hazard keys name it (hazard:{regionId}/{locationId})
    public string? PlaceId { get; set; }                        // a LocationDocument id; null: no place
    public List<PreparedLine> Lines { get; set; } = new();
    public string? Notes { get; set; }                          // markdown, GM only
    public List<string> Tags { get; set; } = new();

    /// <summary>The complex hazards placed at the place (or under it) that do not join the initiative order when this plan starts, each as <see cref="HazardKey"/>.
    /// Empty: every one joins, and one placed later joins too. A plan never holds a hazard line; the hazard itself is placed on the place (ENCOUNTER_BUILDER_DESIGN.md 18).</summary>
    public List<string> HazardsLeftOut { get; set; } = new();

    /// <summary>The key of one placement in <see cref="HazardsLeftOut"/>: <c>{regionId}/{locationId}</c>, as <see cref="HazardActorLink"/> names a placement. A copy of the
    /// campaign renames the region id wherever it stands whole, so the key follows it.</summary>
    public static string HazardKey(string regionId, string locationId) => $"{regionId}/{locationId}";

    public PreparedStatus Status { get; set; }                  // Draft, Ready, Ran
    public List<string> RunEncounterIds { get; set; } = new();  // the live encounters started from it, newest last; only MarkRunAsync adds to it
    public DateTime? LastRunAt { get; set; }                     // when it was last started at the table (UTC); only MarkRunAsync sets it
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public string? FromChatId { get; set; }                     // the world builder chat that proposed it, if any
}

/// <summary>One line of the lineup: a Library or homebrew creature with a count (the <see cref="BestiaryEntry.Key"/> shape), one cast member, or a
/// complex hazard's turn in the order.</summary>
public class PreparedLine
{
    public PreparedLineKind Kind { get; set; }
    public string EntryType { get; set; } = "monster";          // Creature, Hazard: the Library type
    public string EntryId { get; set; } = string.Empty;
    public bool IsHomebrew { get; set; }
    public string? ActorId { get; set; }                        // CastMember: an existing actor
    public string Name { get; set; } = string.Empty;            // as it was when added, for a line whose entry is gone
    public int Count { get; set; } = 1;                         // Creature: 1 to 20; CastMember and Hazard: 1. The service checks this; the repository stores it.
    public string? Note { get; set; }
    public bool StartsHidden { get; set; }                      // true: Hidden at the table; false: Unknown
}

public enum PreparedLineKind
{
    Creature,
    CastMember,
    Hazard
}

public enum PreparedStatus
{
    Draft,
    Ready,
    Ran
}
