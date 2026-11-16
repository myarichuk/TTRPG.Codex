using System.Globalization;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Persistence.Runtime;

/// <summary>What a <see cref="HazardCommand"/> does (HAZARDS_DESIGN.md 4.2).</summary>
public enum HazardAction
{
    /// <summary>A sweep: each named character makes each detect route's check that has one (a passive score where the sheet has it). Reports only; nothing is recorded.</summary>
    Notice,

    /// <summary>One character tries one detect route: with a check it is rolled, without one it is done by what it needs. A success records the route as found.</summary>
    Find,

    /// <summary>One character tries one disarm route. Successes add up to what the route needs; an outcome the route lists in <c>triggersOn</c> sets it off.</summary>
    Disable,

    /// <summary>It goes off on the named targets: an attack against armor class or a save, then damage through the damage event.</summary>
    Trigger,

    /// <summary>The game master marks the named characters as having noticed it (and the named routes as found), which is what reveals it to them.</summary>
    MarkNoticed,

    /// <summary>The game master sets the state by hand.</summary>
    SetState,

    /// <summary>One step of a complex hazard's routine, named by the route id. With creatures named it is the hazard's effect on them (Crush), on any creature's turn.</summary>
    Routine
}

/// <summary>What a typed number is: a check's bonus, a passive score (the score itself), or an armor class.</summary>
public enum HazardAskKind { Bonus, Passive, ArmorClass }

/// <summary>One number the table asks the game master for, for one character and one check. <see cref="Key"/> names the question (character, kind, check); a typed number goes
/// back under it, so one number never answers a different question of the same character.</summary>
public sealed record HazardAsk(string Key, string ActorId, HazardAskKind Kind, string Label, CheckSpec? Spec)
{
    public static string KeyFor(string actorId, HazardAskKind kind, string check) => $"{actorId}|{kind}|{check}";
}

/// <summary>One character who passed one detect route in a <see cref="HazardAction.Notice"/> sweep (<see cref="HazardRoutes.KeyOf"/> names the route).</summary>
public sealed record HazardPass(string ActorId, string RouteKey);

/// <summary>The game master points the running encounter at a place (<see cref="EncounterDocument.LocationId"/>), which the Hazards panel narrows itself to. Nothing else
/// sets it. GM only, like everything about hazards; a place name can spoil a secret.</summary>
public sealed record SetEncounterPlaceCommand(string? PlaceId, string Label) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
        if (runtime.ActiveEncounter is not { } encounter) return;
        encounter.LocationId = string.IsNullOrWhiteSpace(PlaceId) ? null : PlaceId;
        runtime.MarkEncounterDirty();
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Hazard",
        new[] { string.IsNullOrWhiteSpace(PlaceId) ? EventMessage.Of("Hazard.WhereAll", EventTerm.GmOnly(Label)) : EventMessage.Of("Hazard.Where", EventTerm.GmOnly(Label)) });
}

/// <summary>A complex hazard takes its place in initiative (HAZARDS_DESIGN.md 3.3): the initiative die with the hazard's own bonus is rolled once, in secret, and the actor
/// joins the order at the total. GM only like every hazard command: the roll is a secret entry of the roll log, and the log line names the hazard as a GM-only term (a player's
/// view of the order never lists a Hidden actor either). The bonus is the caller's (<see cref="HazardNumbers.InitiativeOf"/> over the placement's numbers), so numbers still
/// come only through <see cref="HazardNumbers.For"/>.</summary>
public sealed record JoinInitiativeCommand(string ActorId, string Expression, string RollerUserId, int TieBreak = 0) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds => _set?.AffectedActorIds ?? Array.Empty<string>();

    private SetInitiativeCommand? _set;
    private int _total;

    public void Apply(CampaignRuntime runtime)
    {
        var result = runtime.DiceRoller.Roll(Expression);
        _total = result.Total;
        runtime.RecordRoll(new RollLogEntry
        {
            ActorId = ActorId,
            RollerUserId = RollerUserId,
            Expression = Expression,
            Result = result.ToString(),
            Total = result.Total,
            Kept = result.Kept.ToList(),
            Dropped = result.Dropped.ToList(),
            Modifier = result.Modifier,
            CheckDieIndex = result.CheckDieIndex,
            IsSecret = true
        });
        _set = new SetInitiativeCommand(ActorId, result.Total, TieBreak, RollerUserId);
        _set.Apply(runtime);
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Hazard",
        new[] { EventMessage.Of("Hazard.Joined", EventTerm.GmOnly(runtime.GetActorName(ActorId)), _total) }, new[] { ActorId });
}

/// <summary>Takes back the last change the table made to a hazard's own state (a Find, a Disable's success, a sweep's marking, the state picker), with what it recorded: the
/// successes, who noticed it, the routes found, and the damage a trigger dealt (taken back on each creature it hurt, as one with the hazard's state). Only while the hazard
/// still stands as that change left it and each of those creatures still has that damage as its last change; otherwise nothing is touched and the log says why.
/// GM only, like every hazard command.</summary>
public sealed record UndoHazardCommand(HazardSite Site) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    /// <summary>Once applied: whether the change was taken back.</summary>
    public bool Done { get; private set; }

    private SessionEvent? _said;

    /// <summary>Once applied: what the log says of it (GM-only).</summary>
    public SessionEvent? Said => _said;

    public void Apply(CampaignRuntime runtime)
    {
        var why = runtime.Hazards.Undo(Site);
        Done = why == null;
        _said = SessionEvent.From("Hazard", new[] { EventMessage.Of(Done ? "Hazard.Undone" : "Hazard.Refused." + why, EventTerm.GmOnly(Site.Name)) });
    }

    public SessionEvent Describe(CampaignRuntime runtime) => _said ?? SessionEvent.From("Hazard", Array.Empty<EventMessage>());
}

/// <summary>A line for the log that the loop adds after a command: what the hazard's write to its place could not do (GM-only messages).</summary>
internal sealed record HazardNoteCommand(IReadOnlyList<EventMessage> Notes, string Type = "Hazard", string? ActorId = null) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From(Type, Notes, ActorId == null ? null : new[] { ActorId });
}

/// <summary>How routes are named: by their id, else their name, else their check.</summary>
public static class HazardRoutes
{
    public static string KeyOf(HazardRoute route) => route.Id ?? route.Name ?? route.Check ?? string.Empty;

    /// <summary>The outcome key a hazard's own check set off for critical failure. The key is a content contract (the system's check roll names it).</summary>
    public const string CriticalFailure = "criticalFailure";

    /// <summary>Whether the outcome sets the hazard off. A route lists the keys that do: <c>criticalFailure</c> names that outcome alone, and <c>failure</c> means a failure or
    /// worse, so it also fires on a critical failure (a route that wants only the plain failure lists no <c>criticalFailure</c> and none of the system's other keys). A passed
    /// outcome never sets anything off.</summary>
    public static bool SetsOff(HazardRoute route, CheckOutcome outcome)
    {
        if (outcome.Passed) return false;
        if (route.TriggersOn.Contains(outcome.Key, StringComparer.OrdinalIgnoreCase)) return true;
        return string.Equals(outcome.Key, CriticalFailure, StringComparison.OrdinalIgnoreCase) && route.TriggersOn.Contains(ActorChecks.Failure, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The table's hazard command (HAZARDS_DESIGN.md 4.2, 11): notice, find, disable, trigger, mark noticed, set by hand. It reads the hazard's numbers only through
/// <see cref="HazardNumbers.For"/> over the placement's level row, makes every check through <see cref="CampaignRuntime.Checks"/> (so a passive is compared and a
/// roll is made on the server), keeps the hazard's state in the runtime's ledger, and says everything as GM-only messages with secret rolls: a player's log never
/// learns the hazard exists. A trigger's damage goes through <see cref="ApplyDamageCommand"/> for each target, each taken back by that creature's own undo.
/// </summary>
public sealed record HazardCommand(HazardSite Site, HazardAction Action, IReadOnlyList<string> ActorIds, string RollerUserId, string? RouteId = null,
    IReadOnlyList<string>? RouteIds = null, HazardState? State = null, IReadOnlyDictionary<string, int>? Bonuses = null, IReadOnlyList<int>? Rolled = null,
    IReadOnlyList<string>? Reach = null, IReadOnlyDictionary<string, int>? Attacked = null) : IRuntimeCommand
{
    // The attack each target rolled (by actor id), kept so the repeat for a creature that waited for a number does not roll it again.
    private readonly Dictionary<string, int> _attacks = new();

    /// <summary>Once applied: the attack total each target rolled. Send it back as <c>Attacked</c> with <c>Rolled</c>, so a creature that waited for a number (its save bonus,
    /// after the attack hit) is not attacked a second time.</summary>
    public IReadOnlyDictionary<string, int> AttackRolled => _attacks;

    private readonly List<(string ActorId, object Token)> _damage = new();
    private readonly List<HazardAsk> _asks = new();
    private IReadOnlyList<int>? _rolledNow;

    /// <summary>Once applied: whether the effect (attack, save, damage) went off on its targets, so a character who needed a number is still waiting for it.</summary>
    public bool Struck { get; private set; }

    /// <summary>Once applied: the damage each part rolled, in the entry's order. Send it back as <c>Rolled</c> with only the creatures that were waiting for a number
    /// (<see cref="NeedsNumber"/>) to finish the effect with the same roll: the others were already hit and are not hit twice.</summary>
    public IReadOnlyList<int>? DamageRolled => _rolledNow;

    /// <summary>Once applied: each number that was asked for, with the check it is for (so the panel labels it and can prefill it from the printed sheet, 4.2).
    /// Send the typed numbers back in <c>Bonuses</c> by <see cref="HazardAsk.Key"/>.</summary>
    public IReadOnlyList<HazardAsk> Asks => _asks;

    private readonly List<EventMessage> _parts = new();
    private readonly List<string> _touched = new();
    private readonly List<HazardPass> _passes = new();
    private readonly List<string> _needsNumber = new();

    /// <summary>Damaged characters, so their documents are saved.</summary>
    public IReadOnlyCollection<string> AffectedActorIds => _touched;

    /// <summary>Once applied: the reason it was refused (<c>NoRoute</c>, <c>Locked</c>, <c>NoActor</c>, <c>NothingToTry</c>, <c>AlreadyDisabled</c>), or null.</summary>
    public string? Refused { get; private set; }

    /// <summary>Once applied: who passed which route in a sweep.</summary>
    public IReadOnlyList<HazardPass> Passes => _passes;

    /// <summary>Once applied: the characters whose number (save, check or armor class) the table did not have, so nothing was done for them. Type one and send it again.</summary>
    public IReadOnlyList<string> NeedsNumber => _needsNumber;

    /// <summary>Once applied: the hazard's state, or null when it was refused.</summary>
    public PlacedHazard? Result { get; private set; }

    /// <summary>Once applied: every check that was made, in order (the panel's status line says them).</summary>
    public IReadOnlyList<CheckResult> Checks => _checks;

    private readonly List<CheckResult> _checks = new();

    private SystemDefinition _system = null!;
    private HazardDefinition _hazard = null!;
    private IReadOnlyDictionary<string, object> _numbers = null!;
    private EventTerm _name = null!;

    private object Actor(CampaignRuntime runtime, string id) => runtime.ActorTerm(id);

    private void Say(string key, params object?[] args) => _parts.Add(EventMessage.Of("Hazard." + key, new object?[] { _name }.Concat(args).ToArray()));

    private void Refuse(string reason, params object?[] args)
    {
        Refused = reason;
        Say("Refused." + reason, args);
    }

    private SessionEvent? _said;

    /// <summary>Once applied: what the log says of it (GM-only messages), for the panel's own status line.</summary>
    public SessionEvent? Said => _said;

    public void Apply(CampaignRuntime runtime)
    {
        Run(runtime);
        _said = SessionEvent.From("Hazard", _parts, ActorIds.Where(runtime.HasActor).Distinct());
    }

    private void Run(CampaignRuntime runtime)
    {
        _name = EventTerm.GmOnly(Site.Name);
        if (runtime.System is not { } system || Site.Type.Hazard is not { } hazard)
        {
            Refuse("NoRoute");
            return;
        }

        _system = system;
        _hazard = hazard;
        _numbers = HazardNumbers.For(Site.Entry, Site.Type, Site.Stored.Levels);
        var state = runtime.Hazards.Begin(Site);
        var before = HazardLedger.Copy(state);
        // With a roll already made, this only finishes what a trigger began on the creatures that were waiting for a number: the same damage, nothing else.
        var changed = Rolled != null ? Finish(runtime) : Action switch
        {
            HazardAction.Notice => Sweep(runtime, state),
            HazardAction.Find => Find(runtime, state),
            HazardAction.Disable => Disable(runtime, state),
            HazardAction.Trigger => Trigger(runtime, state),
            HazardAction.MarkNoticed => MarkNoticed(runtime, state),
            HazardAction.Routine => Routine(runtime, state),
            _ => SetState(runtime, state)
        };

        Result = state;
        if (changed) runtime.Hazards.Commit(Site, before, state, _damage, joins: Rolled != null);
    }

    private bool Finish(CampaignRuntime runtime)
    {
        if (ActorIds.Count == 0)
        {
            Refuse("NoActor");
            return false;
        }

        Strike(runtime, ActorIds);
        return true;
    }

    public SessionEvent Describe(CampaignRuntime runtime) => _said ?? SessionEvent.From("Hazard", _parts);

    // ---- Notice ----

    private static bool Open(HazardRoute route, PlacedHazard state) => route.After == null || state.Found.Contains(route.After, StringComparer.Ordinal);

    private bool Sweep(CampaignRuntime runtime, PlacedHazard state)
    {
        var routes = HazardNumbers.NoticeRoutes(_numbers, _hazard)
            .Where(r => r.HasCheck && Open(r, state) && !state.Found.Contains(HazardRoutes.KeyOf(r), StringComparer.Ordinal)).ToList();
        if (routes.Count == 0 || ActorIds.Count == 0)
        {
            Refuse(ActorIds.Count == 0 ? "NoActor" : "NothingToTry");
            return false;
        }

        Say("Sweep");
        foreach (var actorId in ActorIds)
        {
            foreach (var route in routes)
            {
                // A character without the route's rank does not try it (a sweep says nothing of what it skips).
                if (RankShort(runtime, route, actorId) != null) continue;
                // A passive score where the block allows it and the sheet has one for the check; every other check is rolled. Never a no-check route: the sweep skips those.
                var spec = ActorChecks.Resolve(_system, route.Check!);
                var passive = _hazard.Notice?.Passive == true && route.Passive != false && spec != null && ActorChecks.HasPassive(_system, spec);
                var result = Check(runtime, new CheckRequest { ActorId = actorId, Check = route.Check!, Dc = route.Dc, Passive = passive, Bonus = Typed(AskKey(actorId, route.Check!, null, passive)) });
                if (result is { Failed: CheckFailure.None, Outcome.Passed: true }) _passes.Add(new HazardPass(actorId, HazardRoutes.KeyOf(route)));
            }
        }

        return false;
    }

    private bool Find(CampaignRuntime runtime, PlacedHazard state)
    {
        var route = HazardNumbers.NoticeRoutes(_numbers, _hazard).FirstOrDefault(r => HazardRoutes.KeyOf(r) == RouteId);
        if (route == null || RouteId == null)
        {
            Refuse("NoRoute");
            return false;
        }

        if (!Open(route, state))
        {
            Refuse("Locked", route.Shown, route.After);
            return false;
        }

        if (ActorIds.Count == 0)
        {
            Refuse("NoActor");
            return false;
        }

        var actorId = ActorIds[0];
        var who = Actor(runtime, actorId);
        if (RankShort(runtime, route, actorId) is { } needed)
        {
            Refuse("RankTooLow", route.Shown, needed, who);
            return false;
        }

        if (!Succeeds(runtime, route, actorId, who, passive: false))
        {
            if (!_needsNumber.Contains(actorId)) Say("Missed", who, route.Shown);
            return false;
        }

        // A successful detect route only marks a route found; it never disables.
        AddOnce(state.Found, HazardRoutes.KeyOf(route));
        AddOnce(state.NoticedBy, actorId);
        if (state.State == HazardState.Hidden) state.State = HazardState.Noticed;
        Say("Noticed", who, route.Shown);
        return true;
    }

    private bool MarkNoticed(CampaignRuntime runtime, PlacedHazard state)
    {
        if (ActorIds.Count == 0)
        {
            Refuse("NoActor");
            return false;
        }

        // Only routes that are open when the game master marks: one that comes after another is not found by being listed with it.
        var open = HazardNumbers.NoticeRoutes(_numbers, _hazard).Where(r => Open(r, state)).Select(HazardRoutes.KeyOf).ToHashSet(StringComparer.Ordinal);
        foreach (var key in RouteIds ?? Array.Empty<string>())
        {
            if (open.Contains(key)) AddOnce(state.Found, key);
        }

        foreach (var actorId in ActorIds)
        {
            AddOnce(state.NoticedBy, actorId);
            Say("Revealed", Actor(runtime, actorId));
        }

        if (state.State == HazardState.Hidden) state.State = HazardState.Noticed;
        return true;
    }

    // ---- Disable ----

    private bool Disable(CampaignRuntime runtime, PlacedHazard state)
    {
        var route = HazardNumbers.DisableRoutes(_numbers, _hazard).FirstOrDefault(r => HazardRoutes.KeyOf(r) == RouteId);
        if (route == null || RouteId == null)
        {
            Refuse("NoRoute");
            return false;
        }

        if (!Open(route, state))
        {
            Refuse("Locked", route.Shown, route.After);
            return false;
        }

        if (state.State == HazardState.Disabled)
        {
            Refuse("AlreadyDisabled");
            return false;
        }

        if (ActorIds.Count == 0)
        {
            Refuse("NoActor");
            return false;
        }

        var actorId = ActorIds[0];
        var who = Actor(runtime, actorId);
        if (RankShort(runtime, route, actorId) is { } needed)
        {
            Refuse("RankTooLow", route.Shown, needed, who);
            return false;
        }

        if (Succeeds(runtime, route, actorId, who, passive: false))
        {
            state.Successes++;
            if (state.Successes >= route.Successes)
            {
                // Every successful disarm route disarms.
                state.State = HazardState.Disabled;
                Say("Disabled");
            }
            else
            {
                Say("DisableProgress", state.Successes, route.Successes);
            }

            return true;
        }

        if (_lastCheck is { Failed: CheckFailure.None, Outcome: { } outcome } && HazardRoutes.SetsOff(route, outcome))
        {
            // The outcome sets it off: on the one who tried, and on whoever the game master ticked before the roll (an area hazard, a critical failure).
            var reached = new List<string> { actorId };
            foreach (var other in (Reach ?? Array.Empty<string>()).Where(runtime.HasActor)) AddOnce(reached, other);
            Say("SetOff", who);
            foreach (var other in reached.Skip(1)) Say("SetOffAlso", Actor(runtime, other));
            Strike(runtime, reached);
            state.State = HazardState.Triggered;
            return true;
        }

        return false;
    }

    /// <summary>The name of the rank a route needs when the actor has not got it; null when it can try (no rank asked, or the sheet cannot say: a printed stat block is the game master's call).</summary>
    private string? RankShort(CampaignRuntime runtime, HazardRoute route, string actorId)
    {
        if (route.Rank == null || !route.HasCheck || HazardNumbers.RankPlace(_system, route.Rank) is not { } needed) return null;
        if (runtime.Checks.RankOf(actorId, route.Check!) is not { } has || has >= needed) return null;
        var rank = _system.SheetRules!.Skills!.Ranks[needed - 1];
        return string.IsNullOrWhiteSpace(rank.Label) ? rank.Key : rank.Label;
    }

    private CheckResult? _lastCheck;

    /// <summary>One try at a route: a route with no check is done by what it needs (nothing is rolled); one with a check is rolled, never a passive. False when it
    /// failed, or could not be made (<see cref="NeedsNumber"/>).</summary>
    private bool Succeeds(CampaignRuntime runtime, HazardRoute route, string actorId, object who, bool passive)
    {
        _lastCheck = null;
        if (!route.HasCheck)
        {
            Say("NoRoll", who, route.Shown);
            return true;
        }

        var result = Check(runtime, new CheckRequest { ActorId = actorId, Check = route.Check!, Dc = route.Dc, Passive = passive, Bonus = Typed(AskKey(actorId, route.Check!, null, passive)) });
        _lastCheck = result;
        return result is { Failed: CheckFailure.None, Outcome.Passed: true };
    }

    // ---- Trigger ----

    private bool Trigger(CampaignRuntime runtime, PlacedHazard state)
    {
        if (ActorIds.Count == 0)
        {
            Refuse("NoActor");
            return false;
        }

        Say("Triggered");
        Strike(runtime, ActorIds);
        state.State = HazardState.Triggered;
        return true;
    }

    // ---- Routine ----

    /// <summary>One step of a complex hazard's routine (HAZARDS_DESIGN.md 3.3), named by <see cref="RouteId"/>. Said in the log; a step the data marks <c>strikes: true</c>, with
    /// creatures named, is the hazard's effect on them (the Rolling Stone's Crush), through the same strike as a trigger. It does not depend on whose turn it is: a step such as Crush applies on any creature's
    /// turn (12.6, item 5), so the game master presses it whenever the hazard reaches a creature. A hazard that was disabled does nothing.</summary>
    private bool Routine(CampaignRuntime runtime, PlacedHazard state)
    {
        var step = HazardNumbers.Routine(_numbers, _hazard).FirstOrDefault(s => string.Equals(s.Name, RouteId, StringComparison.OrdinalIgnoreCase));
        if (step == null)
        {
            Refuse("NoRoute");
            return false;
        }

        if (state.State == HazardState.Disabled)
        {
            Refuse("AlreadyDisabled");
            return false;
        }

        // Only the row the data marks `strikes: true` applies the hazard's effect; every other row, with or without creatures named, is said in the log and does nothing more.
        if (!step.Strikes || ActorIds.Count == 0)
        {
            Say("Step", step.Name);
            return false;
        }

        Say("StepOn", step.Name);
        Strike(runtime, ActorIds);
        state.State = HazardState.Triggered;
        return true;
    }

    private bool SetState(CampaignRuntime runtime, PlacedHazard state)
    {
        var to = State ?? HazardState.Hidden;
        state.State = to;
        if (to == HazardState.Hidden)
        {
            // Back to hidden is back to the start: nobody has found it and nothing is counted.
            state.Successes = 0;
            state.NoticedBy.Clear();
            state.Found.Clear();
        }

        Say("State." + to);
        return true;
    }

    /// <summary>The effect on each target: an attack against armor class or a save, then damage by the table's rules. Damage is rolled once; a target that passes
    /// the save takes half or none as the entry's <c>onSave</c> says. Conditions stay text for the game master to add (4.2).</summary>
    private void Strike(CampaignRuntime runtime, IReadOnlyList<string> targets)
    {
        var effect = _hazard.Effect;
        var attack = Number(effect?.Attack);
        var save = Text(effect?.Save);
        var saveDc = Number(effect?.SaveDc);
        var half = string.Equals(Text(effect?.OnSave), "half", StringComparison.OrdinalIgnoreCase);
        var conditions = HazardNumbers.Conditions(_numbers, _hazard).Select(c => c.Name).ToList();
        // Damage is rolled once: a repeat for the creatures that were waiting for a number takes the roll already made.
        var parts = HazardNumbers.Damage(_numbers, _hazard).Select((p, i) => (Part: p, Amount: Rolled != null && i < Rolled.Count ? Rolled[i] : Roll(runtime, p))).ToList();
        _rolledNow = parts.Select(p => p.Amount).ToList();
        Struck = true;
        var key = SystemDefinition.PrimaryKeyOf(_system);

        foreach (var targetId in targets.Where(runtime.HasActor))
        {
            var who = Actor(runtime, targetId);
            var saved = false;
            if (attack is { } bonus)
            {
                var armor = runtime.Checks.Make(new CheckRequest { ActorId = targetId, Check = ActorChecks.ArmorClassName(_system), Kind = CheckKind.ArmorClass, Bonus = Typed(AskKey(targetId, string.Empty, CheckKind.ArmorClass, false)) }, RollerUserId);
                if (armor.Failed != CheckFailure.None)
                {
                    AddAsk(new HazardAsk(AskKey(targetId, string.Empty, CheckKind.ArmorClass, false), targetId, HazardAskKind.ArmorClass, string.Empty, null));
                    NeedNumber(targetId, who);
                    continue;
                }

                // The repeat for a creature that waited for a number keeps the attack it already rolled: only the damage and the save are finished.
                var total = Attacked != null && Attacked.TryGetValue(targetId, out var already) ? already : RollAttack(runtime, targetId, bonus);
                _attacks[targetId] = total;
                if (total < armor.Total)
                {
                    Say("Miss", total, armor.Total, who);
                    continue;
                }

                Say("Hit", total, armor.Total, who);
            }

            if (save.Length > 0)
            {
                var result = Check(runtime, new CheckRequest { ActorId = targetId, Check = save, Kind = CheckKind.Save, Dc = saveDc, Bonus = Typed(AskKey(targetId, save, CheckKind.Save, false)) });
                if (result.Failed != CheckFailure.None)
                {
                    NeedNumber(targetId, who);
                    continue;
                }

                saved = result.Outcome?.Passed == true;
                if (saved) Say(half ? "SavedHalf" : "SavedNone", who);
            }

            // All of a target's damage parts are one undoable change, so its Undo button takes every part back at once.
            var lines = new List<(string? Type, int Shown, DamageReport Report)>();
            runtime.ApplyUndoably(targetId, "Damage", () =>
            {
                foreach (var (part, amount) in parts)
                {
                    var dealt = saved ? (half ? amount / 2 : 0) : amount;
                    if (dealt <= 0) continue;
                    var damage = new ApplyDamageCommand(targetId, key, dealt, string.Empty, part.Type?.ToLowerInvariant());
                    damage.Apply(runtime);
                    lines.Add((part.Type, damage.Dealt > 0 || !TableEvents.Handles(runtime, "damage") ? damage.Dealt : dealt, damage.Report));
                }
            });
            if (lines.Count > 0 && !_touched.Contains(targetId)) _touched.Add(targetId);
            // The creature's own undo point, kept so the hazard's undo takes this damage back with its state (and only while it is still that creature's last change).
            if (lines.Count > 0 && runtime.UndoToken(targetId) is { } token) _damage.Add((targetId, token));
            foreach (var (type, shown, report) in lines)
            {
                if (report.Modifier == "immune") Say("Immune", who, EventTerm.DamageType((type ?? string.Empty).ToLowerInvariant()));
                else if (string.IsNullOrWhiteSpace(type)) Say("Damage", who, shown);
                else Say("DamageTyped", who, shown, EventTerm.DamageType(type.ToLowerInvariant()));

                // What the damage did, for the game master (the damage command's own log line would say the same, but without the hazard's secret name).
                if (report.Modifier is "resisted" or "vulnerable") Say(report.Modifier == "resisted" ? "Resisted" : "Vulnerable", who);
                if (report.Absorbed > 0) Say("Absorbed", who, report.Absorbed);
                if (report.Died) Say("Died", who);
                else if (report.WentDown) Say("Down", who);
                else if (report.FailuresAdded > 0) Say("HitWhileDown", who, report.FailuresAdded);
                if (report.ConcentrationLost) Say("ConcentrationLost", who);
                else if (report.ConcentrationDc is { } dc) Say("ConcentrationSave", who, dc);
            }

            if (!saved && conditions.Count > 0) Say("Conditions", who, string.Join(", ", conditions));
        }
    }

    // ---- Rolls and numbers ----

    private CheckResult Check(CampaignRuntime runtime, CheckRequest request)
    {
        var secret = request with { Secret = true };
        var result = runtime.Checks.Make(secret, RollerUserId);
        _checks.Add(result);
        _parts.Add(CheckMessages.Of(runtime, secret, result));
        if (result.Failed == CheckFailure.NeedsBonus && !_needsNumber.Contains(request.ActorId)) _needsNumber.Add(request.ActorId);
        if (result.Failed == CheckFailure.NeedsBonus)
        {
            var spec = ActorChecks.Resolve(_system, request.Check, request.Kind);
            AddAsk(new HazardAsk(AskKey(request.ActorId, request.Check, request.Kind, request.Passive), request.ActorId,
                request.Passive ? HazardAskKind.Passive : HazardAskKind.Bonus, spec?.Label ?? request.Check, spec));
        }

        return result;
    }

    private void NeedNumber(string actorId, object who)
    {
        if (!_needsNumber.Contains(actorId)) _needsNumber.Add(actorId);
        Say("NeedsNumber", who);
    }

    // A typed number belongs to one question: this character, this kind of reading (a bonus, a passive score, an armor class) and this check. A passive Perception
    // score and an Arcana bonus, or an armor class and a save bonus, are different numbers, so they are asked and sent separately.
    private string AskKey(string actorId, string check, CheckKind? kind, bool passive)
    {
        if (kind == CheckKind.ArmorClass) return HazardAsk.KeyFor(actorId, HazardAskKind.ArmorClass, string.Empty);
        var spec = ActorChecks.Resolve(_system, check, kind);
        return HazardAsk.KeyFor(actorId, passive ? HazardAskKind.Passive : HazardAskKind.Bonus, spec?.Key ?? check.Trim().ToLowerInvariant());
    }

    private int? Typed(string key) => Bonuses != null && Bonuses.TryGetValue(key, out var value) ? value : null;

    private void AddAsk(HazardAsk ask)
    {
        if (!_asks.Any(a => a.Key == ask.Key)) _asks.Add(ask);
    }

    private int RollAttack(CampaignRuntime runtime, string targetId, int bonus)
    {
        var expression = _system.Dice.Check + bonus.ToString("+#;-#;+0", CultureInfo.InvariantCulture);
        var rolled = runtime.DiceRoller.Roll(expression);
        runtime.RecordRoll(new RollLogEntry
        {
            ActorId = targetId,
            RollerUserId = RollerUserId,
            Expression = expression,
            Result = rolled.ToString(),
            Total = rolled.Total,
            IsSecret = true,
            Label = Site.Name,
            Kept = rolled.Kept.ToList(),
            Dropped = rolled.Dropped.ToList(),
            Modifier = bonus,
            CheckDieIndex = rolled.CheckDieIndex
        });
        return rolled.Total;
    }

    /// <summary>One damage part's total: its dice, as many times as its count says (a whole number or dice; once when absent). Every die roll is a secret roll entry.</summary>
    private int Roll(CampaignRuntime runtime, HazardDamagePart part)
    {
        var times = part.Count == null ? 1 : int.TryParse(part.Count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : RollOnce(runtime, part.Count);
        var total = 0;
        for (var i = 0; i < Math.Clamp(times, 0, 20); i++) total += RollOnce(runtime, part.Dice);
        return total;
    }

    private int RollOnce(CampaignRuntime runtime, string expression)
    {
        var rolled = runtime.DiceRoller.Roll(expression);
        runtime.RecordRoll(new RollLogEntry
        {
            RollerUserId = RollerUserId,
            Expression = expression,
            Result = rolled.ToString(),
            Total = rolled.Total,
            IsSecret = true,
            Label = Site.Name,
            Kept = rolled.Kept.ToList(),
            Dropped = rolled.Dropped.ToList(),
            Modifier = rolled.Modifier
        });
        return rolled.Total;
    }

    private string Text(string? field) =>
        field != null && _numbers.TryGetValue(field, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;

    private int? Number(string? field) => int.TryParse(Text(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static void AddOnce(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.Ordinal)) list.Add(value);
    }
}
