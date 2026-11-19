using Codex.Core.Components;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>A handler at the live table reads the table's pools (resources and valued conditions) and writes a valued condition into the pool
/// (<see cref="TableEvents"/>), says it as <c>Condition.Level</c> / <c>Condition.None</c>, and undo puts it back.</summary>
[Collection("RavenDb")]
public class ValuedConditionTableTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    private static SystemDefinition System()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: valued-table
            name: Valued table
            resources: [{ key: HP, label: Hit points, maxKey: HP_Max }]
            conditionEffects:
              Fading:
                value: { max: 4 }
            sheetKinds:
              - kind: hero
                label: Hero
            events:
              peek: { script: rules.js#peek }
              fade: { script: rules.js#fade }
            """);
        system.AddScript("rules.js", """
            export function peek(ev, sheet) { return [{ log: { key: "seen", args: [sheet.resources.HP, sheet.conditionValues.Fading] } }]; }
            export function fade(ev, sheet) {
              const now = sheet.conditionValues.Fading;
              return now >= 2 ? [{ condition: { remove: "Fading" } }] : [{ condition: { add: "Fading", by: 1 } }];
            }
            """);
        return system;
    }

    public ValuedConditionTableTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("ValuedTable_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    [Fact]
    public async Task AHandler_ReadsThePools_WritesAValuedCondition_SaysItsLevel_AndUndoPutsItBack()
    {
        var system = System();
        var campaignId = Guid.NewGuid().ToString();
        var hero = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter };
        hero.Sheet["kind"] = "hero";
        hero.Sheet["HP"] = 10L;
        hero.Sheet["HP_Max"] = 10L;
        ActorSheetSync.ApplySheetToState(hero, system, _registry);
        await _actors.SaveAsync(hero);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: system);
        runtime.Hydrate(new[] { hero });
        await using var _ = runtime;
        var pool = runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>();
        pool.Set("HP", 3); // the table's hit points, not the cached sheet's 10

        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "peek", new Dictionary<string, object?>()));
        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "fade", new Dictionary<string, object?>()));
        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "fade", new Dictionary<string, object?>()));

        Assert.Equal(2, pool.Get("condition_fading"));
        await runtime.EnqueueAsync(new RaiseEventCommand(hero.Id, "fade", new Dictionary<string, object?>()));
        Assert.Equal(0, pool.Get("condition_fading"));
        var log = (await _sessions.GetAsync(session.Id))!.Events.Select(e => e.Description).ToList();
        Assert.Contains(log, d => d.Contains("seen") && d.Contains("3, 0"));
        Assert.Contains("Vex: Fading is now 1.", log);
        Assert.Contains("Vex: Fading is now 2.", log);
        Assert.Contains("Vex: Fading has ended.", log);

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));
        Assert.Equal(2, pool.Get("condition_fading"));
    }
}
