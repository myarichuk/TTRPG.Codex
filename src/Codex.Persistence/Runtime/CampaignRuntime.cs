using System.Threading.Channels;
using Codex.Core;
using Codex.Plugin.Abstractions;
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
    private readonly ILogger _logger;
    private readonly Dictionary<string, Entity> _actorEntities = new();
    private readonly Dictionary<string, ActorDocument> _actorDocs = new();
    private readonly HashSet<string> _dirtyActorIds = new();
    private readonly Channel<QueuedCommand> _commands = Channel.CreateUnbounded<QueuedCommand>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <param name="sessionRepository">Persists <paramref name="activeSession"/>'s appended events (3.2).
    /// Optional so a caller that only needs bare ECS mutation (e.g. a unit test exercising a single
    /// command in isolation) doesn't have to stand up a session too; when omitted, commands still
    /// apply but nothing is logged.</param>
    /// <param name="activeSession">The table's live <see cref="SessionDocument"/> - every applied
    /// command's <see cref="IRuntimeCommand.Describe"/> is appended to its <see cref="SessionDocument.Events"/>.</param>
    public CampaignRuntime(
        string campaignId,
        IActorRepository actorRepository,
        ComponentRegistry componentRegistry,
        ILogger logger,
        ISessionRepository? sessionRepository = null,
        SessionDocument? activeSession = null)
    {
        CampaignId = campaignId;
        _actorRepository = actorRepository;
        _componentRegistry = componentRegistry;
        _logger = logger;
        _sessionRepository = sessionRepository;
        _activeSession = activeSession;
        _loop = Task.Run(RunLoopAsync);
    }

    /// <summary>Promotes a campaign's saved actors into live ECS entities. Called once, right
    /// after construction, before any command is enqueued.</summary>
    public void Hydrate(IEnumerable<ActorDocument> actors)
    {
        foreach (var actor in actors)
        {
            var entity = World.CreateEntity();
            foreach (var component in _componentRegistry.Hydrate(actor.State))
            {
                EntityComponentSync.SetBoxed(entity, component);
            }

            _actorEntities[actor.Id] = entity;
            _actorDocs[actor.Id] = actor;
        }
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

                    await PersistDirtyAsync();
                    await AppendSessionEventAsync(queued.Command);
                    queued.Completion.TrySetResult();
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
