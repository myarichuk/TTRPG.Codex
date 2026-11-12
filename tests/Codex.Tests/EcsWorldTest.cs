using Codex.Core;
using Codex.Core.Components;
using Xunit;

namespace Codex.Tests;

public class EcsWorldTest
{
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
    public void AdjustResource_SpendsAndRegainsWithinZeroAndMax_AndRestRefills()
    {
        using var world = new CodexWorld();
        var entity = world.CreateEntity();

        var pool = new ResourcePoolComponent();
        pool.Set("slots_1", 2);
        pool.Set("slots_1_Max", 2);
        pool.Set("slots_2", 1);
        pool.Set("slots_2_Max", 1);
        entity.Set(pool);

        Assert.True(world.AdjustResource(entity, "slots_1", -1));
        Assert.False(world.AdjustResource(entity, "slots_1", -5)); // more than is left: refused, nothing spent
        world.AdjustResource(entity, "slots_2", 3);
        world.AdjustResource(entity, "slots_9", -1); // a slot level the actor doesn't have

        var result = entity.Get<ResourcePoolComponent>();
        Assert.Equal(1, result.Get("slots_1"));
        Assert.Equal(1, result.Get("slots_2"));
        Assert.False(result.Pools.ContainsKey("slots_9"));

        world.RestoreResources(entity, new[] { "slots_1", "slots_2" });
        Assert.Equal(2, entity.Get<ResourcePoolComponent>().Get("slots_1"));
    }

    [Fact]
    public void AddStatus_TwoEffectsOnSameEntity_BothCoexist()
    {
        // B6: adding Stunned used to overwrite Poisoned, since StatusEffectComponent could only
        // ever hold one status at a time.
        using var world = new CodexWorld();
        var target = world.CreateEntity();

        world.AddStatus(target, "poisoned", "core", 10, EffectExpiry.EndOfTurn, "target");
        world.AddStatus(target, "stunned", "core", 1, EffectExpiry.EndOfTurn, "target");

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

        world.AddStatus(target, "poisoned", "core", 10, EffectExpiry.EndOfTurn, "target");
        world.AddStatus(target, "stunned", "core", 1, EffectExpiry.EndOfTurn, "target");

        world.AdvanceTurn("target");

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
        world.CreateEntity();

        world.AddStatus(target, "poisoned", "core", 5, EffectExpiry.EndOfTurn, "target");

        world.AdvanceTurn("other-actor");

        var effects = target.Get<ActiveEffectsComponent>().Effects;
        Assert.Single(effects);
        Assert.Equal(5, effects[0].RoundsRemaining);
    }
}
