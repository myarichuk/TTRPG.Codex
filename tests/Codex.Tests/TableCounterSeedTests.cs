using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// A feature counter the sheet has never written (an ability the pack only now grants uses to, or a sheet nobody has opened since)
/// joins the live table full when the table loads the actor, so the table card's Use button spends it rather than doing nothing.
/// What was already spent is left alone.
/// </summary>
[Collection("RavenDb")]
public class TableCounterSeedTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: counter-seed
        name: Counter seed
        moments: [{ key: downtime, label: Downtime }]
        sheetKinds:
          - kind: hero
            label: Hero
            features:
              - { list: abilities }
            lists:
              - { key: abilities, label: Abilities, type: ability }
        """);

    private sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static readonly ContentEntry Foresight = new()
    {
        Type = "ability", Id = "foresight", Name = "Foresight", SystemId = "counter-seed", PackId = "test",
        Properties =
        {
            ["uses"] = new Dictionary<string, object>
            {
                ["key"] = "foresight", ["max"] = 2L,
                ["recovers"] = new List<object> { new Dictionary<string, object> { ["on"] = "downtime" } }
            }
        }
    };

    public TableCounterSeedTests(RavenDbFixture fixture)
    {
        _dbService = new RavenDbService(fixture.DbPath, "CounterSeed_" + Guid.NewGuid(), runInMemory: true);
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<ActorDocument> HeroAsync(string campaignId, params (string Key, object Value)[] sheet)
    {
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter };
        doc.Sheet["kind"] = "hero";
        doc.Sheet["abilities"] = new List<object> { "foresight" };
        foreach (var (key, value) in sheet) doc.Sheet[key] = value;
        ActorSheetSync.ApplySheetToState(doc, System, _registry);
        await _actors.SaveAsync(doc);
        return doc;
    }

    private async Task<CampaignRuntime> TableAsync(string campaignId, IEntryResolver resolver, ActorDocument actor)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: System) { Resolver = resolver };
        runtime.Hydrate(new[] { actor });
        return runtime;
    }

    private static int? Pool(CampaignRuntime runtime, string actorId, string key)
    {
        var pool = runtime.GetEntity(actorId)!.Value.Get<ResourcePoolComponent>();
        return pool.Pools.ContainsKey(key) ? pool.Get(key) : null;
    }

    [Fact]
    public async Task ACounterTheSheetNeverWrote_JoinsTheTableFull_AndTheTableCardSpendsIt()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId);
        Assert.False(hero.Sheet.ContainsKey("uses_foresight"));

        var runtime = await TableAsync(campaignId, new Entries(Foresight), hero);
        await using var _ = runtime;

        Assert.Equal(2, Pool(runtime, hero.Id, "uses_foresight"));
        var spend = new AdjustResourceCommand(hero.Id, "uses_foresight", -1, "Foresight");
        await runtime.EnqueueAsync(spend);
        Assert.True(spend.Succeeded);
        Assert.Equal(1, Pool(runtime, hero.Id, "uses_foresight"));
    }

    [Fact]
    public async Task ACounterAlreadySpent_IsLeftAsStored()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId, ("uses_foresight", 0L), ("uses_foresight_Max", 2L));

        var runtime = await TableAsync(campaignId, new Entries(Foresight), hero);
        await using var _ = runtime;

        Assert.Equal(0, Pool(runtime, hero.Id, "uses_foresight"));
    }

    [Fact]
    public async Task ACounterWhoseMaximumChangedWithThePack_TakesTheNewMaximum_AndKeepsWhatWasSpent()
    {
        var campaignId = Guid.NewGuid().ToString();
        // The sheet was written when the pack gave one use: all spent. The pack now gives two.
        var hero = await HeroAsync(campaignId, ("uses_foresight", 0L), ("uses_foresight_Max", 1L));

        var runtime = await TableAsync(campaignId, new Entries(Foresight), hero);
        await using var _ = runtime;

        Assert.Equal(1, Pool(runtime, hero.Id, "uses_foresight")); // the added use is there; the spent one is still spent
        Assert.Equal(2, Pool(runtime, hero.Id, "uses_foresight_Max"));
    }

    [Fact]
    public async Task WithoutEntries_TheTableAddsNothing()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await HeroAsync(campaignId);

        var runtime = await TableAsync(campaignId, NullEntryResolver.Instance, hero);
        await using var _ = runtime;

        Assert.Null(Pool(runtime, hero.Id, "uses_foresight"));
    }
}
