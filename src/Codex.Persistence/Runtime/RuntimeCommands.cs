using Codex.Core;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Persistence.Runtime;

/// <summary>Immediate, clamped damage (3.4/B7) with the table's vitals rules applied: the damage
/// type is checked against the creature's immunities, resistances and vulnerabilities, temporary
/// hit points absorb first, and reaching zero knocks a character down (death saves begin) or kills
/// a creature outright (<see cref="TableRules.ApplyDamage"/>).</summary>
public sealed record ApplyDamageCommand(string ActorId, string PoolName, int Amount, string SourceId, string? DamageType = null, bool IsCritical = false) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private TableRules.DamageOutcome _outcome = TableRules.DamageOutcome.Nothing;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _outcome = TableRules.ApplyDamage(runtime, ActorId, e, PoolName, Amount, DamageType, IsCritical);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.GetActorName(ActorId);
        var type = string.IsNullOrWhiteSpace(DamageType) ? null : EventTerm.DamageType(DamageType.ToLowerInvariant());
        var source = LogTerms.Source(runtime, SourceId);
        var parts = new List<EventMessage>();
        if (_outcome.Modifier == "immune")
        {
            parts.Add(source == null ? EventMessage.Of("Damage.Immune", name, type, Amount) : EventMessage.Of("Damage.ImmuneFrom", name, type, Amount, source));
        }
        else
        {
            parts.Add((type, source) switch
            {
                (null, null) => EventMessage.Of("Damage.Took", name, _outcome.Dealt),
                (null, _) => EventMessage.Of("Damage.TookFrom", name, _outcome.Dealt, source),
                (_, null) => EventMessage.Of("Damage.TookTyped", name, _outcome.Dealt, type),
                _ => EventMessage.Of("Damage.TookTypedFrom", name, _outcome.Dealt, type, source)
            });
            if (_outcome.Modifier != null)
            {
                parts.Add(EventMessage.Of(_outcome.Modifier == "resisted" ? "Damage.Resisted" : _outcome.Modifier == "vulnerable" ? "Damage.Vulnerable" : "Damage.ResistedVulnerable", Amount));
            }

            if (_outcome.Absorbed > 0) parts.Add(EventMessage.Of("Damage.Absorbed", _outcome.Absorbed));
        }

        if (_outcome.Died) parts.Add(EventMessage.Of("Damage.Died", name));
        else if (_outcome.WentDown) parts.Add(EventMessage.Of("Damage.Down", name));
        else if (_outcome.FailuresAdded > 0) parts.Add(EventMessage.Of("Damage.HitWhileDown", name, _outcome.FailuresAdded));

        var player = EventMessage.Of(_outcome.Died ? "Damage.PlayerDied" : _outcome.WentDown ? "Damage.PlayerDown" : "Damage.Player", name);
        return SessionEvent.From("Damage", parts, new[] { ActorId }, new[] { player });
    }
}

public sealed record ApplyHealingCommand(string ActorId, string PoolName, int Amount, string SourceId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private TableRules.HealOutcome _outcome = new(0, false, false);

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _outcome = TableRules.ApplyHealing(runtime, ActorId, e, PoolName, Amount);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.GetActorName(ActorId);
        var source = LogTerms.Source(runtime, SourceId);
        var parts = new List<EventMessage>();
        if (_outcome.WasDead)
        {
            parts.Add(source == null ? EventMessage.Of("Healing.Dead", name, Amount) : EventMessage.Of("Healing.DeadFrom", name, Amount, source));
        }
        else
        {
            var pool = LogTerms.Resource(runtime, PoolName);
            parts.Add(source == null ? EventMessage.Of("Healing.Recovered", name, _outcome.Healed, pool) : EventMessage.Of("Healing.RecoveredFrom", name, _outcome.Healed, pool, source));
            if (_outcome.Revived) parts.Add(EventMessage.Of("Healing.Revived", name));
        }

        return SessionEvent.From("Healing", parts, new[] { ActorId }, new[] { EventMessage.Of(_outcome.Revived ? "Healing.PlayerRevived" : "Healing.Player", name) });
    }
}

/// <summary>Temporary hit points: the creature keeps the higher of what it has and what it is offered.</summary>
public sealed record GrantTemporaryHitPointsCommand(string ActorId, int Amount, string SourceId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private bool _applied;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _applied = TableRules.GrantTemporary(runtime, e, Amount);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.GetActorName(ActorId);
        var source = LogTerms.Source(runtime, SourceId);
        var key = (_applied ? "TempHp.Gained" : "TempHp.Kept") + (source == null ? "" : "From");
        return SessionEvent.From("TemporaryHitPoints",
            new[] { source == null ? EventMessage.Of(key, name, Amount) : EventMessage.Of(key, name, Amount, source) },
            new[] { ActorId },
            new[] { EventMessage.Of(_applied ? "TempHp.PlayerGained" : "TempHp.PlayerKept", name) });
    }
}

/// <summary>Rolls a death save with the table's dice for a character at zero hit points.</summary>
public sealed record RollDeathSaveCommand(string ActorId, string RollerUserId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private TableRules.DeathSaveOutcome _outcome = TableRules.DeathSaveOutcome.None;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _outcome = TableRules.RollDeathSave(runtime, ActorId, e);
            if (_outcome.Roll is { } roll)
            {
                runtime.RecordRoll(new RollLogEntry
                {
                    ActorId = ActorId,
                    RollerUserId = RollerUserId,
                    Expression = runtime.System?.Vitals?.DeathSaves?.Die ?? "1d20",
                    Result = $"Death save: {roll}",
                    Total = roll
                });
            }
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => DeathSaveEvents.Describe(runtime, ActorId, _outcome);
}

/// <summary>Records a death save rolled away from the table (a player's own dice), or corrects the tally.</summary>
public sealed record RecordDeathSaveCommand(string ActorId, bool Success, int Count = 1) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private TableRules.DeathSaveOutcome _outcome = TableRules.DeathSaveOutcome.None;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _outcome = TableRules.RecordDeathSave(runtime, ActorId, e, Success, Count);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => DeathSaveEvents.Describe(runtime, ActorId, _outcome);
}

internal static class DeathSaveEvents
{
    public static SessionEvent Describe(CampaignRuntime runtime, string actorId, TableRules.DeathSaveOutcome outcome)
    {
        var name = runtime.GetActorName(actorId);
        var parts = new List<EventMessage>();
        if (outcome.NotApplicable)
        {
            parts.Add(EventMessage.Of("DeathSave.NotApplicable", name));
        }
        else if (outcome.Revived)
        {
            parts.Add(EventMessage.Of("DeathSave.Revived", name, outcome.Roll));
        }
        else
        {
            parts.Add(EventMessage.Of(outcome.Died ? "DeathSave.Died" : outcome.Stabilized ? "DeathSave.Stable" : outcome.Success == true ? "DeathSave.Succeeded" : "DeathSave.Failed", name));
            if (outcome.Roll is { } r) parts.Add(EventMessage.Of("DeathSave.Rolled", r));
            if (!outcome.Died && !outcome.Stabilized) parts.Add(EventMessage.Of("DeathSave.Tally", outcome.Successes, outcome.Failures));
        }

        return SessionEvent.From("DeathSave", parts, new[] { actorId });
    }
}

/// <summary>Stabilises a creature at zero (a Medicine check, Spare the Dying): no more death saves, still down.</summary>
public sealed record StabilizeCommand(string ActorId, string SourceId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private bool _applied;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _applied = TableRules.Stabilize(runtime, ActorId, e);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Stabilized",
        new[]
        {
            !_applied ? EventMessage.Of("Stabilized.None", runtime.GetActorName(ActorId))
                : LogTerms.Source(runtime, SourceId) is { } source ? EventMessage.Of("Stabilized.DoneBy", runtime.GetActorName(ActorId), source)
                : EventMessage.Of("Stabilized.Done", runtime.GetActorName(ActorId))
        },
        new[] { ActorId });
}

/// <summary>Raises or lowers the exhaustion level; the top level kills.</summary>
public sealed record AdjustExhaustionCommand(string ActorId, int Delta) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private (int Level, bool Died)? _outcome;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _outcome = TableRules.AdjustExhaustion(runtime, ActorId, e, Delta);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.GetActorName(ActorId);
        var message = _outcome is { } o
            ? o.Died ? EventMessage.Of("Exhaustion.Died", name) : o.Level == 0 ? EventMessage.Of("Exhaustion.None", name) : EventMessage.Of("Exhaustion.Level", name, o.Level)
            : EventMessage.Of("Exhaustion.Failed", name);
        return SessionEvent.From("Exhaustion", new[] { message }, new[] { ActorId });
    }
}

/// <summary>Spends hit dice during a rest: each die plus the attribute modifier heals.</summary>
public sealed record SpendHitDiceCommand(string ActorId, int Count, int DieSides) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private TableRules.HitDiceOutcome? _outcome;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _outcome = TableRules.SpendHitDice(runtime, ActorId, e, Count, DieSides);
            if (_outcome is { Spent: > 0 } o)
            {
                runtime.RecordRoll(new RollLogEntry
                {
                    ActorId = ActorId,
                    RollerUserId = "table",
                    Expression = $"{o.Spent}d{DieSides}{(o.Modifier >= 0 ? "+" : "")}{o.Modifier} each",
                    Result = $"Hit dice: {string.Join(", ", o.Rolls)}",
                    Total = o.Healed
                });
            }
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.GetActorName(ActorId);
        if (_outcome is { Spent: > 0 } o)
        {
            return SessionEvent.From("HitDice",
                new[] { EventMessage.Of("HitDice.Spent", name, o.Spent, DieSides, string.Join(", ", o.Rolls), o.Healed) },
                new[] { ActorId },
                new[] { EventMessage.Of("HitDice.Player", name) });
        }

        return SessionEvent.From("HitDice", new[] { EventMessage.Of("HitDice.None", name) }, new[] { ActorId });
    }
}

/// <summary>Adds a status effect without clobbering whatever else <paramref name="ActorId"/> is
/// already under (B6). <paramref name="AnchorActorId"/> is whose turn ticks the duration down -
/// usually the affected actor itself, but a DM-applied hazard might anchor to its source instead.
/// <paramref name="Rounds"/> below one means the effect lasts until it is removed.</summary>
public sealed record AddStatusEffectCommand(string ActorId, string EffectId, string SourceId, int Rounds, EffectExpiry Expiry, string AnchorActorId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            runtime.World.AddStatus(e, EffectId, SourceId, Rounds, Expiry, AnchorActorId);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.GetActorName(ActorId);
        var effect = EventTerm.Condition(EffectId);
        var parts = new List<EventMessage>
        {
            LogTerms.Source(runtime, SourceId) is { } source ? EventMessage.Of("Status.GainedFrom", name, effect, source) : EventMessage.Of("Status.Gained", name, effect)
        };
        var start = Expiry == EffectExpiry.StartOfTurn;
        // The turn is always named, even the creature's own: "their turn" has a gender in Hebrew.
        parts.Add(Rounds < 1
            ? EventMessage.Of("Status.Indefinite")
            : EventMessage.Of(start ? "Status.UntilStart" : "Status.UntilEnd", Rounds, runtime.GetActorName(AnchorActorId)));
        return SessionEvent.From("StatusEffect", parts, new[] { ActorId });
    }
}

/// <summary>Advances the end-of-turn boundary for one actor: every effect anchored to them, across
/// every entity in the encounter, ticks down and expires independently (3.4).</summary>
public sealed record AdvanceTurnCommand(string ActorId) : IRuntimeCommand
{
    // Effects anchored to ActorId can live on any entity in the encounter, not just ActorId's own -
    // Apply marks every affected actor dirty directly rather than trying to predict them here.
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) != null)
        {
            runtime.World.AdvanceTurn(ActorId, EffectExpiry.EndOfTurn);
            runtime.MarkAllHydratedActorsDirty();
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("TurnAdvance", new[] { EventMessage.Of("Turn.Ended", runtime.GetActorName(ActorId)) }, new[] { ActorId });
}

/// <summary>Removes every instance of a status effect from an actor (3.7 "remove conditions") -
/// the counterpart <see cref="AddStatusEffectCommand"/> never got, since B6's fix only needed
/// "add without clobbering."</summary>
public sealed record RemoveStatusEffectCommand(string ActorId, string EffectId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            runtime.World.RemoveStatus(e, EffectId);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("StatusEffectRemoved", new[] { EventMessage.Of("Status.Lost", runtime.GetActorName(ActorId), EventTerm.Condition(EffectId)) }, new[] { ActorId });
}

/// <summary>
/// Adds a participant to the encounter's initiative order if new, or updates their roll if
/// already present (3.5) - the encounter is created lazily on the first roll, since a campaign
/// spends most of its time with no combat happening at all. Ties are broken by
/// <paramref name="TieBreak"/> (a secondary roll or stat), then by actor id for a fully
/// deterministic order - two participants must never silently swap places between renders.
/// Whoever is acting when the order changes keeps the turn.
/// </summary>
public sealed record SetInitiativeCommand(string ActorId, int InitiativeRoll, int TieBreak = 0) : IRuntimeCommand
{
    // Doesn't touch ActorDocument.State - initiative order lives on the EncounterDocument, which
    // the runtime persists separately (see CampaignRuntime.PersistEncounterAsync).
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
        var encounter = runtime.GetOrCreateEncounter();
        // Only a fight under way has a current turn to keep; while the order is still being rolled
        // the turn belongs to whoever ends up on top.
        var underWay = encounter.Round > 1 || encounter.Participants.Any(p => p.HasActed);
        var current = underWay && encounter.TurnIndex < encounter.Participants.Count ? encounter.Participants[encounter.TurnIndex] : null;
        var participant = encounter.Participants.FirstOrDefault(p => p.ActorId == ActorId);
        if (participant == null)
        {
            participant = new EncounterParticipant { ActorId = ActorId };
            encounter.Participants.Add(participant);
        }

        participant.InitiativeRoll = InitiativeRoll;
        participant.TieBreak = TieBreak;
        CampaignRuntime.SortParticipants(encounter);
        encounter.TurnIndex = current != null ? Math.Max(0, encounter.Participants.IndexOf(current)) : 0;
        runtime.MarkEncounterDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("Initiative", new[] { EventMessage.Of("Initiative.Rolled", runtime.GetActorName(ActorId), InitiativeRoll) }, new[] { ActorId });
}

/// <summary>Moves the encounter to the next participant's turn (3.5): end-of-turn effects
/// anchored to the participant whose turn just ended tick, then start-of-turn effects anchored
/// to the one whose turn begins. Dead participants are skipped. Wraps to a new round (and resets
/// who's acted) once every participant has gone.</summary>
public sealed record NextTurnCommand : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        if (encounter == null || encounter.Participants.Count == 0)
        {
            return;
        }

        var current = encounter.Participants[encounter.TurnIndex];
        if (runtime.GetEntity(current.ActorId) != null)
        {
            runtime.World.AdvanceTurn(current.ActorId, EffectExpiry.EndOfTurn);
            runtime.MarkAllHydratedActorsDirty();
        }

        current.HasActed = true;
        for (var step = 0; step < encounter.Participants.Count; step++)
        {
            encounter.TurnIndex++;
            if (encounter.TurnIndex >= encounter.Participants.Count)
            {
                encounter.TurnIndex = 0;
                CampaignRuntime.StartNewRound(encounter);
            }

            var next = encounter.Participants[encounter.TurnIndex];
            if (runtime.GetEntity(next.ActorId) is { } e && TableRules.IsDead(runtime, e))
            {
                next.HasActed = true;
                continue;
            }

            runtime.BeginTurn(next);
            break;
        }

        runtime.MarkEncounterDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        var upNextId = encounter is { Participants.Count: > 0 } ? encounter.Participants[encounter.TurnIndex].ActorId : null;
        var round = encounter?.Round ?? 1;
        return SessionEvent.From("TurnAdvance",
            new[] { upNextId != null ? EventMessage.Of("Turn.Passed", runtime.GetActorName(upNextId), round) : EventMessage.Of("Turn.PassedNobody", round) },
            upNextId != null ? new[] { upNextId } : null);
    }
}

/// <summary>Steps the initiative pointer back one participant (3.5), for correcting a misclick -
/// it does not un-tick durations advanced by the turn it's undoing, since effect expiry isn't
/// reversible without risking a status coming back after its owner was already told it wore off.</summary>
public sealed record PreviousTurnCommand : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        if (encounter == null || encounter.Participants.Count == 0)
        {
            return;
        }

        encounter.TurnIndex--;
        if (encounter.TurnIndex < 0)
        {
            encounter.TurnIndex = encounter.Participants.Count - 1;
            encounter.Round = Math.Max(1, encounter.Round - 1);
            foreach (var participant in encounter.Participants)
            {
                participant.HasActed = true;
                participant.TurnStarted = true;
            }
        }

        encounter.Participants[encounter.TurnIndex].HasActed = false;
        runtime.MarkEncounterDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        var currentId = encounter is { Participants.Count: > 0 } ? encounter.Participants[encounter.TurnIndex].ActorId : null;
        return SessionEvent.From("TurnAdvance",
            new[] { currentId != null ? EventMessage.Of("Turn.Back", runtime.GetActorName(currentId)) : EventMessage.Of("Turn.BackNobody") },
            currentId != null ? new[] { currentId } : null);
    }
}

/// <summary>Marks a participant as delaying or holding a readied action (3.5), or clears that
/// back to normal.</summary>
public sealed record SetTurnStateCommand(string ActorId, TurnState State) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
        var participant = runtime.ActiveEncounter?.Participants.FirstOrDefault(p => p.ActorId == ActorId);
        if (participant == null)
        {
            return;
        }

        participant.State = State;
        runtime.MarkEncounterDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("TurnState",
        new[] { EventMessage.Of(State switch { TurnState.Delayed => "Turn.Delayed", TurnState.Readied => "Turn.Readied", _ => "Turn.Normal" }, runtime.GetActorName(ActorId)) },
        new[] { ActorId });
}

/// <summary>A delayed or readied participant acts now: they move to just before whoever is acting,
/// take the turn, and keep that place in the order from here on.</summary>
public sealed record ActNowCommand(string ActorId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        var participant = encounter?.Participants.FirstOrDefault(p => p.ActorId == ActorId);
        if (encounter == null || participant == null)
        {
            return;
        }

        var current = encounter.Participants[encounter.TurnIndex];
        participant.State = TurnState.Normal;
        participant.HasActed = false;
        if (participant != current)
        {
            // Straight in front of whoever is acting, then tiebreaks that keep exactly this order:
            // nudging one number could still sort them ahead of someone who has already gone.
            encounter.Participants.Remove(participant);
            var at = encounter.Participants.IndexOf(current);
            encounter.Participants.Insert(at, participant);
            participant.InitiativeRoll = current.InitiativeRoll;
            participant.TieBreak = current.TieBreak;
            CampaignRuntime.FixTieOrder(encounter, current.InitiativeRoll);
            encounter.TurnIndex = at;
            runtime.BeginTurn(participant);
        }

        runtime.MarkEncounterDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("TurnAdvance",
        new[] { EventMessage.Of("Turn.ActsNow", runtime.GetActorName(ActorId), runtime.ActiveEncounter?.Round ?? 1) },
        new[] { ActorId });
}

/// <summary>Takes a participant out of the initiative order (a dead monster, a creature that fled).
/// The actor stays in the campaign and at the table.</summary>
public sealed record RemoveParticipantCommand(string ActorId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private bool _removed;

    public void Apply(CampaignRuntime runtime) => _removed = runtime.RemoveParticipant(ActorId);

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("InitiativeRemoved",
        new[] { EventMessage.Of(_removed ? "Initiative.Removed" : "Initiative.NotIn", runtime.GetActorName(ActorId)) },
        new[] { ActorId });
}

/// <summary>Ends the encounter: the order is closed (and kept with its end time), and conditions
/// with a round count expire, since combat is over; indefinite ones stay until removed.</summary>
public sealed record EndEncounterCommand : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private EncounterDocument? _ended;

    public void Apply(CampaignRuntime runtime)
    {
        _ended = runtime.EndEncounter();
        if (_ended == null)
        {
            return;
        }

        foreach (var participant in _ended.Participants)
        {
            if (runtime.GetEntity(participant.ActorId) is { } e && e.Has<ActiveEffectsComponent>())
            {
                e.Get<ActiveEffectsComponent>().Effects.RemoveAll(effect => !effect.IsIndefinite);
            }
        }

        runtime.MarkAllHydratedActorsDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("EncounterEnded",
        new[] { _ended != null ? EventMessage.Of("Encounter.Ended", _ended.Round) : EventMessage.Of("Encounter.None") });
}

/// <summary>Takes a deleted actor out of the live table: its entity and its place in the order go.</summary>
public sealed record RemoveActorCommand(string ActorId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private string _name = string.Empty;

    public void Apply(CampaignRuntime runtime)
    {
        _name = runtime.GetActorName(ActorId);
        runtime.RemoveActor(ActorId);
    }

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("ActorLeft", new[] { EventMessage.Of("Actor.Left", _name) }, new[] { ActorId });
}

/// <summary>
/// Resolves and logs a dice roll (3.6) through whichever <see cref="IDiceRoller"/> the campaign's
/// system plugin provides (<see cref="StandardDiceRoller"/> if it doesn't override one). Doesn't
/// touch ECS state - the roll log lives on the live session, exactly like everything else 3.2
/// already logs - so <see cref="AffectedActorIds"/> is empty.
/// </summary>
public sealed record RollDiceCommand(string Expression, string? ActorId, string RollerUserId, bool IsSecret, string? Label = null) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    // Populated by Apply, read back by Describe on the same command instance right after - a
    // command is applied exactly once by the single-writer loop, so there's no reentrancy risk.
    private DiceRollResult? _result;

    public void Apply(CampaignRuntime runtime)
    {
        _result = runtime.DiceRoller.Roll(Expression);
        runtime.RecordRoll(new RollLogEntry
        {
            ActorId = ActorId,
            RollerUserId = RollerUserId,
            Expression = Expression,
            Result = _result.ToString(),
            Total = _result.Total,
            IsSecret = IsSecret,
            Label = Label,
            Kept = _result.Kept.ToList(),
            Dropped = _result.Dropped.ToList(),
            Modifier = _result.Modifier
        });
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        object roller = ActorId != null ? runtime.GetActorName(ActorId) : EventTerm.User(RollerUserId);
        var message = IsSecret
            ? EventMessage.Of("Roll.Secret", roller, Expression)
            : Label != null
                ? EventMessage.Of("Roll.Labeled", roller, Label, Expression, _result?.Total)
                : EventMessage.Of("Roll.Plain", roller, Expression, _result?.Total);
        return SessionEvent.From("DiceRoll", new[] { message }, ActorId != null ? new[] { ActorId } : null);
    }
}

/// <summary>Appends a DM/player quick note to the live session (3.7) - reuses the same
/// <see cref="SessionDocument.Notes"/> list the recap editor (4.1) will read from, rather than a
/// separate combat-log-only notes concept. With a speaker it is a line of dialogue; the event then names
/// the speaker's id, so a player who can't see them doesn't get it. The runtime stamps the encounter,
/// round and acting combatant, so a note written mid-fight says when it happened without anyone typing it.</summary>
public sealed record AddSessionNoteCommand(
    string AuthorId,
    string Text,
    bool IsSecret,
    string? SpeakerActorId = null,
    NoteKind Kind = NoteKind.Note,
    IReadOnlyList<string>? WithActorIds = null) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime) => runtime.AddSessionNote(new SessionNote
    {
        AuthorId = AuthorId,
        Text = Text,
        IsSecret = IsSecret,
        Kind = Kind,
        SpeakerActorId = SpeakerActorId,
        WithActorIds = SpeakerActorId == null ? new List<string>() : (WithActorIds ?? Array.Empty<string>()).Where(id => id != SpeakerActorId).Distinct().ToList()
    });

    // The note's words stay out of the event log: they are in the notes list, read per viewer, and a note
    // corrected or removed later would otherwise live on here unchanged.
    public SessionEvent Describe(CampaignRuntime runtime) => SessionNoteEvents.Describe(IsSecret, "Added", Kind, SpeakerActorId);
}

/// <summary>Corrects a table note's words or kind. Only its author or the DM may; anyone else's edit is ignored.</summary>
public sealed record EditSessionNoteCommand(string NoteId, string EditorId, bool EditorIsDm, string Text, NoteKind Kind) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private SessionNote? _edited;

    public void Apply(CampaignRuntime runtime) => _edited = runtime.EditSessionNote(NoteId, EditorId, EditorIsDm, Text, Kind);

    public SessionEvent Describe(CampaignRuntime runtime) => SessionNoteEvents.Describe(_edited is { IsSecret: true }, _edited == null ? "Unchanged" : "Corrected", Kind, _edited?.SpeakerActorId);
}

/// <summary>Takes a table note out of the session. Only its author or the DM may.</summary>
public sealed record DeleteSessionNoteCommand(string NoteId, string EditorId, bool EditorIsDm) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private SessionNote? _removed;

    public void Apply(CampaignRuntime runtime) => _removed = runtime.DeleteSessionNote(NoteId, EditorId, EditorIsDm);

    public SessionEvent Describe(CampaignRuntime runtime) => SessionNoteEvents.Describe(_removed is { IsSecret: true }, _removed == null ? "InPlace" : "Removed", _removed?.Kind ?? NoteKind.Note, _removed?.SpeakerActorId);
}

/// <summary>The one shape every note event takes: type "Note" (which the recap digest skips, since it reads the notes
/// themselves), no words and no names, and the speaker's id so a player who can't see them doesn't get the event.</summary>
internal static class SessionNoteEvents
{
    public static SessionEvent Describe(bool secret, string verb, NoteKind kind, string? speakerActorId)
    {
        var noun = speakerActorId != null
            ? "Note.Noun.Quote"
            : kind switch { NoteKind.Clue => "Note.Noun.Clue", NoteKind.Loot => "Note.Noun.Loot", NoteKind.Ruling => "Note.Noun.Ruling", NoteKind.FollowUp => "Note.Noun.FollowUp", _ => "Note.Noun.Note" };
        var message = EventMessage.Of("Note." + verb + (secret ? "Secret" : "")).With(new[] { EventMessage.Of(noun) });
        return SessionEvent.From("Note", new[] { message }, speakerActorId == null ? null : new[] { speakerActorId });
    }
}

/// <summary>Hydrates an already-persisted <see cref="ActorDocument"/> into the already-running
/// runtime (3.7 "quick-add monsters from packs") - <see cref="CampaignRuntime.Hydrate"/> itself
/// only runs once, at startup, before any command is enqueued; an actor added mid-session, from
/// wherever it was created, has to go through the same single-writer loop as everything else.</summary>
public sealed record HydrateActorCommand(ActorDocument Actor) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { Actor.Id };

    public void Apply(CampaignRuntime runtime) => runtime.HydrateOne(Actor);

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("ActorJoined", new[] { EventMessage.Of("Actor.Joined", Actor.Name) }, new[] { Actor.Id });
}

/// <summary>Spends or regains one or more points of an expendable pool (a spell slot level, say),
/// clamped between zero and the pool's maximum. <paramref name="Label"/> is what the log calls it
/// ("level 3 spell slot").</summary>
public sealed record AdjustResourceCommand(string ActorId, string PoolName, int Delta, string Label) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            runtime.World.AdjustResource(e, PoolName, Delta);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From(
        Delta < 0 ? "ResourceSpent" : "ResourceRegained",
        new[] { EventMessage.Of(Delta < 0 ? "Resource.Used" : "Resource.Regained", runtime.GetActorName(ActorId), Math.Abs(Delta), EventTerm.Slot(PoolName, Label)) },
        new[] { ActorId });
}

/// <summary>A rest: refills the named slot pools, then applies whatever the system's rest rule of that
/// name says (hit points, temporary hit points, hit dice, exhaustion, conditions).</summary>
public sealed record RestCommand(string ActorId, string RestName, IReadOnlyList<string> PoolNames) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private List<EventMessage> _happened = new();

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _happened = TableRules.Rest(runtime, ActorId, e, RestName, PoolNames);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Rest",
        new[] { EventMessage.Of(_happened.Count > 0 ? "Rest.TookWith" : "Rest.Took", runtime.GetActorName(ActorId), EventTerm.Rest(RestName)).With(_happened) },
        new[] { ActorId });
}

/// <summary>
/// A character-sheet save made while the table is live (F2). The runtime owns the hydrated copy of
/// the actor, so the sheet cannot store its own copy without reverting table damage and being
/// reverted by the next turn; instead the edit is merged onto the runtime's copy and the entity is
/// re-hydrated. <paramref name="BaselineSheet"/> is the sheet as the page loaded it: a field the
/// editor left as it was keeps its live value (HP the table changed since), a field it changed wins.
/// Throws <see cref="UnauthorizedAccessException"/> when <paramref name="Access"/> may not edit the actor.
/// </summary>
public sealed record ReplaceActorCommand(
    ActorDocument Edited,
    IReadOnlyDictionary<string, object>? BaselineSheet,
    CampaignAccess Access,
    SystemDefinition? System) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { Edited.Id };

    public void Apply(CampaignRuntime runtime) => runtime.ReplaceActor(Edited, BaselineSheet, Access, System);

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("SheetEdited", new[] { EventMessage.Of("Sheet.Edited", runtime.GetActorName(Edited.Id)) }, new[] { Edited.Id });
}

/// <summary>The arguments of log messages that name something rather than count it.</summary>
internal static class LogTerms
{
    /// <summary>The source to name in the log ("from the trap"), or null for none. The table sends no source when a
    /// person just pressed the button: whoever clicked is not where the damage came from.</summary>
    public static string? Source(CampaignRuntime runtime, string? sourceId) =>
        string.IsNullOrWhiteSpace(sourceId) ? null : runtime.GetActorName(sourceId);

    /// <summary>A resource pool by key, with the system's English label.</summary>
    public static EventTerm Resource(CampaignRuntime runtime, string key) =>
        EventTerm.Resource(key, runtime.System?.Resources.FirstOrDefault(r => r.Key == key)?.Label ?? key);
}
