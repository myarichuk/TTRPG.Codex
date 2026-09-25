using Codex.Core;
using Codex.Core.Components;
using Xunit;

namespace Codex.Tests;

public class EcsWorldTest
{
    [Fact]
    public void ApplyDamage_ClampsAtZero_InsteadOfGoingNegative()
    {
        // B7: HP 2 taking 50 damage used to end at -48 because DamageEvent never clamped.
        using var world = new CodexWorld();
        var entity = world.CreateEntity();

        var pool = new ResourcePoolComponent();
        pool.Set("HP", 2);
        entity.Set(pool);

        world.ApplyDamage(entity, "HP", 50);

        Assert.Equal(0, entity.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void ApplyDamage_TwoHitsInSequence_BothLand()
    {
        // B7: two hits used to overwrite each other under the old DamageEvent-component pattern
        // (HP 10 took 3 + 4 and ended at 6, not 3). Applying immediately means there is no shared
        // slot for a second hit to clobber.
        using var world = new CodexWorld();
        var entity = world.CreateEntity();

        var pool = new ResourcePoolComponent();
        pool.Set("HP", 10);
        entity.Set(pool);

        world.ApplyDamage(entity, "HP", 3);
        world.ApplyDamage(entity, "HP", 4);

        Assert.Equal(3, entity.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void ApplyDamage_DrainsTempHpFirst()
    {
        using var world = new CodexWorld();
        var entity = world.CreateEntity();

        var pool = new ResourcePoolComponent();
        pool.Set("HP", 10);
        pool.Set("TempHP", 5);
        entity.Set(pool);

        world.ApplyDamage(entity, "HP", 8);

        var result = entity.Get<ResourcePoolComponent>();
        Assert.Equal(0, result.Get("TempHP"));
        Assert.Equal(7, result.Get("HP"));
    }

    [Fact]
    public void ApplyHealing_ClampsToMax()
    {
        using var world = new CodexWorld();
        var entity = world.CreateEntity();

        var pool = new ResourcePoolComponent();
        pool.Set("HP", 8);
        pool.Set("HP_Max", 10);
        entity.Set(pool);

        world.ApplyHealing(entity, "HP", 50);

        Assert.Equal(10, entity.Get<ResourcePoolComponent>().Get("HP"));
    }

    [Fact]
    public void AddStatus_TwoEffectsOnSameEntity_BothCoexist()
    {
        // B6: adding Stunned used to overwrite Poisoned, since StatusEffectComponent could only
        // ever hold one status at a time.
        using var world = new CodexWorld();
        var target = world.CreateEntity();

        world.AddStatus(target, "poisoned", "core", 10, EffectExpiry.EndOfTurn, target);
        world.AddStatus(target, "stunned", "core", 1, EffectExpiry.EndOfTurn, target);

        var effects = target.Get<ActiveEffectsComponent>().Effects;
        Assert.Equal(2, effects.Count);
        Assert.Contains(effects, e => e.EffectId == "poisoned");
        Assert.Contains(effects, e => e.EffectId == "stunned");
    }

    [Fact]
    public void AdvanceTurn_ExpiresOnlyEffectsAnchoredToThatActor_IndependentlyOfOthers()
    {
        // B6: Poisoned (10 rounds) and Stunned (1 round) must expire independently - the old
        // DurationSystem removed the single StatusEffectComponent slot as soon as any duration hit
        // zero, so Stunned expiring also silently un-poisoned the target.
        using var world = new CodexWorld();
        var target = world.CreateEntity();

        world.AddStatus(target, "poisoned", "core", 10, EffectExpiry.EndOfTurn, target);
        world.AddStatus(target, "stunned", "core", 1, EffectExpiry.EndOfTurn, target);

        world.AdvanceTurn(target);

        var effects = target.Get<ActiveEffectsComponent>().Effects;
        Assert.Single(effects);
        Assert.Equal("poisoned", effects[0].EffectId);
        Assert.Equal(9, effects[0].RoundsRemaining);
    }

    [Fact]
    public void AdvanceTurn_DoesNotTickEffectsAnchoredToADifferentActor()
    {
        using var world = new CodexWorld();
        var target = world.CreateEntity();
        var otherActor = world.CreateEntity();

        world.AddStatus(target, "poisoned", "core", 5, EffectExpiry.EndOfTurn, target);

        world.AdvanceTurn(otherActor);

        var effects = target.Get<ActiveEffectsComponent>().Effects;
        Assert.Single(effects);
        Assert.Equal(5, effects[0].RoundsRemaining);
    }
}
