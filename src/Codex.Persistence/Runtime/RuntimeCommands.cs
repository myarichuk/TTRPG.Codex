using Codex.Core.Abilities;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Persistence.Runtime;

/// <summary>Immediate, clamped damage (3.4/B7) - replaces the old <c>DamageEvent</c> component
/// pattern where two hits landing before the next tick silently overwrote each other and nothing
/// stopped HP going negative.</summary>
public sealed record ApplyDamageCommand(string ActorId, string PoolName, int Amount, string SourceId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public void Apply(CampaignRuntime runtime)
    {
        var entity = runtime.GetEntity(ActorId);
        if (entity is { } e)
        {
            runtime.World.ApplyDamage(e, PoolName, Amount);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "Damage",
        Description = $"{runtime.GetActorName(ActorId)} took {Amount} {PoolName} damage from {SourceId}."
    };
}

public sealed record ApplyHealingCommand(string ActorId, string PoolName, int Amount, string SourceId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public void Apply(CampaignRuntime runtime)
    {
        var entity = runtime.GetEntity(ActorId);
        if (entity is { } e)
        {
            runtime.World.ApplyHealing(e, PoolName, Amount);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "Healing",
        Description = $"{runtime.GetActorName(ActorId)} recovered {Amount} {PoolName} from {SourceId}."
    };
}

/// <summary>Adds a status effect without clobbering whatever else <paramref name="ActorId"/> is
/// already under (B6). <paramref name="AnchorActorId"/> is whose turn ticks the duration down -
/// usually the affected actor itself, but a DM-applied hazard might anchor to its source instead.</summary>
public sealed record AddStatusEffectCommand(string ActorId, string EffectId, string SourceId, int Rounds, EffectExpiry Expiry, string AnchorActorId) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public void Apply(CampaignRuntime runtime)
    {
        var entity = runtime.GetEntity(ActorId);
        var anchor = runtime.GetEntity(AnchorActorId);
        if (entity is { } e && anchor is { } a)
        {
            runtime.World.AddStatus(e, EffectId, SourceId, Rounds, Expiry, a);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "StatusEffect",
        Description = $"{runtime.GetActorName(ActorId)} gained {EffectId} for {Rounds} round(s) from {SourceId}."
    };
}

/// <summary>Advances the turn boundary for one actor: every effect anchored to them, across every
/// entity in the encounter, ticks down and expires independently (3.4).</summary>
public sealed record AdvanceTurnCommand(string ActorId) : IRuntimeCommand
{
    // Effects anchored to ActorId can live on any entity in the encounter, not just ActorId's own -
    // Apply marks every affected actor dirty directly rather than trying to predict them here.
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private IReadOnlyList<TriggeredAbility>? _triggered;

    public void Apply(CampaignRuntime runtime)
    {
        var entity = runtime.GetEntity(ActorId);
        if (entity is { } e)
        {
            runtime.World.AdvanceTurn(e);
            runtime.MarkAllHydratedActorsDirty();
            _triggered = runtime.FireTriggers(ActorId, new TriggerEvent(TriggerEvents.OnTurnEnd));
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "TurnAdvance",
        Description = $"{runtime.GetActorName(ActorId)}'s turn ended.{DescribeTriggered()}"
    };

    private string DescribeTriggered() => _triggered switch
    {
        { Count: > 0 } t => $" Triggered: {string.Join(", ", t.Select(x => x.Ability.Name))}.",
        _ => string.Empty
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
        var entity = runtime.GetEntity(ActorId);
        if (entity is { } e && e.Has<ActiveEffectsComponent>())
        {
            e.Get<ActiveEffectsComponent>().Effects.RemoveAll(effect => effect.EffectId == EffectId);
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "StatusEffectRemoved",
        Description = $"{runtime.GetActorName(ActorId)} lost {EffectId}."
    };
}

/// <summary>
/// Adds a participant to the encounter's initiative order if new, or updates their roll if
/// already present (3.5) - the encounter is created lazily on the first roll, since a campaign
/// spends most of its time with no combat happening at all. Ties are broken by
/// <paramref name="TieBreak"/> (a secondary roll or stat), then by actor id for a fully
/// deterministic order - two participants must never silently swap places between renders.
/// </summary>
public sealed record SetInitiativeCommand(string ActorId, int InitiativeRoll, int TieBreak = 0) : IRuntimeCommand
{
    // Doesn't touch ActorDocument.State - initiative order lives on the EncounterDocument, which
    // the runtime persists separately (see CampaignRuntime.PersistEncounterAsync).
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
        var encounter = runtime.GetOrCreateEncounter();
        var participant = encounter.Participants.FirstOrDefault(p => p.ActorId == ActorId);
        if (participant == null)
        {
            participant = new EncounterParticipant { ActorId = ActorId };
            encounter.Participants.Add(participant);
        }

        participant.InitiativeRoll = InitiativeRoll;
        participant.TieBreak = TieBreak;
        CampaignRuntime.SortParticipants(encounter);
        runtime.MarkEncounterDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "Initiative",
        Description = $"{runtime.GetActorName(ActorId)} rolled {InitiativeRoll} for initiative."
    };
}

/// <summary>Moves the encounter to the next participant's turn (3.5), ticking whichever effects
/// are anchored to the participant whose turn just ended - the same duration bookkeeping
/// <see cref="AdvanceTurnCommand"/> does, just driven by turn order instead of a bare actor id.
/// Wraps to a new round (and resets who's acted) once every participant has gone.</summary>
public sealed record NextTurnCommand : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    private IReadOnlyList<TriggeredAbility>? _triggered;

    public void Apply(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        if (encounter == null || encounter.Participants.Count == 0)
        {
            return;
        }

        var current = encounter.Participants[encounter.TurnIndex];
        var currentEntity = runtime.GetEntity(current.ActorId);
        if (currentEntity is { } e)
        {
            runtime.World.AdvanceTurn(e);
            runtime.MarkAllHydratedActorsDirty();
        }

        current.HasActed = true;
        encounter.TurnIndex++;
        if (encounter.TurnIndex >= encounter.Participants.Count)
        {
            encounter.TurnIndex = 0;
            encounter.Round++;
            foreach (var participant in encounter.Participants)
            {
                participant.HasActed = false;
            }
        }

        runtime.MarkEncounterDirty();
        _triggered = runtime.FireTriggers(encounter.Participants[encounter.TurnIndex].ActorId, new TriggerEvent(TriggerEvents.OnTurnStart));
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        var upNext = encounter is { Participants.Count: > 0 }
            ? runtime.GetActorName(encounter.Participants[encounter.TurnIndex].ActorId)
            : "nobody";
        var triggered = _triggered is { Count: > 0 } t
            ? $" Triggered: {string.Join(", ", t.Select(x => x.Ability.Name))}."
            : string.Empty;
        return new SessionEvent { Type = "TurnAdvance", Description = $"Turn passed to {upNext} (round {encounter?.Round ?? 1}).{triggered}" };
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
        }

        runtime.MarkEncounterDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var encounter = runtime.ActiveEncounter;
        var current = encounter is { Participants.Count: > 0 }
            ? runtime.GetActorName(encounter.Participants[encounter.TurnIndex].ActorId)
            : "nobody";
        return new SessionEvent { Type = "TurnAdvance", Description = $"Turn moved back to {current}." };
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
        Description = State switch
        {
            TurnState.Delayed => $"{runtime.GetActorName(ActorId)} delayed their turn.",
            TurnState.Readied => $"{runtime.GetActorName(ActorId)} readied an action.",
            _ => $"{runtime.GetActorName(ActorId)} is acting normally again."
        }
    };
}

/// <summary>
/// Resolves and logs a dice roll (3.6) through whichever <see cref="IDiceRoller"/> the campaign's
/// system plugin provides (<see cref="StandardDiceRoller"/> if it doesn't override one). Doesn't
/// touch ECS state - the roll log lives on the live session, exactly like everything else 3.2
/// already logs - so <see cref="AffectedActorIds"/> is empty.
/// </summary>
public sealed record RollDiceCommand(string Expression, string? ActorId, string RollerUserId, bool IsSecret) : IRuntimeCommand
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
            IsSecret = IsSecret
        });
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var roller = ActorId != null ? runtime.GetActorName(ActorId) : RollerUserId;
        var description = IsSecret
            ? $"{roller} made a secret roll ({Expression})."
            : $"{roller} rolled {Expression}: {_result?.Total}.";
        return new SessionEvent { Type = "DiceRoll", Description = description };
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

/// <summary>Uses an ability through the TRCE pipeline (5.1) inside the single-writer loop, so
/// Requires checks, Cost payment, and Effect application are one atomic step with write-through
/// persistence and a session-log entry - never a half-paid cost with no effect.</summary>
public sealed record UseAbilityCommand(
    IAbilityDefinition Ability,
    AbilityExecutor Executor,
    string CasterActorId,
    string? TargetActorId = null) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } =
        TargetActorId != null ? new[] { CasterActorId, TargetActorId } : new[] { CasterActorId };

    private AbilityExecutionResult? _result;

    public void Apply(CampaignRuntime runtime)
    {
        var caster = runtime.GetEntity(CasterActorId);
        if (caster is not { } c)
        {
            _result = AbilityExecutionResult.Failed(AbilityExecutor.RequiresStage, $"Unknown caster '{CasterActorId}'.");
            return;
        }

        var target = TargetActorId != null ? runtime.GetEntity(TargetActorId) : null;
        _result = Executor.Execute(Ability, new AbilityExecutionContext(runtime.World, c, target));
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var casterName = runtime.GetActorName(CasterActorId);
        if (_result?.Success == true)
        {
            var targetName = TargetActorId != null ? $" on {runtime.GetActorName(TargetActorId)}" : string.Empty;
            return new SessionEvent { Type = "AbilityUsed", Description = $"{casterName} used {Ability.Name}{targetName}." };
        }

        return new SessionEvent { Type = "AbilityFailed", Description = $"{casterName} tried {Ability.Name} but failed: {_result?.FailureReason ?? "unknown reason"}." };
    }
}

/// <summary>Uses an ability by full id (the combat console's "cast" buttons), resolving the
/// definition inside the single-writer loop so the UI never touches ECS state directly. On a
/// successful targeted use, fires <c>OnHit</c> triggers for both caster and target - once, at
/// this level only, so triggered effects can't recurse back into another <c>OnHit</c>.</summary>
public sealed record UseAbilityByIdCommand(string AbilityFullId, string CasterActorId, string? TargetActorId = null) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } =
        TargetActorId != null ? new[] { CasterActorId, TargetActorId } : new[] { CasterActorId };

    private AbilityExecutionResult? _result;
    private string _abilityName = AbilityFullId;
    private IReadOnlyList<TriggeredAbility>? _triggered;

    public void Apply(CampaignRuntime runtime)
    {
        var ability = runtime.ContentRegistry?.GetAbility(AbilityFullId);
        if (ability != null)
        {
            _abilityName = ability.Name;
        }

        _result = runtime.TryExecuteAbility(AbilityFullId, CasterActorId, TargetActorId);
        if (_result?.Success == true && TargetActorId != null)
        {
            var hit = new TriggerEvent(TriggerEvents.OnHit);
            _triggered = runtime.FireTriggers(CasterActorId, hit, TargetActorId)
                .Concat(runtime.FireTriggers(TargetActorId, hit, CasterActorId))
                .ToList();
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var casterName = runtime.GetActorName(CasterActorId);
        if (_result?.Success == true)
        {
            var targetName = TargetActorId != null ? $" on {runtime.GetActorName(TargetActorId)}" : string.Empty;
            var triggered = _triggered is { Count: > 0 } t
                ? $" Triggered: {string.Join(", ", t.Select(x => x.Ability.Name))}."
                : string.Empty;
            return new SessionEvent { Type = "AbilityUsed", Description = $"{casterName} used {_abilityName}{targetName}.{triggered}" };
        }

        return new SessionEvent { Type = "AbilityFailed", Description = $"{casterName} tried {_abilityName} but failed: {_result?.FailureReason ?? "ability pipeline is not wired for this campaign"}." };
    }
}

/// <summary>Hydrates an already-persisted <see cref="ActorDocument"/> into the already-running
/// runtime (3.7 "quick-add monsters from packs") - <see cref="CampaignRuntime.Hydrate"/> itself
/// only runs once, at startup, before any command is enqueued; a monster added mid-session has to
/// go through the same single-writer loop as everything else.</summary>
public sealed record HydrateActorCommand(ActorDocument Actor) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { Actor.Id };

    public void Apply(CampaignRuntime runtime) => runtime.HydrateOne(Actor);

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "ActorJoined",
        Description = $"{Actor.Name} joined the encounter."
    };
}
