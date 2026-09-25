using Codex.Core.Components;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ComponentRegistry _componentRegistry;

    public CampaignRuntimeTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, "Runtime_" + Guid.NewGuid(), runInMemory: true);
        _actorRepository = new ActorRepository(_dbService);
        _sessionRepository = new RavenSessionRepository(_dbService);
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
}
