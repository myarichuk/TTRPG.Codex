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

    /// <summary>Event types whose account carries someone's exact numbers (hit points, dice spent). One without a players'
    /// version is from before the version existed, or a new command that forgot it: other players don't get it at all,
    /// rather than the exact text (fail closed).</summary>
    private static readonly HashSet<string> NumberBearingTypes = new(StringComparer.Ordinal)
    {
        "Damage", "Healing", "TemporaryHitPoints", "HitDice"
    };

    /// <summary>The log as <paramref name="access"/> may read it. <paramref name="visibleActors"/> is what the
    /// actor repository returns for that viewer (a player's list already omits Hidden actors).</summary>
    public static IReadOnlyList<SessionEvent> ForViewer(IEnumerable<SessionEvent> events, CampaignAccess access, IEnumerable<ActorDocument> visibleActors)
    {
        if (access.IsDm)
        {
            return events.ToList();
        }

        var actors = visibleActors.GroupBy(a => a.Id).Select(g => g.First()).ToList();
        return Project(events,
            actors.ToDictionary(a => a.Id, a => a.OwnerUserId == access.UserId ? a.Name : a.NameFor(isDm: false)),
            actors.Where(a => a.OwnerUserId == access.UserId).Select(a => a.Id).ToHashSet());
    }

    /// <summary>The log for a player-facing audience as a whole (the player digest): only fully known actors, and
    /// nobody's numbers are exact.</summary>
    public static IReadOnlyList<SessionEvent> ForPlayers(IEnumerable<SessionEvent> events, IEnumerable<ActorDocument> actors) =>
        Project(events, actors.Where(a => a.Visibility == ActorVisibility.Known).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Name), new HashSet<string>());

    /// <param name="names">The actors this audience may see, by the name it knows each by.</param>
    private static List<SessionEvent> Project(IEnumerable<SessionEvent> events, IReadOnlyDictionary<string, string> names, HashSet<string> ownedIds)
    {
        var visibleIds = names.Keys.ToHashSet();
        var result = new List<SessionEvent>();
        foreach (var evt in events)
        {
            if (evt.ActorIds.Count == 0 ? ActorBearingTypes.Contains(evt.Type) : evt.ActorIds.Any(id => !visibleIds.Contains(id)))
            {
                continue;
            }

            var own = evt.ActorIds.Count > 0 && evt.ActorIds.All(ownedIds.Contains);
            if (evt.PlayerDescription == null && !own && NumberBearingTypes.Contains(evt.Type))
            {
                continue;
            }

            var exact = evt.PlayerDescription == null || own;
            var parts = (exact ? evt.Parts : evt.PlayerParts ?? new List<EventMessage>()).Select(p => Named(p, names)).ToList();
            result.Add(new SessionEvent
            {
                Type = evt.Type,
                Timestamp = evt.Timestamp,
                ActorIds = evt.ActorIds,
                // Written again from the renamed messages, so no copy of the event holds a name this audience may not know.
                Description = parts.Count > 0 ? string.Join(" ", parts.Select(p => p.Render(English))) : exact ? evt.Description : evt.PlayerDescription!,
                Parts = parts
            });
        }

        return result;
    }

    private static readonly Codex.Localization.Localizer English = Codex.Localization.Localizer.For("en");

    /// <summary>The message with every actor named as this audience knows them (F20): the log stores real names, so a stranger
    /// the party has met would otherwise be named for real, and a hidden creature named as where damage came from.</summary>
    private static EventMessage Named(EventMessage message, IReadOnlyDictionary<string, string> names)
    {
        var copy = new EventMessage
        {
            Key = message.Key,
            Args = message.Args.ToList(),
            Kinds = message.Kinds.ToList(),
            Inner = message.Inner.Select(inner => Named(inner, names)).ToList()
        };
        for (var i = 0; i < copy.Kinds.Count && i < copy.Args.Count; i++)
        {
            if (!copy.Kinds[i].StartsWith(EventTerm.ActorKind + ":", StringComparison.Ordinal)) continue;
            if (names.TryGetValue(copy.Kinds[i][(EventTerm.ActorKind.Length + 1)..], out var name))
            {
                copy.Args[i] = name;
            }
            else
            {
                copy.Args[i] = SessionQuotes.UnknownSpeaker.ToLowerInvariant();
                copy.Kinds[i] = EventTerm.UnseenKind + ":";
            }
        }

        return copy;
    }
}
