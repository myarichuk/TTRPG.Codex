using Codex.Core;
using Codex.Systems.SWFFG;
using Xunit;

namespace Codex.Tests;

public class SwffgWoundSystemTest
{
    [Fact]
    public void WoundSystem_IncrementsWoundsAndRemovesEvent()
    {
        using var world = new CodexWorld();
        world.AddSystem(new WoundSystem(world.InnerWorld));

        var entity = world.CreateEntity();
        entity.Set(new WoundComponent { Current = 5, Threshold = 10 });

        Assert.Equal(5, entity.Get<WoundComponent>().Current);

        entity.Set(new WoundEvent { Amount = 3 });
        world.Tick(0.1f);

        Assert.Equal(8, entity.Get<WoundComponent>().Current);
        Assert.False(entity.Has<WoundEvent>());
    }

    [Fact]
    public void WoundSystem_HandlesMultipleWounds()
    {
        using var world = new CodexWorld();
        world.AddSystem(new WoundSystem(world.InnerWorld));

        var entity = world.CreateEntity();
        entity.Set(new WoundComponent { Current = 0, Threshold = 10 });

        entity.Set(new WoundEvent { Amount = 2 });
        world.Tick(0.1f);
        Assert.Equal(2, entity.Get<WoundComponent>().Current);

        entity.Set(new WoundEvent { Amount = 4 });
        world.Tick(0.1f);
        Assert.Equal(6, entity.Get<WoundComponent>().Current);
    }
}
