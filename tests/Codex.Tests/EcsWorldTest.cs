using Codex.Core;
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
}
