using Codex.Core.Components;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>
/// Exercises CampaignRuntime (3.1) against the same embedded-RavenDB fixture the rest of the
/// persistence suite uses - the runtime is the thing that eventually replaces the CodexWorld
/// singleton (B8), so it needs to prove it actually round-trips ECS state through real actor
/// documents, not just against an in-memory fake.
/// </summary>
[Collection("RavenDb")]
public class CampaignRuntimeTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actorRepository;
    private readonly RavenSessionRepository _sessionRepository;
    private readonly RavenEncounterRepository _encounterRepository;
    private readonly ComponentRegistry _componentRegistry;

    public CampaignRuntimeTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("Runtime_" + Guid.NewGuid());
        _actorRepository = new ActorRepository(_dbService);
        _sessionRepository = new RavenSessionRepository(_dbService);
        _encounterRepository = new RavenEncounterRepository(_dbService);
        _componentRegistry = new ComponentRegistry();
        _componentRegistry.Register<ResourcePoolComponent>();
    }

    private async Task<SessionDocument> SeedSessionAsync(string campaignId)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessionRepository.SaveAsync(session);
        return session;
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<ActorDocument> SeedActorAsync(string campaignId, int hp)
    {
        var pool = new ResourcePoolComponent();
        pool.Set("HP", hp);
        pool.Set("HP_Max", hp);

        var doc = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            Name = "Goblin",
            State = _componentRegistry.Snapshot(new object[] { pool })
        };

        await _actorRepository.SaveAsync(doc);
        return doc;
    }

    private (CampaignRuntimeManager Manager, CampaignRepository Campaigns) BuildManager()
    {
        var hooks = new CampaignLifecycleHooks();
        var campaigns = new CampaignRepository(_dbService, new NoOpSystemCatalog(), _actorRepository, _sessionRepository,
            new RavenNoteRepository(_dbService), new RegionRepository(_dbService), new FactRepository(_dbService),
            new RavenHomebrewRepository(_dbService), _encounterRepository, lifecycle: hooks);
        var loader = new Codex.Core.PluginLoader(NullLogger<Codex.Core.PluginLoader>.Instance, _componentRegistry, Substitute.For<IContentPackLoader>());
        return (new CampaignRuntimeManager(_actorRepository, _sessionRepository, campaigns, _encounterRepository, _componentRegistry, loader, NullLoggerFactory.Instance, hooks), campaigns);
    }

    [Fact]
    public async Task DeletingACampaign_StopsItsLiveTable_AndTheTableCannotResurrectAnything_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Doomed" };
        await campaigns.SaveAsync(campaign);
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);

        var runtime = await manager.GetOrStartAsync(campaign, dm);
        var changes = new List<string>();
        manager.RuntimeChanged += changes.Add;

        Assert.Equal(CampaignDeleteResult.Deleted, await campaigns.DeleteAsync(campaignId, "owner"));

        Assert.Null(manager.TryGet(campaignId));
        Assert.Contains(campaignId, changes);
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 1, "late")));
        Assert.Empty(await _actorRepository.GetVisibleForCampaignAsync(dm));
        Assert.Empty(await _sessionRepository.GetAllForCampaignAsync(campaignId));
    }

    [Fact]
    public async Task ARuntimeNobodyHasUsedForAWhile_IsEvicted_AndItsSessionResumedOnTheNextStart_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Forgotten" };
        await campaigns.SaveAsync(campaign);
        var runtime = await manager.GetOrStartAsync(campaign, new CampaignAccess(campaignId, "owner", CampaignRole.DM));

        Assert.Empty(await manager.EvictIdleAsync(TimeSpan.FromHours(2), DateTime.UtcNow));
        Assert.NotNull(manager.TryGet(campaignId));

        Assert.Equal(new[] { campaignId }, await manager.EvictIdleAsync(TimeSpan.FromHours(2), runtime.LastActivityUtc.AddHours(3)));
        Assert.Null(manager.TryGet(campaignId));

        // A quiet table is not a finished evening: the session stays Live and the next start picks it up.
        var session = Assert.Single(await _sessionRepository.GetAllForCampaignAsync(campaignId));
        Assert.Equal(SessionStatus.Live, session.Status);
        var restarted = await manager.GetOrStartAsync(campaign, new CampaignAccess(campaignId, "owner", CampaignRole.DM));
        Assert.Equal(session.Id, restarted.ActiveSession!.Id);
        Assert.Single(await _sessionRepository.GetAllForCampaignAsync(campaignId));
        await manager.EvictAsync(campaignId);
    }

    /// <summary>Overhaul step 10: a table keeps the system it started with through a reload, and the session records each
    /// system it was played under.</summary>
    [Fact]
    public async Task ATable_KeepsItsSystemThroughAReload_AndTheSessionRecordsEachSystemItRanUnder_Async()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-runtime-reload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "street"));
        var yaml = Path.Combine(root, "street", "system.yaml");
        File.WriteAllText(yaml, "id: street\nname: Street\nversion: 1.0.0\n");
        try
        {
            var hooks = new CampaignLifecycleHooks();
            var campaigns = new CampaignRepository(_dbService, new NoOpSystemCatalog(), _actorRepository, _sessionRepository,
                new RavenNoteRepository(_dbService), new RegionRepository(_dbService), new FactRepository(_dbService),
                new RavenHomebrewRepository(_dbService), _encounterRepository, lifecycle: hooks);
            var loader = new Codex.Core.PluginLoader(NullLogger<Codex.Core.PluginLoader>.Instance, _componentRegistry, Substitute.For<IContentPackLoader>());
            await loader.LoadAndInitializeAsync(root);
            var manager = new CampaignRuntimeManager(_actorRepository, _sessionRepository, campaigns, _encounterRepository, _componentRegistry, loader, NullLoggerFactory.Instance, hooks);
            var campaignId = Guid.NewGuid().ToString();
            var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Night City", SystemId = "street" };
            await campaigns.SaveAsync(campaign);
            var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);

            var runtime = await manager.GetOrStartAsync(campaign, dm);
            await runtime.EnqueueAsync(new RollDiceCommand("1d6", null, "owner", false));
            File.WriteAllText(yaml, "id: street\nname: Street\nversion: 1.1.0\n");
            Assert.Equal(new[] { "street" }, (await loader.ReloadAsync()).Changed);

            Assert.Equal("1.0.0", runtime.System!.Version);
            Assert.Same(runtime, await manager.GetOrStartAsync(campaign, dm));

            // Ended and started again, the same session runs under the new system, and says so.
            await manager.EvictIdleAsync(TimeSpan.Zero, DateTime.UtcNow.AddHours(1));
            var restarted = await manager.GetOrStartAsync(campaign, dm);
            Assert.Equal("1.1.0", restarted.System!.Version);
            var session = (await _sessionRepository.GetAsync(restarted.ActiveSession!.Id))!;
            Assert.Equal(new[] { "1.0.0", "1.1.0" }, session.SystemStamps.Select(s => s.Version));
            // Each roll names the stamp it was made under.
            await restarted.EnqueueAsync(new RollDiceCommand("1d6", null, "owner", false));
            await manager.EvictIdleAsync(TimeSpan.Zero, DateTime.UtcNow.AddHours(1));
            Assert.Equal(new int?[] { 0, 1 }, (await _sessionRepository.GetAsync(restarted.ActiveSession!.Id))!.RollLog.Select(r => r.SystemStamp));
            restarted = await manager.GetOrStartAsync(campaign, dm);
            await manager.EvictIdleAsync(TimeSpan.Zero, DateTime.UtcNow.AddHours(1));
            Assert.Equal(2, (await manager.GetOrStartAsync(campaign, dm)).ActiveSession!.SystemStamps.Count);
            await manager.EvictAsync(campaignId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Overhaul step 10: a sheet saved at a running table after its system was reloaded is merged under the definition
    /// the table runs, not the reloaded one: here the reload drops the Grit resource the table still plays.</summary>
    [Fact]
    public async Task ASheetSavedAtATable_AfterAReload_IsMergedUnderTheTablesSystem_Async()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-runtime-save-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "street"));
        var yaml = Path.Combine(root, "street", "system.yaml");
        File.WriteAllText(yaml, "id: street\nname: Street\nversion: 1.0.0\nresources:\n  - { key: Grit, label: Grit, maxKey: Grit_Max, default: 4 }\n");
        try
        {
            var hooks = new CampaignLifecycleHooks();
            var campaigns = new CampaignRepository(_dbService, new NoOpSystemCatalog(), _actorRepository, _sessionRepository,
                new RavenNoteRepository(_dbService), new RegionRepository(_dbService), new FactRepository(_dbService),
                new RavenHomebrewRepository(_dbService), _encounterRepository, lifecycle: hooks);
            var loader = new Codex.Core.PluginLoader(NullLogger<Codex.Core.PluginLoader>.Instance, _componentRegistry, Substitute.For<IContentPackLoader>());
            await loader.LoadAndInitializeAsync(root);
            var manager = new CampaignRuntimeManager(_actorRepository, _sessionRepository, campaigns, _encounterRepository, _componentRegistry, loader, NullLoggerFactory.Instance, hooks);
            var campaignId = Guid.NewGuid().ToString();
            var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Night City", SystemId = "street" };
            await campaigns.SaveAsync(campaign);
            var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);
            var pool = new ResourcePoolComponent();
            pool.Set("Grit", 4);
            pool.Set("Grit_Max", 4);
            var actor = new ActorDocument
            {
                Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Rook",
                Sheet = new Dictionary<string, object> { ["Grit"] = 4, ["Grit_Max"] = 4 },
                State = _componentRegistry.Snapshot(new object[] { pool })
            };
            await _actorRepository.SaveAsync(actor);

            await manager.GetOrStartAsync(campaign, dm);
            File.WriteAllText(yaml, "id: street\nname: Street\nversion: 1.1.0\n");
            Assert.Equal(new[] { "street" }, (await loader.ReloadAsync()).Changed);

            // The page loaded after the reload and sends the new definition; the table's own is the one the merge uses.
            var edited = new ActorDocument { Id = actor.Id, CampaignId = campaignId, Name = "Rook", Sheet = new Dictionary<string, object> { ["Grit"] = 2, ["Grit_Max"] = 4 } };
            Assert.True(await manager.TrySaveActorAsync(edited, new Dictionary<string, object> { ["Grit"] = 4, ["Grit_Max"] = 4 }, dm, loader.GetDefinition("street")));
            await manager.EvictAsync(campaignId);

            var stored = await _actorRepository.GetVisibleAsync(actor.Id, dm);
            Assert.Equal(2, _componentRegistry.Hydrate(stored!.State).OfType<ResourcePoolComponent>().Single().Get("Grit"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>R6: two "Start table" clicks at once used to open two sessions and patch the campaign twice before one runtime
    /// lost the race to publish.</summary>
    [Fact]
    public async Task TwoStartsAtOnce_RunOneTable_OnOneSession_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Eager" };
        await campaigns.SaveAsync(campaign);
        await SeedActorAsync(campaignId, hp: 10);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);

        var started = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => manager.GetOrStartAsync(campaign, dm))));

        Assert.All(started, r => Assert.Same(started[0], r));
        Assert.Single(await _sessionRepository.GetAllForCampaignAsync(campaignId));
        await manager.EvictAsync(campaignId);
    }

    /// <summary>R6: the runtime used to be published before it was hydrated, and stayed cached, half built, when hydrating
    /// failed.</summary>
    [Fact]
    public async Task ATableThatFailsToLoadItsActors_IsNotLeftRunning_AndTheNextStartTriesAgain_Async()
    {
        var (_, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Broken" };
        await campaigns.SaveAsync(campaign);
        var dm = new CampaignAccess(campaignId, "owner", CampaignRole.DM);

        var actors = Substitute.For<IActorRepository>();
        var fail = true;
        actors.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => fail
            ? Task.FromException<IEnumerable<ActorDocument>>(new TimeoutException("database busy"))
            : Task.FromResult<IEnumerable<ActorDocument>>(Array.Empty<ActorDocument>()));
        var loader = new Codex.Core.PluginLoader(NullLogger<Codex.Core.PluginLoader>.Instance, _componentRegistry, Substitute.For<IContentPackLoader>());
        var manager = new CampaignRuntimeManager(actors, _sessionRepository, campaigns, _encounterRepository, _componentRegistry, loader, NullLoggerFactory.Instance);
        var changes = new List<string>();
        manager.RuntimeChanged += changes.Add;

        await Assert.ThrowsAsync<TimeoutException>(() => manager.GetOrStartAsync(campaign, dm));
        Assert.Null(manager.TryGet(campaignId));
        Assert.Empty(changes);

        fail = false;
        Assert.NotNull(await manager.GetOrStartAsync(campaign, dm));
        Assert.Equal(new[] { campaignId }, changes);
        await manager.EvictAsync(campaignId);
    }

    /// <summary>R6: ending the table cancelled its loop without running what was queued, so a sheet save or a roll queued just
    /// before waited forever (the save spinner never stopped). Queued commands now finish first.</summary>
    [Fact]
    public async Task EndingTheTable_FinishesTheCommandsAlreadyQueued_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Last orders" };
        await campaigns.SaveAsync(campaign);
        var actor = await SeedActorAsync(campaignId, hp: 20);
        var runtime = await manager.GetOrStartAsync(campaign, new CampaignAccess(campaignId, "owner", CampaignRole.DM));

        var queued = Enumerable.Range(0, 5).Select(_ => runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 1, "parting shot"))).ToList();
        await manager.EvictAsync(campaignId);

        await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(30));
        var stored = await _actorRepository.GetVisibleAsync(actor.Id, new CampaignAccess(campaignId, "owner", CampaignRole.DM));
        Assert.Equal(15, _componentRegistry.Hydrate(stored!.State).OfType<ResourcePoolComponent>().Single().Get("HP"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 1, "too late")));
    }

    /// <summary>R6: idle eviction counted only commands, so two hours of roleplay with four pages open on the table took the
    /// table away from under them. A table someone is watching is in use.</summary>
    [Fact]
    public async Task AQuietTableWithPagesOpenOnIt_IsNotEvicted_UntilTheyClose_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Roleplay" };
        await campaigns.SaveAsync(campaign);
        var runtime = await manager.GetOrStartAsync(campaign, new CampaignAccess(campaignId, "owner", CampaignRole.DM));
        Action<IReadOnlyCollection<string>> page = _ => { };
        runtime.StateChanged += page;

        var later = runtime.LastActivityUtc.AddHours(3);
        Assert.Empty(await manager.EvictIdleAsync(TimeSpan.FromHours(2), later));

        runtime.StateChanged -= page;
        Assert.Equal(new[] { campaignId }, await manager.EvictIdleAsync(TimeSpan.FromHours(2), later));
    }

    [Fact]
    public async Task ARuntimeThatIsStillTakingCommands_IsNotIdle_Async()
    {
        var (manager, campaigns) = BuildManager();
        var campaignId = Guid.NewGuid().ToString();
        var campaign = new CampaignDocument { Id = campaignId, OwnerId = "owner", Name = "Busy" };
        await campaigns.SaveAsync(campaign);
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var runtime = await manager.GetOrStartAsync(campaign, new CampaignAccess(campaignId, "owner", CampaignRole.DM));
        var before = runtime.LastActivityUtc;

        await Task.Delay(20);
        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 1, "dm"));

        Assert.True(runtime.LastActivityUtc > before);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task SessionLog_ForAPlayer_NeverNamesAHiddenActor_AndNeverShowsExactMonsterDamage_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var ambusher = await SeedActorAsync(campaignId, hp: 40);
        ambusher.Name = "Strahd the Ambusher";
        ambusher.Visibility = ActorVisibility.Hidden;
        var goblin = await SeedActorAsync(campaignId, hp: 12);
        goblin.Name = "Goblin Skirmisher";
        goblin.Kind = ActorKind.Monster;
        var hero = await SeedActorAsync(campaignId, hp: 30);
        hero.Name = "Aria";
        hero.Kind = ActorKind.PlayerCharacter;
        hero.OwnerUserId = "player-1";
        // The runtime refreshes its cached documents from the store after each persist, so the
        // renamed actors must be stored, not just edited in memory.
        foreach (var doc in new[] { ambusher, goblin, hero }) await _actorRepository.SaveAsync(doc);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { ambusher, goblin, hero });

        await runtime.EnqueueAsync(new SetInitiativeCommand(ambusher.Id, 19));
        await runtime.EnqueueAsync(new SetInitiativeCommand(hero.Id, 12));
        await runtime.EnqueueAsync(new ApplyDamageCommand(ambusher.Id, "HP", 13, "dm-user"));
        await runtime.EnqueueAsync(new ApplyDamageCommand(goblin.Id, "HP", 7, "dm-user"));
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 4, "dm-user"));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(ambusher.Id, "frightened", "dm-user", 2, EffectExpiry.EndOfTurn, ambusher.Id));
        await runtime.EnqueueAsync(new NextTurnCommand());
        await runtime.EnqueueAsync(new RestCommand(ambusher.Id, "short rest", new[] { "HP" }));

        var events = (await _sessionRepository.GetAsync(session.Id))!.Events;
        Assert.Contains(events, e => e.Description.Contains("Strahd"));

        // What the actor repository hands this player: no Hidden actors.
        var access = new CampaignAccess(campaignId, "player-1", CampaignRole.Player);
        var visible = new[] { goblin, hero };
        var seen = SessionEventRedactor.ForViewer(events, access, visible);

        Assert.DoesNotContain(seen, e => e.Description.Contains("Strahd") || e.Description.Contains("Ambusher"));
        Assert.DoesNotContain(seen, e => e.Description.Contains("took 7"));
        Assert.Contains(seen, e => e.Description == "Goblin Skirmisher took damage.");
        Assert.Contains(seen, e => e.Description.StartsWith("Aria took 4 damage"));

        // The DM keeps the full account.
        Assert.Contains(SessionEventRedactor.ForViewer(events, new CampaignAccess(campaignId, "dm", CampaignRole.DM), new[] { ambusher, goblin, hero }),
            e => e.Description.Contains("Strahd the Ambusher took 13 damage"));

        // And so does nothing the player-facing AI digest is built from.
        var digest = SessionDigest.Build(await _sessionRepository.GetAsync(session.Id) ?? session, Array.Empty<NoteDocument>(), forDm: false, actors: new[] { ambusher, goblin, hero });
        Assert.DoesNotContain(digest.Events, line => line.Contains("Strahd") || line.Contains("took 7"));
        Assert.Contains(digest.Events, line => line.Contains("Goblin Skirmisher took damage."));
        Assert.DoesNotContain(digest.Events, line => line.Contains("took 4"));
    }

    [Fact]
    public void SessionLog_EventsWrittenBeforeActorIdsExisted_AreNotHandedToPlayers()
    {
        var legacy = new SessionEvent { Type = "Damage", Description = "Strahd took 13 HP damage from dm-user." };
        var note = new SessionEvent { Type = "Note", Description = "Note: the bridge is trapped" };

        var seen = SessionEventRedactor.ForViewer(new[] { legacy, note }, new CampaignAccess("c", "p", CampaignRole.Player), Array.Empty<ActorDocument>());

        Assert.Equal(new[] { "Note: the bridge is trapped" }, seen.Select(e => e.Description));
    }

    [Fact]
    public void SessionLog_ANumberEventWithoutAPlayersVersion_ReachesOnlyTheOwner()
    {
        var hero = new ActorDocument { Id = "hero", Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = "p1", Visibility = ActorVisibility.Known };
        var hurt = new SessionEvent { Type = "Damage", Description = "Aria took 13 HP damage.", ActorIds = { "hero" } };
        var rested = new SessionEvent { Type = "Rest", Description = "Aria rested.", ActorIds = { "hero" } };

        var other = SessionEventRedactor.ForViewer(new[] { hurt, rested }, new CampaignAccess("c", "p2", CampaignRole.Player), new[] { hero });
        var owner = SessionEventRedactor.ForViewer(new[] { hurt, rested }, new CampaignAccess("c", "p1", CampaignRole.Player), new[] { hero });

        Assert.Equal(new[] { "Aria rested." }, other.Select(e => e.Description));
        Assert.Equal(new[] { "Aria took 13 HP damage.", "Aria rested." }, owner.Select(e => e.Description));
    }

    [Fact]
    public async Task ApplyDamageCommand_PersistsClampedHp_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        // Two hits in the same "tick" must both land (B7) and the total must clamp at zero rather
        // than go negative.
        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 3, "goblin-sword"));
        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 20, "goblin-sword"));

        var persisted = await _actorRepository.GetVisibleAsync(actor.Id, new CampaignAccess(campaignId, "dm", CampaignRole.DM));
        var pool = _componentRegistry.Hydrate(persisted!.State).OfType<ResourcePoolComponent>().Single();
        Assert.Equal(0, pool.Get("HP"));
    }

    [Fact]
    public async Task AddStatusThenAdvanceTurn_KeepsEffectsIndependent_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new AddStatusEffectCommand(actor.Id, "poisoned", "trap", 10, EffectExpiry.EndOfTurn, actor.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(actor.Id, "stunned", "trap", 1, EffectExpiry.EndOfTurn, actor.Id));

        var entity = runtime.GetEntity(actor.Id)!.Value;
        Assert.Equal(2, entity.Get<ActiveEffectsComponent>().Effects.Count);

        await runtime.EnqueueAsync(new AdvanceTurnCommand(actor.Id));

        var effects = entity.Get<ActiveEffectsComponent>().Effects;
        Assert.Single(effects);
        Assert.Equal("poisoned", effects[0].EffectId);
    }

    [Fact]
    public async Task EnqueueAsync_AppendsAndPersistsOneSessionEventPerCommand_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 3, "goblin-sword"));
        await runtime.EnqueueAsync(new ApplyHealingCommand(actor.Id, "HP", 1, "healing-word"));

        // The runtime keeps the same SessionDocument instance alive rather than re-reading it, so
        // the log is visible on it directly - but it must also actually be the thing on disk, not
        // just an in-memory list nobody persisted (the whole point of 3.2 is an audit trail that
        // survives the process).
        Assert.Equal(2, session.Events.Count);
        Assert.Equal("Damage", session.Events[0].Type);
        Assert.Contains("took 3 damage", session.Events[0].Description);
        Assert.Equal("Healing", session.Events[1].Type);

        var persisted = await _sessionRepository.GetAsync(session.Id);
        Assert.Equal(2, persisted!.Events.Count);
    }

    [Fact]
    public async Task StateChanged_FiresWithAffectedActorIds_ForEachCommand_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        var broadcasts = new List<IReadOnlyCollection<string>>();
        runtime.StateChanged += ids => broadcasts.Add(ids);

        await runtime.EnqueueAsync(new ApplyDamageCommand(actor.Id, "HP", 3, "goblin-sword"));

        Assert.Single(broadcasts);
        Assert.Equal(new[] { actor.Id }, broadcasts[0]);

        // AdvanceTurnCommand's own AffectedActorIds is empty (it can't predict who it'll touch),
        // but it marks every hydrated actor dirty directly - the broadcast must reflect that, not
        // the empty declared set, since a subscriber that trusted AffectedActorIds alone would
        // never learn this actor's effects just ticked down.
        await runtime.EnqueueAsync(new AdvanceTurnCommand(actor.Id));

        Assert.Equal(2, broadcasts.Count);
        Assert.Equal(new[] { actor.Id }, broadcasts[1]);
    }

    [Fact]
    public async Task SetInitiativeCommand_OrdersParticipantsByRollThenTieBreak_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var alice = await SeedActorAsync(campaignId, hp: 10);
        var bob = await SeedActorAsync(campaignId, hp: 10);
        var carol = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session, _encounterRepository);
        runtime.Hydrate(new[] { alice, bob, carol });

        // Bob and Carol tie on the roll; Carol's higher tiebreak must put her ahead of Bob, and
        // both must still fall behind Alice's outright-higher roll (3.5).
        await runtime.EnqueueAsync(new SetInitiativeCommand(alice.Id, 15));
        await runtime.EnqueueAsync(new SetInitiativeCommand(bob.Id, 10, TieBreak: 2));
        await runtime.EnqueueAsync(new SetInitiativeCommand(carol.Id, 10, TieBreak: 5));

        var order = runtime.ActiveEncounter!.Participants.Select(p => p.ActorId).ToArray();
        Assert.Equal(new[] { alice.Id, carol.Id, bob.Id }, order);

        var persisted = await _encounterRepository.GetAsync(runtime.ActiveEncounter!.Id);
        Assert.Equal(3, persisted!.Participants.Count);
    }

    [Fact]
    public async Task NextTurnCommand_AdvancesThroughOrderAndWrapsToNewRound_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var alice = await SeedActorAsync(campaignId, hp: 10);
        var bob = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session, _encounterRepository);
        runtime.Hydrate(new[] { alice, bob });

        await runtime.EnqueueAsync(new SetInitiativeCommand(alice.Id, 20));
        await runtime.EnqueueAsync(new SetInitiativeCommand(bob.Id, 10));

        Assert.Equal(1, runtime.ActiveEncounter!.Round);
        Assert.Equal(0, runtime.ActiveEncounter!.TurnIndex);

        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Equal(1, runtime.ActiveEncounter!.TurnIndex);
        Assert.Equal(1, runtime.ActiveEncounter!.Round);

        // Wrapping past the last participant starts round 2 and resets who's acted.
        await runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Equal(0, runtime.ActiveEncounter!.TurnIndex);
        Assert.Equal(2, runtime.ActiveEncounter!.Round);
        Assert.All(runtime.ActiveEncounter!.Participants, p => Assert.False(p.HasActed));
    }

    [Fact]
    public async Task RemoveStatusEffectCommand_RemovesOnlyTheNamedEffect_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new AddStatusEffectCommand(actor.Id, "poisoned", "trap", 10, EffectExpiry.EndOfTurn, actor.Id));
        await runtime.EnqueueAsync(new AddStatusEffectCommand(actor.Id, "stunned", "trap", 1, EffectExpiry.EndOfTurn, actor.Id));

        await runtime.EnqueueAsync(new RemoveStatusEffectCommand(actor.Id, "stunned"));

        var effects = runtime.GetEntity(actor.Id)!.Value.Get<ActiveEffectsComponent>().Effects;
        Assert.Single(effects);
        Assert.Equal("poisoned", effects[0].EffectId);
    }

    [Fact]
    public async Task RollDiceCommand_LogsResultToSessionRollLog_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);
        var diceRoller = new StandardDiceRoller(new Random(42));

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session, _encounterRepository, diceRoller);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new RollDiceCommand("2d6+3", actor.Id, "dm-user", IsSecret: false));
        await runtime.EnqueueAsync(new RollDiceCommand("1d20", null, "dm-user", IsSecret: true));

        Assert.Equal(2, session.RollLog.Count);
        Assert.Equal(actor.Id, session.RollLog[0].ActorId);
        Assert.False(session.RollLog[0].IsSecret);
        Assert.True(session.RollLog[1].IsSecret);

        var persisted = await _sessionRepository.GetAsync(session.Id);
        Assert.Equal(2, persisted!.RollLog.Count);
    }

    [Fact]
    public async Task RollDiceCommand_KeepsTheDiceAndLabel_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session, _encounterRepository, new StandardDiceRoller(new Random(7)));
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new RollDiceCommand("adv+5", actor.Id, "p1", IsSecret: false, Label: "Athletics check"));

        var entry = Assert.Single(session.RollLog);
        Assert.Equal("Athletics check", entry.Label);
        Assert.Single(entry.Kept);
        Assert.Single(entry.Dropped);
        Assert.True(entry.Kept[0] >= entry.Dropped[0]);
        Assert.Equal(5, entry.Modifier);
        Assert.Equal(entry.Kept[0] + 5, entry.Total);
    }

    [Fact]
    public async Task RollInitiativeCommand_RollsOnce_AndTheOrderShowsTheLoggedTotal_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session, _encounterRepository, new StandardDiceRoller(new Random(3)));
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new RollInitiativeCommand(actor.Id, "1d20+2", "dm-user", TieBreak: 4));

        var entry = Assert.Single(session.RollLog);
        Assert.Equal(actor.Id, entry.ActorId);
        Assert.Equal(2, entry.Modifier);
        var participant = Assert.Single(runtime.ActiveEncounter!.Participants);
        Assert.Equal(entry.Total, participant.InitiativeRoll);
        Assert.Equal(4, participant.TieBreak);
    }

    [Fact]
    public async Task AddSessionNoteCommand_AppendsNoteToLiveSession_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new AddSessionNoteCommand("dm-user", "The party finds a hidden door.", IsSecret: false));

        Assert.Single(session.Notes);
        Assert.Equal("The party finds a hidden door.", session.Notes[0].Text);
    }

    [Fact]
    public async Task AddSessionNoteCommand_WithSpeaker_StoresAQuote_AndItsEventNamesNobody_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new AddSessionNoteCommand("dm-user", "Mind the tide.", IsSecret: false, SpeakerActorId: actor.Id));

        Assert.Equal(actor.Id, Assert.Single(session.Notes).SpeakerActorId);
        var evt = Assert.Single(session.Events, e => e.Type == "Note");
        Assert.Equal(new[] { actor.Id }, evt.ActorIds);
        Assert.DoesNotContain(actor.Name, evt.Description);
    }

    [Fact]
    public async Task AddSessionNoteCommand_InAnEncounter_StampsTheRoundAndWhoseTurnItIs_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new AddSessionNoteCommand("dm-user", "Before the fight.", IsSecret: false));
        await runtime.EnqueueAsync(new SetInitiativeCommand(actor.Id, 14));
        await runtime.EnqueueAsync(new AddSessionNoteCommand("dm-user", "The goblin found a key.", IsSecret: false, Kind: NoteKind.Loot));

        var before = session.Notes[0];
        var during = session.Notes[1];
        Assert.Null(before.Round);
        Assert.Equal(1, during.Round);
        Assert.Equal(actor.Id, during.TurnActorId);
        Assert.Equal(runtime.ActiveEncounter!.Id, during.EncounterId);
        Assert.Equal(NoteKind.Loot, during.Kind);
        Assert.NotEqual(before.Id, during.Id);
    }

    [Fact]
    public async Task AddSessionNoteCommand_KeepsTheConversation_WithoutTheSpeaker_AndItsEventCarriesNoWords_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new AddSessionNoteCommand("dm-user", "Mind the tide.", IsSecret: false, SpeakerActorId: actor.Id, WithActorIds: new[] { actor.Id, "pc-1", "pc-1" }));

        Assert.Equal(new[] { "pc-1" }, Assert.Single(session.Notes).WithActorIds);
        Assert.DoesNotContain("tide", Assert.Single(session.Events, e => e.Type == "Note").Description);
    }

    [Fact]
    public async Task EditAndDeleteSessionNote_OnlyTheAuthorOrTheDm_AndTheStoredSessionFollows_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        await runtime.EnqueueAsync(new AddSessionNoteCommand("player-1", "The brige is out.", IsSecret: false));
        await runtime.EnqueueAsync(new AddSessionNoteCommand("player-1", "Typo note", IsSecret: false));
        var first = session.Notes[0];
        var second = session.Notes[1];
        var snapshotBefore = runtime.Snapshot.Notes[0];

        await runtime.EnqueueAsync(new EditSessionNoteCommand(first.Id, "player-2", EditorIsDm: false, "Vandalised", NoteKind.Note));
        Assert.Equal("The brige is out.", session.Notes[0].Text);

        await runtime.EnqueueAsync(new EditSessionNoteCommand(first.Id, "player-1", EditorIsDm: false, "The bridge is out.", NoteKind.FollowUp));
        await runtime.EnqueueAsync(new DeleteSessionNoteCommand(second.Id, "player-2", EditorIsDm: false));
        Assert.Equal(2, session.Notes.Count);
        await runtime.EnqueueAsync(new DeleteSessionNoteCommand(second.Id, "dm-user", EditorIsDm: true));

        var note = Assert.Single(session.Notes);
        Assert.Equal("The bridge is out.", note.Text);
        Assert.Equal(NoteKind.FollowUp, note.Kind);
        // The old snapshot is never changed under a render that may still be reading it.
        Assert.Equal("The brige is out.", snapshotBefore.Text);

        var stored = await _sessionRepository.GetAsync(session.Id);
        var storedNote = Assert.Single(stored!.Notes);
        Assert.Equal("The bridge is out.", storedNote.Text);
        Assert.Equal(first.Id, storedNote.Id);
    }

    [Fact]
    public async Task HydrateActorCommand_AddsANewlyPersistedActorToTheRunningRuntime_Async()
    {
        var campaignId = Guid.NewGuid().ToString();
        var actor = await SeedActorAsync(campaignId, hp: 10);
        var session = await SeedSessionAsync(campaignId);

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance, _sessionRepository, session);
        runtime.Hydrate(new[] { actor });

        // A monster quick-added mid-session (3.7) is saved to Raven first, then hydrated into
        // the already-running runtime through the same single-writer loop as every other command.
        var goblin = await SeedActorAsync(campaignId, hp: 7);
        Assert.Null(runtime.GetEntity(goblin.Id));

        await runtime.EnqueueAsync(new HydrateActorCommand(goblin));

        Assert.NotNull(runtime.GetEntity(goblin.Id));
    }
}
