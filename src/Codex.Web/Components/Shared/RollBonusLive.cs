using Codex.Core.Rules;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>
/// What a ticked roll bonus costs, taken where the actor is live at a table (docs/ROLL_BONUSES_DESIGN.md): the spend goes through the same
/// command a table card uses, and the sheet page takes the table's number as its own, so both views and the next save agree.
/// An opt-in's <c>ends</c> removes a condition only when the actor has it.
/// </summary>
public static class RollBonusLive
{
    /// <summary>Spends (or regains) a counter at the live table through <see cref="AdjustResourceCommand"/>, as a table card does. True only when the
    /// runtime spent it (not refused for too little left, and the table still open). When it did, the table's number for the counter is written into
    /// each of <paramref name="sheets"/> (the sheet being edited, as loaded, and the merge baseline), so the next save does not spend it a second time.</summary>
    public static async Task<bool> SpendAsync(CampaignRuntime live, string actorId, (string Key, int Delta, string Label) change, params IDictionary<string, object>[] sheets)
    {
        var command = new AdjustResourceCommand(actorId, change.Key, change.Delta, change.Label);
        try
        {
            await live.EnqueueAsync(command);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }

        if (!command.Succeeded) return false;
        if (live.Snapshot.Actor(actorId)?.Pool(change.Key) is { } value)
        {
            foreach (var sheet in sheets) sheet[change.Key] = (long)value;
        }

        return true;
    }

    /// <summary>The conditions the applied opt-ins end that the actor has at the table (as the table names them), each once.</summary>
    public static IReadOnlyList<string> ToEnd(IEnumerable<RollOptIn> applied, ActorStateSnapshot? state)
    {
        var ended = new List<string>();
        if (state == null) return ended;
        foreach (var name in applied.Select(o => o.Ends).OfType<string>())
        {
            var held = state.Effects.FirstOrDefault(e => string.Equals(e.EffectId, name, StringComparison.OrdinalIgnoreCase));
            if (held != null && !ended.Contains(held.EffectId, StringComparer.OrdinalIgnoreCase)) ended.Add(held.EffectId);
        }

        return ended;
    }

    /// <summary>Removes the conditions the applied opt-ins end from the live table's actor, through the table's own command. Returns how many it took off.</summary>
    public static async Task<int> EndAtTableAsync(CampaignRuntime live, string actorId, IEnumerable<RollOptIn> applied)
    {
        var count = 0;
        foreach (var condition in ToEnd(applied, live.Snapshot.Actor(actorId)))
        {
            await live.EnqueueAsync(new RemoveStatusEffectCommand(actorId, condition));
            count++;
        }

        return count;
    }

    /// <summary>Removes the conditions the applied opt-ins end from the sheet's own conditions (off the table), the way any handler's
    /// <c>condition remove</c> does; one the sheet does not have is left alone. True when the sheet's conditions changed.</summary>
    public static bool EndOnSheet(SystemDefinition system, IEntryResolver resolver, Dictionary<string, object> sheet, IEnumerable<RollOptIn> applied, string? actorId, ILinkedSheets? linked = null)
    {
        var intents = applied.Select(o => o.Ends).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new SheetIntent("condition", null, new Dictionary<string, object?> { ["remove"] = name }))
            .ToList();
        if (intents.Count == 0) return false;
        return SheetIntents.Apply(system, resolver, sheet, intents, actorId, linked).Changed.Contains(SheetModel.ConditionsKey);
    }
}
