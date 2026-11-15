using Codex.Persistence;
using Codex.Persistence.Runtime;

namespace Codex.Web.Components.Shared;

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

    /// <summary>The latest rolls this viewer may see: no secret rolls, and none made for an actor they cannot see, since a
    /// hidden monster's initiative or attack would announce it, dice and all, the moment the DM rolled for it (as the
    /// session log already avoids).</summary>
    public static IEnumerable<RollLogEntry> Rolls(IEnumerable<RollLogEntry> log, bool isDm, IReadOnlySet<string> visibleActorIds, int take = 20) =>
        log.Where(entry => isDm || (!entry.IsSecret && (entry.ActorId == null || visibleActorIds.Contains(entry.ActorId))))
            .OrderByDescending(entry => entry.Timestamp)
            .Take(take);
}
