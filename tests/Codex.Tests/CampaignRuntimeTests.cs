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
    private readonly ComponentRegistry _componentRegistry;

    public CampaignRuntimeTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, "Runtime_" + Guid.NewGuid(), runInMemory: true);
        _actorRepository = new ActorRepository(_dbService);
        _componentRegistry = new ComponentRegistry();
        _componentRegistry.Register<ResourcePoolComponent>();
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

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance);
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

        await using var runtime = new CampaignRuntime(campaignId, _actorRepository, _componentRegistry, NullLogger.Instance);
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
}
