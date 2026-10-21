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
        _dbService = new RavenDbService(fixture.DbPath, "Runtime_" + Guid.NewGuid(), runInMemory: true);
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
    public async Task ARuntimeNobodyHasUsedForAWhile_IsEvicted_AndItsSessionClosed_Async()
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
        var session = Assert.Single(await _sessionRepository.GetAllForCampaignAsync(campaignId));
        Assert.Equal(SessionStatus.Completed, session.Status);
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
        Assert.Contains(seen, e => e.Description.StartsWith("Aria took 4 HP damage"));

        // The DM keeps the full account.
        Assert.Contains(SessionEventRedactor.ForViewer(events, new CampaignAccess(campaignId, "dm", CampaignRole.DM), new[] { ambusher, goblin, hero }),
            e => e.Description.Contains("Strahd the Ambusher took 13 HP damage"));

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
        Assert.Contains("took 3 HP damage", session.Events[0].Description);
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
