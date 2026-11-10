using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using DefaultEcs;

namespace Codex.Persistence.Runtime;

/// <summary>
/// The table's vitals rules, as <c>system.yaml</c>'s <c>vitals:</c> section describes them: damage
/// types against a creature's resistances, what reaching zero does, death saves, temporary hit
/// points, exhaustion levels, hit dice and rests. The rules are <see cref="VitalsHandlers"/>, which return intents; this
/// reads the creature for them, applies what they return to the entity on the runtime's command loop, and carries out the
/// intents only the table can (a kill, an end of concentration). The commands call in and describe what came back.
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
    /// <param name="withoutPools">Reads a creature with no pools too (reviving changes only its conditions then).</param>
    private static VitalsView? View(CampaignRuntime runtime, string actorId, Entity entity, bool withoutPools = false)
    {
        var hasPools = entity.Has<ResourcePoolComponent>();
        if (!hasPools && !withoutPools) return null;
        var doc = runtime.GetActorDocument(actorId);
        var conditions = entity.Has<ActiveEffectsComponent>() ? entity.Get<ActiveEffectsComponent>().Effects.Select(e => e.EffectId).ToList() : new List<string>();
        var pools = hasPools ? new Dictionary<string, int>(entity.Get<ResourcePoolComponent>().Pools) : new Dictionary<string, int>();
        return new VitalsView(pools, conditions, doc?.Kind == ActorKind.PlayerCharacter, doc?.Sheet, hasPools);
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

    /// <summary>Healing by the vitals rules (<see cref="VitalsHandlers.Heal"/>), applied to the creature.</summary>
    public static HealReport ApplyHealing(CampaignRuntime runtime, string actorId, Entity entity, string poolName, int amount) =>
        View(runtime, actorId, entity) is { } view
            ? Run(runtime, actorId, entity, VitalsHandlers.Heal(runtime.System, view, poolName, amount))
            : HealReport.Nothing;

    /// <summary>Temporary hit points by the vitals rules (<see cref="VitalsHandlers.GrantTemporary"/>): they do not stack.</summary>
    public static bool GrantTemporary(CampaignRuntime runtime, string actorId, Entity entity, int amount) =>
        View(runtime, actorId, entity) is { } view && Run(runtime, actorId, entity, VitalsHandlers.GrantTemporary(runtime.System, view, amount));

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

    /// <summary>Spends hit dice by the vitals rules (<see cref="VitalsHandlers.SpendHitDice"/>), rolled with the table's dice and
    /// the creature's attribute modifier.</summary>
    public static HitDiceReport? SpendHitDice(CampaignRuntime runtime, string actorId, Entity entity, int count, int dieSides)
    {
        if (runtime.System?.Vitals?.HitDice is not { } rules || View(runtime, actorId, entity) is not { } view) return null;
        var modifier = AttributeModifier(runtime, actorId, entity, rules.Attribute);
        return Run(runtime, actorId, entity, VitalsHandlers.SpendHitDice(runtime.System, view, count, dieSides, modifier,
            die => runtime.DiceRoller.Roll(die).Total));
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

    /// <summary>Back from the dead (a revival spell, or a death marked by mistake), R3, by <see cref="VitalsHandlers.Revive"/>.
    /// Returns false when it was not dead.</summary>
    public static bool Revive(CampaignRuntime runtime, string actorId, Entity entity) =>
        View(runtime, actorId, entity, withoutPools: true) is { } view && Run(runtime, actorId, entity, VitalsHandlers.Revive(runtime.System, view));

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
