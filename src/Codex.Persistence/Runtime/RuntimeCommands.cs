using Codex.Core.Components;

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

    public void Apply(CampaignRuntime runtime)
    {
        var entity = runtime.GetEntity(ActorId);
        if (entity is { } e)
        {
            runtime.World.AdvanceTurn(e);
            runtime.MarkAllHydratedActorsDirty();
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime) => new()
    {
        Type = "TurnAdvance",
        Description = $"{runtime.GetActorName(ActorId)}'s turn ended."
    };
}
