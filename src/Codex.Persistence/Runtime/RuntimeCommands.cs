using Codex.Core;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Persistence.Runtime;

/// <summary>Immediate, clamped damage (3.4/B7) with the table's vitals rules applied: the damage
/// type is checked against the creature's immunities, resistances and vulnerabilities, temporary
/// hit points absorb first, and reaching zero knocks a character down (death saves begin) or kills
/// a creature outright (<see cref="TableRules.ApplyDamage"/>).</summary>
public sealed record ApplyDamageCommand(string ActorId, string PoolName, int Amount, string SourceId, string? DamageType = null, bool IsCritical = false) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "Damage";

    private TableRules.DamageOutcome _outcome = TableRules.DamageOutcome.Nothing;

    /// <summary>Once applied: the save the creature owes to keep concentrating, or null when it was not concentrating (R4).</summary>
    public int? ConcentrationDc => _outcome.ConcentrationDc;

    // A system with sheet kinds that handles damage itself: what its handler did, instead of the vitals rules.
    private TableEvents.Outcome? _raised;

    public void Apply(CampaignRuntime runtime)
    {
        if (TableEvents.Handles(runtime, "damage"))
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["amount"] = (double)Amount, ["critical"] = IsCritical, ["source"] = SourceId };
            if (!string.IsNullOrWhiteSpace(DamageType)) fields["damageType"] = DamageType;
            _raised = TableEvents.Raise(runtime, ActorId, new Codex.Core.Rules.SheetEvent("damage", fields));
            return;
        }

        if (runtime.GetEntity(ActorId) is { } e)
        {
            _outcome = TableRules.ApplyDamage(runtime, ActorId, e, PoolName, Amount, DamageType, IsCritical);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        if (_raised is { } raised) return RaiseEventCommand.Describe(runtime, "Damage", ActorId, raised);
        var name = runtime.ActorTerm(ActorId);
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

        if (_outcome.ConcentrationLost) parts.Add(EventMessage.Of("Concentration.Lost", name));
        else if (_outcome.ConcentrationDc is { } dc) parts.Add(EventMessage.Of("Concentration.Save", name, dc));

        var player = EventMessage.Of(_outcome.Died ? "Damage.PlayerDied" : _outcome.WentDown ? "Damage.PlayerDown" : "Damage.Player", name);
        return SessionEvent.From("Damage", parts, new[] { ActorId }, new[] { player });
    }
}

public sealed record ApplyHealingCommand(string ActorId, string PoolName, int Amount, string SourceId) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "Healing";

    private TableRules.HealOutcome _outcome = new(0, false, false);

    private TableEvents.Outcome? _raised;

    public void Apply(CampaignRuntime runtime)
    {
        if (TableEvents.Handles(runtime, "heal"))
        {
            _raised = TableEvents.Raise(runtime, ActorId, new Codex.Core.Rules.SheetEvent("heal",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["amount"] = (double)Amount, ["source"] = SourceId }));
            return;
        }

        if (runtime.GetEntity(ActorId) is { } e)
        {
            _outcome = TableRules.ApplyHealing(runtime, ActorId, e, PoolName, Amount);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        if (_raised is { } raised) return RaiseEventCommand.Describe(runtime, "Healing", ActorId, raised);
        var name = runtime.ActorTerm(ActorId);
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
public sealed record GrantTemporaryHitPointsCommand(string ActorId, int Amount, string SourceId) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "TemporaryHitPoints";

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
        var name = runtime.ActorTerm(ActorId);
        var source = LogTerms.Source(runtime, SourceId);
        var key = (_applied ? "TempHp.Gained" : "TempHp.Kept") + (source == null ? "" : "From");
        return SessionEvent.From("TemporaryHitPoints",
            new[] { source == null ? EventMessage.Of(key, name, Amount) : EventMessage.Of(key, name, Amount, source) },
            new[] { ActorId },
            new[] { EventMessage.Of(_applied ? "TempHp.PlayerGained" : "TempHp.PlayerKept", name) });
    }
}

/// <summary>Rolls a death save with the table's dice for a character at zero hit points.</summary>
public sealed record RollDeathSaveCommand(string ActorId, string RollerUserId) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "DeathSave";

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
public sealed record RecordDeathSaveCommand(string ActorId, bool Success, int Count = 1) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "DeathSave";

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
        var name = runtime.ActorTerm(actorId);
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
public sealed record StabilizeCommand(string ActorId, string SourceId) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "Stabilized";

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
            !_applied ? EventMessage.Of("Stabilized.None", runtime.ActorTerm(ActorId))
                : LogTerms.Source(runtime, SourceId) is { } source ? EventMessage.Of("Stabilized.DoneBy", runtime.ActorTerm(ActorId), source)
                : EventMessage.Of("Stabilized.Done", runtime.ActorTerm(ActorId))
        },
        new[] { ActorId });
}

/// <summary>Raises or lowers the exhaustion level; the top level kills.</summary>
public sealed record AdjustExhaustionCommand(string ActorId, int Delta) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "Exhaustion";

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
        var name = runtime.ActorTerm(ActorId);
        var message = _outcome is { } o
            ? o.Died ? EventMessage.Of("Exhaustion.Died", name) : o.Level == 0 ? EventMessage.Of("Exhaustion.None", name) : EventMessage.Of("Exhaustion.Level", name, o.Level)
            : EventMessage.Of("Exhaustion.Failed", name);
        return SessionEvent.From("Exhaustion", new[] { message }, new[] { ActorId });
    }
}

/// <summary>Spends hit dice during a rest: each die plus the attribute modifier heals.</summary>
public sealed record SpendHitDiceCommand(string ActorId, int Count, int DieSides) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "HitDice";

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
        var name = runtime.ActorTerm(ActorId);
        if (_outcome is { Spent: > 0 } o)
        {
            return SessionEvent.From("HitDice",
                new[] { EventMessage.Of("HitDice.Spent", name, o.Spent, DieSides, string.Join(", ", o.Rolls), o.Healed) },
                new[] { ActorId },
                new[] { EventMessage.Of("HitDice.Player", name) });
        }

        // Nothing to hide, but said so: a hit-dice event without a players' version is not shown to other players.
        var none = new[] { EventMessage.Of("HitDice.None", name) };
        return SessionEvent.From("HitDice", none, new[] { ActorId }, none);
    }
}

/// <summary>Adds a status effect without clobbering whatever else <paramref name="ActorId"/> is
/// already under (B6). <paramref name="AnchorActorId"/> is whose turn ticks the duration down -
/// usually the affected actor itself, but a DM-applied hazard might anchor to its source instead.
/// <paramref name="Rounds"/> below one means the effect lasts until it is removed. <paramref name="HeldBy"/> names the creature
/// whose concentration keeps it up (R4): it gets the system's concentration condition, and the effect ends when that does.</summary>
public sealed record AddStatusEffectCommand(string ActorId, string EffectId, string SourceId, int Rounds, EffectExpiry Expiry, string AnchorActorId, string? HeldBy = null) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = string.IsNullOrEmpty(HeldBy) || HeldBy == ActorId ? new[] { ActorId } : new[] { ActorId, HeldBy };

    public string UndoKind => "StatusEffect";

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            var concentration = TableRules.ConcentrationCondition(runtime);
            var holder = concentration != null && !string.IsNullOrEmpty(HeldBy) ? runtime.GetEntity(HeldBy) : null;
            runtime.World.AddStatus(e, EffectId, SourceId, Rounds, Expiry, AnchorActorId, holder != null ? HeldBy : null);
            if (holder is { } concentrating && !TableRules.IsConcentrating(runtime, concentrating))
            {
                runtime.World.AddStatus(concentrating, concentration!, "concentrating", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, HeldBy!);
            }
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.ActorTerm(ActorId);
        var effect = EventTerm.Condition(EffectId);
        var parts = new List<EventMessage>
        {
            LogTerms.Source(runtime, SourceId) is { } source ? EventMessage.Of("Status.GainedFrom", name, effect, source) : EventMessage.Of("Status.Gained", name, effect)
        };
        var start = Expiry == EffectExpiry.StartOfTurn;
        // The turn is always named, even the creature's own: "their turn" has a gender in Hebrew.
        parts.Add(Rounds < 1
            ? EventMessage.Of("Status.Indefinite")
            : EventMessage.Of(start ? "Status.UntilStart" : "Status.UntilEnd", Rounds, runtime.ActorTerm(AnchorActorId)));
        if (!string.IsNullOrEmpty(HeldBy) && TableRules.ConcentrationCondition(runtime) != null)
        {
            parts.Add(EventMessage.Of("Concentration.Holds", runtime.ActorTerm(HeldBy)));
        }

        return SessionEvent.From("StatusEffect", parts, string.IsNullOrEmpty(HeldBy) ? new[] { ActorId } : new[] { ActorId, HeldBy });
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
        SessionEvent.From("TurnAdvance", new[] { EventMessage.Of("Turn.Ended", runtime.ActorTerm(ActorId)) }, new[] { ActorId });
}

/// <summary>Removes every instance of a status effect from an actor (3.7 "remove conditions") -
/// the counterpart <see cref="AddStatusEffectCommand"/> never got, since B6's fix only needed
/// "add without clobbering."</summary>
public sealed record RemoveStatusEffectCommand(string ActorId, string EffectId) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "StatusEffectRemoved";

    // How many effects the creature's concentration held up, when the condition removed was concentration.
    private int _released;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _released = string.Equals(EffectId, TableRules.ConcentrationCondition(runtime), StringComparison.OrdinalIgnoreCase)
                ? TableRules.EndConcentration(runtime, ActorId, e)
                : 0;
            runtime.World.RemoveStatus(e, EffectId);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var parts = new List<EventMessage> { EventMessage.Of("Status.Lost", runtime.ActorTerm(ActorId), EventTerm.Condition(EffectId)) };
        if (_released > 0) parts.Add(EventMessage.Of("Concentration.Released", _released));
        return SessionEvent.From("StatusEffectRemoved", parts, new[] { ActorId });
    }
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
        SessionEvent.From("Initiative", new[] { EventMessage.Of("Initiative.Rolled", runtime.ActorTerm(ActorId), InitiativeRoll) }, new[] { ActorId });
}

/// <summary>Rolls <paramref name="Expression"/> (the initiative die with the actor's bonus) once, logs that roll and puts
/// the actor in the order at its total. The console used to roll on its own thread and enqueue a separate roll, so the
/// log and the order showed two different numbers (D7).</summary>
public sealed record RollInitiativeCommand(string ActorId, string Expression, string RollerUserId, int TieBreak = 0) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    // Set by Apply and read by Describe on the same instance, as RollDiceCommand does.
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
            Kept = _result.Kept.ToList(),
            Dropped = _result.Dropped.ToList(),
            Modifier = _result.Modifier,
            CheckDieIndex = _result.CheckDieIndex
        });
        new SetInitiativeCommand(ActorId, _result.Total, TieBreak).Apply(runtime);
    }

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("Initiative", new[] { EventMessage.Of("Initiative.Rolled", runtime.ActorTerm(ActorId), _result?.Total ?? 0) }, new[] { ActorId });
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

        // A system with sheet kinds hears the turn end and the next one start (a death save each turn while mortally wounded).
        if (TableEvents.Handles(runtime, "turnEnd")) _raised.AddRange(TableEvents.Raise(runtime, current.ActorId, new Codex.Core.Rules.SheetEvent("turnEnd", new Dictionary<string, object?>())).Messages);

        current.HasActed = true;
        runtime.MoveTurnOn(encounter, fromPointer: false);
        runtime.MarkEncounterDirty();

        if (encounter.Participants.Count > 0 && TableEvents.Handles(runtime, "turnStart"))
        {
            var next = encounter.Participants[encounter.TurnIndex].ActorId;
            _raised.AddRange(TableEvents.Raise(runtime, next, new Codex.Core.Rules.SheetEvent("turnStart", new Dictionary<string, object?>())).Messages);
        }
    }

    private readonly List<EventMessage> _raised = new();

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        var upNextId = encounter is { Participants.Count: > 0 } ? encounter.Participants[encounter.TurnIndex].ActorId : null;
        var round = encounter?.Round ?? 1;
        return SessionEvent.From("TurnAdvance",
            new[] { upNextId != null ? EventMessage.Of("Turn.Passed", runtime.ActorTerm(upNextId), round) : EventMessage.Of("Turn.PassedNobody", round) }.Concat(_raised),
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
            new[] { currentId != null ? EventMessage.Of("Turn.Back", runtime.ActorTerm(currentId)) : EventMessage.Of("Turn.BackNobody") },
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
        new[] { EventMessage.Of(State switch { TurnState.Delayed => "Turn.Delayed", TurnState.Readied => "Turn.Readied", _ => "Turn.Normal" }, runtime.ActorTerm(ActorId)) },
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
        new[] { EventMessage.Of("Turn.ActsNow", runtime.ActorTerm(ActorId), runtime.ActiveEncounter?.Round ?? 1) },
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
        new[] { EventMessage.Of(_removed ? "Initiative.Removed" : "Initiative.NotIn", runtime.ActorTerm(ActorId)) },
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

/// <summary>The DM marks a creature dead (R3): through the table's rules, so it is no longer down or stable, its death-save
/// tally goes, and what it concentrated on or counted on its turns ends (<see cref="TableRules.Kill"/>).</summary>
public sealed record KillCommand(string ActorId) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "Killed";

    private bool _killed;

    public void Apply(CampaignRuntime runtime) =>
        _killed = runtime.GetEntity(ActorId) is { } e && TableRules.Kill(runtime, e, ActorId);

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.ActorTerm(ActorId);
        return SessionEvent.From("Killed", new[] { EventMessage.Of(_killed ? "Killed.Done" : "Killed.Already", name) }, new[] { ActorId });
    }
}

/// <summary>Brings a dead creature back (R3): not dead, down or stable, death saves reset, on at least one hit point
/// (<see cref="TableRules.Revive"/>). Removing the dead condition by hand left a character unconscious with its failed saves.</summary>
public sealed record ReviveCommand(string ActorId) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "Revived";

    private bool _revived;

    public void Apply(CampaignRuntime runtime) =>
        _revived = runtime.GetEntity(ActorId) is { } e && TableRules.Revive(runtime, e);

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.ActorTerm(ActorId);
        return SessionEvent.From("Revived", new[] { EventMessage.Of(_revived ? "Revived.Done" : "Revived.NotDead", name) }, new[] { ActorId });
    }
}

/// <summary>Takes back the last change to a creature's pools and conditions (R5): a damage number typed wrong, a condition put
/// on the wrong row. Refused, and logged as such, when something has changed the creature since; see <see cref="CampaignRuntime.Undo"/>.</summary>
public sealed record UndoLastChangeCommand(string ActorId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    private string? _undone;

    public void Apply(CampaignRuntime runtime) => _undone = runtime.Undo(ActorId);

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var name = runtime.ActorTerm(ActorId);
        return SessionEvent.From("Undo",
            new[] { _undone != null ? EventMessage.Of("Undo.Done", name) : EventMessage.Of("Undo.Nothing", name) },
            new[] { ActorId });
    }
}

/// <summary>Gives the table a new copy of an actor's document after a change outside its live state (the DM revealed it, R1),
/// so every open table page reloads who it may see. The change itself is already stored.</summary>
public sealed record RefreshActorCommand(ActorDocument Actor, ActorVisibility? Revealed = null) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime) => runtime.RefreshDocument(Actor);

    // Players read the name they know it by: a stranger's public name, never the real one.
    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("ActorVisibility",
        new[] { EventMessage.Of(Revealed switch { ActorVisibility.Known => "Visibility.Known", ActorVisibility.Unknown => "Visibility.Unknown", _ => "Visibility.Hidden" }, EventTerm.Actor(Actor.Id, Actor.Name)) },
        new[] { Actor.Id },
        new[] { EventMessage.Of("Visibility.Player", EventTerm.Actor(Actor.Id, Actor.NameFor(isDm: false))) });
}

/// <summary>Takes a deleted actor out of the live table: its entity and its place in the order go.</summary>
public sealed record RemoveActorCommand(string ActorId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private object _name = string.Empty;

    public void Apply(CampaignRuntime runtime)
    {
        _name = runtime.ActorTerm(ActorId);
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
            Modifier = _result.Modifier,
            CheckDieIndex = _result.CheckDieIndex
        });
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        object roller = ActorId != null ? runtime.ActorTerm(ActorId) : EventTerm.User(RollerUserId);
        var message = IsSecret
            ? EventMessage.Of("Roll.Secret", roller, Expression)
            : Label != null
                ? EventMessage.Of("Roll.Labeled", roller, Label, Expression, _result?.Total)
                : EventMessage.Of("Roll.Plain", roller, Expression, _result?.Total);
        return SessionEvent.From("DiceRoll", new[] { message }, ActorId != null ? new[] { ActorId } : null);
    }
}

/// <summary>
/// A roll made from the system's roll definitions (overhaul steps 4 and 6) for an actor at the table: made on the actor's live
/// sheet with the host's dice, recorded in the roll log with its outcome and mark, and what it asks of the actor applied as an
/// event's intents are (<see cref="TableEvents.Roll"/>). Undoable like damage, for what it changed.
/// </summary>
public sealed record MakeRollCommand(string ActorId, string RollerUserId, Codex.Core.Rules.SheetRollRequest Request, bool IsSecret = false,
    IDiceSource? Dice = null) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "DiceRoll";

    private TableEvents.Outcome _outcome = TableEvents.Outcome.None;
    private Codex.Core.Rules.SheetRollResult? _rolled;

    /// <summary>Once applied: the roll, or null when it could not be made (<see cref="Failed"/> says why).</summary>
    public Codex.Core.Rules.SheetRollResult? Rolled => _rolled;

    public string? Failed => _outcome.Failed;

    public void Apply(CampaignRuntime runtime)
    {
        _outcome = TableEvents.Roll(runtime, ActorId, Request, Dice ?? new RandomDiceSource(), out _rolled);
        if (_rolled is not { } roll) return;
        runtime.RecordRoll(new RollLogEntry
        {
            ActorId = ActorId,
            RollerUserId = RollerUserId,
            Expression = roll.Definition,
            Result = string.Join(" ", roll.Kept) + " = " + roll.Total.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Total = (int)Math.Round(roll.Total),
            IsSecret = IsSecret,
            Label = roll.Label ?? roll.Definition,
            Kept = roll.Kept.ToList(),
            Dropped = roll.Dropped.ToList(),
            Modifier = (int)Math.Round(roll.Modifier),
            Outcome = roll.Outcome,
            Mark = roll.Mark
        });
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var roller = runtime.ActorTerm(ActorId);
        var parts = new List<EventMessage>();
        if (_rolled is { } roll)
        {
            var label = new EventTerm(EventTerm.SystemKind, roll.Label ?? roll.Definition, runtime.System?.DisplayValue(roll.Label ?? roll.Definition) ?? roll.Definition);
            parts.Add(IsSecret ? EventMessage.Of("Roll.Secret", roller, roll.Definition)
                : roll.Outcome is { } outcome
                    ? EventMessage.Of("Roll.Outcome", roller, label, new EventTerm(EventTerm.SystemKind, outcome, runtime.System?.DisplayValue(outcome) ?? outcome), roll.Total)
                    : EventMessage.Of("Roll.Labeled", roller, label, roll.Definition, roll.Total));
        }

        if (!IsSecret) parts.AddRange(_outcome.Messages);
        else if (_outcome.Failed != null) parts.AddRange(_outcome.Messages);
        return SessionEvent.From("DiceRoll", parts.Count > 0 ? parts : new[] { EventMessage.Of("Sheet.Nothing", roller, Request.Definition) },
            _outcome.Touched.Count > 0 ? _outcome.Touched.Distinct().ToArray() : new[] { ActorId });
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
        SessionEvent.From("ActorJoined", new[] { EventMessage.Of("Actor.Joined", EventTerm.Actor(Actor.Id, Actor.Name)) }, new[] { Actor.Id });
}

/// <summary>Logs that the DM revealed a secret of a shared wiki page to the party, while a session is on: the table sees
/// it happen, and the recap has it. Changes nothing in the world; the page itself was saved before this was sent. Only the
/// page's title is logged, never the secret's words, and only for a page the party can already open.</summary>
public sealed record LogWikiRevealCommand(string PageTitle) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
    }

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("WikiReveal", new[] { EventMessage.Of("Wiki.Revealed", PageTitle) });
}

/// <summary>
/// Raises one of the system's declared events on an actor at the table (overhaul step 6): <c>sessionEnd</c> with the answers,
/// <c>advance</c>, a custom one (<c>assist</c>), or an intent the GM makes directly (<c>mark</c> harm, <c>adjust</c> stress).
/// The handler's intents are applied as <see cref="TableEvents"/> says, and can be undone like damage.
/// </summary>
public sealed record RaiseEventCommand(string ActorId, string Type, IReadOnlyDictionary<string, object?> Fields) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "SheetEvent";

    private TableEvents.Outcome _outcome = TableEvents.Outcome.None;

    /// <summary>Once applied: why the handler could not run, or null.</summary>
    public string? Failed => _outcome.Failed;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.System is not { SheetKinds.Count: > 0 } system || !Codex.Core.Rules.SheetEvents.Handles(system, Type)) return;
        _outcome = TableEvents.Raise(runtime, ActorId, new Codex.Core.Rules.SheetEvent(Type, Fields));
    }

    public SessionEvent Describe(CampaignRuntime runtime) => Describe(runtime, "SheetEvent", ActorId, _outcome);

    internal static SessionEvent Describe(CampaignRuntime runtime, string type, string actorId, TableEvents.Outcome outcome) =>
        SessionEvent.From(type, outcome.Messages.Count > 0 ? outcome.Messages : new[] { EventMessage.Of("Sheet.Nothing", runtime.ActorTerm(actorId), type) },
            outcome.Touched.Count > 0 ? outcome.Touched.Distinct().ToArray() : new[] { actorId });
}

/// <summary>Spends or regains one or more points of an expendable pool (a spell slot level, say),
/// clamped between zero and the pool's maximum. <paramref name="Label"/> is what the log calls it
/// ("level 3 spell slot").</summary>
public sealed record AdjustResourceCommand(string ActorId, string PoolName, int Delta, string Label) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => Delta < 0 ? "ResourceSpent" : "ResourceRegained";

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            runtime.World.AdjustResource(e, PoolName, Delta, key => TableRules.MaxKeyOf(runtime, key));
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From(
        Delta < 0 ? "ResourceSpent" : "ResourceRegained",
        new[] { EventMessage.Of(Delta < 0 ? "Resource.Used" : "Resource.Regained", runtime.ActorTerm(ActorId), Math.Abs(Delta), EventTerm.Slot(PoolName, Label)) },
        new[] { ActorId });
}

/// <summary>A rest: refills the named slot pools, then applies whatever the system's rest rule of that
/// name says (hit points, temporary hit points, hit dice, exhaustion, conditions).</summary>
public sealed record RestCommand(string ActorId, string RestName, IReadOnlyList<string> PoolNames) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "Rest";

    private List<EventMessage> _happened = new();

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.GetEntity(ActorId) is { } e)
        {
            _happened = TableRules.Rest(runtime, ActorId, e, RestName, PoolNames);
            if (TableEvents.Handles(runtime, "rest"))
            {
                _happened.AddRange(TableEvents.Raise(runtime, ActorId, new Codex.Core.Rules.SheetEvent("rest",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = RestName })).Messages);
            }
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Rest",
        new[] { EventMessage.Of(_happened.Count > 0 ? "Rest.TookWith" : "Rest.Took", runtime.ActorTerm(ActorId), EventTerm.Rest(RestName)).With(_happened) },
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
        SessionEvent.From("SheetEdited", new[] { EventMessage.Of("Sheet.Edited", runtime.ActorTerm(Edited.Id)) }, new[] { Edited.Id });
}

/// <summary>The arguments of log messages that name something rather than count it.</summary>
internal static class LogTerms
{
    /// <summary>The source to name in the log ("from the trap"), or null for none. The table sends no source when a
    /// person just pressed the button: whoever clicked is not where the damage came from.</summary>
    public static object? Source(CampaignRuntime runtime, string? sourceId) =>
        string.IsNullOrWhiteSpace(sourceId) ? null : runtime.ActorTerm(sourceId);

    /// <summary>A resource pool by key, with the system's English label.</summary>
    public static EventTerm Resource(CampaignRuntime runtime, string key) =>
        EventTerm.Resource(key, runtime.System?.Resources.FirstOrDefault(r => r.Key == key)?.Label ?? key);
}

/// <summary>An event that is the campaign's (overhaul step 7): a clock ticked (<c>adjust { clock, by }</c>), a faction's status
/// moved, or a declared event with no actor whose handlers act on the campaign's objects. GM only, which the caller checks.</summary>
public sealed record CampaignEventCommand(string Type, IReadOnlyDictionary<string, object?> Fields) : IRuntimeCommand
{
    private TableEvents.Outcome _outcome = TableEvents.Outcome.None;

    public IReadOnlyCollection<string> AffectedActorIds => _outcome.Touched;

    /// <summary>Once applied: why the handler could not run, or null.</summary>
    public string? Failed => _outcome.Failed;

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.System is not { } system || !Codex.Core.Rules.SheetEvents.Handles(system, Type)) return;
        _outcome = TableEvents.RaiseOnCampaign(runtime, new Codex.Core.Rules.SheetEvent(Type, Fields));
    }

    public SessionEvent Describe(CampaignRuntime runtime) =>
        SessionEvent.From("Campaign", _outcome.Messages.Count > 0 ? _outcome.Messages : new[] { EventMessage.Of("Campaign.Nothing", Type) },
            _outcome.Touched.Count > 0 ? _outcome.Touched.Distinct().ToArray() : null);
}

/// <summary>The GM changing the campaign's objects themselves (a clock added, renamed or resized; a faction added): the edit
/// runs on the table's copy and returns false to change nothing. <see cref="Said"/> is what the log says; a message about
/// something kept for the GM names it with <see cref="EventTerm.GmOnly"/>.</summary>
public sealed record EditCampaignObjectsCommand(Func<Dictionary<string, object>, bool> Edit, EventMessage Said) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    /// <summary>Once applied: whether the edit changed anything.</summary>
    public bool Changed { get; private set; }

    public void Apply(CampaignRuntime runtime)
    {
        var state = runtime.CampaignObjectState;
        var before = Codex.Core.Rules.CampaignObjects.Copy(state);
        Changed = Edit(state);
        if (!Changed) return;
        foreach (var key in state.Keys.Union(before.Keys).Where(k => !Codex.Core.YamlValues.SameValue(state.GetValueOrDefault(k), before.GetValueOrDefault(k))).ToList())
        {
            runtime.CampaignKeyWritten(key);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Campaign", new[] { Changed ? Said : EventMessage.Of("Campaign.Unchanged") });
}
