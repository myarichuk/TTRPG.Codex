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
public sealed class CampaignRuntimeManager(
    IActorRepository actorRepository,
    ISessionRepository sessionRepository,
    ICampaignRepository campaignRepository,
    IEncounterRepository encounterRepository,
    ComponentRegistry componentRegistry,
    PluginLoader pluginLoader,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CampaignRuntime> _runtimes = new();

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

        return runtime;
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

            campaign.CurrentSessionId = session.Id;
            await campaignRepository.SaveAsync(campaign);
        }
        else if (session.Status != SessionStatus.Live)
        {
            session.Status = SessionStatus.Live;
            await sessionRepository.SaveAsync(session);
        }

        return session;
    }

    /// <summary>Stops and disposes a campaign's runtime, and closes out its live session. Nothing
    /// currently calls this on a timer - idle eviction is wiring left for whenever the web app
    /// grows a hosted background service to drive it, not something this class should reach for a
    /// Timer to do on its own.</summary>
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
            var liveSession = await sessionRepository.GetAsync(sessionId);
            if (liveSession is { Status: SessionStatus.Live })
            {
                liveSession.Status = SessionStatus.Completed;
                await sessionRepository.SaveAsync(liveSession);
            }
        }
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
