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

    /// <summary>House rules: how strictly the system's sheet rules apply here. "inherit" (or null)
    /// uses the system's own setting; "strict", "warn" or "off" override it for this campaign.</summary>
    public string? RulesEnforcement { get; set; }

    /// <summary>Every user with access to this campaign, and what they can do here (1.4).</summary>
    public List<CampaignMember> Members { get; set; } = new();

    /// <summary>Shareable code a player redeems to join as a Member. Empty means invites are closed.</summary>
    public string InviteCode { get; set; } = string.Empty;

    public string? CurrentSessionId { get; set; }

    /// <summary>True once a cover image is stored as the "cover" attachment; <see cref="CoverVersion"/> busts caches.</summary>
    public bool HasCover { get; set; }
    public int CoverVersion { get; set; }

    /// <summary>The DM's shortlist of creatures for this campaign: ones in play and ones planned for later.</summary>
    public List<BestiaryEntry> Bestiary { get; set; } = new();

    /// <summary>Art the DM attached to pack entries in this campaign's Library, keyed
    /// <c>type/id</c>, valued by a version that busts caches. The bytes are attachments on this
    /// document (see <c>RavenMediaStore.PackArtName</c>); homebrew keeps its art on its own document.</summary>
    public Dictionary<string, int> PackArt { get; set; } = new();

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

    /// <summary>
    /// The schema-driven character sheet: attribute scores, resource values and the system's own
    /// actor fields (level, class, species...), keyed by the field keys of the system's actor
    /// schema. This is what players and the DM edit. Resource values are mirrored into
    /// <see cref="State"/> by <see cref="ActorSheetSync"/> so the live table and the sheet agree.
    /// </summary>
    public Dictionary<string, object> Sheet { get; set; } = new();

    /// <summary>Serialized ECS component snapshot; see <see cref="Codex.Plugin.Abstractions.ComponentRegistry"/>. Runtime-owned.</summary>
    public Dictionary<string, object> State { get; set; } = new();

    /// <summary>History, personality, goals: the prose the owner (or the DM) writes and the AI can polish.</summary>
    public string? Biography { get; set; }

    /// <summary>The DM's private notes on this actor. Stripped by the repository before a non-DM ever sees the document.</summary>
    public string? DmNotes { get; set; }

    /// <summary>True once a portrait is stored as the "portrait" attachment; <see cref="PortraitVersion"/> busts caches.</summary>
    public bool HasPortrait { get; set; }
    public int PortraitVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One creature on a campaign's bestiary shortlist: a Library entry (pack or homebrew)
/// the DM intends to use, with a note on where, and how many.</summary>
public class BestiaryEntry
{
    public string EntryType { get; set; } = "monster";
    public string EntryId { get; set; } = string.Empty;
    public bool IsHomebrew { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int Count { get; set; } = 1;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public string Key => $"{(IsHomebrew ? "homebrew" : "pack")}:{EntryType}:{EntryId}";
}

public class UserDocument
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public List<ExternalLogin> ExternalLogins { get; set; } = new();

    /// <summary>The chat-model providers this user configured (their own keys). Keys are stored
    /// protected by ASP.NET Data Protection, never in clear text.</summary>
    public List<AiProviderConfig> AiProviders { get; set; } = new();

    /// <summary>Which of <see cref="AiProviders"/> the AI buttons use. Null falls back to the server-wide configuration.</summary>
    public string? DefaultAiProviderId { get; set; }

    /// <summary>Image models for the art slots, kept apart from the chat models: the best writer
    /// rarely draws, and the two speak different protocols. Same shape, keys protected the same way.</summary>
    public List<AiProviderConfig> ImageProviders { get; set; } = new();

    public string? DefaultImageProviderId { get; set; }
}

/// <summary>One OpenAI-compatible chat endpoint a user has set up: OpenRouter, OpenAI, a local
/// Ollama or LM Studio, or anything else that speaks the chat-completions API.</summary>
public class AiProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;

    /// <summary>A preset key ("openrouter", "openai", "ollama", "custom"...). Only affects defaults and labels; the wire protocol is always OpenAI-compatible.</summary>
    public string Kind { get; set; } = "openai-compatible";

    public string BaseUrl { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    /// <summary>The API key, protected with Data Protection. Empty for local servers that need none.</summary>
    public string ProtectedApiKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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

    /// <summary>The player-facing recap. Everyone in the campaign can read it.</summary>
    public string Recap { get; set; } = string.Empty;

    /// <summary>The DM's own summary, drafted from everything including secret notes and rolls. Never shown to players.</summary>
    public string DmSummary { get; set; } = string.Empty;
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
    /// <summary>Set when the note is added at the table, so it can be corrected or removed later. Empty on notes
    /// stored before ids existed; those can't be edited.</summary>
    public string Id { get; set; } = string.Empty;

    public string AuthorId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public NoteKind Kind { get; set; }

    /// <summary>Who said <see cref="Text"/>, when the note is a line of dialogue; null for a plain note.
    /// Players read the speaker by the name they know them by, and not at all if they can't see them.</summary>
    public string? SpeakerActorId { get; set; }

    /// <summary>Who else was in the conversation a line of dialogue was said in. Each viewer sees only the ones they can see.</summary>
    public List<string> WithActorIds { get; set; } = new();

    /// <summary>The encounter, round and acting combatant when the note was written; null outside an encounter.
    /// Stamped by the runtime, never typed. A viewer who can't see <see cref="TurnActorId"/> gets the round alone.</summary>
    public string? EncounterId { get; set; }
    public int? Round { get; set; }
    public string? TurnActorId { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>What a table note records. A line of dialogue is a <see cref="Note"/> with a speaker.</summary>
public enum NoteKind
{
    Note,
    Clue,
    Loot,
    Ruling,
    FollowUp
}

public class SessionEvent
{
    public string Type { get; set; } = string.Empty;

    /// <summary>The full account, written for the DM: real names, exact numbers. Players read it through
    /// <see cref="SessionEventRedactor"/>, never directly.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Every actor the description names. A viewer who can't see one of them doesn't see the event.</summary>
    public List<string> ActorIds { get; set; } = new();

    /// <summary>What anyone who may see the actors reads instead when <see cref="Description"/> carries numbers
    /// they may not know (a monster's exact damage). Null means the description is safe as it stands.</summary>
    public string? PlayerDescription { get; set; }

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

    /// <summary>What the roll was for ("Athletics check"); null for a free-standing roll.</summary>
    public string? Label { get; set; }

    /// <summary>The dice that counted, in the order rolled. Empty for entries that only have <see cref="Result"/> text (older rolls, death saves, hit dice).</summary>
    public List<int> Kept { get; set; } = new();

    public List<int> Dropped { get; set; } = new();
    public int Modifier { get; set; }
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

    /// <summary>Their start-of-turn effects have already ticked this round, so handing the turn back to
    /// them (after someone acted ahead of them, or after a step back) doesn't tick them twice.</summary>
    public bool TurnStarted { get; set; }

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

    /// <summary>Set when the DM ends the encounter; an encounter with no end is restored when its session's table starts again.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Active status-effect/duration state not owned by a single actor (area effects, hazards).</summary>
    public Dictionary<string, object> ActiveEffects { get; set; } = new();
}
