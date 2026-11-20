using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>One reroll a viewer may press on a roll: the system's key, its name (a word of the system, said in the reader's language by the page), and whether
/// the actor can pay for it now.</summary>
public sealed record RerollChoice(string Key, string Label, bool CanPay, int Cost = 1, string Track = "");

/// <summary>
/// What the table shows a viewer who is not the DM. Their actor list already leaves out Hidden actors, so anything the
/// console renders from the runtime's full lists (the order, the roll log) is cut down to the actors in that list here.
/// Split out of <see cref="CombatConsole"/> so it can be tested without rendering the component (S2).
/// </summary>
public static class TableVisibility
{
    /// <summary>"Turn 2 of 4" for this viewer. A player counts only the rows they can see, and gets no count while an
    /// unseen participant acts, so neither the order's length nor a hidden ambusher's place in it shows.</summary>
    public static (int Position, int Count)? TurnPosition(EncounterSnapshot encounter, bool isDm, IReadOnlySet<string> visibleActorIds)
    {
        if (isDm) return (encounter.TurnIndex + 1, encounter.Participants.Count);
        var position = 0;
        var count = 0;
        for (var i = 0; i < encounter.Participants.Count; i++)
        {
            if (!visibleActorIds.Contains(encounter.Participants[i].ActorId)) continue;
            count++;
            if (i == encounter.TurnIndex) position = count;
        }

        return position == 0 ? null : (position, count);
    }

    /// <summary>The roll a player just made for <paramref name="actorId"/>, read back from the shared log: the latest entry of theirs with that label,
    /// never a secret one and never one another user rolled. A secret check the GM made for the same actor (a hazard's Perception) can land in the log
    /// between the player's roll and this read; it is not theirs to see.</summary>
    public static RollLogEntry? OwnLast(IEnumerable<RollLogEntry> log, string actorId, string? label, string rollerUserId) =>
        log.LastOrDefault(r => r.ActorId == actorId && r.Label == label && !r.IsSecret && r.RollerUserId == rollerUserId);

    /// <summary>The rerolls this viewer may press on a roll: the system's, when the roll can be rerolled, for the GM and the actor's owner (a secret roll for the GM
    /// alone), each with whether the actor can pay now. Empty otherwise.</summary>
    public static IReadOnlyList<RerollChoice> RerollsFor(SystemDefinition? system, RollLogEntry entry, bool isDm, bool isOwner, ActorStateSnapshot? state, ActorDocument? actor)
    {
        if (system == null || actor == null || !(isDm || isOwner) || (entry.IsSecret && !isDm)) return Array.Empty<RerollChoice>();
        var kind = Codex.Core.Rules.SheetLayout.KindOf(system, actor.Sheet);
        return Rerolls.Offered(system, entry, actor.Sheet)
            .Select(r => new RerollChoice(r.Key, r.Label.Length > 0 ? r.Label : r.Key, Rerolls.CanPay(system, r, state, actor.Sheet), r.Spend!.By,
                kind?.Tracks.FirstOrDefault(t => t.Key == r.Spend.Track)?.Label is { Length: > 0 } track ? track : r.Spend.Track))
            .ToList();
    }

    /// <summary>The rerolls on the sheet page's last roll: only a roll that is in the table's shared log (as the table holds it now, so one already rerolled
    /// is not offered again) and only to someone who can edit the sheet (the GM or its owner).</summary>
    public static IReadOnlyList<RerollChoice> RerollsOnSheet(SystemDefinition? system, RuntimeSnapshot snapshot, RollLogEntry? roll, ActorDocument? actor, bool isDm, bool canEdit)
    {
        if (roll == null || actor == null || !canEdit) return Array.Empty<RerollChoice>();
        return snapshot.RollLog.FirstOrDefault(r => r.Id == roll.Id) is { } current
            ? RerollsFor(system, current, isDm, true, snapshot.Actor(actor.Id), actor)
            : Array.Empty<RerollChoice>();
    }

    /// <summary>The latest rolls this viewer may see: no secret rolls, and none made for an actor they cannot see, since a
    /// hidden monster's initiative or attack would announce it, dice and all, the moment the DM rolled for it (as the
    /// session log already avoids).</summary>
    public static IEnumerable<RollLogEntry> Rolls(IEnumerable<RollLogEntry> log, bool isDm, IReadOnlySet<string> visibleActorIds, int take = 20) =>
        log.Where(entry => isDm || (!entry.IsSecret && (entry.ActorId == null || visibleActorIds.Contains(entry.ActorId))))
            .OrderByDescending(entry => entry.Timestamp)
            .Take(take);
}
