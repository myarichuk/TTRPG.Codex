using Codex.Core.Components;

namespace Codex.Persistence.Runtime;

/// <summary>
/// An immutable picture of everything a table page renders, published by the runtime's command
/// loop after each command (F13). The loop mutates the live <see cref="EncounterDocument"/>, the
/// session's roll log and notes and the ECS components on its own thread; a Blazor circuit that
/// enumerated those directly while rendering could collide with a write and throw inside render,
/// killing that player's circuit. Render from a snapshot instead: nothing in it is ever mutated.
/// <see cref="RosterVersion"/> changes whenever an actor joins or leaves the table or its document changes (a reveal), so a
/// page knows to reload the list of who it may see. <see cref="Campaign"/> is the campaign's clocks and tables, whole: a page
/// shows a player only what <c>CampaignObjects.Visible</c> leaves.
/// </summary>
public sealed record RuntimeSnapshot(
    EncounterSnapshot? Encounter,
    IReadOnlyList<RollLogEntry> RollLog,
    IReadOnlyList<SessionNote> Notes,
    IReadOnlyDictionary<string, ActorStateSnapshot> Actors,
    int RosterVersion = 0,
    IReadOnlyDictionary<string, object>? Campaign = null)
{
    public static RuntimeSnapshot Empty { get; } = new(
        null,
        Array.Empty<RollLogEntry>(),
        Array.Empty<SessionNote>(),
        new Dictionary<string, ActorStateSnapshot>());

    public ActorStateSnapshot? Actor(string actorId) => Actors.GetValueOrDefault(actorId);
}

public sealed record EncounterSnapshot(string Id, int Round, int TurnIndex, IReadOnlyList<ParticipantSnapshot> Participants)
{
    public ParticipantSnapshot? Current => Participants.Count > 0 && TurnIndex >= 0 && TurnIndex < Participants.Count ? Participants[TurnIndex] : null;
}

public sealed record ParticipantSnapshot(string ActorId, int InitiativeRoll, int TieBreak, TurnState State, bool HasActed);

/// <summary>One actor's live pools and conditions; <see cref="Pools"/> is null for an actor with no resource pool.
/// <see cref="UndoKind"/> names the last change the DM can still take back (an event type, "Damage"), or is null (R5).
/// <see cref="IsDead"/> and <see cref="IsDown"/> are the table rules' own verdicts, so a page never works them out again.</summary>
public sealed record ActorStateSnapshot(IReadOnlyDictionary<string, int>? Pools, IReadOnlyList<EffectSnapshot> Effects, string? UndoKind = null,
    bool IsDead = false, bool IsDown = false)
{
    public int? Pool(string key) => Pools != null && Pools.TryGetValue(key, out var value) ? value : null;
}

/// <summary>A condition on an actor; <see cref="RoundsRemaining"/> below zero means it lasts until removed.</summary>
public sealed record EffectSnapshot(string EffectId, string SourceId, int RoundsRemaining, EffectExpiry Expiry, string? HeldBy = null)
{
    public bool IsIndefinite => RoundsRemaining < 0;
}
