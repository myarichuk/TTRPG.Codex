using Codex.Core;
using Codex.Core.Abilities;
using Codex.Core.Components;
using Codex.Core.Models;
using Codex.Core.Scripting;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Codex.Tests;

public class TriggerDispatcherTests : IAsyncDisposable
{
    private readonly CodexWorld _world = new();
    private readonly AbilityExecutor _executor;
    private readonly ComponentRegistry _components = new();
    private readonly List<CampaignRuntime> _runtimes = new();

    public TriggerDispatcherTests()
    {
        _executor = new AbilityExecutor(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        _components.Register<ResourcePoolComponent>();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes)
        {
            await runtime.DisposeAsync();
        }

        _world.Dispose();
    }

    private static AbilityDefinition Ability(string name, string trigger, int heal)
        => new()
        {
            Id = name, SystemId = "test", PackId = "test", Name = name,
            Triggers = new() { new TypedComponent(trigger) },
            Effects = new() { new TypedComponent("Heal", new Dictionary<string, object> { ["Pool"] = "HP", ["Amount"] = heal }) }
        };

    private static DefaultEcs.Entity EntityWithHp(CodexWorld world, int hp, int max)
    {
        var entity = world.CreateEntity();
        var pool = new ResourcePoolComponent();
        pool.Set("HP", hp);
        pool.Set("HP_Max", max);
        entity.Set(pool);
        return entity;
    }

    [Fact]
    public void ExecuteForEvent_OnlyMatchingTriggerTypesFire()
    {
        var caster = EntityWithHp(_world, 5, 10);
        var abilities = new IAbilityDefinition[]
        {
            Ability("regen", TriggerEvents.OnTurnEnd, 2),
            Ability("riposte", TriggerEvents.OnHit, 99)
        };

        var fired = TriggerDispatcher.ExecuteForEvent(
            abilities,
            new TriggerEvent(TriggerEvents.OnTurnEnd),
            new AbilityExecutionContext(_world, caster, caster),
            _executor);

        Assert.Single(fired);
        Assert.Equal("regen", fired[0].Ability.Name);
        Assert.True(fired[0].Result.Success);
        Assert.Equal(7, caster.Get<ResourcePoolComponent>().Get("HP"));
    }

    private CampaignRuntime RuntimeWithTroll(out ActorDocument doc, out IActorRepository actors)
    {
        var pool = new ResourcePoolComponent();
        pool.Set("HP", 5);
        pool.Set("HP_Max", 10);
        doc = new ActorDocument
        {
            Id = "actor-troll",
            CampaignId = "c1",
            Name = "Troll",
            BlueprintId = "test:troll",
            State = _components.Snapshot(new object[] { pool })
        };

        var registry = Substitute.For<IContentRegistry>();
        registry.GetActor("test:troll").Returns(new ActorDefinition
        {
            Id = "troll", SystemId = "test", PackId = "test", Name = "Troll",
            Abilities = new() { "test:regen" }
        });
        registry.GetAbility("test:regen").Returns(Ability("regen", TriggerEvents.OnTurnEnd, 2));

        actors = Substitute.For<IActorRepository>();
        var runtime = new CampaignRuntime(
            "c1", actors, _components, NullLogger.Instance,
            abilityExecutor: _executor, contentRegistry: registry);
        runtime.Hydrate(new[] { doc });
        _runtimes.Add(runtime);
        return runtime;
    }

    [Fact]
    public void FireTriggers_OnTurnEnd_RunsBlueprintAbility()
    {
        var runtime = RuntimeWithTroll(out var doc, out _);

        var fired = runtime.FireTriggers(doc.Id, new TriggerEvent(TriggerEvents.OnTurnEnd));

        Assert.Single(fired);
        Assert.Equal(7, runtime.GetEntity(doc.Id)!.Value.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void FireTriggers_NonMatchingEvent_FiresNothing()
    {
        var runtime = RuntimeWithTroll(out var doc, out _);

        var fired = runtime.FireTriggers(doc.Id, new TriggerEvent(TriggerEvents.OnHit));

        Assert.Empty(fired);
        Assert.Equal(5, runtime.GetEntity(doc.Id)!.Value.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void FireTriggers_WithoutExecutor_IsNoOp()
    {
        var actors = Substitute.For<IActorRepository>();
        var runtime = new CampaignRuntime("c1", actors, _components, NullLogger.Instance);
        _runtimes.Add(runtime);

        Assert.Empty(runtime.FireTriggers("nobody", new TriggerEvent(TriggerEvents.OnTurnEnd)));
    }

    [Fact]
    public async Task AdvanceTurnCommand_FiresOnTurnEnd_PersistsThroughLoop()
    {
        var runtime = RuntimeWithTroll(out var doc, out var actors);

        await runtime.EnqueueAsync(new AdvanceTurnCommand(doc.Id));

        Assert.Equal(7, runtime.GetEntity(doc.Id)!.Value.Get<ResourcePoolComponent>().Get("HP"));
        await actors.Received().SaveAsync(Arg.Is<ActorDocument>(d => d.Id == doc.Id));
    }
}
