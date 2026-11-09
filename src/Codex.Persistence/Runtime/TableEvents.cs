using System.Collections;
using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using DefaultEcs;

namespace Codex.Persistence.Runtime;

/// <summary>
/// The live table raising a system's declared events (overhaul step 6, "events → intents"): damage, healing, a turn starting
/// or ending, a rest, or any other event a sheet kind's system handles. The handler runs against the actor's sheet as the
/// table has it now (the cached document with the live pools and conditions laid over it); the intents it returns are applied
/// by <see cref="SheetIntents"/> as on the sheet page, then written back where the table keeps each thing:
/// <list type="bullet">
/// <item>numeric tracks in the actor's <see cref="ResourcePoolComponent"/>;</item>
/// <item>slot tracks and other sheet keys on the cached document, persisted key by key (<see cref="CampaignRuntime.SheetKeyWritten"/>);</item>
/// <item>conditions added or removed as table statuses, and on the sheet's own list.</item>
/// </list>
/// What the sheet leaves for the table is done here too: another actor's direct intents are applied to that actor (one level
/// deep: what they leave is said, not followed), and rolls, questions and log lines go to the session log.
/// <para>Intents that name a clock or a campaign table change the campaign's objects the table holds
/// (<see cref="CampaignRuntime.CampaignObjectState"/>), written back key by key; an event with no actor
/// (<see cref="RaiseOnCampaign"/>) is a campaign's own: a clock ticked from the table.</para>
/// </summary>
internal static class TableEvents
{
    /// <summary>Whether this runtime's system has sheet kinds and handles <paramref name="type"/> itself.</summary>
    public static bool Handles(CampaignRuntime runtime, string type) =>
        runtime.System is { SheetKinds.Count: > 0 } system && system.Events.ContainsKey(type);

    /// <summary>What raising an event did, for the session log.</summary>
    public sealed record Outcome(IReadOnlyList<EventMessage> Messages, IReadOnlyCollection<string> Touched, string? Failed = null)
    {
        public static readonly Outcome None = new(Array.Empty<EventMessage>(), Array.Empty<string>());
    }

    /// <param name="dice">Where a script handler's <c>ctx.d</c> rolls (null: at random); the faces it rolled are logged.</param>
    public static Outcome Raise(CampaignRuntime runtime, string actorId, SheetEvent ev, IDiceSource? dice = null)
    {
        var rolled = new RecordingDice(dice ?? RandomDiceSource.Shared);
        return Change(runtime, actorId, ev.Type, (system, live, messages) =>
        {
            var raised = SheetEvents.Raise(system, runtime.Resolver, live, ev, actorId, runtime.CampaignObjectState, rolled);
            if (rolled.Said is { } faces) messages.Add(EventMessage.Of("Sheet.ScriptRolled", runtime.ActorTerm(actorId), Term(system, ev.Type), faces));
            Campaign(runtime, system, raised.Campaign, messages);
            return raised.Applied;
        });
    }

    /// <summary>The faces a script handler rolled, in order, so the log replays the dice actually rolled (overhaul section 4,
    /// rule 3).</summary>
    private sealed class RecordingDice(IDiceSource inner) : IDiceSource
    {
        private readonly List<string> _faces = new();

        public int Roll(int sides)
        {
            var face = inner.Roll(sides);
            _faces.Add($"d{sides}: {face}");
            return face;
        }

        public string? Said => _faces.Count == 0 ? null : string.Join(", ", _faces);
    }

    /// <summary>Raises an event that is the campaign's, on no sheet: its handlers read only the event, and what they ask of
    /// actors is done to those actors.</summary>
    public static Outcome RaiseOnCampaign(CampaignRuntime runtime, SheetEvent ev, IDiceSource? dice = null)
    {
        if (runtime.System is not { } system) return Outcome.None;
        var messages = new List<EventMessage>();
        var touched = new List<string>();
        var rolled = new RecordingDice(dice ?? RandomDiceSource.Shared);
        SheetEventResult raised;
        try
        {
            raised = SheetEvents.Raise(system, runtime.Resolver, new Dictionary<string, object>(), ev, null, runtime.CampaignObjectState, rolled);
            if (rolled.Said is { } faces) messages.Add(EventMessage.Of("Sheet.ScriptRolled", system.Name, Term(system, ev.Type), faces));
        }
        catch (Exception ex) when (ex is SheetEventException or FormulaException)
        {
            return new Outcome(new[] { EventMessage.Of("Campaign.Failed", Term(system, ev.Type), ex.Message) }, touched, ex.Message);
        }

        Campaign(runtime, system, raised.Campaign, messages);
        Follow(runtime, system, null, raised.Applied, messages, touched);
        return new Outcome(messages, touched);
    }

    /// <summary>Writes back what the campaign's intents changed, and says it: each clock or row that moved, by its number.</summary>
    private static void Campaign(CampaignRuntime runtime, SystemDefinition system, CampaignIntentResult? result, List<EventMessage> messages)
    {
        if (result == null) return;
        foreach (var key in result.Changed) runtime.CampaignKeyWritten(key);
        var clocks = CampaignObjects.Clocks(runtime.CampaignObjectState);
        foreach (var (key, name, field, was, now) in result.Changes)
        {
            if (key == CampaignObjects.ClocksKey)
            {
                var clock = clocks.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                messages.Add(EventMessage.Of("Campaign.ClockChanged", ClockTerm(clock, name), was, now, clock?.Segments ?? 0));
            }
            else
            {
                var table = CampaignObjects.TableOf(system, key);
                var named = table?.Visibility == CampaignObjects.GmOnly ? EventTerm.GmOnly(name) : (object)name;
                messages.Add(EventMessage.Of("Campaign.RowChanged", Term(system, table?.Label ?? key), named, Term(system, field ?? ""), was, now));
            }
        }
    }

    // A clock's name as the log says it: marked for the GM alone when the clock is kept for the GM.
    private static object ClockTerm(CampaignClock? clock, string name) =>
        clock != null && string.Equals(clock.Visibility, CampaignObjects.GmOnly, StringComparison.OrdinalIgnoreCase) ? EventTerm.GmOnly(name) : name;

    /// <summary>Makes a roll from the system's definitions on the actor's live sheet and applies what it asks (a cost, a mark).
    /// <paramref name="made"/> receives the roll, for the roll log; null when the actor or the system is not there.</summary>
    public static Outcome Roll(CampaignRuntime runtime, string actorId, SheetRollRequest request, IDiceSource dice, out SheetRollResult? made)
    {
        SheetRollResult? rolled = null;
        var outcome = Change(runtime, actorId, request.Definition, (system, live, messages) =>
        {
            rolled = SheetRolls.Roll(system, runtime.Resolver, live, request, dice);
            var all = rolled.AllIntents().ToList();
            var applied = SheetIntents.Apply(system, runtime.Resolver, live, all.Where(i => !CampaignObjects.IsFor(i)), actorId);
            var onCampaign = CampaignObjects.Apply(system, runtime.CampaignObjectState, all.Where(CampaignObjects.IsFor));
            Campaign(runtime, system, onCampaign, messages);
            applied.Left.AddRange(onCampaign.Left);
            applied.Left.AddRange(onCampaign.Raised);
            return applied;
        });
        made = rolled;
        return outcome;
    }

    /// <summary>Runs <paramref name="act"/> on the actor's live sheet, writes back what changed, and does what is left for the
    /// table. A handler or roll that cannot run is said in the log and changes nothing.</summary>
    private static Outcome Change(CampaignRuntime runtime, string actorId, string what, Func<SystemDefinition, Dictionary<string, object>, List<EventMessage>, SheetIntentResult> act)
    {
        if (runtime.System is not { } system || runtime.GetEntity(actorId) is not { } entity || runtime.GetActorDocument(actorId) is not { } doc)
        {
            return Outcome.None;
        }

        var messages = new List<EventMessage>();
        var touched = new List<string> { actorId };
        SheetIntentResult result;
        try
        {
            result = Apply(runtime, system, actorId, entity, doc, live => act(system, live, messages), messages);
        }
        catch (Exception ex) when (ex is SheetEventException or RollException or FormulaException)
        {
            return new Outcome(new[] { EventMessage.Of("Sheet.Failed", runtime.ActorTerm(actorId), what, ex.Message) }, touched, ex.Message);
        }

        Follow(runtime, system, actorId, result, messages, touched);
        return new Outcome(messages, touched);
    }

    /// <summary>What a sheet (or the campaign, with no <paramref name="actorId"/>) left for the table: another actor's direct
    /// intents applied to them, the rest said.</summary>
    private static void Follow(CampaignRuntime runtime, SystemDefinition system, string? actorId, SheetIntentResult result, List<EventMessage> messages, List<string> touched)
    {
        foreach (var left in result.Left)
        {
            if (SheetEvents.TableKinds.Contains(left.Kind, StringComparer.Ordinal))
            {
                OnCreature(runtime, system, left.Actor ?? actorId, left, messages, touched);
                continue;
            }

            if (left.Actor is { } other && other != actorId && SheetEvents.DirectKinds.Contains(left.Kind, StringComparer.Ordinal) && !CampaignObjects.IsFor(left))
            {
                if (runtime.GetEntity(other) is { } otherEntity && runtime.GetActorDocument(other) is { } otherDoc)
                {
                    var fields = left.Fields.Where(f => f.Key != "actor").ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
                    try
                    {
                        var theirs = Apply(runtime, system, other, otherEntity, otherDoc,
                            live => SheetEvents.Raise(system, runtime.Resolver, live, new SheetEvent(left.Kind, fields), other, runtime.CampaignObjectState).Applied, messages);
                        foreach (var further in theirs.Left) messages.Add(Said(runtime, system, other, further));
                    }
                    catch (SheetEventException ex)
                    {
                        messages.Add(EventMessage.Of("Sheet.Failed", runtime.ActorTerm(other), left.Kind, ex.Message));
                    }

                    touched.Add(other);
                }
                else
                {
                    messages.Add(EventMessage.Of("Sheet.NotAtTable", Term(system, other), left.Kind));
                }

                continue;
            }

            messages.Add(Said(runtime, system, actorId, left));
        }
    }

    /// <summary>
    /// What the table does to a creature rather than to its sheet (the vitals handlers' intents, which a system's handler may
    /// return too): <c>kill</c> (by the system's vitals when it has them, which add its dead condition; otherwise what the
    /// creature held and the effects counted on its turns end, and the intent's <c>condition</c>, if any, is added),
    /// <c>endConcentration</c> (every effect it held up ends) and <c>counter { key, to | by | clear }</c> (a tally kept in its
    /// pools, beside the sheet).
    /// </summary>
    private static void OnCreature(CampaignRuntime runtime, SystemDefinition system, string? actorId, SheetIntent intent, List<EventMessage> messages, List<string> touched)
    {
        if (actorId == null || runtime.GetEntity(actorId) is not { } entity)
        {
            messages.Add(EventMessage.Of("Sheet.NotAtTable", Term(system, actorId ?? "?"), intent.Kind));
            return;
        }

        touched.Add(actorId);
        var name = runtime.ActorTerm(actorId);
        switch (intent.Kind)
        {
            case VitalsHandlers.KillKind:
                if (system.Vitals?.Down != null)
                {
                    if (TableRules.Kill(runtime, entity, actorId)) messages.Add(EventMessage.Of("Sheet.Killed", name));
                    break;
                }

                TableRules.EndConcentration(runtime, actorId, entity);
                TableRules.ExpireTurnsOf(runtime, actorId);
                if (intent.Fields.GetValueOrDefault("condition")?.ToString() is { Length: > 0 } condition && !CodexWorld.HasStatus(entity, condition))
                {
                    runtime.World.AddStatus(entity, condition, "died", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actorId);
                }

                messages.Add(EventMessage.Of("Sheet.Killed", name));
                break;
            case VitalsHandlers.EndConcentrationKind:
                messages.Add(EventMessage.Of("Sheet.ConcentrationEnded", name, TableRules.EndConcentration(runtime, actorId, entity)));
                break;
            case VitalsHandlers.CounterKind when intent.Fields.GetValueOrDefault("key")?.ToString() is { Length: > 0 } key:
                var was = entity.Has<ResourcePoolComponent>() ? entity.Get<ResourcePoolComponent>().Get(key) : 0;
                TableRules.Counter(entity, intent.Fields);
                messages.Add(EventMessage.Of("Sheet.CounterChanged", name, Term(system, key), was, entity.Get<ResourcePoolComponent>().Get(key)));
                break;
            default:
                messages.Add(EventMessage.Of("Sheet.NotDone", name, intent.Kind));
                break;
        }
    }

    /// <summary>Runs <paramref name="act"/> on one actor's live sheet and writes back what changed; the changes are described
    /// into <paramref name="messages"/>.</summary>
    private static SheetIntentResult Apply(CampaignRuntime runtime, SystemDefinition system, string actorId, Entity entity, ActorDocument doc,
        Func<Dictionary<string, object>, SheetIntentResult> act, List<EventMessage> messages)
    {
        if (!entity.Has<ResourcePoolComponent>()) entity.Set(new ResourcePoolComponent());
        var pool = entity.Get<ResourcePoolComponent>();
        var model = new SheetModel(system, runtime.Resolver, doc.Sheet);
        var numeric = model.TrackKeys().Select(model.TrackOf).OfType<SheetTrack>().Where(t => !t.IsSlots).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);

        // The sheet as the table has it: the pools win over the cached sheet for numeric tracks, and the table's conditions join
        // the sheet's own.
        var live = new Dictionary<string, object>(doc.Sheet);
        foreach (var key in numeric)
        {
            if (pool.Pools.TryGetValue(key, out var value)) live[key] = (long)value;
        }

        var statuses = Statuses(entity);
        var own = SheetRuleEngine.RawList(doc.Sheet, SheetModel.ConditionsKey);
        var before = own.Concat(statuses).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        live[SheetModel.ConditionsKey] = before.Cast<object>().ToList();

        var result = act(live);
        var name = runtime.ActorTerm(actorId);
        foreach (var key in result.Changed)
        {
            if (key == SheetModel.ConditionsKey)
            {
                var after = SheetRuleEngine.RawList(live, SheetModel.ConditionsKey);
                foreach (var added in after.Where(c => !before.Contains(c, StringComparer.OrdinalIgnoreCase)))
                {
                    if (!CodexWorld.HasStatus(entity, added)) runtime.World.AddStatus(entity, added, "rules", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actorId);
                    if (!own.Contains(added, StringComparer.OrdinalIgnoreCase)) own.Add(added);
                    messages.Add(EventMessage.Of("Sheet.ConditionAdded", name, EventTerm.Condition(added)));
                }

                foreach (var removed in before.Where(c => !after.Contains(c, StringComparer.OrdinalIgnoreCase)))
                {
                    runtime.World.RemoveStatus(entity, removed);
                    own.RemoveAll(c => string.Equals(c, removed, StringComparison.OrdinalIgnoreCase));
                    messages.Add(EventMessage.Of("Sheet.ConditionRemoved", name, EventTerm.Condition(removed)));
                }

                if (own.Count == 0) doc.Sheet.Remove(SheetModel.ConditionsKey);
                else doc.Sheet[SheetModel.ConditionsKey] = own.Cast<object>().ToList();
                runtime.SheetKeyWritten(actorId, SheetModel.ConditionsKey);
                continue;
            }

            var label = Term(system, model.TrackOf(key)?.Label ?? key);
            if (numeric.Contains(key))
            {
                var was = pool.Pools.TryGetValue(key, out var p) ? p : YamlValues.GetInt(doc.Sheet, key) ?? 0;
                var now = YamlValues.GetInt(live, key) ?? 0;
                pool.Set(key, now);
                doc.Sheet[key] = (long)now;
                messages.Add(EventMessage.Of("Sheet.TrackChanged", name, label, was, now));
            }
            else
            {
                if (live.TryGetValue(key, out var value)) doc.Sheet[key] = value;
                else doc.Sheet.Remove(key);
                // A list's entries changed (a skill's level): said by the list's name. A slot track says what it holds.
                var list = model.Kind?.KeyedLists.FirstOrDefault(l => l.Key == key)?.Label ?? model.Kind?.Lists.FirstOrDefault(l => l.Key == key)?.Label;
                messages.Add(list != null
                    ? EventMessage.Of("Sheet.ListChanged", name, Term(system, list))
                    : EventMessage.Of("Sheet.SlotsChanged", name, label, string.Join(", ", Texts(live.GetValueOrDefault(key)))));
            }

            runtime.SheetKeyWritten(actorId, key);
        }

        return result;
    }

    private static List<string> Statuses(Entity entity) =>
        entity.Has<ActiveEffectsComponent>() ? entity.Get<ActiveEffectsComponent>().Effects.Select(e => e.EffectId).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();

    // What a slot track holds, flat: a list, or by level.
    private static IEnumerable<string> Texts(object? value) => value switch
    {
        IDictionary<string, object> levels => levels.OrderBy(l => l.Key, StringComparer.Ordinal).SelectMany(l => Texts(l.Value)),
        IEnumerable list and not string => list.Cast<object?>().Select(o => o?.ToString() ?? "").Where(t => t.Length > 0),
        _ => Array.Empty<string>()
    };

    /// <summary>A roll, a question or a log line the sheet could not answer, as the session log says it.</summary>
    private static EventMessage Said(CampaignRuntime runtime, SystemDefinition system, string? actorId, SheetIntent intent)
    {
        var who = intent.Actor is { } actor && actor != actorId ? runtime.ActorTerm(actor) : actorId != null ? runtime.ActorTerm(actorId) : Term(system, system.Name);
        if (CampaignObjects.IsFor(intent))
        {
            var named = intent.Fields.GetValueOrDefault("clock") ?? intent.Fields.GetValueOrDefault("row");
            return EventMessage.Of("Campaign.NotThere", named?.ToString() ?? "?");
        }

        switch (intent.Kind)
        {
            case SheetEvents.RollKind:
                return EventMessage.Of("Sheet.RollAsked", who, Term(system, intent.Fields.GetValueOrDefault("definition")?.ToString() ?? "?"));
            case SheetEvents.AskKind:
                var choices = intent.Fields.GetValueOrDefault("choices") is IEnumerable list and not string
                    ? string.Join(", ", list.Cast<object?>().Select(c => system.DisplayValue(c?.ToString() ?? "")))
                    : "";
                var asked = intent.Fields.GetValueOrDefault("who")?.ToString() == "gm" ? "Sheet.AskGm" : "Sheet.AskPlayer";
                return EventMessage.Of(asked, who, choices);
            case SheetEvents.LogKind when intent.Fields.GetValueOrDefault("key")?.ToString() == CampaignObjects.FullLogKey
                                           && intent.Fields.GetValueOrDefault("args") is IEnumerable full and not string:
                var name = full.Cast<object?>().FirstOrDefault()?.ToString() ?? "";
                return EventMessage.Of("Campaign.ClockFull", ClockTerm(CampaignObjects.Clocks(runtime.CampaignObjectState).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)), name));
            case SheetEvents.LogKind:
                var args = intent.Fields.GetValueOrDefault("args") is IEnumerable values and not string
                    ? values.Cast<object?>().Select(a => a?.ToString() ?? "").Select(a => runtime.GetActorDocument(a)?.Name ?? a).ToArray()
                    : Array.Empty<string>();
                var key = intent.Fields.GetValueOrDefault("key")?.ToString() ?? "";
                return EventMessage.Of("Sheet.Log", who, Term(system, key), string.Join(", ", args));
            default:
                return EventMessage.Of("Sheet.NotDone", who, intent.Kind);
        }
    }

    /// <summary>A word of the system's (a track's label, an event name, a log key), shown in the reader's language when the system's
    /// translation names it.</summary>
    private static EventTerm Term(SystemDefinition system, string word) => new(EventTerm.SystemKind, word, system.DisplayValue(word));
}
