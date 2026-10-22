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
        var type = string.IsNullOrWhiteSpace(DamageType) ? "" : DamageType.ToLowerInvariant() + " ";
        var text = _outcome.Modifier == "immune"
            ? $"{name} is immune to {type}damage: {Amount} from {SourceId} did nothing."
            : $"{name} took {_outcome.Dealt} {type}damage from {SourceId}{(_outcome.Modifier != null ? $" ({_outcome.Modifier}, {Amount} rolled)" : "")}{(_outcome.Absorbed > 0 ? $", {_outcome.Absorbed} absorbed by temporary hit points" : "")}.";
        if (_outcome.Died) text += $" {name} died.";
        else if (_outcome.WentDown) text += $" {name} dropped to 0 and is down.";
        else if (_outcome.FailuresAdded > 0) text += $" {name} was hit while down: {_outcome.FailuresAdded} failed death save{(_outcome.FailuresAdded == 1 ? "" : "s")}.";

        return new SessionEvent
        {
            Type = "Damage",
            Description = text,
            ActorIds = { ActorId },
            PlayerDescription = _outcome.Died ? $"{name} took damage and died." : _outcome.WentDown ? $"{name} took damage and went down." : $"{name} took damage."
        };
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
        return new SessionEvent
        {
            Type = "Healing",
            Description = _outcome.WasDead
                ? $"{name} is dead; {Amount} healing from {SourceId} did nothing."
                : $"{name} recovered {_outcome.Healed} {PoolName} from {SourceId}.{(_outcome.Revived ? $" {name} is back on their feet." : "")}",
            ActorIds = { ActorId },
            PlayerDescription = _outcome.Revived ? $"{name} was healed and is back up." : $"{name} was healed."
        };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "TemporaryHitPoints",
        Description = _applied
            ? $"{runtime.GetActorName(ActorId)} gained {Amount} temporary hit points from {SourceId}."
            : $"{runtime.GetActorName(ActorId)} already had at least {Amount} temporary hit points; {SourceId} changed nothing.",
        ActorIds = { ActorId },
        PlayerDescription = _applied ? $"{runtime.GetActorName(ActorId)} gained temporary hit points." : $"{runtime.GetActorName(ActorId)} already had temporary hit points."
    };
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
        var rolled = outcome.Roll is { } r ? $" (rolled {r})" : "";
        var text = outcome.NotApplicable
            ? $"{name} is not making death saves."
            : outcome.Revived
                ? $"{name} rolled a natural {outcome.Roll} on a death save and is back up with 1 hit point."
                : outcome.Died
                    ? $"{name} failed a death save{rolled} and died."
                    : outcome.Stabilized
                        ? $"{name} succeeded on a death save{rolled} and is stable."
                        : $"{name} {(outcome.Success == true ? "succeeded on" : "failed")} a death save{rolled}: {outcome.Successes} success{(outcome.Successes == 1 ? "" : "es")}, {outcome.Failures} failure{(outcome.Failures == 1 ? "" : "s")}.";
        return new SessionEvent { Type = "DeathSave", Description = text, ActorIds = { actorId } };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "Stabilized",
        Description = _applied ? $"{runtime.GetActorName(ActorId)} was stabilized by {SourceId}." : $"{runtime.GetActorName(ActorId)} did not need stabilizing.",
        ActorIds = { ActorId }
    };
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
        return new SessionEvent
        {
            Type = "Exhaustion",
            Description = _outcome is { } o
                ? o.Died ? $"{name} reached the final level of exhaustion and died." : o.Level == 0 ? $"{name} is no longer exhausted." : $"{name} is at exhaustion level {o.Level}."
                : $"{name}'s exhaustion could not be changed.",
            ActorIds = { ActorId }
        };
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
        return new SessionEvent
        {
            Type = "HitDice",
            Description = _outcome is { Spent: > 0 } o
                ? $"{name} spent {o.Spent} hit {(o.Spent == 1 ? "die" : "dice")} (d{DieSides}: {string.Join(", ", o.Rolls)}) and recovered {o.Healed} hit points."
                : $"{name} has no hit dice to spend.",
            ActorIds = { ActorId },
            PlayerDescription = _outcome is { Spent: > 0 } ? $"{name} spent hit dice and recovered hit points." : null
        };
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
        var duration = Rounds < 1
            ? "until removed"
            : $"for {Rounds} round{(Rounds == 1 ? "" : "s")}, ending at the {(Expiry == EffectExpiry.StartOfTurn ? "start" : "end")} of {(AnchorActorId == ActorId ? "their" : runtime.GetActorName(AnchorActorId) + "'s")} turn";
        return new SessionEvent
        {
            Type = "StatusEffect",
            Description = $"{runtime.GetActorName(ActorId)} gained {EffectId} {duration} from {SourceId}.",
            ActorIds = { ActorId }
        };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "TurnAdvance",
        Description = $"{runtime.GetActorName(ActorId)}'s turn ended.",
        ActorIds = { ActorId }
    };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "StatusEffectRemoved",
        Description = $"{runtime.GetActorName(ActorId)} lost {EffectId}.",
        ActorIds = { ActorId }
    };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "Initiative",
        Description = $"{runtime.GetActorName(ActorId)} rolled {InitiativeRoll} for initiative.",
        ActorIds = { ActorId }
    };
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
        var upNext = upNextId != null ? runtime.GetActorName(upNextId) : "nobody";
        var evt = new SessionEvent { Type = "TurnAdvance", Description = $"Turn passed to {upNext} (round {encounter?.Round ?? 1})." };
        if (upNextId != null)
        {
            evt.ActorIds.Add(upNextId);
        }

        return evt;
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
        var current = currentId != null ? runtime.GetActorName(currentId) : "nobody";
        var evt = new SessionEvent { Type = "TurnAdvance", Description = $"Turn moved back to {current}." };
        if (currentId != null)
        {
            evt.ActorIds.Add(currentId);
        }

        return evt;
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "TurnState",
        ActorIds = { ActorId },
        Description = State switch
        {
            TurnState.Delayed => $"{runtime.GetActorName(ActorId)} delayed their turn.",
            TurnState.Readied => $"{runtime.GetActorName(ActorId)} readied an action.",
            _ => $"{runtime.GetActorName(ActorId)} is acting normally again."
        }
    };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "TurnAdvance",
        Description = $"{runtime.GetActorName(ActorId)} acts now (round {runtime.ActiveEncounter?.Round ?? 1}).",
        ActorIds = { ActorId }
    };
}

/// <summary>Takes a participant out of the initiative order (a dead monster, a creature that fled).
/// The actor stays in the campaign and at the table.</summary>
public sealed record RemoveParticipantCommand(string ActorId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private bool _removed;

    public void Apply(CampaignRuntime runtime) => _removed = runtime.RemoveParticipant(ActorId);

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "InitiativeRemoved",
        Description = _removed ? $"{runtime.GetActorName(ActorId)} left the initiative order." : $"{runtime.GetActorName(ActorId)} was not in the initiative order.",
        ActorIds = { ActorId }
    };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "EncounterEnded",
        Description = _ended != null ? $"The encounter ended after {_ended.Round} round{(_ended.Round == 1 ? "" : "s")}." : "There was no encounter to end."
    };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "ActorLeft",
        Description = $"{_name} left the table.",
        ActorIds = { ActorId }
    };
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
        var roller = ActorId != null ? runtime.GetActorName(ActorId) : RollerUserId;
        var description = IsSecret
            ? $"{roller} made a secret roll ({Expression})."
            : $"{roller} rolled {(Label != null ? Label + " " : "")}{Expression}: {_result?.Total}.";
        var evt = new SessionEvent { Type = "DiceRoll", Description = description };
        if (ActorId != null)
        {
            evt.ActorIds.Add(ActorId);
        }

        return evt;
    }
}

/// <summary>Appends a DM/player quick note to the live session (3.7) - reuses the same
/// <see cref="SessionDocument.Notes"/> list the recap editor (4.1) will read from, rather than a
/// separate combat-log-only notes concept.</summary>
public sealed record AddSessionNoteCommand(string AuthorId, string Text, bool IsSecret) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime) => runtime.AddSessionNote(new SessionNote { AuthorId = AuthorId, Text = Text, IsSecret = IsSecret });

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "Note",
        Description = IsSecret ? $"{AuthorId} added a secret note." : $"Note: {Text}"
    };
}

/// <summary>Hydrates an already-persisted <see cref="ActorDocument"/> into the already-running
/// runtime (3.7 "quick-add monsters from packs") - <see cref="CampaignRuntime.Hydrate"/> itself
/// only runs once, at startup, before any command is enqueued; an actor added mid-session, from
/// wherever it was created, has to go through the same single-writer loop as everything else.</summary>
public sealed record HydrateActorCommand(ActorDocument Actor) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { Actor.Id };

    public void Apply(CampaignRuntime runtime) => runtime.HydrateOne(Actor);

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "ActorJoined",
        Description = $"{Actor.Name} joined the table.",
        ActorIds = { Actor.Id }
    };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = Delta < 0 ? "ResourceSpent" : "ResourceRegained",
        ActorIds = { ActorId },
        Description = Delta < 0
            ? $"{runtime.GetActorName(ActorId)} used {(-Delta == 1 ? "a" : (-Delta).ToString())} {Label}{(-Delta == 1 ? "" : "s")}."
            : $"{runtime.GetActorName(ActorId)} regained {(Delta == 1 ? "a" : Delta.ToString())} {Label}{(Delta == 1 ? "" : "s")}."
    };
}

/// <summary>A rest: refills the named slot pools, then applies whatever the system's rest rule of that
/// name says (hit points, temporary hit points, hit dice, exhaustion, conditions).</summary>
public sealed record RestCommand(string ActorId, string RestName, IReadOnlyList<string> PoolNames) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private List<string> _happened = new();

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _happened = TableRules.Rest(runtime, ActorId, e, RestName, PoolNames);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "Rest",
        Description = $"{runtime.GetActorName(ActorId)} took a {RestName}{(_happened.Count > 0 ? ": " + string.Join(", ", _happened) : "")}.",
        ActorIds = { ActorId }
    };
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

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "SheetEdited",
        Description = $"{runtime.GetActorName(Edited.Id)}'s sheet was edited.",
        ActorIds = { Edited.Id }
    };
}
