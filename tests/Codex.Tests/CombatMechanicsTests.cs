using Codex.Core;
using Codex.Core.Abilities;
using Codex.Core.Components;
using Codex.Core.Models;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>TRCE combat mechanics (6.3): attack rolls, saving throws, and multi-target
/// fan-out. Dice are stubbed - these pin the rules, not the randomness.</summary>
public class CombatMechanicsTests : IDisposable
{
    private readonly CodexWorld _world = new();
    private readonly ScriptEvaluator _evaluator = new(NullLogger<ScriptEvaluator>.Instance);

    public void Dispose() => _world.Dispose();

    private static DiceRollResult FixedRoll(int total)
        => new("fixed", new[] { total }, Array.Empty<int>(), 0, total);

    private AbilityExecutor ExecutorWithRolls(params int[] totals)
    {
        var roller = Substitute.For<IDiceRoller>();
        var results = totals.Select(FixedRoll).ToArray();
        roller.Roll(Arg.Any<string>()).Returns(results[0], results.Skip(1).ToArray());
        return new AbilityExecutor(
            _evaluator,
            requirementHandlers: BuiltInHandlers.Requirements(roller),
            effectHandlers: BuiltInHandlers.Effects(roller));
    }

    private DefaultEcs.Entity EntityWithPool(string pool, int amount)
    {
        var entity = _world.CreateEntity();
        var pools = new ResourcePoolComponent();
        pools.Set(pool, amount);
        entity.Set(pools);
        return entity;
    }

    private static void SetModifiers(DefaultEcs.Entity entity, params (string Stat, int Value)[] modifiers)
    {
        entity.Set(new StatModifierComponent
        {
            Modifiers = modifiers.Select(m => new Modifier(m.Stat, m.Value, "test")).ToList()
        });
    }

    private static AbilityDefinition AbilityWith(
        List<TypedComponent>? requires = null,
        List<TypedComponent>? effects = null)
        => new() { Id = "test", Name = "Test", Requires = requires, Effects = effects };

    private static TypedComponent P(string type, Dictionary<string, object>? p = null)
        => new(type, p);

    // ---------------------------------------------------------------- attacks

    [Fact]
    public void Attack_Hit_WhenRollPlusBonusMeetsAc()
    {
        var executor = ExecutorWithRolls(15); // d20 15 + 5 = 20 vs AC 15
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 10);
        var ability = AbilityWith(
            requires: new() { P("Attack", new() { ["attackBonus"] = 5, ["targetAc"] = 15 }) });

        var result = executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.True(result.Success);
    }

    [Fact]
    public void Attack_Miss_FailsRequirement()
    {
        var executor = ExecutorWithRolls(5); // d20 5 + 5 = 10 vs AC 15
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 10);
        var ability = AbilityWith(
            requires: new() { P("Attack", new() { ["attackBonus"] = 5, ["targetAc"] = 15 }) });

        var result = executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.False(result.Success);
        Assert.Equal(AbilityExecutor.RequiresStage, result.FailedStage);
        Assert.Contains("missed", result.FailureReason);
    }

    [Fact]
    public void Attack_Natural20_HitsRegardlessOfAc()
    {
        var executor = ExecutorWithRolls(20);
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 10);
        var ability = AbilityWith(
            requires: new() { P("Attack", new() { ["attackBonus"] = 0, ["targetAc"] = 30 }) });

        Assert.True(executor.Execute(ability, new AbilityExecutionContext(_world, caster, target)).Success);
    }

    [Fact]
    public void Attack_Natural1_MissesRegardlessOfBonus()
    {
        var executor = ExecutorWithRolls(1);
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 10);
        var ability = AbilityWith(
            requires: new() { P("Attack", new() { ["attackBonus"] = 10, ["targetAc"] = 5 }) });

        Assert.False(executor.Execute(ability, new AbilityExecutionContext(_world, caster, target)).Success);
    }

    [Fact]
    public void Attack_FailsClosed_WithoutTargetAc()
    {
        var executor = ExecutorWithRolls(15);
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 10); // no "ac" modifier anywhere
        var ability = AbilityWith(requires: new() { P("Attack", new() { ["attackBonus"] = 5 }) });

        var result = executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.False(result.Success);
        Assert.Contains("AC", result.FailureReason);
    }

    [Fact]
    public void Attack_ReadsBonusAndAc_FromStatModifiers()
    {
        var executor = ExecutorWithRolls(11); // 11 + 4 = 15 vs AC 15
        var caster = EntityWithPool("HP", 10);
        SetModifiers(caster, ("attack", 4));
        var target = EntityWithPool("HP", 10);
        SetModifiers(target, ("ac", 15));
        var ability = AbilityWith(requires: new() { P("Attack") }); // bare, like imports

        Assert.True(executor.Execute(ability, new AbilityExecutionContext(_world, caster, target)).Success);
    }

    // ------------------------------------------------------------------ saves

    [Fact]
    public void Damage_HalfOnSuccessfulSave()
    {
        var executor = ExecutorWithRolls(18); // save: 18 + 0 vs DC 10
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 50);
        var ability = AbilityWith(effects: new()
        {
            P("Damage", new() { ["pool"] = "HP", ["amount"] = 20,
                ["saveAbility"] = "dex", ["saveSuccess"] = "half", ["saveDc"] = 10 })
        });

        var result = executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.True(result.Success);
        Assert.Equal(40, target.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void Damage_FullOnFailedSave()
    {
        var executor = ExecutorWithRolls(2); // save: 2 + 0 vs DC 10
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 50);
        var ability = AbilityWith(effects: new()
        {
            P("Damage", new() { ["pool"] = "HP", ["amount"] = 20,
                ["saveAbility"] = "dex", ["saveSuccess"] = "half", ["saveDc"] = 10 })
        });

        executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.Equal(30, target.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void Damage_NegatedOnSuccessfulSave_WhenNegates()
    {
        var executor = ExecutorWithRolls(18);
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 50);
        var ability = AbilityWith(effects: new()
        {
            P("Damage", new() { ["pool"] = "HP", ["amount"] = 20,
                ["saveAbility"] = "wis", ["saveSuccess"] = "negates", ["saveDc"] = 10 })
        });

        executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.Equal(50, target.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void Damage_FailsClosed_WithoutSaveDc()
    {
        var executor = ExecutorWithRolls(18);
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 50);
        var ability = AbilityWith(effects: new()
        {
            P("Damage", new() { ["pool"] = "HP", ["amount"] = 20, ["saveAbility"] = "dex" })
        });

        var result = executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.False(result.Success);
        Assert.Equal(AbilityExecutor.EffectsStage, result.FailedStage);
        Assert.Contains("DC", result.FailureReason);
    }

    [Fact]
    public void Damage_SaveUsesTargetSaveModifier()
    {
        var executor = ExecutorWithRolls(10); // 10 + 5 = 15 vs DC 15: saved
        var caster = EntityWithPool("HP", 10);
        var target = EntityWithPool("HP", 50);
        SetModifiers(target, ("save:dex", 5));
        var ability = AbilityWith(effects: new()
        {
            P("Damage", new() { ["pool"] = "HP", ["amount"] = 20,
                ["saveAbility"] = "dex", ["saveSuccess"] = "half", ["saveDc"] = 15 })
        });

        executor.Execute(ability, new AbilityExecutionContext(_world, caster, target));

        Assert.Equal(40, target.Get<ResourcePoolComponent>().Get("HP"));
    }

    // ------------------------------------------------------------------- areas

    [Fact]
    public void Damage_FansOutAcrossTargets_WithOneSharedRoll()
    {
        var roller = Substitute.For<IDiceRoller>();
        roller.Roll("8d6").Returns(FixedRoll(30));
        var executor = new AbilityExecutor(
            _evaluator,
            requirementHandlers: BuiltInHandlers.Requirements(roller),
            effectHandlers: BuiltInHandlers.Effects(roller));
        var caster = EntityWithPool("HP", 10);
        var first = EntityWithPool("HP", 50);
        var second = EntityWithPool("HP", 50);
        var ability = AbilityWith(effects: new()
        {
            P("Damage", new() { ["pool"] = "HP", ["dice"] = "8d6" })
        });

        var context = new AbilityExecutionContext(_world, caster, Target: first,
            Targets: new[] { first, second });
        var result = executor.Execute(ability, context);

        Assert.True(result.Success);
        Assert.Equal(20, first.Get<ResourcePoolComponent>().Get("HP"));
        Assert.Equal(20, second.Get<ResourcePoolComponent>().Get("HP"));
        roller.Received(1).Roll("8d6"); // one roll shared, not one per target
    }

    [Fact]
    public void Damage_SavesResolveIndividuallyPerTarget()
    {
        // Fixed 20 damage; first target saves (18), second fails (2) -> 10 and 20.
        var executor = ExecutorWithRolls(18, 2);
        var caster = EntityWithPool("HP", 10);
        var first = EntityWithPool("HP", 50);
        var second = EntityWithPool("HP", 50);
        var ability = AbilityWith(effects: new()
        {
            P("Damage", new() { ["pool"] = "HP", ["amount"] = 20,
                ["saveAbility"] = "dex", ["saveSuccess"] = "half", ["saveDc"] = 10 })
        });

        var context = new AbilityExecutionContext(_world, caster,
            Targets: new[] { first, second });
        executor.Execute(ability, context);

        Assert.Equal(40, first.Get<ResourcePoolComponent>().Get("HP"));
        Assert.Equal(30, second.Get<ResourcePoolComponent>().Get("HP"));
    }
}
