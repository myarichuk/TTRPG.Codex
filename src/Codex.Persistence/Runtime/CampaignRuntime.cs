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
    // Hydrated ids, readable from any thread: lets a caller decide between the runtime and the
    // repository without touching the loop-owned dictionaries.
    private readonly ConcurrentDictionary<string, byte> _hydratedIds = new();
    // Session rows appended since the last flush. The session is persisted by appending these to
    // the stored document, never by storing the runtime's copy whole (F3).
    private readonly List<SessionEvent> _pendingEvents = new();
    private readonly List<RollLogEntry> _pendingRolls = new();
    private readonly List<SessionNote> _pendingNotes = new();
    private readonly List<string> _pendingEncounterIds = new();
    private volatile RuntimeSnapshot _snapshot = RuntimeSnapshot.Empty;
    private EncounterDocument? _activeEncounter;
    // The encounter the next persist writes: the live one when a command changed it, or the one
    // just ended (which is no longer active but still has to be saved with its end time).
    private EncounterDocument? _encounterToPersist;
    private readonly Channel<QueuedCommand> _commands = Channel.CreateUnbounded<QueuedCommand>(
        new UnboundedChannelOptions { SingleReader = true });
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
            HydrateOne(actor);
        }

        PublishSnapshot();
    }

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
        RemoveParticipant(actorId);
        return true;
    }

    /// <summary>The runtime's cached document for an actor (its sheet carries the stat block the
    /// table rules read: resistances, kind, hit dice); null for an actor it never hydrated.</summary>
    public ActorDocument? GetActorDocument(string actorId) => _actorDocs.GetValueOrDefault(actorId);

    /// <summary>True once <paramref name="actorId"/> has been hydrated into this runtime; safe from any thread.</summary>
    public bool HasActor(string actorId) => _hydratedIds.ContainsKey(actorId);

    public Entity? GetEntity(string actorId) => _actorEntities.TryGetValue(actorId, out var entity) ? entity : null;

    /// <summary>Resolves an actor id to its display name for a command's <see cref="SessionEvent"/>
    /// description; falls back to the raw id for an actor the runtime never hydrated (e.g. a source
    /// id that names a trap or effect rather than a living actor).</summary>
    public string GetActorName(string actorId) => _actorDocs.TryGetValue(actorId, out var doc) ? doc.Name : actorId;

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

    /// <summary>Drops a participant from the order, keeping the turn pointer on whoever is acting
    /// now (or the next in line when it was their turn). Returns false when they were not in it.</summary>
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

        MarkEncounterDirty();
        return true;
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
    internal void RecordRoll(RollLogEntry entry)
    {
        if (_activeSession == null) return;
        _activeSession.RollLog.Add(entry);
        _pendingRolls.Add(entry);
    }

    /// <summary>Appends a quick note to the live session (3.7); a no-op when no session is
    /// attached, matching <see cref="AppendSessionEventAsync"/>'s guard.</summary>
    internal void AddSessionNote(SessionNote note)
    {
        if (_activeSession == null) return;
        _activeSession.Notes.Add(note);
        _pendingNotes.Add(note);
    }

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

    /// <summary>Queues a command onto the single-writer loop and returns a task that completes once
    /// it has actually been applied and its dirty actors persisted - so a caller (the DM console)
    /// can await a definite result instead of firing and hoping.</summary>
    public Task EnqueueAsync(IRuntimeCommand command)
    {
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new QueuedCommand(command, completion)))
        {
            completion.TrySetException(new InvalidOperationException("Campaign runtime is no longer accepting commands."));
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
                    queued.Command.Apply(this);
                    foreach (var actorId in queued.Command.AffectedActorIds)
                    {
                        _dirtyActorIds.Add(actorId);
                    }

                    // Snapshot before PersistDirtyAsync clears it - this is exactly what this one
                    // command touched, which for AdvanceTurnCommand (MarkAllHydratedActorsDirty) is
                    // broader than its own AffectedActorIds.
                    var affectedActorIds = _dirtyActorIds.ToArray();

                    await PersistDirtyAsync();
                    await AppendSessionEventAsync(queued.Command);
                    await PersistEncounterAsync();
                    PublishSnapshot();
                    queued.Completion.TrySetResult();

                    // Deliberately still inside this try: a misbehaving subscriber (e.g. a circuit
                    // that throws while re-rendering) must not be able to take down the whole
                    // campaign's single-writer loop - it's caught and logged below like any other
                    // command failure, even though the command itself already succeeded.
                    StateChanged?.Invoke(affectedActorIds);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Command {Command} failed in campaign runtime {CampaignId}", queued.Command.GetType().Name, CampaignId);
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
            var now = DateTime.UtcNow;

            var stored = await _actorRepository.UpdateAsync(actorId, doc =>
            {
                if (sheetEdit.Source != null)
                {
                    ActorRepository.CopyEditableFields(doc, sheetEdit.Source, sheetEdit.IsDm);
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
            throw new UnauthorizedAccessException("Only the DM or the owning player can edit this sheet.");
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
        var encounterIds = _pendingEncounterIds.ToArray();
        await _sessionRepository.UpdateAsync(_activeSession.Id, stored =>
        {
            stored.Events.AddRange(events);
            stored.RollLog.AddRange(rolls);
            stored.Notes.AddRange(notes);
            stored.EncounterIds.AddRange(encounterIds.Where(id => !stored.EncounterIds.Contains(id)));
        });

        _pendingEvents.Clear();
        _pendingRolls.Clear();
        _pendingNotes.Clear();
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
                ? entity.Get<ActiveEffectsComponent>().Effects.Select(e => new EffectSnapshot(e.EffectId, e.SourceId, e.RoundsRemaining, e.Expiry)).ToArray()
                : Array.Empty<EffectSnapshot>();
            actors[actorId] = new ActorStateSnapshot(pools, effects);
        }

        _snapshot = new RuntimeSnapshot(
            encounter,
            _activeSession?.RollLog.ToArray() ?? Array.Empty<RollLogEntry>(),
            _activeSession?.Notes.ToArray() ?? Array.Empty<SessionNote>(),
            actors);
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

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        World.Dispose();
        _cts.Dispose();
    }

    private sealed record QueuedCommand(IRuntimeCommand Command, TaskCompletionSource Completion);
}
