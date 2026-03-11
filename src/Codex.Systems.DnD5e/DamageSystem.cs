using DefaultEcs;
using DefaultEcs.System;

namespace Codex.Systems.DnD5e;

public struct DamageEvent
{
    public int Amount;
}

[With(typeof(HitPointsComponent), typeof(DamageEvent))]
public sealed class DamageSystem : AEntitySetSystem<float>
{
    public DamageSystem(World world) : base(world)
    {
    }

    protected override void Update(float state, in Entity entity)
    {
        ref var hp = ref entity.Get<HitPointsComponent>();
        var damage = entity.Get<DamageEvent>();

        hp.Current -= damage.Amount;
        if (hp.Current < 0) hp.Current = 0;

        entity.Remove<DamageEvent>();
    }
}
