using Codex.Core;
using Codex.Core.Abilities;
using Codex.Core.Components;
using Codex.Core.Models;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Codex.Tests;

public class AbilityExecutorTests : IDisposable
{
    private readonly CodexWorld _world = new();
    private readonly ScriptEvaluator _scripts = new(NullLogger<ScriptEvaluator>.Instance);
    private readonly AbilityExecutor _executor;

    public AbilityExecutorTests()
    {
        _executor = new AbilityExecutor(_scripts);
    }

    public void Dispose() => _world.Dispose();

    private static AbilityDefinition Ability(
        List<TypedComponent>? requires = null,
        List<TypedComponent>? costs = null,
        List<TypedComponent>? effects = null)
        => new() { Id = "test", SystemId = "test", PackId = "test", Name = "Test", Requires = requires, Costs = costs, Effects = effects };

    private static Dictionary<string, object> P(params (string K, object V)[] pairs)
        => pairs.ToDictionary(p => p.K, p => p.V);

    private DefaultEcs.Entity EntityWithPool(int hp, int max)
    {
        var e = _world.CreateEntity();
        var pool = new ResourcePoolComponent();
        pool.Set("HP", hp);
        pool.Set("HP_Max", max);
        e.Set(pool);
        return e;
    }

    [Fact]
    public void Execute_UnmetRequirement_BlocksCostsAndEffects()
    {
        var caster = EntityWithPool(10, 10);
        var target = EntityWithPool(10, 10);
        var ability = Ability(
            requires: new() { new TypedComponent("ResourceRequirement", P(("Pool", (object)"HP"), ("MinAmount", (object)99))) },
            costs: new() { new TypedComponent("ResourceCost", P(("Pool", (object)"HP"), ("Amount", (object)2))) },
            effects: new() { new TypedComponent("Damage", P(("Pool", (object)"HP"), ("Amount", (object)4))) });

        var result = _executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.False(result.Success);
        Assert.Equal(AbilityExecutor.RequiresStage, result.FailedStage);
        Assert.Equal(10, caster.Get<ResourcePoolComponent>().Get("HP"));
        Assert.Equal(10, target.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void Execute_OneUnpayableCost_PaysNothingAndAppliesNoEffect()
    {
        var caster = EntityWithPool(3, 10);
        var target = EntityWithPool(10, 10);
        var ability = Ability(
            costs: new()
            {
                new TypedComponent("ResourceCost", P(("Pool", (object)"HP"), ("Amount", (object)2))),
                new TypedComponent("ResourceCost", P(("Pool", (object)"HP"), ("Amount", (object)99)))
            },
            effects: new() { new TypedComponent("Damage", P(("Pool", (object)"HP"), ("Amount", (object)4))) });

        var result = _executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.False(result.Success);
        Assert.Equal(AbilityExecutor.CostsStage, result.FailedStage);
        Assert.Equal(3, caster.Get<ResourcePoolComponent>().Get("HP"));
        Assert.Equal(10, target.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void Execute_CostPlusDamage_PaysAndDamagesWithClamp()
    {
        var caster = EntityWithPool(10, 10);
        var target = EntityWithPool(5, 10);
        var ability = Ability(
            costs: new() { new TypedComponent("ResourceCost", P(("Pool", (object)"HP"), ("Amount", (object)2))) },
            effects: new() { new TypedComponent("Damage", P(("Pool", (object)"HP"), ("Amount", (object)99))) });

        var result = _executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.True(result.Success);
        Assert.Equal(8, caster.Get<ResourcePoolComponent>().Get("HP"));
        Assert.Equal(0, target.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void Execute_HealAndAddStatus_ApplyToTarget()
    {
        var caster = EntityWithPool(10, 10);
        var target = EntityWithPool(2, 10);
        var ability = Ability(effects: new()
        {
            new TypedComponent("Heal", P(("Pool", (object)"HP"), ("Amount", (object)5))),
            new TypedComponent("AddStatus", P(("EffectId", (object)"blessed"), ("Rounds", (object)2)))
        });

        var result = _executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.True(result.Success);
        Assert.Equal(7, target.Get<ResourcePoolComponent>().Get("HP"));
        Assert.Contains(target.Get<ActiveEffectsComponent>().Effects, e => e.EffectId == "blessed");
    }

    [Fact]
    public void Execute_ScriptEffect_FallsBackToInterpreter()
    {
        var caster = EntityWithPool(10, 10);
        var target = _world.CreateEntity();
        var ability = Ability(effects: new()
        {
            new TypedComponent("Custom", P(("Script", (object)"world.AddStatus(target.Value, \"burning\", \"core\", 3.0)")))
        });

        var result = _executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.True(result.Success);
        Assert.Contains(target.Get<ActiveEffectsComponent>().Effects, e => e.EffectId == "burning");
    }

    [Fact]
    public void Execute_UnknownRequirement_FailsClosed()
    {
        var caster = EntityWithPool(10, 10);
        var ability = Ability(
            requires: new() { new TypedComponent("NoSuchRequirement") },
            effects: new() { new TypedComponent("Damage", P(("Amount", (object)1))) });

        var result = _executor.Execute(ability, new AbilityExecutionContext(_world, caster, null));

        Assert.False(result.Success);
        Assert.Equal(AbilityExecutor.RequiresStage, result.FailedStage);
    }

    [Fact]
    public void Execute_UnknownEffectWithoutScript_FailsByName()
    {
        var caster = EntityWithPool(10, 10);
        var target = EntityWithPool(10, 10);
        var ability = Ability(effects: new() { new TypedComponent("NoSuchEffect") });

        var result = _executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.False(result.Success);
        Assert.Equal(AbilityExecutor.EffectsStage, result.FailedStage);
        Assert.Contains("NoSuchEffect", result.FailureReason ?? string.Empty);
    }
}
