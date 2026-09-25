namespace Codex.Persistence;

public enum CampaignRole
{
    DM,
    Player,
    Observer
}

public class CampaignMember
{
    public string UserId { get; set; } = string.Empty;
    public CampaignRole Role { get; set; } = CampaignRole.Player;
}

public class CampaignDocument
{
    public string Id { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The id of the loaded <c>ICodexSystemPlugin</c> that rules this campaign (e.g. "dnd5e").
    /// Was named <c>System</c>; renamed so it reads as what it is - a foreign key into the plugin
    /// catalog, not a live .NET reflection object - and so it round-trips through the
    /// System-&gt;SystemId compare-exchange migration in <see cref="RavenDbService"/> (1.1).
    /// </summary>
    public string SystemId { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Content pack ids active for this campaign, in load-priority order.</summary>
    public List<string> ActivePacks { get; set; } = new();

    /// <summary>Every user with access to this campaign, and what they can do here (1.4).</summary>
    public List<CampaignMember> Members { get; set; } = new();

    /// <summary>Shareable code a player redeems to join as a Member. Empty means invites are closed.</summary>
    public string InviteCode { get; set; } = string.Empty;

    public string? CurrentSessionId { get; set; }

    public Dictionary<string, object> WorldState { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ActorKind
{
    PlayerCharacter,
    NonPlayerCharacter,
    Monster
}

public enum ActorVisibility
{
    /// <summary>Doesn't appear to players at all - not even a name.</summary>
    Hidden,

    /// <summary>Players know something is there, but only <see cref="ActorDocument.PublicName"/>/<see cref="ActorDocument.PublicDescription"/>, never the real sheet.</summary>
    Unknown,

    /// <summary>Fully visible to players with access to the campaign.</summary>
    Known
}

/// <summary>
/// A PC/NPC/Monster instance living in a campaign (1.2). Replaces the old bare
/// <c>CharacterDocument</c>, which had no notion of who could see or own it.
/// </summary>
public class ActorDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public ActorKind Kind { get; set; } = ActorKind.NonPlayerCharacter;

    /// <summary>Owning player's user id. Null for NPCs/Monsters, which the DM owns implicitly.</summary>
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// Reference to the content-pack actor blueprint this instance was created from (e.g.
    /// "dnd5e:goblin"). A dangling reference (pack uninstalled) is expected to be handled by the
    /// UI showing "Missing Content," never by throwing.
    /// </summary>
    public string? BlueprintId { get; set; }

    public string Name { get; set; } = string.Empty;
    public ActorVisibility Visibility { get; set; } = ActorVisibility.Known;

    /// <summary>What an Unknown actor shows players instead of the real name/description.</summary>
    public string? PublicName { get; set; }
    public string? PublicDescription { get; set; }

    /// <summary>Serialized ECS component snapshot; see <see cref="Codex.Plugin.Abstractions.ComponentRegistry"/> (1.5).</summary>
    public Dictionary<string, object> State { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class UserDocument
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public List<ExternalLogin> ExternalLogins { get; set; } = new();
}

public class ExternalLogin
{
    public string Provider { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
}

public enum SessionStatus
{
    Planned,
    Live,
    Completed
}

public class SessionDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public SessionStatus Status { get; set; } = SessionStatus.Planned;
    public string Recap { get; set; } = string.Empty;
    public List<SessionNote> Notes { get; set; } = new();
    public List<SessionEvent> Events { get; set; } = new();
    public List<string> EncounterIds { get; set; } = new();

    /// <summary>The table's shared roll log (3.6) - every roll made through the live runtime,
    /// DM-secret ones included. Visibility filtering (a secret roll is DM-only until revealed)
    /// happens where this is read, never by omitting the entry here.</summary>
    public List<RollLogEntry> RollLog { get; set; } = new();
}

public class SessionNote
{
    public string AuthorId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class SessionEvent
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>One resolved dice roll (3.6), logged regardless of secrecy so the DM always has the
/// full record; a Player-facing projection filters <see cref="IsSecret"/> entries that aren't
/// theirs rather than never recording them.</summary>
public class RollLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The actor this roll was made for/by, if any (e.g. an attack roll) - null for a
    /// free-standing DM roll not tied to a specific actor.</summary>
    public string? ActorId { get; set; }

    public string RollerUserId { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public int Total { get; set; }
    public bool IsSecret { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>Whether a participant is acting normally, holding their action to interrupt
/// (readied), or has pushed their turn later in the order (delayed) (3.5).</summary>
public enum TurnState
{
    Normal,
    Delayed,
    Readied
}

public class EncounterParticipant
{
    public string ActorId { get; set; } = string.Empty;
    public int InitiativeRoll { get; set; }

    /// <summary>Secondary roll/stat used to break an <see cref="InitiativeRoll"/> tie (e.g. a
    /// Dexterity-based tiebreak roll); higher goes first, same as the primary roll.</summary>
    public int TieBreak { get; set; }

    public bool HasActed { get; set; }
    public TurnState State { get; set; } = TurnState.Normal;
}

/// <summary>Live combat/scene state for one encounter within a session (1.3).</summary>
public class EncounterDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? LocationId { get; set; }
    public List<EncounterParticipant> Participants { get; set; } = new();
    public int Round { get; set; } = 1;
    public int TurnIndex { get; set; }

    /// <summary>Active status-effect/duration state not owned by a single actor (area effects, hazards).</summary>
    public Dictionary<string, object> ActiveEffects { get; set; } = new();
}
