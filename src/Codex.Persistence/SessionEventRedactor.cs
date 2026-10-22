namespace Codex.Persistence;

/// <summary>
/// The session log is written for the DM: real names, exact damage. This is the one place that
/// turns it into what a given viewer may read, using the same rules the live table applies: an
/// actor the viewer can't see is left out entirely, and anyone else's hit points are never exact.
/// </summary>
public static class SessionEventRedactor
{
    /// <summary>Event types that name an actor but predate <see cref="SessionEvent.ActorIds"/>. Without ids they
    /// can't be checked against visibility, so a player never receives them.</summary>
    private static readonly HashSet<string> ActorBearingTypes = new(StringComparer.Ordinal)
    {
        "Damage", "Healing", "StatusEffect", "StatusEffectRemoved", "TurnAdvance", "Initiative", "TurnState",
        "DiceRoll", "ActorJoined", "ResourceSpent", "ResourceRegained", "Rest",
        "TemporaryHitPoints", "DeathSave", "Stabilized", "Exhaustion", "HitDice", "InitiativeRemoved", "ActorLeft", "SheetEdited"
    };

    /// <summary>The log as <paramref name="access"/> may read it. <paramref name="visibleActors"/> is what the
    /// actor repository returns for that viewer (a player's list already omits Hidden actors).</summary>
    public static IReadOnlyList<SessionEvent> ForViewer(IEnumerable<SessionEvent> events, CampaignAccess access, IEnumerable<ActorDocument> visibleActors)
    {
        if (access.IsDm)
        {
            return events.ToList();
        }

        var actors = visibleActors.ToList();
        return Project(events,
            actors.Select(a => a.Id).ToHashSet(),
            actors.Where(a => a.OwnerUserId == access.UserId).Select(a => a.Id).ToHashSet());
    }

    /// <summary>The log for a player-facing audience as a whole (the player digest): only fully known actors, and
    /// nobody's numbers are exact.</summary>
    public static IReadOnlyList<SessionEvent> ForPlayers(IEnumerable<SessionEvent> events, IEnumerable<ActorDocument> actors) =>
        Project(events, actors.Where(a => a.Visibility == ActorVisibility.Known).Select(a => a.Id).ToHashSet(), new HashSet<string>());

    private static List<SessionEvent> Project(IEnumerable<SessionEvent> events, HashSet<string> visibleIds, HashSet<string> ownedIds)
    {
        var result = new List<SessionEvent>();
        foreach (var evt in events)
        {
            if (evt.ActorIds.Count == 0 ? ActorBearingTypes.Contains(evt.Type) : evt.ActorIds.Any(id => !visibleIds.Contains(id)))
            {
                continue;
            }

            var exact = evt.PlayerDescription == null || (evt.ActorIds.Count > 0 && evt.ActorIds.All(ownedIds.Contains));
            result.Add(new SessionEvent
            {
                Type = evt.Type,
                Timestamp = evt.Timestamp,
                ActorIds = evt.ActorIds,
                Description = exact ? evt.Description : evt.PlayerDescription!
            });
        }

        return result;
    }
}
