using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Newtonsoft.Json.Linq;

namespace Codex.Persistence.Runtime;

/// <summary>A hazard placed on a place, as the table works on it: the Library entry resolved, and the placement as it was stored when the table read it
/// (HAZARDS_DESIGN.md 4.1, 11). The command takes it whole, because a command runs on the loop and the loop reads no repository.</summary>
public sealed record HazardSite(string RegionId, string LocationId, string Name, PlacedHazard Stored, ContentEntry Entry, ContentTypeDefinition Type);

/// <summary>What the running encounter holds of one hazard: the placement as the encounter took it (<see cref="Taken"/>) and as the table has it now
/// (<see cref="Current"/>). Kept in <c>EncounterDocument.ActiveEffects</c>, so a table that restarts finds it again.</summary>
public sealed class LiveHazard
{
    public string RegionId { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PlacedHazard Taken { get; set; } = new();
    public PlacedHazard Current { get; set; } = new();
}

/// <summary>
/// The live state of the hazards the table has worked on, owned by the runtime's loop. While an encounter runs, a hazard's state lives in the encounter's
/// <c>ActiveEffects</c> (key <see cref="KeyOf"/>) and is written to its place when the encounter ends, unless the place's hazard was changed meanwhile or
/// nothing changed. Outside an encounter a change is written to the place at once. Both writes are patches of <c>PlaceFeature.Hazard</c> alone
/// (<see cref="IRegionRepository.PatchPlacedHazardAsync"/>), never the whole place.
/// </summary>
public sealed class HazardLedger(CampaignRuntime runtime)
{
    public const string KeyPrefix = "hazard:";

    private readonly List<(string RegionId, string LocationId, string Name, Func<PlacedHazard, PlacedHazard?> Change)> _pending = new();

    /// <summary>The key of a place's hazard in <c>EncounterDocument.ActiveEffects</c> and in the snapshot.</summary>
    public static string KeyOf(string regionId, string locationId) => $"{KeyPrefix}{regionId}/{locationId}";

    /// <summary>A copy of the placement as the table has it now: the encounter's live state when it holds one, else the placement as stored.</summary>
    public PlacedHazard Begin(HazardSite site)
    {
        if (runtime.ActiveEncounter is { } encounter
            && encounter.ActiveEffects.TryGetValue(KeyOf(site.RegionId, site.LocationId), out var raw) && Read(raw) is { } live)
        {
            return Copy(live.Current);
        }

        return Copy(site.Stored);
    }

    // The last change to each hazard, as the state just before and just after it, with the damage a trigger dealt (each creature and its own undo point): what the
    // hazard's undo puts back, all of it or none, while nothing has changed it or those creatures since.
    private sealed record UndoPoint(PlacedHazard Before, PlacedHazard After, IReadOnlyList<(string ActorId, object Token)> Damage);

    private readonly Dictionary<string, UndoPoint> _undo = new(StringComparer.Ordinal);

    /// <summary>The keys of the hazards whose last change can still be offered to undo (the panel's button; <see cref="Undo"/> says whether it still can).</summary>
    public IReadOnlyCollection<string> Undoable() => _undo.Keys.ToList();

    /// <summary>Keeps <paramref name="changed"/> as the hazard's state: in the running encounter, or written to the place after the command. <paramref name="before"/> is the
    /// state the command began from, kept with it as the hazard's last change, together with the damage the command dealt (<paramref name="damage"/>). A command that
    /// <paramref name="joins"/> finishes what an earlier one began (the creatures that were waiting for a number): its damage is added to that change's undo point.</summary>
    public void Commit(HazardSite site, PlacedHazard before, PlacedHazard changed, IReadOnlyList<(string ActorId, object Token)>? damage = null, bool joins = false)
    {
        var key = KeyOf(site.RegionId, site.LocationId);
        var hit = damage ?? Array.Empty<(string, object)>();
        if (joins && hit.Count > 0 && _undo.TryGetValue(key, out var open) && SameState(open.After, before))
        {
            _undo[key] = open with { After = Copy(changed), Damage = open.Damage.Concat(hit).ToList() };
        }
        else if (!SameState(before, changed) || hit.Count > 0)
        {
            _undo[key] = new UndoPoint(Copy(before), Copy(changed), hit.ToList());
        }

        Store(site, before, changed);
    }

    private void Store(HazardSite site, PlacedHazard before, PlacedHazard changed)
    {
        if (runtime.ActiveEncounter is { } encounter)
        {
            var key = KeyOf(site.RegionId, site.LocationId);
            var taken = encounter.ActiveEffects.TryGetValue(key, out var raw) && Read(raw) is { } live ? live.Taken : Copy(site.Stored);
            encounter.ActiveEffects[key] = new LiveHazard { RegionId = site.RegionId, LocationId = site.LocationId, Name = site.Name, Taken = taken, Current = Copy(changed) };
            runtime.MarkEncounterDirty();
            return;
        }

        // A hazard taken from the Library alone (no place) has nothing to write to.
        if (site.RegionId.Length == 0) return;

        // Written only over the state the command began from: a place changed meanwhile (by another table, or on its page) is not written over.
        var was = Copy(before);
        var now = Copy(changed);
        _pending.Add((site.RegionId, site.LocationId, site.Name, stored => SameState(stored, was) ? Merge(stored, now) : null));
    }

    /// <summary>Takes back the hazard's last change and the damage it dealt, as one: only while the hazard still stands as that change left it (the encounter's live copy,
    /// else <paramref name="site"/>'s stored placement) and every creature it hurt still has that damage as its last change. Otherwise nothing is touched. Null when it was
    /// taken back, else why not: <c>CannotUndo</c> (nothing to take back, or the hazard changed) or <c>DamageChanged</c> (a creature changed since).</summary>
    public string? Undo(HazardSite site)
    {
        var key = KeyOf(site.RegionId, site.LocationId);
        if (!_undo.TryGetValue(key, out var point)) return "CannotUndo";
        var now = Begin(site);
        if (!SameState(now, point.After)) return "CannotUndo";
        if (point.Damage.Any(d => !ReferenceEquals(runtime.UndoToken(d.ActorId), d.Token))) return "DamageChanged";
        foreach (var (actorId, _) in point.Damage) runtime.Undo(actorId);
        _undo.Remove(key);
        Store(site, point.After, point.Before);
        return null;
    }

    /// <summary>The live copies of the running encounter's hazards, by key, for the snapshot.</summary>
    public IReadOnlyDictionary<string, PlacedHazard> Live()
    {
        var live = new Dictionary<string, PlacedHazard>(StringComparer.Ordinal);
        if (runtime.ActiveEncounter is not { } encounter) return live;
        foreach (var (key, raw) in encounter.ActiveEffects)
        {
            if (key.StartsWith(KeyPrefix, StringComparison.Ordinal) && Read(raw) is { } hazard) live[key] = Copy(hazard.Current);
        }

        return live;
    }

    /// <summary>The encounter ended: each hazard it holds that changed is queued to be written to its place, unless the place's hazard is no longer what the
    /// encounter took (the game master changed it on the place's page meanwhile: the table does not write over that). The encounter forgets them.</summary>
    public void WriteBack(EncounterDocument ended)
    {
        foreach (var key in ended.ActiveEffects.Keys.Where(k => k.StartsWith(KeyPrefix, StringComparison.Ordinal)).ToList())
        {
            if (Read(ended.ActiveEffects[key]) is { RegionId.Length: > 0 } live && !SameState(live.Current, live.Taken))
            {
                var taken = live.Taken;
                var current = live.Current;
                _pending.Add((live.RegionId, live.LocationId, live.Name, stored => SameState(stored, taken) ? Merge(stored, current) : null));
            }

            ended.ActiveEffects.Remove(key);
        }
    }

    /// <summary>Writes what the loop queued, once per command: a patch of each placement, nothing else. Each write stands alone: one that fails or is refused (the place
    /// changed meanwhile, so the table's state is not written over it) does not stop the others or the command's log, and is said as a GM-only line, returned for the log.</summary>
    public async Task<IReadOnlyList<EventMessage>> FlushAsync()
    {
        var notes = new List<EventMessage>();
        if (_pending.Count == 0) return notes;
        var writes = _pending.ToList();
        _pending.Clear();
        if (runtime.Places is not { } places) return notes;
        foreach (var (regionId, locationId, name, change) in writes)
        {
            try
            {
                if (!await places.PatchPlacedHazardAsync(runtime.CampaignId, regionId, locationId, change))
                {
                    notes.Add(EventMessage.Of("Hazard.NotWritten", EventTerm.GmOnly(name)));
                }
            }
            catch (Exception)
            {
                notes.Add(EventMessage.Of("Hazard.WriteFailed", EventTerm.GmOnly(name)));
            }
        }

        return notes;
    }

    /// <summary>The place in the order a complex hazard gave up when it was disarmed or destroyed, kept in <c>EncounterDocument.ActiveEffects</c> under
    /// <see cref="SeatPrefix"/> and the actor's id so that taking that back puts it in the same place.</summary>
    public sealed class Seat
    {
        public int InitiativeRoll { get; set; }
        public int TieBreak { get; set; }
    }

    /// <summary>Not <see cref="KeyPrefix"/>: the write-back at the end of an encounter must not read a seat as a hazard.</summary>
    public const string SeatPrefix = "hazardseat:";

    /// <summary>The key of a complex hazard actor's live state: its placement's, or, for one taken from the Library alone, one of its own.</summary>
    public static string KeyOf(ActorDocument actor) =>
        actor.Hazard is { IsPlaced: true } link ? KeyOf(link.RegionId, link.LocationId) : KeyOf(string.Empty, actor.Id);

    /// <summary>Keeps the initiative order in step with the complex hazards at the table (HAZARDS_DESIGN.md 3.3): one that is disabled (as the encounter holds it live) or
    /// destroyed (no hit points left, or dead) leaves the order and keeps its place; one that stands again (the disarming or the damage was undone, or the game master set its
    /// state back) returns to that place. A hazard the game master took out of the order by hand has no kept place and is not put back. Run after every command.</summary>
    public void SyncInitiative()
    {
        if (runtime.ActiveEncounter is not { } encounter) return;
        foreach (var doc in runtime.HazardActorDocs().ToList())
        {
            var inOrder = encounter.Participants.FirstOrDefault(p => p.ActorId == doc.Id);
            var seatKey = SeatPrefix + doc.Id;
            var off = (encounter.ActiveEffects.TryGetValue(KeyOf(doc), out var raw) && Read(raw) is { } live && live.Current.State == HazardState.Disabled)
                || (runtime.GetEntity(doc.Id) is { } entity && TableRules.IsDestroyed(runtime, entity));
            if (off && inOrder != null)
            {
                encounter.ActiveEffects[seatKey] = new Seat { InitiativeRoll = inOrder.InitiativeRoll, TieBreak = inOrder.TieBreak };
                // Leaving on its own turn in a fight under way passes the turn on (RemoveParticipant), and whoever inherits it begins it: the sheet's turnStart events and
                // the valued conditions that fall at the start of a turn are raised, as "Next turn" raises them.
                var heldTheTurn = encounter.Participants.IndexOf(inOrder) == encounter.TurnIndex
                    && (encounter.Round > 1 || inOrder.TurnStarted || encounter.Participants.Any(p => p.HasActed));
                runtime.RemoveParticipant(doc.Id);
                _syncNotes.Add(EventMessage.Of("Hazard.Left", EventTerm.GmOnly(doc.Name)));
                if (heldTheTurn && encounter.Participants.Count > 0)
                {
                    var next = encounter.Participants[encounter.TurnIndex].ActorId;
                    if (TableEvents.Handles(runtime, "turnStart")) _turnNotes.AddRange(TableEvents.Raise(runtime, next, new SheetEvent("turnStart", new Dictionary<string, object?>())).Messages);
                    _turnNotes.AddRange(runtime.DecreaseConditionsUndoably(next, "turnStart"));
                    _turnActor = next;
                }
            }
            else if (!off && inOrder == null && encounter.ActiveEffects.TryGetValue(seatKey, out var seatRaw))
            {
                if (ReadSeat(seatRaw) is { } seat && runtime.RestoreParticipant(doc.Id, seat.InitiativeRoll, seat.TieBreak))
                {
                    _syncNotes.Add(EventMessage.Of("Hazard.Returned", EventTerm.GmOnly(doc.Name), seat.InitiativeRoll));
                }

                encounter.ActiveEffects.Remove(seatKey);
                runtime.MarkEncounterDirty();
            }
        }
    }

    // What SyncInitiative did, said in the log after the command: GM-only lines (a hazard left or returned) and, apart from them, what the creature who inherited the turn
    // begins with (a turn event the players see as they see any turn start, never labelled as the hazard's).
    private readonly List<EventMessage> _syncNotes = new();
    private readonly List<EventMessage> _turnNotes = new();
    private string? _turnActor;

    /// <summary>The GM-only lines <see cref="SyncInitiative"/> has to say since the last call, and the turn-start lines of the creature who inherited the turn (with its id); the loop logs each as its own event.</summary>
    public (IReadOnlyList<EventMessage> GmOnly, IReadOnlyList<EventMessage> TurnStart, string? Actor) TakeSyncNotes()
    {
        var taken = (_syncNotes.ToList(), _turnNotes.ToList(), _turnActor);
        _syncNotes.Clear();
        _turnNotes.Clear();
        _turnActor = null;
        return taken;
    }

    private static Seat? ReadSeat(object? raw) => raw switch
    {
        Seat seat => seat,
        JToken token => token.ToObject<Seat>(),
        System.Collections.IDictionary map => JObject.FromObject(map).ToObject<Seat>(),
        _ => null
    };

    /// <summary>A live hazard as an encounter holds it: the object itself in memory, or what the database returns for it after the encounter is restored.</summary>
    internal static LiveHazard? Read(object? raw) => raw switch
    {
        LiveHazard live => live,
        JToken token => token.ToObject<LiveHazard>(),
        System.Collections.IDictionary map => JObject.FromObject(map).ToObject<LiveHazard>(),
        _ => null
    };

    public static PlacedHazard Copy(PlacedHazard hazard) => new()
    {
        Entry = hazard.Entry,
        IsHomebrew = hazard.IsHomebrew,
        Levels = hazard.Levels,
        State = hazard.State,
        Successes = hazard.Successes,
        NoticedBy = hazard.NoticedBy.ToList(),
        Found = hazard.Found.ToList()
    };

    private static bool SameEntry(PlacedHazard a, PlacedHazard b) => a.Entry == b.Entry && a.IsHomebrew == b.IsHomebrew;

    /// <summary>The same entry, version, state, count and lists (in any order).</summary>
    public static bool SameState(PlacedHazard a, PlacedHazard b) =>
        SameEntry(a, b) && a.Levels == b.Levels && a.State == b.State && a.Successes == b.Successes
        && a.NoticedBy.ToHashSet(StringComparer.Ordinal).SetEquals(b.NoticedBy) && a.Found.ToHashSet(StringComparer.Ordinal).SetEquals(b.Found);

    /// <summary>The stored placement with the table's state laid over it.</summary>
    private static PlacedHazard Merge(PlacedHazard stored, PlacedHazard now)
    {
        var merged = Copy(stored);
        merged.State = now.State;
        merged.Successes = now.Successes;
        merged.NoticedBy = now.NoticedBy.ToList();
        merged.Found = now.Found.ToList();
        return merged;
    }
}
