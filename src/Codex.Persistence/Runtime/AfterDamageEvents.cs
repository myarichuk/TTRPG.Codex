using Codex.Core.Components;
using Codex.Core.Rules;

namespace Codex.Persistence.Runtime;

/// <summary>
/// The <c>damaged</c> and <c>healed</c> events: raised after the table's own vitals rules took damage or healing, so a system can
/// follow a blow with rules of its own (a wound, a condition at half hit points) without replacing the built-in damage the way a
/// <c>damage</c> or <c>heal</c> handler does. Raised only when the system declares a handler for the name.
/// </summary>
internal static class AfterDamageEvents
{
    /// <summary>The pool's number now; 0 when the creature has none.</summary>
    public static int Pool(CampaignRuntime runtime, string actorId, string key) =>
        runtime.GetEntity(actorId) is { } e && e.Has<ResourcePoolComponent>() ? e.Get<ResourcePoolComponent>().Get(key) : 0;

    /// <summary>Raises <c>damaged</c> when something was dealt and the system handles it; null otherwise.
    /// <c>before</c> is the pool before the blow, <c>after</c> and <c>max</c> are read now. <paramref name="save"/> is the outcome key
    /// of the creature's own save when the damage came from one.</summary>
    public static TableEvents.Outcome? Damaged(CampaignRuntime runtime, string actorId, string pool, int before, int amount, int dealt, int absorbed,
        bool critical, string? save, string source, string? damageType, bool wentDown, bool died)
    {
        if (dealt <= 0 || !TableEvents.Handles(runtime, "damaged")) return null;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = (double)amount,
            ["dealt"] = (double)dealt,
            ["absorbed"] = (double)absorbed,
            ["critical"] = critical,
            ["source"] = source,
            ["pool"] = pool,
            ["before"] = (double)before,
            ["after"] = (double)Pool(runtime, actorId, pool),
            ["max"] = (double)Pool(runtime, actorId, TableRules.MaxKeyOf(runtime, pool)),
            ["wentDown"] = wentDown,
            ["died"] = died
        };
        if (!string.IsNullOrWhiteSpace(save)) fields["save"] = save;
        if (!string.IsNullOrWhiteSpace(damageType)) fields["damageType"] = damageType;
        return TableEvents.Raise(runtime, actorId, new SheetEvent("damaged", fields));
    }

    /// <summary>Raises <c>healed</c> when something was healed on a creature that was not dead and the system handles it; null otherwise.</summary>
    public static TableEvents.Outcome? Healed(CampaignRuntime runtime, string actorId, string pool, int before, int amount, HealReport report, string source)
    {
        if (report.Healed <= 0 || report.WasDead || !TableEvents.Handles(runtime, "healed")) return null;
        return TableEvents.Raise(runtime, actorId, new SheetEvent("healed", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = (double)amount,
            ["healed"] = (double)report.Healed,
            ["source"] = source,
            ["pool"] = pool,
            ["before"] = (double)before,
            ["after"] = (double)Pool(runtime, actorId, pool),
            ["max"] = (double)Pool(runtime, actorId, TableRules.MaxKeyOf(runtime, pool)),
            ["revived"] = report.Revived
        }));
    }
}
