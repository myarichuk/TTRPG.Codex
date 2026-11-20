using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Persistence.Runtime;

/// <summary>What the system's <c>rerolls</c> offer on a roll: which rolls can be made again, and what the actor holds to pay with. Pure, so the table pages and the
/// command agree.</summary>
public static class Rerolls
{
    private static readonly Regex OneD20 = new(@"^\s*1d20\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Whether a named roll can be made again by itself: it only rolls, with no follow-up roll or intents (<c>then</c>) and no group of participants, since
    /// what the first roll applied would otherwise stand beside the second.</summary>
    public static bool CanReplay(SystemDefinition? system, string definition) =>
        system?.Rolls.FirstOrDefault(r => r.Key == definition) is { } roll && roll.Then.Count == 0 && roll.Participants == null;

    /// <summary>The system's rerolls that apply to this roll: none unless it was made for an actor with a check die, carries what it takes to make it again
    /// (<see cref="RollLogEntry.Replay"/>), has not been rerolled and is not itself a reroll.</summary>
    public static IReadOnlyList<SystemRerollDefinition> Offered(SystemDefinition? system, RollLogEntry entry)
    {
        if (system == null || system.Rerolls.Count == 0 || entry.ActorId == null || entry.Replay is not { } replay) return Array.Empty<SystemRerollDefinition>();
        if (entry.RerolledBy != null || entry.RerollOf != null) return Array.Empty<SystemRerollDefinition>();
        var hasCheckDie = replay.Kind switch
        {
            RollReplay.OfDefinition => CanReplay(system, replay.RollKey ?? string.Empty)
                && system.Rolls.First(r => r.Key == replay.RollKey) is { Pool: null, Dice: { } dice } && OneD20.IsMatch(dice),
            RollReplay.OfExpression or RollReplay.OfCheck => entry.CheckDieIndex != null,
            _ => false
        };
        return hasCheckDie ? system.Rerolls.Where(r => r.Spend != null).ToList() : Array.Empty<SystemRerollDefinition>();
    }

    /// <summary>What the sheet holds of a numeric track, laid out as the table has it (<see cref="ActorStateSnapshot.LiveView"/>); 0 when it holds none.</summary>
    public static int Held(IReadOnlyDictionary<string, object> liveSheet, string track) => YamlValues.GetInt(liveSheet, track) ?? 0;

    /// <summary>Whether an actor can pay for the reroll, from the table's snapshot and the actor's stored sheet.</summary>
    public static bool CanPay(SystemDefinition system, SystemRerollDefinition reroll, ActorStateSnapshot? state, IReadOnlyDictionary<string, object> sheet) =>
        reroll.Spend is { } spend && Held(state?.LiveView(system, sheet) ?? sheet, spend.Track) >= spend.By;
}

/// <summary>Why a reroll was not made.</summary>
public enum RerollFailure
{
    None,

    /// <summary>The roll is not there, cannot be rerolled (no replay data, a roll with a follow-up, already rerolled, itself a reroll) or the system has no such reroll.</summary>
    NotOffered,

    /// <summary>Not the actor's owner or the GM, or a secret roll offered to someone who is not the GM.</summary>
    NotAllowed,

    /// <summary>The actor holds less than the reroll costs.</summary>
    Short,

    /// <summary>The roll could not be made again (its definition or dice failed); nothing was spent.</summary>
    CouldNotRoll
}

/// <summary>
/// Makes a roll again at the cost the system's <c>rerolls</c> declare (<see cref="SystemRerollDefinition"/>). The cost is taken from the actor's track as any intent takes it
/// (so undoing the command gives it back), the roll is made again the way it was made the first time (a dice expression against the same DC, a named roll
/// with the same inputs, a check), and both entries stay in the roll log: the first is marked <see cref="RollLogEntry.RerolledBy"/>, the new one
/// <see cref="RollLogEntry.RerollOf"/>. A named roll costs nothing a second time, as what its bonuses spent was paid with the first. Refused, and changing nothing,
/// when the actor cannot pay. <c>keep: higher</c> leaves the higher of the two totals standing (<see cref="Standing"/>).
/// </summary>
/// <param name="ActorId">The actor who made the roll.</param>
/// <param name="AsGm">Whether the one asking is the GM; otherwise only the actor's owner may, and never on a secret roll.</param>
public sealed record RerollCommand(string ActorId, string EntryId, string RerollKey, string UserId, bool AsGm = false, IDiceSource? Dice = null) : IUndoableCommand
{
    public IReadOnlyCollection<string> AffectedActorIds { get; } = new[] { ActorId };

    public string UndoKind => "DiceRoll";

    /// <summary>Why nothing was done; <see cref="RerollFailure.None"/> when the roll was made again.</summary>
    public RerollFailure Failed { get; private set; }

    /// <summary>Once applied: the new roll, as logged.</summary>
    public RollLogEntry? Made { get; private set; }

    /// <summary>Once applied: the roll that counts, the new one or (with <c>keep: higher</c>) the first when it was the higher.</summary>
    public RollLogEntry? Standing { get; private set; }

    /// <summary>Once applied, for a named roll: the new roll as the rules worked it out (its bonuses, dice and outcome), for the sheet's roll card.</summary>
    public SheetRollResult? Rolled { get; private set; }

    private RollLogEntry? _old;
    private SystemRerollDefinition? _reroll;
    private TableEvents.Outcome _spent = TableEvents.Outcome.None;

    public void Apply(CampaignRuntime runtime)
    {
        var system = runtime.System;
        _old = runtime.FindRoll(EntryId);
        _reroll = system?.Rerolls.FirstOrDefault(r => r.Key == RerollKey);
        var doc = runtime.GetActorDocument(ActorId);
        if (system == null || _old == null || _reroll?.Spend is not { } spend || doc == null || _old.ActorId != ActorId || !Rerolls.Offered(system, _old).Contains(_reroll))
        {
            Failed = RerollFailure.NotOffered;
            return;
        }

        if (!AsGm && (_old.IsSecret || doc.OwnerUserId != UserId))
        {
            Failed = RerollFailure.NotAllowed;
            return;
        }

        if (Rerolls.Held(runtime.LiveSheetOf(doc), spend.Track) < spend.By)
        {
            Failed = RerollFailure.Short;
            return;
        }

        _spent = TableEvents.Spend(runtime, ActorId, spend.Track, spend.By);
        var made = _spent.Failed == null ? Again(runtime, system, doc, _old) : null;
        if (made == null)
        {
            // Nothing was rolled, so nothing is kept: the cost goes back.
            if (_spent.Failed == null) TableEvents.Spend(runtime, ActorId, spend.Track, -spend.By);
            _spent = TableEvents.Outcome.None;
            Failed = RerollFailure.CouldNotRoll;
            return;
        }

        made.RerollOf = _old.Id;
        made.RerollKey = _reroll.Key;
        runtime.MarkRerolled(_old, made);
        Made = made;
        Standing = _reroll.KeepsHigher && _old.Total > made.Total ? _old : made;
    }

    private RollLogEntry? Again(CampaignRuntime runtime, SystemDefinition system, ActorDocument doc, RollLogEntry old)
    {
        var replay = old.Replay!;
        switch (replay.Kind)
        {
            case RollReplay.OfExpression:
                DiceRollResult result;
                try
                {
                    result = runtime.DiceRoller.Roll(old.Expression);
                }
                catch (FormatException)
                {
                    return null;
                }

                var entry = new RollLogEntry
                {
                    ActorId = ActorId,
                    RollerUserId = UserId,
                    Expression = old.Expression,
                    Result = result.ToString(),
                    Total = result.Total,
                    IsSecret = old.IsSecret,
                    Label = old.Label,
                    Kept = result.Kept.ToList(),
                    Dropped = result.Dropped.ToList(),
                    Modifier = result.Modifier,
                    CheckDieIndex = result.CheckDieIndex
                };
                // Against the same number as the first roll, so its degree is worked out again.
                CheckGrading.Apply(entry, system, runtime.Resolver, runtime.LiveSheetOf(doc), replay.Dc);
                runtime.RecordRoll(entry);
                return entry;
            case RollReplay.OfDefinition:
                var request = new SheetRollRequest
                {
                    Definition = replay.RollKey ?? string.Empty,
                    Inputs = replay.Inputs,
                    SuppliedBy = replay.SuppliedBy,
                    OptIns = replay.OptIns
                };
                var outcome = TableEvents.Roll(runtime, ActorId, request, Dice ?? new RandomDiceSource(), out var rolled, applyIntents: false);
                if (rolled == null || outcome.Failed != null) return null;
                Rolled = rolled;
                var made = MakeRollCommand.EntryOf(runtime, ActorId, UserId, rolled, old.IsSecret);
                runtime.RecordRoll(made);
                return made;
            case RollReplay.OfCheck:
                var check = runtime.Checks.Make(new CheckRequest
                {
                    ActorId = ActorId,
                    Check = replay.Check ?? string.Empty,
                    Kind = Enum.TryParse<CheckKind>(replay.CheckKind, out var kind) ? kind : null,
                    Dc = replay.Dc,
                    Bonus = replay.Bonus,
                    Dice = replay.Dice,
                    OptIns = replay.OptIns,
                    Secret = old.IsSecret
                }, UserId);
                return check.Failed == CheckFailure.None ? check.Logged : null;
            default:
                return null;
        }
    }

    public SessionEvent Describe(CampaignRuntime runtime)
    {
        var actor = runtime.ActorTerm(ActorId);
        var system = runtime.System;
        var cost = new EventTerm(EventTerm.SystemKind, _reroll?.Label is { Length: > 0 } label ? label : RerollKey, system?.DisplayValue(_reroll?.Label is { Length: > 0 } l ? l : RerollKey) ?? RerollKey);
        EventMessage message;
        var parts = new List<EventMessage>();
        if (Failed != RerollFailure.None)
        {
            message = Failed == RerollFailure.Short ? EventMessage.Of("Roll.Reroll.Short", actor, cost) : EventMessage.Of("Roll.Reroll.Refused", actor);
        }
        else if (Made!.IsSecret)
        {
            message = EventMessage.Of("Roll.Reroll.Secret", actor);
        }
        else
        {
            var what = _old!.Name ?? _old.Label ?? _old.Expression;
            var named = new EventTerm(EventTerm.SystemKind, what, system?.DisplayValue(what) ?? what);
            message = EventMessage.Of(_reroll!.KeepsHigher ? "Roll.RerolledHigher" : "Roll.Rerolled", actor, cost, named, Made.Total, _old.Total);
            parts.AddRange(_spent.Messages);
        }

        parts.Insert(0, message);
        return SessionEvent.From("DiceRoll", parts, new[] { ActorId });
    }
}
