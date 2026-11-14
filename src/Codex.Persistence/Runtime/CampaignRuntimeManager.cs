using System.Collections.Concurrent;
using Codex.Core;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
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
    private readonly ICampaignObjectsRepository? campaignObjects;
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
        CampaignLifecycleHooks? lifecycle = null,
        ICampaignObjectsRepository? campaignObjects = null)
    {
        this.campaignObjects = campaignObjects;
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

    /// <summary>Loads the campaign's library for the rules a table runs (packs and homebrew), set by the host. The table reads
    /// it when it starts; an entry added while it runs is seen by the next table. None: the table knows no entries.</summary>
    public Func<CampaignDocument, CampaignAccess, SystemDefinition, Task<Codex.Core.Rules.IEntryResolver>>? EntryResolvers { get; set; }

    public CampaignRuntime? TryGet(string campaignId) => _runtimes.GetValueOrDefault(campaignId);

    /// <summary>Starts a campaign's runtime if it isn't already running, hydrating it from every
    /// actor the DM can see (the runtime is server-authoritative and must never be visibility-
    /// filtered the way a player's own queries are). Only the DM can start a campaign - there is no
    /// concept of a player unilaterally spinning up table state.</summary>
    /// <param name="sessionId">The session to run: a planned one the DM picked, or a completed one to
    /// reopen. Null runs the campaign's current session if it is live or planned, else a new one.</param>
    public async Task<CampaignRuntime> GetOrStartAsync(CampaignDocument campaign, CampaignAccess dmAccess, string? sessionId = null)
    {
        if (TryGetRunning(campaign.Id, sessionId) is { } running)
        {
            return running;
        }

        if (!dmAccess.IsDm)
        {
            throw new InvalidOperationException("Only the campaign's GM can start its runtime.");
        }

        // A table runs the system's rules; with the system gone there are none to run, and the campaign is read-only.
        if (dmAccess.SystemMissing)
        {
            throw new Codex.Localization.LocalizedException("Campaign.SystemMissing", campaign.SystemId);
        }

        // One start (or end) per campaign at a time (R6): two "Start table" clicks used to both open a session and both
        // patch the campaign before one of them lost the race to publish its runtime.
        var gate = GateFor(campaign.Id);
        await gate.WaitAsync();
        try
        {
            if (TryGetRunning(campaign.Id, sessionId) is { } started)
            {
                return started;
            }

            var session = await OpenSessionAsync(campaign, sessionId);
            var encounter = await FindOpenEncounterAsync(session);
            var plugin = pluginLoader.GetPlugin(campaign.SystemId);
            session = await StampSessionAsync(session, campaign.SystemId);

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
            // Every roll says which of the session's systems it was made under; a session reopened after the rules changed
            // has two, and its rolls are told apart.
            if (session.SystemStamps.Count > 0 && session.SystemStamps[^1].SystemId == campaign.SystemId) runtime.SystemStampIndex = session.SystemStamps.Count - 1;

            // Built whole before anyone can see it (R6): published first, a command could walk the actors while they were
            // still being added, and a failed hydrate left a half-built table cached that nothing announced.
            try
            {
                if (plugin != null)
                {
                    pluginLoader.InitializePlugins(new[] { plugin }, runtime.World);
                }

                // A table reads the campaign's library when its system has features to work out: a sheet kind's, or sheetRules'
                // (counters of uses). Without it a system's counters would wait for each sheet to be opened and saved.
                if (EntryResolvers != null && plugin?.Definition is { } definition && (definition.SheetKinds.Count > 0 || definition.SheetRules is { Features.Count: > 0 }))
                {
                    runtime.Resolver = await EntryResolvers(campaign, dmAccess, definition);
                }

                runtime.Hydrate((await actorRepository.GetVisibleForCampaignAsync(dmAccess)).Where(a => !a.Retired));
                if (campaignObjects != null)
                {
                    runtime.CampaignObjectsStore = campaignObjects;
                    runtime.HydrateCampaignObjects((await campaignObjects.GetDocumentAsync(campaign.Id))?.State);
                }
            }
            catch
            {
                await runtime.DisposeAsync();
                throw;
            }

            _runtimes[campaign.Id] = runtime;
            RuntimeChanged?.Invoke(campaign.Id);
            return runtime;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The GM changing the campaign's clocks and tables (overhaul step 7): through the table when it is live, so the
    /// change is the table's and in its log, else straight to the stored objects. <paramref name="edit"/> returns false to change
    /// nothing, and may run more than once on the stored path (once per write that lost a race). Returns whether it changed
    /// anything; false for anyone but the GM.</summary>
    public async Task<bool> TryChangeCampaignObjectsAsync(CampaignAccess access, Func<Dictionary<string, object>, bool> edit, EventMessage said)
    {
        if (!access.IsDm || access.SystemMissing) return false;
        if (TryGet(access.CampaignId) is { } runtime)
        {
            var command = new EditCampaignObjectsCommand(edit, said);
            await runtime.EnqueueAsync(command);
            return command.Changed;
        }

        if (campaignObjects == null) return false;
        var changed = false;
        var saved = await campaignObjects.TryChangeAsync(access, state => changed = edit(state));
        return saved != null && changed;
    }

    /// <summary>Raises an event that is the campaign's (a clock ticked, or a declared event with no actor): at the table when it
    /// is live, else on the stored objects alone (what it asks of actors waits for a table). Returns why it could not, or null.
    /// GM only.</summary>
    public async Task<string?> RaiseCampaignEventAsync(CampaignAccess access, SystemDefinition system, string type, IReadOnlyDictionary<string, object?> fields)
    {
        if (!access.IsDm || access.SystemMissing) return "Only the GM changes the campaign's objects.";
        if (TryGet(access.CampaignId) is { } runtime)
        {
            var command = new CampaignEventCommand(type, fields);
            await runtime.EnqueueAsync(command);
            return command.Failed;
        }

        if (campaignObjects == null) return "The campaign's objects are not stored here.";
        string? failed = null;
        await campaignObjects.TryChangeAsync(access, state =>
        {
            try
            {
                var raised = Codex.Core.Rules.SheetEvents.Raise(system, Codex.Core.Rules.NullEntryResolver.Instance, new Dictionary<string, object>(),
                    new Codex.Core.Rules.SheetEvent(type, fields), null, state);
                return raised.Campaign is { Changed.Count: > 0 };
            }
            catch (Exception ex) when (ex is Codex.Core.Rules.SheetEventException or Codex.Core.Rules.FormulaException)
            {
                failed = ex.Message;
                return false;
            }
        });
        return failed;
    }

    private CampaignRuntime? TryGetRunning(string campaignId, string? sessionId)
    {
        if (!_runtimes.TryGetValue(campaignId, out var existing))
        {
            return null;
        }

        if (sessionId != null && existing.ActiveSession?.Id != sessionId)
        {
            throw new InvalidOperationException("The table is already running another session. End it first.");
        }

        return existing;
    }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    private SemaphoreSlim GateFor(string campaignId) => _gates.GetOrAdd(campaignId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Saves a character sheet. While the campaign's table is live and has hydrated the actor, the
    /// save goes through the runtime (<see cref="ReplaceActorCommand"/>) so it is merged with the
    /// table's current state instead of overwriting it; otherwise it is an ordinary guarded save.
    /// Returns false when <paramref name="access"/> may not edit the actor.
    /// </summary>
    /// <param name="baselineSheet">The sheet as the editor loaded it, so fields it did not change keep their live value.</param>
    public async Task<bool> TrySaveActorAsync(ActorDocument edited, IReadOnlyDictionary<string, object>? baselineSheet, CampaignAccess access, SystemDefinition? system)
    {
        // The repository refuses this access too (NowAsync); the live-table path below reads the role itself, so it checks here.
        if (access.SystemMissing) return false;

        var runtime = TryGet(access.CampaignId);
        if (runtime == null || !runtime.HasActor(edited.Id))
        {
            return await actorRepository.TrySaveAsync(edited, access);
        }

        // The table writes what it is given without asking the store, so the role is read here, as every repository write
        // reads it (F13): a co-DM demoted with the sheet open saves as a player, someone removed saves nothing.
        var campaign = await campaignRepository.GetAsync(access.CampaignId);
        if (campaign == null || CampaignAccessResolver.RoleOf(campaign, access.UserId) is not { } role)
        {
            return false;
        }

        access = role == access.Role ? access : new CampaignAccess(access.CampaignId, access.UserId, role, access.SystemMissing);
        // The table plays the definition it started with (overhaul step 10): a system reloaded since is not the one its rules
        // run, so the save is merged under the table's, in the reader's language as the page sent it.
        if (runtime.System != null) system = runtime.System.Localize(System.Globalization.CultureInfo.CurrentUICulture.Name);
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

    /// <summary>Takes an actor out of play (D17) and off the live table. Its document stays, so everything that mentions it
    /// still names it. Returns false when <paramref name="access"/> may not retire it.</summary>
    public async Task<bool> TryRetireActorAsync(string actorId, CampaignAccess access)
    {
        if (await actorRepository.TryRetireAsync(actorId, access) == null)
        {
            return false;
        }

        await DetachAsync(access.CampaignId, actorId);
        return true;
    }

    /// <summary>Brings a retired actor back, onto the live table too when one is running. The DM only.</summary>
    public async Task<bool> TryRestoreActorAsync(string actorId, CampaignAccess access)
    {
        if (await actorRepository.TryRestoreAsync(actorId, access) is not { } restored)
        {
            return false;
        }

        await SendIfLiveAsync(access.CampaignId, new HydrateActorCommand(restored));
        return true;
    }

    /// <summary>Changes who among the players may see an actor (R1). While the table is live, every open table page learns
    /// of it at once and reloads who it may see. The DM only.</summary>
    public async Task<bool> TrySetVisibilityAsync(string actorId, ActorVisibility visibility, CampaignAccess access)
    {
        if (await actorRepository.TrySetVisibilityAsync(actorId, visibility, access) is not { } stored)
        {
            return false;
        }

        if (TryGet(access.CampaignId) is { } runtime && runtime.HasActor(actorId))
        {
            await SendIfLiveAsync(access.CampaignId, new RefreshActorCommand(stored, visibility));
        }

        return true;
    }

    /// <summary>The dice the campaign's system rolls with (R8): its plugin's roller, the standard one otherwise. A sheet rolling
    /// away from the table uses this too, so it rolls as the table would.</summary>
    public IDiceRoller DiceRollerFor(string systemId) => pluginLoader.GetPlugin(systemId)?.GetDiceRoller() ?? StandardDiceRoller.Instance;

    private async Task DetachAsync(string campaignId, string actorId)
    {
        if (TryGet(campaignId) is { } runtime && runtime.HasActor(actorId))
        {
            await SendIfLiveAsync(campaignId, new RemoveActorCommand(actorId));
        }
    }

    private async Task SendIfLiveAsync(string campaignId, IRuntimeCommand command)
    {
        if (TryGet(campaignId) is not { } runtime)
        {
            return;
        }

        try
        {
            await runtime.EnqueueAsync(command);
        }
        catch (InvalidOperationException)
        {
            // The table ended in between; the stored actor is what the next start reads.
        }
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
            if (current is { Status: SessionStatus.Live } && LastActivity(current) is { } last && DateTime.UtcNow - last > StaleLiveSession
                && await FindOpenEncounterAsync(current) == null) // a fight left open is waiting for the same table, however long
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

    /// <summary>Records on the session the system the table starts under, when it is not the one it last ran under (overhaul
    /// section 6, "Determinism"). The table keeps this system until it ends, whatever is reloaded meanwhile.</summary>
    private async Task<SessionDocument> StampSessionAsync(SessionDocument session, string systemId)
    {
        if (pluginLoader.GetFingerprint(systemId) is not { } fingerprint) return session;
        if (session.SystemStamps.LastOrDefault() is { } last && last.ChangesTo(systemId, fingerprint).Count == 0 && last.SystemId == systemId) return session;
        var stamp = SystemStamp.Of(systemId, fingerprint, DateTime.UtcNow);
        return await sessionRepository.UpdateAsync(session.Id, stored => stored.SystemStamps.Add(stamp)) ?? session;
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
    /// <summary>Ends the campaign's table. The session is marked complete unless a fight is still open: then it stays
    /// open, so the next start of the table picks the initiative order back up (D6). Returns whether it was completed.</summary>
    public Task<bool> EvictAsync(string campaignId) => EvictAsync(campaignId, completeSession: true);

    /// <param name="completeSession">False leaves the session Live, so the next start resumes it instead of
    /// opening a new one: what idle eviction wants, since a quiet table is not a finished evening.</param>
    private async Task<bool> EvictAsync(string campaignId, bool completeSession)
    {
        // Waits for a start under way, so a campaign deleted mid-start does not get its table published afterwards.
        var gate = GateFor(campaignId);
        await gate.WaitAsync();
        CampaignRuntime? runtime;
        try
        {
            if (!_runtimes.TryRemove(campaignId, out runtime))
            {
                return false;
            }
        }
        finally
        {
            gate.Release();
        }

        await runtime.DisposeAsync();
        // A system DLL replaced while this table ran may now have nothing left using its old load context.
        Codex.Core.Systems.FolderLoadContexts.Sweep();

        completeSession &= runtime.ActiveEncounter is not { EndedAt: null, Participants.Count: > 0 };
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
        return completeSession;
    }

    /// <summary>Evicts every runtime that has taken no command for <paramref name="idleFor"/> and that no open page is
    /// watching, so a table nobody ended doesn't stay in memory forever. A table with pages open on it is in use however
    /// quiet (four players roleplaying for two hours send no commands) and is left alone (R6). The session stays Live: a
    /// long stretch of play without commands must not end the evening, and the next start resumes it. The manager owns no
    /// timer; a hosted service calls this. Returns the campaigns it stopped.</summary>
    public async Task<IReadOnlyList<string>> EvictIdleAsync(TimeSpan idleFor, DateTime utcNow)
    {
        var idle = _runtimes.Where(r => !r.Value.IsWatched && utcNow - r.Value.LastActivityUtc >= idleFor).Select(r => r.Key).ToList();
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
