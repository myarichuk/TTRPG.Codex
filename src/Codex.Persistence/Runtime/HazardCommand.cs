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
    SetState
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
internal sealed record HazardNoteCommand(IReadOnlyList<EventMessage> Notes) : IRuntimeCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = Array.Empty<string>();

    public void Apply(CampaignRuntime runtime)
    {
    }

    public SessionEvent Describe(CampaignRuntime runtime) => SessionEvent.From("Hazard", Notes);
}

/// <summary>How routes are named: by their id, else their name, else their check.</summary>
public static class HazardRoutes
{
    public static string KeyOf(HazardRoute route) => route.Id ?? route.Name ?? route.Check ?? string.Empty;
}

/// <summary>
/// The table's hazard command (HAZARDS_DESIGN.md 4.2, 11): notice, find, disable, trigger, mark noticed, set by hand. It reads the hazard's numbers only through
/// <see cref="HazardNumbers.For"/> over the placement's level row, makes every check through <see cref="CampaignRuntime.Checks"/> (so a passive is compared and a
/// roll is made on the server), keeps the hazard's state in the runtime's ledger, and says everything as GM-only messages with secret rolls: a player's log never
/// learns the hazard exists. A trigger's damage goes through <see cref="ApplyDamageCommand"/> for each target, each taken back by that creature's own undo.
/// </summary>
public sealed record HazardCommand(HazardSite Site, HazardAction Action, IReadOnlyList<string> ActorIds, string RollerUserId, string? RouteId = null,
    IReadOnlyList<string>? RouteIds = null, HazardState? State = null, IReadOnlyDictionary<string, int>? Bonuses = null, IReadOnlyList<int>? Rolled = null) : IRuntimeCommand
{
    private readonly List<(string ActorId, object Token)> _damage = new();
    private readonly Dictionary<string, CheckSpec> _needSpecs = new(StringComparer.Ordinal);
    private IReadOnlyList<int>? _rolledNow;

    /// <summary>Once applied: whether the effect (attack, save, damage) went off on its targets, so a character who needed a number is still waiting for it.</summary>
    public bool Struck { get; private set; }

    /// <summary>Once applied: the damage each part rolled, in the entry's order. Send it back as <c>Rolled</c> with only the creatures that were waiting for a number
    /// (<see cref="NeedsNumber"/>) to finish the effect with the same roll: the others were already hit and are not hit twice.</summary>
    public IReadOnlyList<int>? DamageRolled => _rolledNow;

    /// <summary>Once applied: the check each waiting character's number is for, so the panel can prefill it from the printed sheet (4.2).</summary>
    public IReadOnlyDictionary<string, CheckSpec> NeedsSpecs => _needSpecs;

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
                // A passive score where the block allows it and the sheet has one for the check; every other check is rolled. Never a no-check route: the sweep skips those.
                var spec = ActorChecks.Resolve(_system, route.Check!);
                var passive = _hazard.Notice?.Passive == true && route.Passive != false && spec != null && ActorChecks.HasPassive(_system, spec);
                var result = Check(runtime, new CheckRequest { ActorId = actorId, Check = route.Check!, Dc = route.Dc, Passive = passive, Bonus = Typed(actorId) });
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

        if (_lastCheck is { Failed: CheckFailure.None, Outcome: { } outcome } && route.TriggersOn.Contains(outcome.Key, StringComparer.OrdinalIgnoreCase))
        {
            // The outcome sets it off: on the one who tried.
            Say("SetOff", who);
            Strike(runtime, new[] { actorId });
            state.State = HazardState.Triggered;
            return true;
        }

        return false;
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

        var result = Check(runtime, new CheckRequest { ActorId = actorId, Check = route.Check!, Dc = route.Dc, Passive = passive, Bonus = Typed(actorId) });
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
                var armor = runtime.Checks.Make(new CheckRequest { ActorId = targetId, Check = ActorChecks.ArmorClassName(_system), Kind = CheckKind.ArmorClass, Bonus = Typed(targetId) }, RollerUserId);
                if (armor.Failed != CheckFailure.None)
                {
                    NeedNumber(targetId, who);
                    continue;
                }

                var total = RollAttack(runtime, targetId, bonus);
                if (total < armor.Total)
                {
                    Say("Miss", total, armor.Total, who);
                    continue;
                }

                Say("Hit", total, armor.Total, who);
            }

            if (save.Length > 0)
            {
                var result = Check(runtime, new CheckRequest { ActorId = targetId, Check = save, Kind = CheckKind.Save, Dc = saveDc, Bonus = Typed(targetId) });
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
        if (result.Failed == CheckFailure.NeedsBonus && ActorChecks.Resolve(_system, request.Check, request.Kind) is { } spec) _needSpecs[request.ActorId] = spec;
        return result;
    }

    private void NeedNumber(string actorId, object who)
    {
        if (!_needsNumber.Contains(actorId)) _needsNumber.Add(actorId);
        Say("NeedsNumber", who);
    }

    private int? Typed(string actorId) => Bonuses != null && Bonuses.TryGetValue(actorId, out var value) ? value : null;

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
