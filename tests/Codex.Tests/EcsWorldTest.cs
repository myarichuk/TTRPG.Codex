using Codex.Core;
using Codex.Core.Components;
using Codex.Systems.DnD5e;
using Xunit;

namespace Codex.Tests;

public class EcsWorldTest
{
    [Fact]
    public void EcsWorld_EntityCreationAndSystemExecution()
    {
        using var world = new CodexWorld();
        world.AddSystem(new DamageSystem(world.InnerWorld));

        var entity = world.CreateEntity();
        entity.Set(new HitPointsComponent { Current = 15, Maximum = 20 });

        Assert.Equal(15, entity.Get<HitPointsComponent>().Current);

        entity.Set(new DamageEvent { Amount = 5 });
        world.Tick(0.1f);

        Assert.Equal(10, entity.Get<HitPointsComponent>().Current);
        Assert.False(entity.Has<DamageEvent>());
    }

    [Fact]
    public void DurationSystem_Should_DecrementAndRemoveComponents()
    {
        using var world = new CodexWorld();
        // DurationSystem is added in CodexWorld constructor

        var entity = world.CreateEntity();
        entity.Set(new DurationComponent { RoundsRemaining = 1.0f });
        entity.Set(new StatusEffectComponent { EffectId = "stunned", PackId = "core" });

        // One round passes
        world.Tick(1.0f);

        Assert.False(entity.Has<DurationComponent>());
        Assert.False(entity.Has<StatusEffectComponent>());
    }

    [Fact]
    public void DurationSystem_Should_NotRemoveIfDurationRemains()
    {
        using var world = new CodexWorld();

        var entity = world.CreateEntity();
        entity.Set(new DurationComponent { RoundsRemaining = 2.0f });

        // Half a round passes
        world.Tick(0.5f);

        Assert.True(entity.Has<DurationComponent>());
        Assert.Equal(1.5f, entity.Get<DurationComponent>().RoundsRemaining);
    }
}