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

public class AbilityCommandTests : IAsyncDisposable
{
    private readonly ComponentRegistry _components = new();
    private readonly AbilityExecutor _executor;
    private readonly IContentRegistry _registry = Substitute.For<IContentRegistry>();
    private readonly List<CampaignRuntime> _runtimes = new();

    public AbilityCommandTests()
    {
        _components.Register<ResourcePoolComponent>();
        _executor = new AbilityExecutor(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes)
        {
            await runtime.DisposeAsync();
        }
    }

    private static Dictionary<string, object> P(params (string K, object V)[] pairs)
        => pairs.ToDictionary(p => p.K, p => p.V);

    private ActorDocument Doc(string id, int hp, string? blueprint = null)
    {
        var pool = new ResourcePoolComponent();
        pool.Set("HP", hp);
        pool.Set("HP_Max", hp);
        return new ActorDocument
        {
            Id = id, CampaignId = "c1", Name = id, BlueprintId = blueprint,
            State = _components.Snapshot(new object[] { pool })
        };
    }

    private CampaignRuntime Runtime(params ActorDocument[] docs)
    {
        var runtime = new CampaignRuntime(
            "c1", Substitute.For<IActorRepository>(), _components, NullLogger.Instance,
            abilityExecutor: _executor, contentRegistry: _registry);
        runtime.Hydrate(docs);
        _runtimes.Add(runtime);
        return runtime;
    }

    private int Hp(CampaignRuntime runtime, string id)
        => runtime.GetEntity(id)!.Value.Get<ResourcePoolComponent>().Get("HP");

    [Fact]
    public async Task UseAbilityById_DamagesTargetThroughLoop()
    {
        _registry.GetAbility("test:jab").Returns(new AbilityDefinition
        {
            Id = "jab", SystemId = "test", PackId = "test", Name = "Jab",
            Effects = new() { new TypedComponent("Damage", P(("pool", (object)"HP"), ("amount", (object)3))) }
        });
        var runtime = Runtime(Doc("caster", 10), Doc("target", 10));

        await runtime.EnqueueAsync(new UseAbilityByIdCommand("test:jab", "caster", "target"));

        Assert.Equal(7, Hp(runtime, "target"));
        Assert.Equal(10, Hp(runtime, "caster"));
    }

    [Fact]
    public async Task UseAbilityById_OnHit_FiresThornsBackAtAttacker()
    {
        _registry.GetAbility("test:jab").Returns(new AbilityDefinition
        {
            Id = "jab", SystemId = "test", PackId = "test", Name = "Jab",
            Effects = new() { new TypedComponent("Damage", P(("pool", (object)"HP"), ("amount", (object)3))) }
        });
        _registry.GetActor("test:spiky").Returns(new ActorDefinition
        {
            Id = "spiky", SystemId = "test", PackId = "test", Name = "Spiky",
            Abilities = new() { "test:thorns" }
        });
        _registry.GetAbility("test:thorns").Returns(new AbilityDefinition
        {
            Id = "thorns", SystemId = "test", PackId = "test", Name = "Thorns",
            Triggers = new() { new TypedComponent(TriggerEvents.OnHit) },
            Effects = new() { new TypedComponent("Damage", P(("pool", (object)"HP"), ("amount", (object)2))) }
        });
        var runtime = Runtime(Doc("caster", 10), Doc("target", 10, "test:spiky"));

        await runtime.EnqueueAsync(new UseAbilityByIdCommand("test:jab", "caster", "target"));

        Assert.Equal(7, Hp(runtime, "target"));
        Assert.Equal(8, Hp(runtime, "caster"));
    }

    [Fact]
    public async Task UseAbilityById_UnknownAbility_LeavesStateUntouched()
    {
        var runtime = Runtime(Doc("caster", 10), Doc("target", 10));

        await runtime.EnqueueAsync(new UseAbilityByIdCommand("test:nope", "caster", "target"));

        Assert.Equal(10, Hp(runtime, "target"));
        Assert.Equal(10, Hp(runtime, "caster"));
    }

    [Fact]
    public async Task UseAbilityById_UnwiredRuntime_DoesNotThrow()
    {
        var runtime = new CampaignRuntime(
            "c1", Substitute.For<IActorRepository>(), _components, NullLogger.Instance);
        runtime.Hydrate(new[] { Doc("caster", 10) });
        _runtimes.Add(runtime);

        await runtime.EnqueueAsync(new UseAbilityByIdCommand("test:jab", "caster"));

        Assert.Equal(10, Hp(runtime, "caster"));
    }
}
