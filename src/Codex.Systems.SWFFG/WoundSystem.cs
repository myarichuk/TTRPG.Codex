using DefaultEcs;
using DefaultEcs.System;

namespace Codex.Systems.SWFFG;

public struct WoundEvent
{
    public int Amount;
}

[With(typeof(WoundComponent), typeof(WoundEvent))]
public sealed class WoundSystem : AEntitySetSystem<float>
{
    public WoundSystem(World world) : base(world)
    {
    }

    protected override void Update(float state, in Entity entity)
    {
        ref var wounds = ref entity.Get<WoundComponent>();
        var damage = entity.Get<WoundEvent>();

        wounds.Current += damage.Amount;

        entity.Remove<WoundEvent>();
    }
}
