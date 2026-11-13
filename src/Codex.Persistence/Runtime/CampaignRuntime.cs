using System.Collections.Concurrent;
using System.Threading.Channels;
using Codex.Core;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using DefaultEcs;
using Microsoft.Extensions.Logging;

namespace Codex.Persistence.Runtime;

/// <summary>
/// One live campaign's ECS state (3.1), replacing the single <c>CodexWorld</c> DI singleton every
/// campaign and every circuit used to share (B8 - not thread-safe, and one bad script or a stray
/// demo click advanced every duration on the server). Every mutation goes through
/// <see cref="EnqueueAsync"/> onto a <see cref="Channel{T}"/> drained by exactly one loop, so the
/// underlying <see cref="CodexWorld"/> is only ever touched from that one thread: no locks, no
/// races, deterministic ordering. After each command, the actors it touched are persisted in one
/// Raven write - a table's command rate (~1/s) makes write-through cheap and simpler than a
/// timed flush.
/// </summary>
public sealed class CampaignRuntime : IAsyncDisposable
{
    public string CampaignId { get; }
    public CodexWorld World { get; } = new();

    private readonly IActorRepository _actorRepository;
    private readonly ComponentRegistry _componentRegistry;
    private readonly ISessionRepository? _sessionRepository;
    private readonly SessionDocument? _activeSession;
    private readonly IEncounterRepository? _encounterRepository;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Entity> _actorEntities = new();
    private readonly Dictionary<string, ActorDocument> _actorDocs = new();
    private readonly HashSet<string> _dirtyActorIds = new();
    // Actors whose sheet was replaced by ReplaceActorCommand, so the next persist also writes the
    // editable fields (the value says whether the editor was the DM). Everything else is a State patch.
    private readonly Dictionary<string, bool> _pendingSheetWrites = new();
    // Sheet keys the table itself changed (an event's intents: a slot track, the sheet's conditions), by actor: the next persist
    // writes exactly these from the cached sheet, so a field nobody at the table touched is never written over.
    private readonly Dictionary<string, HashSet<string>> _pendingSheetKeys = new();
    // The campaign's clocks and tables (overhaul step 7), loop-owned like the actors, and the keys the table changed since the
    // last persist: the persist writes exactly those, so a key nobody at the table touched is never written over.
    private Dictionary<string, object> _campaignObjects = new();
    private readonly HashSet<string> _pendingCampaignKeys = new(StringComparer.Ordinal);
    // Hydrated ids, readable from any thread: lets a caller decide between the runtime and the
    // repository without touching the loop-owned dictionaries.
    private readonly ConcurrentDictionary<string, byte> _hydratedIds = new();
    // Session rows appended since the last flush. The session is persisted by appending these to
    // the stored document, never by storing the runtime's copy whole (F3).
    private readonly List<SessionEvent> _pendingEvents = new();
    private readonly List<RollLogEntry> _pendingRolls = new();
    private readonly List<SessionNote> _pendingNotes = new();
    // Corrections and removals of notes already stored, by id, in order: a null note means removed.
    private readonly List<(string Id, SessionNote? Note)> _pendingNoteChanges = new();
    private readonly List<string> _pendingEncounterIds = new();
    private volatile RuntimeSnapshot _snapshot = RuntimeSnapshot.Empty;
    private EncounterDocument? _activeEncounter;
    // The encounter the next persist writes: the live one when a command changed it, or the one
    // just ended (which is no longer active but still has to be saved with its end time).
    private EncounterDocument? _encounterToPersist;
    // Bounded: a page stuck in a loop, or a flood of clicks, fills this and is refused instead of growing the queue without
    // end. A table at play never has more than a few commands waiting.
    internal const int CommandQueueCapacity = 256;
    private readonly Channel<QueuedCommand> _commands = Channel.CreateBounded<QueuedCommand>(
        new BoundedChannelOptions(CommandQueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>This campaign system's dice roller (3.6) - <see cref="StandardDiceRoller"/> unless
    /// the plugin overrides it (SWFFG's narrative dice, eventually).</summary>
    public IDiceRoller DiceRoller { get; }

    /// <summary>The encounter currently in progress, if any (3.5) - null until the first
    /// initiative roll of a session creates one via <see cref="GetOrCreateEncounter"/>, or the
    /// session's unfinished encounter restored when the table started.</summary>
    public EncounterDocument? ActiveEncounter => _activeEncounter;

    /// <summary>The campaign's system, for the table rules (<see cref="SystemDefinition.Vitals"/>,
    /// dice conventions). Null for a bare runtime, which then applies plain clamped damage.</summary>
    public SystemDefinition? System { get; }

    /// <summary>The campaign's library for the rules the table runs (an event handler reading an item, a track's maximum from a
    /// class): set by the host when the table starts. None knows no entries.</summary>
    public Codex.Core.Rules.IEntryResolver Resolver { get; set; } = Codex.Core.Rules.NullEntryResolver.Instance;

    /// <summary>Where the campaign's clocks and tables are written back; set by the host when the table starts. None: they
    /// change at the table and are not stored.</summary>
    public ICampaignObjectsRepository? CampaignObjectsStore { get; set; }

    /// <summary>The live session commands log to (3.2) and read back from - a combat console
    /// reads <see cref="SessionDocument.RollLog"/> and <see cref="SessionDocument.Notes"/>
    /// directly off this rather than re-querying Raven on every render.</summary>
    public SessionDocument? ActiveSession => _activeSession;

    /// <summary>When the runtime started or last accepted a command; idle eviction measures from here.</summary>
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    /// <summary>The latest immutable picture of the table, republished after every command and
    /// before <see cref="StateChanged"/> fires. A render must read this, never
    /// <see cref="ActiveEncounter"/>, <see cref="ActiveSession"/> or the entities, which the
    /// command loop mutates on its own thread (F13).</summary>
    public RuntimeSnapshot Snapshot => _snapshot;

    /// <param name="sessionRepository">Persists <paramref name="activeSession"/>'s appended events (3.2).
    /// Optional so a caller that only needs bare ECS mutation (e.g. a unit test exercising a single
    /// command in isolation) doesn't have to stand up a session too; when omitted, commands still
    /// apply but nothing is logged.</param>
    /// <param name="activeSession">The table's live <see cref="SessionDocument"/> - every applied
    /// command's <see cref="IRuntimeCommand.Describe"/> is appended to its <see cref="SessionDocument.Events"/>.</param>
    /// <param name="encounterRepository">Persists the live <see cref="ActiveEncounter"/> (3.5).
    /// Optional for the same reason <paramref name="sessionRepository"/> is - a bare-ECS test has
    /// no initiative tracker to persist.</param>
    /// <param name="diceRoller">This campaign's <see cref="IDiceRoller"/> (3.6); defaults to
    /// <see cref="StandardDiceRoller"/> when the plugin doesn't provide its own.</param>
    /// <param name="system">The system definition whose table rules apply.</param>
    /// <param name="activeEncounter">The session's unfinished encounter, restored as the live one.</param>
    public CampaignRuntime(
        string campaignId,
        IActorRepository actorRepository,
        ComponentRegistry componentRegistry,
        ILogger logger,
        ISessionRepository? sessionRepository = null,
        SessionDocument? activeSession = null,
        IEncounterRepository? encounterRepository = null,
        IDiceRoller? diceRoller = null,
        SystemDefinition? system = null,
        EncounterDocument? activeEncounter = null)
    {
        CampaignId = campaignId;
        _actorRepository = actorRepository;
        _componentRegistry = componentRegistry;
        _logger = logger;
        _sessionRepository = sessionRepository;
        _activeSession = activeSession;
        _encounterRepository = encounterRepository;
        DiceRoller = diceRoller ?? StandardDiceRoller.Instance;
        System = system;
        _activeEncounter = activeEncounter is { EndedAt: null } ? activeEncounter : null;
        PublishSnapshot();
        _loop = Task.Run(RunLoopAsync);
    }

    /// <summary>Promotes a campaign's saved actors into live ECS entities. Called once, right
    /// after construction, before any command is enqueued.</summary>
    public void Hydrate(IEnumerable<ActorDocument> actors)
    {
        foreach (var actor in actors)
        {
            SeedNewCounters(actor);
            HydrateOne(actor);
        }

        PublishSnapshot();
    }

    /// <summary>A feature counter the sheet has never written (an ability the pack only now grants uses to, or a sheet seeded
    /// without opening it) joins the table full, so its Use button works before anyone opens and saves the sheet. Only missing
    /// counters are added: what was spent, and every other key, is left as stored.</summary>
    private void SeedNewCounters(ActorDocument actor)
    {
        if (System == null || Resolver is Codex.Core.Rules.NullEntryResolver) return;
        IReadOnlyList<Codex.Core.Rules.FeatureCounter> counters;
        try
        {
            counters = new Codex.Core.Rules.SheetRuleEngine(System, Resolver).Evaluate(actor.Sheet).Counters;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not work out the feature counters of {ActorId} for the table", actor.Id);
            return;
        }

        var added = false;
        foreach (var counter in counters.Where(c => !actor.Sheet.ContainsKey(c.Key)))
        {
            actor.Sheet[counter.Key] = (long)counter.Max;
            actor.Sheet[counter.MaxKey] = (long)counter.Max;
            actor.Sheet[counter.Key + Codex.Core.Rules.SheetRuleEngine.UsesLabelSuffix] = counter.Label;
            added = true;
        }

        if (added) ActorSheetSync.ApplySheetToState(actor, System, _componentRegistry);
    }

    /// <summary>Takes the campaign's clocks and tables as stored. Called once, with <see cref="Hydrate"/>.</summary>
    public void HydrateCampaignObjects(IReadOnlyDictionary<string, object>? state)
    {
        _campaignObjects = Codex.Core.Rules.CampaignObjects.Copy(state);
        PublishSnapshot();
    }

    /// <summary>The campaign's clocks and tables as the table has them now. Loop-owned: only a command may read or change it; a
    /// page reads <see cref="RuntimeSnapshot.Campaign"/>.</summary>
    internal Dictionary<string, object> CampaignObjectState => _campaignObjects;

    /// <summary>A command changed this key of the campaign's objects: the next persist writes it.</summary>
    internal void CampaignKeyWritten(string key) => _pendingCampaignKeys.Add(key);

    /// <summary>Hydrates one actor into the live ECS world - the common step behind both the
    /// startup <see cref="Hydrate"/> pass and <c>HydrateActorCommand</c> (3.7), which adds a
    /// quick-added monster to an encounter already in progress.</summary>
    internal void HydrateOne(ActorDocument actor)
    {
        // Idempotent: an actor hydrated twice (saved from two places while the table is live) keeps
        // its one entity and takes the newer document's components.
        if (!_actorEntities.TryGetValue(actor.Id, out var entity))
        {
            entity = World.CreateEntity();
        }

        foreach (var component in _componentRegistry.Hydrate(actor.State))
        {
            EntityComponentSync.SetBoxed(entity, component);
        }

        _actorEntities[actor.Id] = entity;
        _actorDocs[actor.Id] = actor;
        _hydratedIds[actor.Id] = 0;
        RosterChanged();
    }

    // Bumped whenever who is at the table, or what players may know of them, changes; pages compare it to reload their list.
    private int _rosterVersion;

    internal void RosterChanged() => _rosterVersion++;

    /// <summary>Takes a new copy of an actor's document (its visibility changed, say) without touching its live state.</summary>
    internal void RefreshDocument(ActorDocument actor)
    {
        if (_actorDocs.ContainsKey(actor.Id))
        {
            _actorDocs[actor.Id] = actor;
            RosterChanged();
        }
    }

    // ---- Undo (R5) ----

    // The last change each creature took from an undoable command: its pools and conditions just before and just after.
    private readonly Dictionary<string, UndoPoint> _undo = new();

    private sealed record UndoPoint(string Kind, ActorState Before, ActorState After);

    // An event's intents can change the sheet as well (a slot track, the sheet's conditions), so the state a change is undone to
    // includes the sheet, compared and put back key by key for the keys the change touched.
    private sealed record ActorState(Dictionary<string, int> Pools, List<ActiveEffect> Effects, Dictionary<string, object>? Sheet = null)
    {
        public static ActorState Of(Entity entity, ActorDocument? doc = null) => new(
            entity.Has<ResourcePoolComponent>() ? new Dictionary<string, int>(entity.Get<ResourcePoolComponent>().Pools) : new Dictionary<string, int>(),
            entity.Has<ActiveEffectsComponent>() ? entity.Get<ActiveEffectsComponent>().Effects.ToList() : new List<ActiveEffect>(),
            doc == null ? null : new Dictionary<string, object>(doc.Sheet));

        public bool SameAs(ActorState other) =>
            Pools.Count == other.Pools.Count && Pools.All(p => other.Pools.TryGetValue(p.Key, out var v) && v == p.Value)
            && Effects.SequenceEqual(other.Effects);

        /// <summary>The sheet keys that differ between two states.</summary>
        public IReadOnlyList<string> SheetKeysChangedFrom(ActorState before) =>
            Sheet == null || before.Sheet == null ? Array.Empty<string>()
                : Sheet.Keys.Union(before.Sheet.Keys).Where(k => !YamlValues.SameValue(Sheet.GetValueOrDefault(k), before.Sheet.GetValueOrDefault(k))).ToList();

        public void Restore(Entity entity)
        {
            if (!entity.Has<ResourcePoolComponent>()) entity.Set(new ResourcePoolComponent());
            var pools = entity.Get<ResourcePoolComponent>().Pools;
            pools.Clear();
            foreach (var (key, value) in Pools) pools[key] = value;

            if (!entity.Has<ActiveEffectsComponent>()) entity.Set(new ActiveEffectsComponent());
            var effects = entity.Get<ActiveEffectsComponent>().Effects;
            effects.Clear();
            effects.AddRange(Effects);
        }
    }

    /// <summary>What the creature's last change was (an event type: "Damage"), while it can still be taken back: nothing has
    /// changed the creature since. Null otherwise.</summary>
    private string? UndoableKind(string actorId, Entity entity) =>
        _undo.TryGetValue(actorId, out var point) && point.After.SameAs(ActorState.Of(entity)) && SheetStillAfter(actorId, point) ? point.Kind : null;

    // The keys the change wrote still hold what it wrote: nothing has changed them since.
    private bool SheetStillAfter(string actorId, UndoPoint point)
    {
        var keys = point.After.SheetKeysChangedFrom(point.Before);
        if (keys.Count == 0) return true;
        var sheet = _actorDocs.GetValueOrDefault(actorId)?.Sheet;
        return sheet != null && keys.All(k => YamlValues.SameValue(sheet.GetValueOrDefault(k), point.After.Sheet!.GetValueOrDefault(k)));
    }

    /// <summary>Puts the creature's pools and conditions back as they were before its last change (R5). Returns the kind of
    /// change undone, or null when there is none or something has changed the creature since (a turn ticked its conditions,
    /// another hit landed): undoing then would wipe that out too. Effects the change ended on other creatures (a death ends
    /// what the dead one concentrated on) do not come back.</summary>
    internal string? Undo(string actorId)
    {
        if (!_actorEntities.TryGetValue(actorId, out var entity) || UndoableKind(actorId, entity) is not { } kind)
        {
            return null;
        }

        var point = _undo[actorId];
        point.Before.Restore(entity);
        if (_actorDocs.TryGetValue(actorId, out var doc))
        {
            foreach (var key in point.After.SheetKeysChangedFrom(point.Before))
            {
                if (point.Before.Sheet!.TryGetValue(key, out var was)) doc.Sheet[key] = was;
                else doc.Sheet.Remove(key);
                SheetKeyWritten(actorId, key);
            }
        }

        _undo.Remove(actorId);
        _dirtyActorIds.Add(actorId);
        return kind;
    }

    /// <summary>Takes an actor out of the table: its entity goes, and so does its place in the
    /// initiative order. Used when the actor is deleted while the table is live.</summary>
    internal bool RemoveActor(string actorId)
    {
        if (!_actorEntities.Remove(actorId, out var entity))
        {
            return false;
        }

        entity.Dispose();
        _actorDocs.Remove(actorId);
        _hydratedIds.TryRemove(actorId, out _);
        _dirtyActorIds.Remove(actorId);
        _pendingSheetWrites.Remove(actorId);
        _pendingSheetKeys.Remove(actorId);
        _undo.Remove(actorId);
        // What it held up by concentrating, and what was counted on its turns, end with it leaving the table (R4).
        if (World.EndHeldBy(actorId).Count > 0)
        {
            MarkAllHydratedActorsDirty();
        }

        if (!RemoveParticipant(actorId))
        {
            TableRules.ExpireTurnsOf(this, actorId);
        }

        RosterChanged();
        return true;
    }

    /// <summary>The runtime's cached document for an actor (its sheet carries the stat block the
    /// table rules read: resistances, kind, hit dice); null for an actor it never hydrated.</summary>
    public ActorDocument? GetActorDocument(string actorId) => _actorDocs.GetValueOrDefault(actorId);

    /// <summary>The table changed this sheet key on the cached document (an event's intents): the next persist writes it.</summary>
    internal void SheetKeyWritten(string actorId, string key)
    {
        if (!_pendingSheetKeys.TryGetValue(actorId, out var keys)) _pendingSheetKeys[actorId] = keys = new HashSet<string>(StringComparer.Ordinal);
        keys.Add(key);
        _dirtyActorIds.Add(actorId);
    }

    /// <summary>True once <paramref name="actorId"/> has been hydrated into this runtime; safe from any thread.</summary>
    public bool HasActor(string actorId) => _hydratedIds.ContainsKey(actorId);

    public Entity? GetEntity(string actorId) => _actorEntities.TryGetValue(actorId, out var entity) ? entity : null;

    /// <summary>Resolves an actor id to its display name for a command's <see cref="SessionEvent"/>
    /// description; falls back to the raw id for an actor the runtime never hydrated (e.g. a source
    /// id that names a trap or effect rather than a living actor).</summary>
    public string GetActorName(string actorId) => _actorDocs.TryGetValue(actorId, out var doc) ? doc.Name : actorId;

    /// <summary>An actor as a log message names it: a term each reader sees by the name they know (F20), or, for an id the
    /// table never hydrated (a source like "trap"), the text as it is.</summary>
    public object ActorTerm(string actorId) =>
        _actorDocs.TryGetValue(actorId, out var doc) ? EventTerm.Actor(actorId, doc.Name) : actorId;

    internal void MarkAllHydratedActorsDirty()
    {
        foreach (var actorId in _actorEntities.Keys)
        {
            _dirtyActorIds.Add(actorId);
        }
    }

    /// <summary>Returns the live encounter, creating one and linking it into the active session
    /// if this is the first initiative roll of the session (3.5).</summary>
    internal EncounterDocument GetOrCreateEncounter()
    {
        if (_activeEncounter == null)
        {
            _activeEncounter = new EncounterDocument
            {
                Id = Guid.NewGuid().ToString(),
                CampaignId = CampaignId,
                SessionId = _activeSession?.Id ?? string.Empty
            };
            if (_activeSession != null)
            {
                _activeSession.EncounterIds.Add(_activeEncounter.Id);
                _pendingEncounterIds.Add(_activeEncounter.Id);
            }
        }

        return _activeEncounter;
    }

    internal void MarkEncounterDirty() => _encounterToPersist = _activeEncounter;

    /// <summary>Closes the live encounter: it is saved once more with its end time and stops being
    /// the active one, so the next initiative roll starts a fresh order. Returns the encounter
    /// that ended, or null when none was running.</summary>
    internal EncounterDocument? EndEncounter()
    {
        if (_activeEncounter == null)
        {
            return null;
        }

        var ended = _activeEncounter;
        ended.EndedAt = DateTime.UtcNow;
        _encounterToPersist = ended;
        _activeEncounter = null;
        return ended;
    }

    /// <summary>Drops a participant from the order, keeping the turn pointer on whoever is acting now. When it was their own
    /// turn in a fight under way, the turn passes as "Next turn" passes it (R2): the dead are skipped and the next one begins
    /// their turn. Timed effects counted on their turns end, since those turns will not come (R4). Returns false when they
    /// were not in the order.</summary>
    internal bool RemoveParticipant(string actorId)
    {
        var encounter = _activeEncounter;
        if (encounter == null)
        {
            return false;
        }

        var index = encounter.Participants.FindIndex(p => p.ActorId == actorId);
        if (index < 0)
        {
            return false;
        }

        var removed = encounter.Participants[index];
        var heldTheTurn = index == encounter.TurnIndex
            && (encounter.Round > 1 || removed.TurnStarted || encounter.Participants.Any(p => p.HasActed));
        encounter.Participants.RemoveAt(index);
        if (index < encounter.TurnIndex)
        {
            encounter.TurnIndex--;
        }

        if (encounter.Participants.Count == 0)
        {
            encounter.TurnIndex = 0;
        }
        else if (encounter.TurnIndex >= encounter.Participants.Count)
        {
            // The last participant was removed on their own turn: the round is over.
            encounter.TurnIndex = 0;
            StartNewRound(encounter);
        }

        if (heldTheTurn && encounter.Participants.Count > 0)
        {
            MoveTurnOn(encounter, fromPointer: true);
        }

        TableRules.ExpireTurnsOf(this, actorId);
        MarkEncounterDirty();
        return true;
    }

    /// <summary>Moves the turn to the next participant still in the fight (R2, shared by "Next turn" and by removing whoever
    /// is acting): past the end of the order is a new round, the dead are passed over and count as having acted, and the one
    /// it lands on begins their turn. With <paramref name="fromPointer"/> the participant the pointer is on now is the first
    /// candidate, for when the one who held the turn has just left the order.</summary>
    internal void MoveTurnOn(EncounterDocument encounter, bool fromPointer)
    {
        for (var step = 0; step < encounter.Participants.Count; step++)
        {
            if (step > 0 || !fromPointer)
            {
                encounter.TurnIndex++;
                if (encounter.TurnIndex >= encounter.Participants.Count)
                {
                    encounter.TurnIndex = 0;
                    StartNewRound(encounter);
                }
            }

            var next = encounter.Participants[encounter.TurnIndex];
            if (GetEntity(next.ActorId) is { } e && TableRules.IsDead(this, e))
            {
                next.HasActed = true;
                continue;
            }

            BeginTurn(next);
            return;
        }
    }

    /// <summary>Moves the encounter into its next round: nobody has acted or begun a turn in it yet.</summary>
    internal static void StartNewRound(EncounterDocument encounter)
    {
        encounter.Round++;
        foreach (var participant in encounter.Participants)
        {
            participant.HasActed = false;
            participant.TurnStarted = false;
        }
    }

    /// <summary>Starts <paramref name="participant"/>'s turn: start-of-turn effects anchored to them tick,
    /// once per round however often the turn comes back to them.</summary>
    internal void BeginTurn(EncounterParticipant participant)
    {
        if (participant.TurnStarted)
        {
            return;
        }

        participant.TurnStarted = true;
        if (_actorEntities.ContainsKey(participant.ActorId))
        {
            World.AdvanceTurn(participant.ActorId, EffectExpiry.StartOfTurn);
            MarkAllHydratedActorsDirty();
        }
    }

    /// <summary>Deterministic initiative order: highest roll first, <see cref="EncounterParticipant.TieBreak"/>
    /// breaks a tied roll, and actor id breaks a tied tiebreak - two participants must never
    /// silently swap places between one render and the next.</summary>
    internal static void SortParticipants(EncounterDocument encounter)
    {
        encounter.Participants.Sort((a, b) =>
        {
            var byRoll = b.InitiativeRoll.CompareTo(a.InitiativeRoll);
            if (byRoll != 0)
            {
                return byRoll;
            }

            var byTieBreak = b.TieBreak.CompareTo(a.TieBreak);
            return byTieBreak != 0 ? byTieBreak : string.CompareOrdinal(a.ActorId, b.ActorId);
        });
    }

    /// <summary>Makes the tiebreaks of everyone on <paramref name="roll"/> strictly descending in their
    /// current list order, raising only those that need it, so a later <see cref="SortParticipants"/>
    /// keeps an order that was arranged by hand.</summary>
    internal static void FixTieOrder(EncounterDocument encounter, int roll)
    {
        int? below = null;
        for (var i = encounter.Participants.Count - 1; i >= 0; i--)
        {
            var participant = encounter.Participants[i];
            if (participant.InitiativeRoll != roll)
            {
                continue;
            }

            if (below is { } next && participant.TieBreak <= next)
            {
                participant.TieBreak = next + 1;
            }

            below = participant.TieBreak;
        }
    }

    /// <summary>Appends a roll to the live session's shared roll log (3.6); a no-op when no
    /// session is attached, matching <see cref="AppendSessionEventAsync"/>'s guard.</summary>
    /// <summary>The index of the session's system stamp this table runs under (<see cref="SessionDocument.SystemStamps"/>), which
    /// each roll it records carries; null when the system has no fingerprint.</summary>
    public int? SystemStampIndex { get; internal set; }

    internal void RecordRoll(RollLogEntry entry)
    {
        if (_activeSession == null) return;
        entry.SystemStamp ??= SystemStampIndex;
        _activeSession.RollLog.Add(entry);
        _pendingRolls.Add(entry);
    }

    /// <summary>Appends a quick note to the live session (3.7); a no-op when no session is
    /// attached, matching <see cref="AppendSessionEventAsync"/>'s guard.</summary>
    internal void AddSessionNote(SessionNote note)
    {
        if (_activeSession == null) return;
        note.Id = Guid.NewGuid().ToString("N");
        if (_activeEncounter is { Participants.Count: > 0 } encounter)
        {
            note.EncounterId = encounter.Id;
            note.Round = encounter.Round;
            note.TurnActorId = encounter.TurnIndex >= 0 && encounter.TurnIndex < encounter.Participants.Count
                ? encounter.Participants[encounter.TurnIndex].ActorId
                : null;
        }

        _activeSession.Notes.Add(note);
        _pendingNotes.Add(note);
    }

    /// <summary>Replaces a note's words and kind when <paramref name="editorId"/> may change it; returns the new note, or
    /// null when there is no such note or they may not. The note is replaced, never changed in place: the last
    /// snapshot still holds the old one and may be rendering it.</summary>
    internal SessionNote? EditSessionNote(string noteId, string editorId, bool editorIsDm, string text, NoteKind kind)
    {
        var index = FindEditableNote(noteId, editorId, editorIsDm);
        if (index < 0 || string.IsNullOrWhiteSpace(text)) return null;
        var old = _activeSession!.Notes[index];
        var edited = new SessionNote
        {
            Id = old.Id, AuthorId = old.AuthorId, Text = text, IsSecret = old.IsSecret, Kind = old.SpeakerActorId != null ? NoteKind.Note : kind,
            SpeakerActorId = old.SpeakerActorId, WithActorIds = old.WithActorIds.ToList(),
            EncounterId = old.EncounterId, Round = old.Round, TurnActorId = old.TurnActorId, Timestamp = old.Timestamp
        };
        _activeSession.Notes[index] = edited;
        _pendingNoteChanges.Add((noteId, edited));
        return edited;
    }

    /// <summary>Takes a note out when <paramref name="editorId"/> may; returns the removed note, or null.</summary>
    internal SessionNote? DeleteSessionNote(string noteId, string editorId, bool editorIsDm)
    {
        var index = FindEditableNote(noteId, editorId, editorIsDm);
        if (index < 0) return null;
        var removed = _activeSession!.Notes[index];
        _activeSession.Notes.RemoveAt(index);
        _pendingNoteChanges.Add((noteId, null));
        return removed;
    }

    private int FindEditableNote(string noteId, string editorId, bool editorIsDm) =>
        _activeSession == null || string.IsNullOrEmpty(noteId)
            ? -1
            : _activeSession.Notes.FindIndex(n => n.Id == noteId && SessionNotes.CanEdit(n, editorId, editorIsDm));

    /// <summary>
    /// Fires after a command's dirty actors are persisted (3.3) - the runtime IS the "in-process
    /// per-campaign hub" the plan describes, since <see cref="CampaignRuntimeManager"/> already
    /// caches exactly one instance per live campaign. A Blazor circuit subscribes on entry (having
    /// resolved its own <see cref="CampaignAccess"/> first) and MUST unsubscribe in its own
    /// disposal - this event outlives any one circuit, and a component that forgets to detach leaks
    /// for as long as the campaign stays live. The payload is the actor ids this command actually
    /// touched, so a subscriber can re-project only what changed instead of re-rendering blind.
    /// </summary>
    public event Action<IReadOnlyCollection<string>>? StateChanged;

    /// <summary>Whether any page is subscribed to <see cref="StateChanged"/>: someone has the table (or the campaign) open.</summary>
    internal bool IsWatched => StateChanged != null;

    /// <summary>Queues a command onto the single-writer loop and returns a task that completes once
    /// it has actually been applied and its dirty actors persisted - so a caller (the DM console)
    /// can await a definite result instead of firing and hoping.</summary>
    public Task EnqueueAsync(IRuntimeCommand command)
    {
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new QueuedCommand(command, completion)))
        {
            completion.TrySetException(new InvalidOperationException(_cts.IsCancellationRequested
                ? "Campaign runtime is no longer accepting commands."
                : $"Campaign runtime has {CommandQueueCapacity} commands waiting; this one was refused."));
        }

        return completion.Task;
    }

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (var queued in _commands.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    // An undoable change keeps the creature's state from just before and just after it (R5).
                    var undoable = queued.Command as IUndoableCommand;
                    var target = undoable != null ? GetEntity(undoable.ActorId) : null;
                    var before = target is { } t ? ActorState.Of(t, GetActorDocument(undoable!.ActorId)) : null;
                    queued.Command.Apply(this);
                    if (undoable != null && target is { IsAlive: true } changed && before != null)
                    {
                        var after = ActorState.Of(changed, GetActorDocument(undoable.ActorId));
                        if (!after.SameAs(before) || after.SheetKeysChangedFrom(before).Count > 0)
                        {
                            _undo[undoable.ActorId] = new UndoPoint(undoable.UndoKind, before, after);
                        }
                    }

                    foreach (var actorId in queued.Command.AffectedActorIds)
                    {
                        _dirtyActorIds.Add(actorId);
                    }

                    // Snapshot before PersistDirtyAsync clears it - this is exactly what this one
                    // command touched, which for AdvanceTurnCommand (MarkAllHydratedActorsDirty) is
                    // broader than its own AffectedActorIds.
                    var affectedActorIds = _dirtyActorIds.ToArray();

                    await PersistDirtyAsync();
                    await PersistCampaignObjectsAsync();
                    await AppendSessionEventAsync(queued.Command);
                    await PersistEncounterAsync();
                    PublishSnapshot();

                    // Raised before the caller's task completes, so an awaited EnqueueAsync means
                    // subscribers have already been told. A misbehaving subscriber (e.g. a circuit
                    // that throws while re-rendering) must neither take down the campaign's
                    // single-writer loop nor fail a command that already succeeded, so it is
                    // logged here instead of reaching the command-failure catch below.
                    try
                    {
                        StateChanged?.Invoke(affectedActorIds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "A StateChanged subscriber failed in campaign runtime {CampaignId}", CampaignId);
                    }

                    queued.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    // A mistyped dice expression is the person's typo, which the page tells them; not a fault of the server.
                    _logger.Log(ex is FormatException ? LogLevel.Information : LogLevel.Error, ex,
                        "Command {Command} failed in campaign runtime {CampaignId}", queued.Command.GetType().Name, CampaignId);
                    queued.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on DisposeAsync.
        }
    }

    /// <summary>
    /// Writes each touched actor back as a patch of the stored document: <see cref="ActorDocument.State"/>
    /// and <see cref="ActorDocument.UpdatedAt"/> only, with the live resources and attributes mirrored
    /// onto <see cref="ActorDocument.Sheet"/> so the Cast and Overview lists (which read the sheet)
    /// agree with the table. The runtime's cached copy of the document is never stored whole - that
    /// reverted every edit made to the sheet since the table started (F2).
    /// </summary>
    private async Task PersistDirtyAsync()
    {
        if (_dirtyActorIds.Count == 0)
        {
            return;
        }

        foreach (var actorId in _dirtyActorIds)
        {
            if (!_actorEntities.TryGetValue(actorId, out var entity) || !_actorDocs.TryGetValue(actorId, out var cached))
            {
                continue;
            }

            var state = _componentRegistry.Snapshot(EntityComponentSync.SnapshotAll(entity, _componentRegistry.RegisteredComponents));
            var sheetEdit = _pendingSheetWrites.TryGetValue(actorId, out var editedByDm) ? (IsDm: editedByDm, Source: cached) : default;
            var sheetKeys = _pendingSheetKeys.TryGetValue(actorId, out var keys) ? keys.ToList() : new List<string>();
            var sheetNow = cached.Sheet;
            var now = DateTime.UtcNow;

            var stored = await _actorRepository.UpdateAsync(actorId, doc =>
            {
                if (sheetEdit.Source != null)
                {
                    // The table's copy already holds the player's history merged with the stored one (ReplaceActor).
                    ActorRepository.CopyEditableFields(doc, sheetEdit.Source, sheetEdit.IsDm, merged: true);
                }

                foreach (var key in sheetKeys)
                {
                    if (sheetNow.TryGetValue(key, out var value)) doc.Sheet[key] = value;
                    else doc.Sheet.Remove(key);
                }

                doc.State = state;
                doc.UpdatedAt = now;
                ActorSheetSync.ApplyStateToSheet(doc, null, _componentRegistry);
            });

            if (stored != null)
            {
                _actorDocs[actorId] = stored;
            }
        }

        _dirtyActorIds.Clear();
        _pendingSheetWrites.Clear();
        _pendingSheetKeys.Clear();
    }

    private async Task PersistCampaignObjectsAsync()
    {
        if (_pendingCampaignKeys.Count == 0) return;
        var keys = _pendingCampaignKeys.ToList();
        _pendingCampaignKeys.Clear();
        if (CampaignObjectsStore != null) await CampaignObjectsStore.WriteKeysAsync(CampaignId, Codex.Core.Rules.CampaignObjects.Copy(_campaignObjects), keys);
    }

    /// <summary>Applies a sheet save made while the table is live (see <c>ReplaceActorCommand</c>):
    /// the page's edits are merged onto the runtime's current copy - a field the editor did not change
    /// keeps its live value, so a stale sheet never reverts table damage - and the entity is
    /// re-hydrated from the result.</summary>
    internal void ReplaceActor(ActorDocument edited, IReadOnlyDictionary<string, object>? baselineSheet, CampaignAccess access, SystemDefinition? system)
    {
        if (!_actorDocs.TryGetValue(edited.Id, out var cached) || !_actorEntities.TryGetValue(edited.Id, out var entity))
        {
            throw new KeyNotFoundException($"Actor {edited.Id} is not part of this table.");
        }

        if (cached.CampaignId != access.CampaignId || (!access.IsDm && cached.OwnerUserId != access.UserId))
        {
            throw new UnauthorizedAccessException("Only the GM or the owning player can edit this sheet.");
        }

        // The table's own view of the character right now: cached document, live state, mirrored sheet.
        var liveState = _componentRegistry.Snapshot(EntityComponentSync.SnapshotAll(entity, _componentRegistry.RegisteredComponents));
        var live = new ActorDocument { Sheet = new Dictionary<string, object>(cached.Sheet), State = liveState };
        ActorSheetSync.ApplyStateToSheet(live, system, _componentRegistry);

        var merged = new Dictionary<string, object>();
        foreach (var (key, value) in edited.Sheet)
        {
            var carriedAlong = baselineSheet != null && baselineSheet.TryGetValue(key, out var was) && YamlValues.SameValue(was, value);
            merged[key] = carriedAlong && live.Sheet.TryGetValue(key, out var current) ? current : value;
        }

        if (baselineSheet != null)
        {
            // Keys the editor never saw (a pool the table created since it opened) are not its to drop.
            foreach (var (key, value) in live.Sheet)
            {
                if (!merged.ContainsKey(key) && !baselineSheet.ContainsKey(key))
                {
                    merged[key] = value;
                }
            }

            // Hit points, pools and slots move at the table too: the editor's change is added to the live value, not put over it.
            SheetCounters.MergeByDelta(merged, edited.Sheet, baselineSheet, live.Sheet, system);
        }

        ActorRepository.CopyEditableFields(cached, edited, access.IsDm);
        cached.Sheet = merged;
        cached.State = liveState;
        ActorSheetSync.ApplySheetToState(cached, system, _componentRegistry);

        foreach (var component in _componentRegistry.Hydrate(cached.State))
        {
            EntityComponentSync.SetBoxed(entity, component);
        }

        _pendingSheetWrites[edited.Id] = access.IsDm;
        _dirtyActorIds.Add(edited.Id);
    }

    /// <summary>The command log is the session log (3.2): every applied command's description
    /// becomes a <see cref="SessionEvent"/>, giving the recap raw material and an audit trail for
    /// free rather than a bolted-on separate logging path a DM console could forget to call.</summary>
    private async Task AppendSessionEventAsync(IRuntimeCommand command)
    {
        if (_sessionRepository == null || _activeSession == null)
        {
            _pendingRolls.Clear();
            _pendingNotes.Clear();
            _pendingNoteChanges.Clear();
            _pendingEncounterIds.Clear();
            return;
        }

        var logged = command.Describe(this);
        _activeSession.Events.Add(logged);
        _pendingEvents.Add(logged);

        // Append-only: the stored session gets these rows added to whatever it holds now, so a
        // recap or title the DM saved from the session page in the meantime survives (F3).
        var events = _pendingEvents.ToArray();
        var rolls = _pendingRolls.ToArray();
        var notes = _pendingNotes.ToArray();
        var noteChanges = _pendingNoteChanges.ToArray();
        var encounterIds = _pendingEncounterIds.ToArray();
        await _sessionRepository.UpdateAsync(_activeSession.Id, stored =>
        {
            stored.Events.AddRange(events);
            stored.RollLog.AddRange(rolls);
            stored.Notes.AddRange(notes);
            foreach (var (id, changed) in noteChanges)
            {
                var index = stored.Notes.FindIndex(n => n.Id == id);
                if (index < 0) continue;
                if (changed == null) stored.Notes.RemoveAt(index);
                else stored.Notes[index] = changed;
            }

            stored.EncounterIds.AddRange(encounterIds.Where(id => !stored.EncounterIds.Contains(id)));
        });

        _pendingEvents.Clear();
        _pendingRolls.Clear();
        _pendingNotes.Clear();
        _pendingNoteChanges.Clear();
        _pendingEncounterIds.Clear();
    }

    private void PublishSnapshot()
    {
        EncounterSnapshot? encounter = null;
        if (_activeEncounter is { } live)
        {
            encounter = new EncounterSnapshot(
                live.Id,
                live.Round,
                live.TurnIndex,
                live.Participants.Select(p => new ParticipantSnapshot(p.ActorId, p.InitiativeRoll, p.TieBreak, p.State, p.HasActed)).ToArray());
        }

        var actors = new Dictionary<string, ActorStateSnapshot>(_actorEntities.Count);
        foreach (var (actorId, entity) in _actorEntities)
        {
            IReadOnlyDictionary<string, int>? pools = entity.Has<ResourcePoolComponent>()
                ? new Dictionary<string, int>(entity.Get<ResourcePoolComponent>().Pools)
                : null;
            IReadOnlyList<EffectSnapshot> effects = entity.Has<ActiveEffectsComponent>()
                ? entity.Get<ActiveEffectsComponent>().Effects.Select(e => new EffectSnapshot(e.EffectId, e.SourceId, e.RoundsRemaining, e.Expiry, e.HeldBy)).ToArray()
                : Array.Empty<EffectSnapshot>();
            actors[actorId] = new ActorStateSnapshot(pools, effects, UndoableKind(actorId, entity), TableRules.IsDead(this, entity), TableRules.IsDown(this, entity));
        }

        _snapshot = new RuntimeSnapshot(
            encounter,
            _activeSession?.RollLog.ToArray() ?? Array.Empty<RollLogEntry>(),
            _activeSession?.Notes.ToArray() ?? Array.Empty<SessionNote>(),
            actors,
            _rosterVersion,
            Codex.Core.Rules.CampaignObjects.Copy(_campaignObjects));
    }

    /// <summary>Write-through for the initiative tracker (3.5), mirroring
    /// <see cref="AppendSessionEventAsync"/>'s cheap-command-rate reasoning - only actually hits
    /// Raven when a command marked the encounter dirty, so a pure-ECS command (damage, healing)
    /// doesn't pay for a save nothing changed.</summary>
    private async Task PersistEncounterAsync()
    {
        var encounter = _encounterToPersist;
        _encounterToPersist = null;
        if (_encounterRepository == null || encounter == null)
        {
            return;
        }

        await _encounterRepository.SaveAsync(encounter);
    }

    /// <summary>How long ending the table waits for commands already queued before it gives up on them.</summary>
    internal static TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private int _disposed;

    /// <summary>Stops taking commands and lets the loop finish those already queued: each has a caller awaiting it (a sheet
    /// save's spinner, a roll) that must get an answer, and cancelling the loop left them waiting forever (R6). A loop still
    /// busy after <see cref="DrainTimeout"/> is cancelled, and what is left fails as "no longer accepting commands".</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _commands.Writer.TryComplete();
        if (await Task.WhenAny(_loop, Task.Delay(DrainTimeout)) != _loop)
        {
            _cts.Cancel();
        }

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected when the drain timed out.
        }

        while (_commands.Reader.TryRead(out var left))
        {
            left.Completion.TrySetException(new InvalidOperationException("Campaign runtime is no longer accepting commands."));
        }

        World.Dispose();
        _cts.Dispose();
    }

    private sealed record QueuedCommand(IRuntimeCommand Command, TaskCompletionSource Completion);
}
