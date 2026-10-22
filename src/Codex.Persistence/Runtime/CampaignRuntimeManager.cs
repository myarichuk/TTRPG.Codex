using System.Collections.Concurrent;
using Codex.Core;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Codex.Persistence.Runtime;

/// <summary>
/// Starts, caches, and evicts one <see cref="CampaignRuntime"/> per live campaign (3.1). A DI
/// singleton - the cache of runtimes needs to outlive any single Blazor circuit, since two players
/// and the DM in the same campaign must share one runtime, not one each.
/// </summary>
public sealed class CampaignRuntimeManager : IAsyncDisposable
{
    private readonly IActorRepository actorRepository;
    private readonly ISessionRepository sessionRepository;
    private readonly ICampaignRepository campaignRepository;
    private readonly IEncounterRepository encounterRepository;
    private readonly ComponentRegistry componentRegistry;
    private readonly PluginLoader pluginLoader;
    private readonly ILoggerFactory loggerFactory;
    private readonly ConcurrentDictionary<string, CampaignRuntime> _runtimes = new();

    /// <param name="lifecycle">When given, a campaign's runtime is stopped just before the campaign is
    /// deleted, so the table can't write actors and sessions back into a campaign that no longer exists.</param>
    public CampaignRuntimeManager(
        IActorRepository actorRepository,
        ISessionRepository sessionRepository,
        ICampaignRepository campaignRepository,
        IEncounterRepository encounterRepository,
        ComponentRegistry componentRegistry,
        PluginLoader pluginLoader,
        ILoggerFactory loggerFactory,
        CampaignLifecycleHooks? lifecycle = null)
    {
        this.actorRepository = actorRepository;
        this.sessionRepository = sessionRepository;
        this.campaignRepository = campaignRepository;
        this.encounterRepository = encounterRepository;
        this.componentRegistry = componentRegistry;
        this.pluginLoader = pluginLoader;
        this.loggerFactory = loggerFactory;
        lifecycle?.OnDeleting(EvictAsync);
    }

    /// <summary>
    /// Fires with the campaign id when a table starts or stops, so a player's already-open table
    /// page can attach to the runtime without a reload (the gap the Phase 3 test worked around).
    /// </summary>
    public event Action<string>? RuntimeChanged;

    public CampaignRuntime? TryGet(string campaignId) => _runtimes.GetValueOrDefault(campaignId);

    /// <summary>Starts a campaign's runtime if it isn't already running, hydrating it from every
    /// actor the DM can see (the runtime is server-authoritative and must never be visibility-
    /// filtered the way a player's own queries are). Only the DM can start a campaign - there is no
    /// concept of a player unilaterally spinning up table state.</summary>
    /// <param name="sessionId">The session to run: a planned one the DM picked, or a completed one to
    /// reopen. Null runs the campaign's current session if it is live or planned, else a new one.</param>
    public async Task<CampaignRuntime> GetOrStartAsync(CampaignDocument campaign, CampaignAccess dmAccess, string? sessionId = null)
    {
        if (_runtimes.TryGetValue(campaign.Id, out var existing))
        {
            if (sessionId != null && existing.ActiveSession?.Id != sessionId)
            {
                throw new InvalidOperationException("The table is already running another session. End it first.");
            }

            return existing;
        }

        if (!dmAccess.IsDm)
        {
            throw new InvalidOperationException("Only the campaign's DM can start its runtime.");
        }

        var session = await OpenSessionAsync(campaign, sessionId);
        var encounter = await FindOpenEncounterAsync(session);
        var plugin = pluginLoader.GetPlugin(campaign.SystemId);

        var runtime = new CampaignRuntime(
            campaign.Id,
            actorRepository,
            componentRegistry,
            loggerFactory.CreateLogger<CampaignRuntime>(),
            sessionRepository,
            session,
            encounterRepository,
            plugin?.GetDiceRoller(),
            plugin?.Definition,
            encounter);

        if (!_runtimes.TryAdd(campaign.Id, runtime))
        {
            await runtime.DisposeAsync();
            return _runtimes[campaign.Id];
        }

        if (plugin != null)
        {
            pluginLoader.InitializePlugins(new[] { plugin }, runtime.World);
        }

        var actors = await actorRepository.GetVisibleForCampaignAsync(dmAccess);
        runtime.Hydrate(actors);

        RuntimeChanged?.Invoke(campaign.Id);
        return runtime;
    }

    /// <summary>
    /// Saves a character sheet. While the campaign's table is live and has hydrated the actor, the
    /// save goes through the runtime (<see cref="ReplaceActorCommand"/>) so it is merged with the
    /// table's current state instead of overwriting it; otherwise it is an ordinary guarded save.
    /// Returns false when <paramref name="access"/> may not edit the actor.
    /// </summary>
    /// <param name="baselineSheet">The sheet as the editor loaded it, so fields it did not change keep their live value.</param>
    public async Task<bool> TrySaveActorAsync(ActorDocument edited, IReadOnlyDictionary<string, object>? baselineSheet, CampaignAccess access, SystemDefinition? system)
    {
        var runtime = TryGet(access.CampaignId);
        if (runtime == null || !runtime.HasActor(edited.Id))
        {
            return await actorRepository.TrySaveAsync(edited, access);
        }

        try
        {
            await runtime.EnqueueAsync(new ReplaceActorCommand(edited, baselineSheet, access, system));
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            // The actor was deleted while this save was queued. Not a fall-back to the repository:
            // with nothing stored, that save would create the actor again.
            return false;
        }
        catch (InvalidOperationException) when (TryGet(access.CampaignId) != runtime)
        {
            // The table ended while this save was queued: nothing live left to merge with.
            return await actorRepository.TrySaveAsync(edited, access);
        }
    }

    /// <summary>
    /// Saves a new actor and, while the campaign's table is live, hydrates it into the runtime at
    /// once - wherever it was created from (the cast, the bestiary, the library, the AI), so it can
    /// be put in initiative and take damage straight away (F10). Returns false when
    /// <paramref name="access"/> may not create it.
    /// </summary>
    public async Task<bool> TryCreateActorAsync(ActorDocument actor, CampaignAccess access)
    {
        if (!await actorRepository.TrySaveAsync(actor, access))
        {
            return false;
        }

        if (TryGet(access.CampaignId) is { } runtime)
        {
            try
            {
                await runtime.EnqueueAsync(new HydrateActorCommand(actor));
            }
            catch (InvalidOperationException)
            {
                // The table ended in between; the actor is saved and will be hydrated next start.
            }
        }

        return true;
    }

    /// <summary>Deletes an actor and takes it off the live table (its entity and its place in the
    /// initiative order). Returns false when <paramref name="access"/> may not delete it.</summary>
    public async Task<bool> TryDeleteActorAsync(string actorId, CampaignAccess access)
    {
        if (!await actorRepository.TryDeleteAsync(actorId, access))
        {
            return false;
        }

        if (TryGet(access.CampaignId) is { } runtime && runtime.HasActor(actorId))
        {
            try
            {
                await runtime.EnqueueAsync(new RemoveActorCommand(actorId));
            }
            catch (InvalidOperationException)
            {
                // The table ended in between; nothing live is left to detach from.
            }
        }

        return true;
    }

    /// <summary>
    /// The session the table runs (3.2 - the command log needs somewhere to append to before the
    /// first command arrives). An explicit <paramref name="sessionId"/> is opened as it is: a planned
    /// session starts, a completed one resumes. Otherwise the campaign's current session is resumed
    /// while it is live or planned; once it is completed, a new session begins rather than
    /// appending a second evening's events to the first (F4).
    /// </summary>
    private async Task<SessionDocument> OpenSessionAsync(CampaignDocument campaign, string? sessionId)
    {
        SessionDocument? session = null;
        if (sessionId != null)
        {
            session = await sessionRepository.GetAsync(sessionId);
            if (session == null || session.CampaignId != campaign.Id)
            {
                throw new InvalidOperationException("That session does not belong to this campaign.");
            }
        }
        else if (campaign.CurrentSessionId != null)
        {
            var current = await sessionRepository.GetAsync(campaign.CurrentSessionId);
            if (current is { Status: SessionStatus.Live } && LastActivity(current) is { } last && DateTime.UtcNow - last > StaleLiveSession)
            {
                // A table left idle (evicted with its session still Live) and only started again days
                // later is a new evening, not more of the old one: close the old one out.
                await sessionRepository.UpdateAsync(current.Id, stored =>
                {
                    if (stored.Status == SessionStatus.Live)
                    {
                        stored.Status = SessionStatus.Completed;
                    }
                });
                current = null;
            }

            session = current is { Status: not SessionStatus.Completed } ? current : null;
        }

        if (session == null)
        {
            session = new SessionDocument
            {
                Id = Guid.NewGuid().ToString(),
                CampaignId = campaign.Id,
                Title = $"Session - {DateTime.UtcNow:yyyy-MM-dd}",
                Status = SessionStatus.Live
            };
            await sessionRepository.SaveAsync(session);
        }
        else if (session.Status != SessionStatus.Live)
        {
            session = await sessionRepository.UpdateAsync(session.Id, stored => stored.Status = SessionStatus.Live) ?? session;
        }

        if (campaign.CurrentSessionId != session.Id)
        {
            // Patch just the pointer: the campaign we were handed was loaded when the DM's page
            // opened, and storing it whole would drop members who joined since (F8).
            var id = session.Id;
            await campaignRepository.UpdateAsync(campaign.Id, stored => stored.CurrentSessionId = id);
            campaign.CurrentSessionId = id;
        }

        return session;
    }

    /// <summary>How long a Live session may sit without anything logged before a plain start treats it as over.</summary>
    internal static readonly TimeSpan StaleLiveSession = TimeSpan.FromHours(12);

    /// <summary>When anything was last logged to the session; null when nothing ever was.</summary>
    private static DateTime? LastActivity(SessionDocument session) =>
        session.Events.Select(e => e.Timestamp)
            .Concat(session.RollLog.Select(r => r.Timestamp))
            .Concat(session.Notes.Select(n => n.Timestamp))
            .DefaultIfEmpty()
            .Max() is var last && last != default ? last : null;

    /// <summary>The session's last encounter, when it was never ended: the table picks it up where
    /// it left off instead of wiping the initiative order on every start (F9).</summary>
    private async Task<EncounterDocument?> FindOpenEncounterAsync(SessionDocument session)
    {
        for (var i = session.EncounterIds.Count - 1; i >= 0; i--)
        {
            var encounter = await encounterRepository.GetAsync(session.EncounterIds[i]);
            if (encounter == null)
            {
                continue;
            }

            return encounter.EndedAt == null ? encounter : null;
        }

        return null;
    }

    /// <summary>Stops and disposes a campaign's runtime, and closes out its live session. Called
    /// when the DM ends the table, just before a campaign is deleted, and by
    /// <see cref="EvictIdleAsync"/>.</summary>
    public Task EvictAsync(string campaignId) => EvictAsync(campaignId, completeSession: true);

    /// <param name="completeSession">False leaves the session Live, so the next start resumes it instead of
    /// opening a new one: what idle eviction wants, since a quiet table is not a finished evening.</param>
    private async Task EvictAsync(string campaignId, bool completeSession)
    {
        if (!_runtimes.TryRemove(campaignId, out var runtime))
        {
            return;
        }

        await runtime.DisposeAsync();

        var campaign = completeSession ? await campaignRepository.GetAsync(campaignId) : null;
        if (campaign?.CurrentSessionId is { } sessionId)
        {
            await sessionRepository.UpdateAsync(sessionId, stored =>
            {
                if (stored.Status == SessionStatus.Live)
                {
                    stored.Status = SessionStatus.Completed;
                }
            });
        }

        RuntimeChanged?.Invoke(campaignId);
    }

    /// <summary>Evicts every runtime that has taken no command for <paramref name="idleFor"/>, so a table
    /// nobody ended doesn't stay in memory forever. The session stays Live: a long stretch of play
    /// without commands (roleplay, a break) must not end the evening, and the next start resumes it. The manager owns no timer;
    /// a hosted service calls this. Returns the campaigns it stopped.</summary>
    public async Task<IReadOnlyList<string>> EvictIdleAsync(TimeSpan idleFor, DateTime utcNow)
    {
        var idle = _runtimes.Where(r => utcNow - r.Value.LastActivityUtc >= idleFor).Select(r => r.Key).ToList();
        foreach (var campaignId in idle)
        {
            await EvictAsync(campaignId, completeSession: false);
        }

        return idle;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes.Values)
        {
            await runtime.DisposeAsync();
        }

        _runtimes.Clear();
    }
}
