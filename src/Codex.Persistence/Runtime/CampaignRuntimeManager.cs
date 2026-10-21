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
    public async Task<CampaignRuntime> GetOrStartAsync(CampaignDocument campaign, CampaignAccess dmAccess)
    {
        if (_runtimes.TryGetValue(campaign.Id, out var existing))
        {
            return existing;
        }

        if (!dmAccess.IsDm)
        {
            throw new InvalidOperationException("Only the campaign's DM can start its runtime.");
        }

        var session = await GetOrOpenLiveSessionAsync(campaign);
        var plugin = pluginLoader.GetPlugin(campaign.SystemId);

        var runtime = new CampaignRuntime(
            campaign.Id,
            actorRepository,
            componentRegistry,
            loggerFactory.CreateLogger<CampaignRuntime>(),
            sessionRepository,
            session,
            encounterRepository,
            plugin?.GetDiceRoller());

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
        catch (InvalidOperationException) when (TryGet(access.CampaignId) != runtime)
        {
            // The table ended while this save was queued: nothing live left to merge with.
            return await actorRepository.TrySaveAsync(edited, access);
        }
    }

    /// <summary>Resumes the campaign's current session if it's still Live, reopens it if the DM
    /// left it Planned/Completed, or starts a brand-new one if this campaign has never had one
    /// (3.2 - the command log needs somewhere to append to before the first command arrives).</summary>
    private async Task<SessionDocument> GetOrOpenLiveSessionAsync(CampaignDocument campaign)
    {
        var session = campaign.CurrentSessionId != null
            ? await sessionRepository.GetAsync(campaign.CurrentSessionId)
            : null;

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

            // Patch just the pointer: the campaign we were handed was loaded when the DM's page
            // opened, and storing it whole would drop members who joined since (F8).
            await campaignRepository.UpdateAsync(campaign.Id, stored => stored.CurrentSessionId = session.Id);
            campaign.CurrentSessionId = session.Id;
        }
        else if (session.Status != SessionStatus.Live)
        {
            session = await sessionRepository.UpdateAsync(session.Id, stored => stored.Status = SessionStatus.Live) ?? session;
        }

        return session;
    }

    /// <summary>Stops and disposes a campaign's runtime, and closes out its live session. Called
    /// when the DM ends the table, just before a campaign is deleted, and by
    /// <see cref="EvictIdleAsync"/>.</summary>
    public async Task EvictAsync(string campaignId)
    {
        if (!_runtimes.TryRemove(campaignId, out var runtime))
        {
            return;
        }

        await runtime.DisposeAsync();

        var campaign = await campaignRepository.GetAsync(campaignId);
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
    /// nobody ended doesn't stay in memory (and its session stay Live) forever. The manager owns no timer;
    /// a hosted service calls this. Returns the campaigns it stopped.</summary>
    public async Task<IReadOnlyList<string>> EvictIdleAsync(TimeSpan idleFor, DateTime utcNow)
    {
        var idle = _runtimes.Where(r => utcNow - r.Value.LastActivityUtc >= idleFor).Select(r => r.Key).ToList();
        foreach (var campaignId in idle)
        {
            await EvictAsync(campaignId);
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
