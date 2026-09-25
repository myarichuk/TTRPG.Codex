using System.Threading.Channels;
using Codex.Core;
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
    private EncounterDocument? _activeEncounter;
    private bool _encounterDirty;
    private readonly Channel<QueuedCommand> _commands = Channel.CreateUnbounded<QueuedCommand>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>This campaign system's dice roller (3.6) - <see cref="StandardDiceRoller"/> unless
    /// the plugin overrides it (SWFFG's narrative dice, eventually).</summary>
    public IDiceRoller DiceRoller { get; }

    /// <summary>The encounter currently in progress, if any (3.5) - null until the first
    /// initiative roll of a session creates one via <see cref="GetOrCreateEncounter"/>.</summary>
    public EncounterDocument? ActiveEncounter => _activeEncounter;

    /// <summary>The live session commands log to (3.2) and read back from - a combat console
    /// reads <see cref="SessionDocument.RollLog"/> and <see cref="SessionDocument.Notes"/>
    /// directly off this rather than re-querying Raven on every render.</summary>
    public SessionDocument? ActiveSession => _activeSession;

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
    public CampaignRuntime(
        string campaignId,
        IActorRepository actorRepository,
        ComponentRegistry componentRegistry,
        ILogger logger,
        ISessionRepository? sessionRepository = null,
        SessionDocument? activeSession = null,
        IEncounterRepository? encounterRepository = null,
        IDiceRoller? diceRoller = null)
    {
        CampaignId = campaignId;
        _actorRepository = actorRepository;
        _componentRegistry = componentRegistry;
        _logger = logger;
        _sessionRepository = sessionRepository;
        _activeSession = activeSession;
        _encounterRepository = encounterRepository;
        DiceRoller = diceRoller ?? StandardDiceRoller.Instance;
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
    }

    /// <summary>Hydrates one actor into the live ECS world - the common step behind both the
    /// startup <see cref="Hydrate"/> pass and <c>HydrateActorCommand</c> (3.7), which adds a
    /// quick-added monster to an encounter already in progress.</summary>
    internal void HydrateOne(ActorDocument actor)
    {
        var entity = World.CreateEntity();
        foreach (var component in _componentRegistry.Hydrate(actor.State))
        {
            EntityComponentSync.SetBoxed(entity, component);
        }

        _actorEntities[actor.Id] = entity;
        _actorDocs[actor.Id] = actor;
    }

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
            _activeSession?.EncounterIds.Add(_activeEncounter.Id);
        }

        return _activeEncounter;
    }

    internal void MarkEncounterDirty() => _encounterDirty = true;

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

    /// <summary>Appends a roll to the live session's shared roll log (3.6); a no-op when no
    /// session is attached, matching <see cref="AppendSessionEventAsync"/>'s guard.</summary>
    internal void RecordRoll(RollLogEntry entry) => _activeSession?.RollLog.Add(entry);

    /// <summary>Appends a quick note to the live session (3.7); a no-op when no session is
    /// attached, matching <see cref="AppendSessionEventAsync"/>'s guard.</summary>
    internal void AddSessionNote(SessionNote note) => _activeSession?.Notes.Add(note);

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

    private async Task PersistDirtyAsync()
    {
        if (_dirtyActorIds.Count == 0)
        {
            return;
        }

        foreach (var actorId in _dirtyActorIds)
        {
            if (!_actorEntities.TryGetValue(actorId, out var entity) || !_actorDocs.TryGetValue(actorId, out var doc))
            {
                continue;
            }

            doc.State = _componentRegistry.Snapshot(EntityComponentSync.SnapshotAll(entity, _componentRegistry.RegisteredComponents));
            doc.UpdatedAt = DateTime.UtcNow;
            await _actorRepository.SaveAsync(doc);
        }

        _dirtyActorIds.Clear();
    }

    /// <summary>The command log is the session log (3.2): every applied command's description
    /// becomes a <see cref="SessionEvent"/>, giving the recap raw material and an audit trail for
    /// free rather than a bolted-on separate logging path a DM console could forget to call.</summary>
    private async Task AppendSessionEventAsync(IRuntimeCommand command)
    {
        if (_sessionRepository == null || _activeSession == null)
        {
            return;
        }

        _activeSession.Events.Add(command.Describe(this));
        await _sessionRepository.SaveAsync(_activeSession);
    }

    /// <summary>Write-through for the initiative tracker (3.5), mirroring
    /// <see cref="AppendSessionEventAsync"/>'s cheap-command-rate reasoning - only actually hits
    /// Raven when a command marked the encounter dirty, so a pure-ECS command (damage, healing)
    /// doesn't pay for a save nothing changed.</summary>
    private async Task PersistEncounterAsync()
    {
        if (_encounterRepository == null || _activeEncounter == null || !_encounterDirty)
        {
            return;
        }

        await _encounterRepository.SaveAsync(_activeEncounter);
        _encounterDirty = false;
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
