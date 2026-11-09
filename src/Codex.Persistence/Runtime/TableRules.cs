using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using DefaultEcs;

namespace Codex.Persistence.Runtime;

/// <summary>
/// The table's vitals rules, as <c>system.yaml</c>'s <c>vitals:</c> section describes them: damage
/// types against a creature's resistances, what reaching zero does, death saves, temporary hit
/// points, exhaustion levels, hit dice and rests. Everything here runs on the runtime's command
/// loop and mutates the entity it is handed; the commands call in and describe what came back.
/// A system without <see cref="VitalsRules"/> gets the plain behaviour: damage stops at zero.
/// </summary>
internal static class TableRules
{
    public static string PrimaryKey(CampaignRuntime runtime) => SystemDefinition.PrimaryKeyOf(runtime.System);
    public static string PrimaryMaxKey(CampaignRuntime runtime) => MaxKeyOf(runtime, PrimaryKey(runtime));
    public static string TemporaryKey(CampaignRuntime runtime) => SystemDefinition.TemporaryKeyOf(runtime.System);

    /// <summary>The one rule for where a pool's maximum lives (R9): <see cref="SystemDefinition.MaxKeyOf"/>.</summary>
    public static string MaxKeyOf(CampaignRuntime runtime, string key) => SystemDefinition.PoolMaxKey(runtime.System, key);

    // ---- The vitals handlers' view and intents ----

    /// <summary>The creature as <see cref="VitalsHandlers"/> read it; null when it has no pools, which the rules do not touch.</summary>
    private static VitalsView? View(CampaignRuntime runtime, string actorId, Entity entity)
    {
        if (!entity.Has<ResourcePoolComponent>()) return null;
        var doc = runtime.GetActorDocument(actorId);
        var conditions = entity.Has<ActiveEffectsComponent>() ? entity.Get<ActiveEffectsComponent>().Effects.Select(e => e.EffectId).ToList() : new List<string>();
        return new VitalsView(new Dictionary<string, int>(entity.Get<ResourcePoolComponent>().Pools), conditions, doc?.Kind == ActorKind.PlayerCharacter, doc?.Sheet);
    }

    /// <summary>Applies what a vitals handler returned to the creature it read, in order: pools, the table's tallies,
    /// conditions, a kill, an end of concentration. A roll it asks for is the caller's to say.</summary>
    internal static void Apply(CampaignRuntime runtime, string actorId, Entity entity, IEnumerable<SheetIntent> intents)
    {
        foreach (var intent in intents)
        {
            var fields = intent.Fields;
            switch (intent.Kind)
            {
                case "set" when fields.GetValueOrDefault("track")?.ToString() is { } track && YamlValues.ToNumber(fields.GetValueOrDefault("to")) is { } to:
                    Pools(entity).Set(track, (int)to);
                    break;
                case VitalsHandlers.CounterKind:
                    Counter(entity, fields);
                    break;
                case "condition" when fields.GetValueOrDefault("add")?.ToString() is { Length: > 0 } add:
                    runtime.World.AddStatus(entity, add, fields.GetValueOrDefault("source")?.ToString() ?? "rules", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actorId);
                    break;
                case "condition" when fields.GetValueOrDefault("remove")?.ToString() is { Length: > 0 } remove:
                    runtime.World.RemoveStatus(entity, remove);
                    break;
                case VitalsHandlers.KillKind:
                    Kill(runtime, entity, actorId);
                    break;
                case VitalsHandlers.EndConcentrationKind:
                    EndConcentration(runtime, actorId, entity);
                    break;
            }
        }
    }

    /// <summary><c>counter { key, to } | { key, by } | { key, clear: true }</c>: a tally the table keeps in the creature's pools,
    /// beside the sheet.</summary>
    internal static void Counter(Entity entity, IReadOnlyDictionary<string, object?> fields)
    {
        if (fields.GetValueOrDefault("key")?.ToString() is not { Length: > 0 } key) return;
        var pool = Pools(entity);
        if (fields.GetValueOrDefault("clear") is true) pool.Pools.Remove(key);
        else if (YamlValues.ToNumber(fields.GetValueOrDefault("to")) is { } to) pool.Set(key, (int)to);
        else if (YamlValues.ToNumber(fields.GetValueOrDefault("by")) is { } by) pool.Modify(key, (int)by);
    }

    private static ResourcePoolComponent Pools(Entity entity)
    {
        if (!entity.Has<ResourcePoolComponent>()) entity.Set(new ResourcePoolComponent());
        return entity.Get<ResourcePoolComponent>();
    }

    private static T Run<T>(CampaignRuntime runtime, string actorId, Entity entity, VitalsResult<T> result)
    {
        Apply(runtime, actorId, entity, result.Intents);
        return result.Report;
    }

    // ---- Damage ----

    /// <summary>Damage by the vitals rules (<see cref="VitalsHandlers.Damage"/>), applied to the creature.</summary>
    public static DamageReport ApplyDamage(CampaignRuntime runtime, string actorId, Entity entity, string poolName, int amount, string? damageType, bool isCritical) =>
        View(runtime, actorId, entity) is { } view
            ? Run(runtime, actorId, entity, VitalsHandlers.Damage(runtime.System, view, poolName, amount, damageType, isCritical))
            : DamageReport.Nothing;

    // ---- Healing and temporary hit points ----

    public sealed record HealOutcome(int Healed, bool WasDead, bool Revived);

    /// <summary>Heals, clamped to the maximum. A dead creature is not healed - the DM removes the dead
    /// condition first (a revival spell) - and a creature brought above zero is back on its feet:
    /// the down and stable conditions go and the death-save tally resets.</summary>
    public static HealOutcome ApplyHealing(CampaignRuntime runtime, string actorId, Entity entity, string poolName, int amount)
    {
        if (amount <= 0 || !entity.Has<ResourcePoolComponent>())
        {
            return new HealOutcome(0, false, false);
        }

        if (IsDead(runtime, entity))
        {
            return new HealOutcome(0, true, false);
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var before = pool.Get(poolName);
        runtime.World.ApplyHealing(entity, poolName, amount, key => MaxKeyOf(runtime, key));
        var after = pool.Get(poolName);

        var revived = poolName == PrimaryKey(runtime) && before <= 0 && after > 0 && GetUp(runtime, entity);
        return new HealOutcome(after - before, false, revived);
    }

    /// <summary>Temporary hit points don't stack: the creature keeps the higher of what it has and what it is offered.</summary>
    public static bool GrantTemporary(CampaignRuntime runtime, Entity entity, int amount)
    {
        if (amount <= 0 || !entity.Has<ResourcePoolComponent>())
        {
            return false;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var key = TemporaryKey(runtime);
        if (pool.Get(key) >= amount)
        {
            return false;
        }

        pool.Set(key, amount);
        return true;
    }

    // ---- Death saves ----

    /// <summary>Rolls a death save with the table's dice and records it (<see cref="VitalsHandlers.RollDeathSave"/>).</summary>
    public static DeathSaveReport RollDeathSave(CampaignRuntime runtime, string actorId, Entity entity) =>
        View(runtime, actorId, entity) is { } view
            ? Run(runtime, actorId, entity, VitalsHandlers.RollDeathSave(runtime.System, view, die => runtime.DiceRoller.Roll(die).Total))
            : DeathSaveReport.None;

    /// <summary>Records a death save the player rolled themselves (or a hit while down).</summary>
    public static DeathSaveReport RecordDeathSave(CampaignRuntime runtime, string actorId, Entity entity, bool success, int count) =>
        View(runtime, actorId, entity) is { } view
            ? Run(runtime, actorId, entity, VitalsHandlers.RecordDeathSave(runtime.System, view, success, count))
            : DeathSaveReport.None;

    /// <summary>A creature at zero that is no longer dying: still unconscious, no more saves.</summary>
    public static bool Stabilize(CampaignRuntime runtime, string actorId, Entity entity) =>
        View(runtime, actorId, entity) is { } view && Run(runtime, actorId, entity, VitalsHandlers.Stabilize(runtime.System, view));

    // ---- Exhaustion ----

    /// <summary>Moves the exhaustion level by <paramref name="delta"/> within 0 and the maximum; reaching the maximum kills.</summary>
    public static (int Level, bool Died)? AdjustExhaustion(CampaignRuntime runtime, string actorId, Entity entity, int delta) =>
        View(runtime, actorId, entity) is { } view
            ? Run(runtime, actorId, entity, VitalsHandlers.AdjustExhaustion(runtime.System, view, delta))
            : null;

    // ---- Hit dice ----

    public sealed record HitDiceOutcome(int Spent, int Healed, IReadOnlyList<int> Rolls, int Modifier);

    /// <summary>Spends up to <paramref name="count"/> hit dice of <paramref name="dieSides"/> sides: each die plus the
    /// attribute modifier (never below zero per die) heals the primary resource.</summary>
    public static HitDiceOutcome? SpendHitDice(CampaignRuntime runtime, string actorId, Entity entity, int count, int dieSides)
    {
        if (runtime.System?.Vitals?.HitDice is not { } rules || count <= 0 || dieSides < 2 || !entity.Has<ResourcePoolComponent>())
        {
            return null;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        var available = pool.Get(rules.Key);
        var spend = Math.Min(count, available);
        if (spend <= 0)
        {
            return new HitDiceOutcome(0, 0, Array.Empty<int>(), 0);
        }

        var modifier = AttributeModifier(runtime, actorId, entity, rules.Attribute);
        var rolls = new List<int>();
        var healed = 0;
        for (var i = 0; i < spend; i++)
        {
            var roll = runtime.DiceRoller.Roll($"1d{dieSides}").Total;
            rolls.Add(roll);
            healed += Math.Max(0, roll + modifier);
        }

        pool.Set(rules.Key, available - spend);
        var outcome = ApplyHealing(runtime, actorId, entity, PrimaryKey(runtime), healed);
        return new HitDiceOutcome(spend, outcome.Healed, rolls, modifier);
    }

    // ---- Rests ----

    /// <summary>Applies a named rest (<see cref="VitalsHandlers.Rest"/>): the slot pools the caller passes, then whatever the
    /// rest rule says. Returns what happened, in words, for the log.</summary>
    public static List<EventMessage> Rest(CampaignRuntime runtime, string actorId, Entity entity, string restName, IEnumerable<string> slotPools)
    {
        var pools = slotPools.ToList();
        if (View(runtime, actorId, entity) is not { } view)
        {
            return pools.Count > 0 ? new List<EventMessage> { EventMessage.Of("Rest.Slots") } : new List<EventMessage>();
        }

        return Run(runtime, actorId, entity, VitalsHandlers.Rest(runtime.System, view, restName, pools)).Select(step => step.Kind switch
        {
            "slots" => EventMessage.Of("Rest.Slots"),
            "restored" => EventMessage.Of("Rest.Restored", LogTerms.Resource(runtime, step.Key!)),
            "cleared" => EventMessage.Of("Rest.Cleared", LogTerms.Resource(runtime, step.Key!)),
            "hitDice" => EventMessage.Of("Rest.HitDice", step.Amount),
            "exhaustionGone" => EventMessage.Of("Rest.ExhaustionGone"),
            "exhaustionNow" => EventMessage.Of("Rest.ExhaustionNow", step.Amount),
            _ => EventMessage.Of("Rest.Lifted", EventTerm.Condition(step.Key!))
        }).ToList();
    }

    // ---- State queries ----

    public static bool IsDead(CampaignRuntime runtime, Entity entity) =>
        runtime.System?.Vitals?.Down is { } down && CodexWorld.HasStatus(entity, down.DeadCondition);

    /// <summary>At zero on the primary resource and not dead.</summary>
    public static bool IsDown(CampaignRuntime runtime, Entity entity) =>
        entity.Has<ResourcePoolComponent>() && entity.Get<ResourcePoolComponent>().Get(PrimaryKey(runtime)) <= 0 && !IsDead(runtime, entity);

    /// <summary>The attribute's modifier for a creature: from its live attributes, else its sheet, else the default.</summary>
    public static int AttributeModifier(CampaignRuntime runtime, string actorId, Entity entity, string attributeKey)
    {
        var definition = runtime.System?.Attributes.FirstOrDefault(a => a.Key == attributeKey);
        if (definition == null)
        {
            return 0;
        }

        int? score = entity.Has<AttributesComponent>() && entity.Get<AttributesComponent>().Values.TryGetValue(attributeKey, out var live) ? live : null;
        score ??= runtime.GetActorDocument(actorId) is { } doc ? YamlValues.GetInt(doc.Sheet, attributeKey) : null;
        return definition.ModifierFor(score ?? definition.Default) ?? 0;
    }

    /// <summary>Death, by the rules or by the DM's hand (R3): the dead condition, no longer down or stable, the death-save
    /// tally gone, and what hung on this creature ends with it (R4): its concentration, and the timed effects counted on its
    /// turns, which will not come round again. Returns false when it was already dead or the system has no death.</summary>
    public static bool Kill(CampaignRuntime runtime, Entity entity, string actorId)
    {
        if (runtime.System?.Vitals?.Down is not { } down || IsDead(runtime, entity))
        {
            return false;
        }

        runtime.World.RemoveStatus(entity, down.Condition);
        runtime.World.RemoveStatus(entity, down.StableCondition);
        ResetDeathSaves(runtime, entity);
        EndConcentration(runtime, actorId, entity);
        ExpireTurnsOf(runtime, actorId);
        runtime.World.AddStatus(entity, down.DeadCondition, "died", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actorId);
        return true;
    }

    /// <summary>Back from the dead (a revival spell, or a death marked by mistake), R3: no longer dead, down or stable, the
    /// death-save tally reset, and on at least one hit point, so the next hit does not finish a creature still carrying two
    /// failed saves. Returns false when it was not dead.</summary>
    public static bool Revive(CampaignRuntime runtime, Entity entity)
    {
        if (runtime.System?.Vitals?.Down is not { } down || !IsDead(runtime, entity))
        {
            return false;
        }

        runtime.World.RemoveStatus(entity, down.DeadCondition);
        GetUp(runtime, entity);
        if (entity.Has<ResourcePoolComponent>())
        {
            var pool = entity.Get<ResourcePoolComponent>();
            pool.Set(PrimaryKey(runtime), Math.Max(1, pool.Get(PrimaryKey(runtime))));
        }

        return true;
    }

    /// <summary>Ends the timed effects counted on <paramref name="actorId"/>'s turns, on every creature: it died or left the
    /// order, so those turns will not come (R4). Returns how many ended.</summary>
    public static int ExpireTurnsOf(CampaignRuntime runtime, string actorId)
    {
        var ended = runtime.World.ExpireAnchoredTo(actorId).Count;
        if (ended > 0)
        {
            runtime.MarkAllHydratedActorsDirty();
        }

        return ended;
    }

    // ---- Concentration ----

    public static bool IsConcentrating(CampaignRuntime runtime, Entity entity) =>
        runtime.System?.Vitals?.Concentration is { } rules && CodexWorld.HasStatus(entity, rules.Condition);

    /// <summary>The condition a concentrating creature carries, or null when the system has no concentration.</summary>
    public static string? ConcentrationCondition(CampaignRuntime runtime) => runtime.System?.Vitals?.Concentration?.Condition;

    /// <summary>Ends <paramref name="actorId"/>'s concentration: the condition goes, and so does every effect it held up, on
    /// whichever creature carries it (R4). Returns how many held effects ended.</summary>
    public static int EndConcentration(CampaignRuntime runtime, string actorId, Entity entity)
    {
        if (ConcentrationCondition(runtime) is { } condition)
        {
            runtime.World.RemoveStatus(entity, condition);
        }

        var ended = runtime.World.EndHeldBy(actorId).Count;
        if (ended > 0)
        {
            runtime.MarkAllHydratedActorsDirty();
        }

        return ended;
    }

    /// <summary>Back above zero: no longer down or stable, death saves reset. Returns whether anything changed.</summary>
    private static bool GetUp(CampaignRuntime runtime, Entity entity)
    {
        if (runtime.System?.Vitals?.Down is not { } down)
        {
            return false;
        }

        var removed = runtime.World.RemoveStatus(entity, down.Condition) + runtime.World.RemoveStatus(entity, down.StableCondition);
        ResetDeathSaves(runtime, entity);
        return removed > 0;
    }

    private static void ResetDeathSaves(CampaignRuntime runtime, Entity entity)
    {
        if (runtime.System?.Vitals?.DeathSaves is not { } rules || !entity.Has<ResourcePoolComponent>())
        {
            return;
        }

        var pool = entity.Get<ResourcePoolComponent>();
        pool.Pools.Remove(rules.SuccessesKey);
        pool.Pools.Remove(rules.FailuresKey);
    }
}
