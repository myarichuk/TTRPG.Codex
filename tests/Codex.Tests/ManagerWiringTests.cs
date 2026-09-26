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

/// <summary>Proves the manager wires the TRCE pipeline into every runtime it starts: a runtime
/// from <see cref="CampaignRuntimeManager.GetOrStartAsync"/> fires blueprint triggers for real,
/// with no test-side executor involved.</summary>
public class ManagerWiringTests : IAsyncDisposable
{
    private readonly ComponentRegistry _components = new();
    private readonly IContentRegistry _contentRegistry = Substitute.For<IContentRegistry>();
    private readonly List<CampaignRuntimeManager> _managers = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }
    }

    private CampaignRuntimeManager CreateManager(IActorRepository actors)
    {
        var manager = new CampaignRuntimeManager(
            actors,
            Substitute.For<ISessionRepository>(),
            Substitute.For<ICampaignRepository>(),
            Substitute.For<IEncounterRepository>(),
            _components,
            new PluginLoader(NullLogger<PluginLoader>.Instance, _components, Substitute.For<IContentPackLoader>()),
            NullLoggerFactory.Instance,
            new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance),
            _contentRegistry);
        _managers.Add(manager);
        return manager;
    }

    [Fact]
    public async Task StartedRuntime_FiresTriggersThroughWiredExecutor()
    {
        _components.Register<ResourcePoolComponent>();
        var pool = new ResourcePoolComponent();
        pool.Set("HP", 5);
        pool.Set("HP_Max", 10);
        var doc = new ActorDocument
        {
            Id = "actor-troll",
            CampaignId = "c1",
            Name = "Troll",
            BlueprintId = "test:troll",
            State = _components.Snapshot(new object[] { pool })
        };

        var actors = Substitute.For<IActorRepository>();
        actors.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(new[] { doc });
        var manager = CreateManager(actors);

        _contentRegistry.GetActor("test:troll").Returns(new ActorDefinition
        {
            Id = "troll", SystemId = "test", PackId = "test", Name = "Troll",
            Abilities = new() { "test:regen" }
        });
        _contentRegistry.GetAbility("test:regen").Returns(new AbilityDefinition
        {
            Id = "regen", SystemId = "test", PackId = "test", Name = "Regeneration",
            Triggers = new() { new TypedComponent(TriggerEvents.OnTurnEnd) },
            Effects = new() { new TypedComponent("Heal", new Dictionary<string, object> { ["pool"] = "HP", ["amount"] = 2 }) }
        });

        var campaign = new CampaignDocument { Id = "c1", OwnerId = "dm1", SystemId = "NoSuchSystem" };
        var runtime = await manager.GetOrStartAsync(campaign, new CampaignAccess("c1", "dm1", CampaignRole.DM));

        var fired = runtime.FireTriggers(doc.Id, new TriggerEvent(TriggerEvents.OnTurnEnd));

        Assert.Single(fired);
        Assert.Equal(7, runtime.GetEntity(doc.Id)!.Value.Get<ResourcePoolComponent>().Get("HP"));
    }
}
