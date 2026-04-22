using DefaultEcs;
using DefaultEcs.System;
using Codex.Core.Components;

namespace Codex.Core.Systems;

public sealed class DurationSystem : AEntitySetSystem<float>
{
    public DurationSystem(World world)
        : base(world.GetEntities().With<DurationComponent>().AsSet())
    {
    }

    protected override void Update(float deltaTime, ReadOnlySpan<Entity> entities)
    {
        foreach (ref readonly var entity in entities)
        {
            ref var duration = ref entity.Get<DurationComponent>();
            duration.RoundsRemaining -= deltaTime;

            if (duration.RoundsRemaining <= 0)
            {
                entity.Remove<DurationComponent>();
                // Also remove StatusEffect if it was tied to this duration
                if (entity.Has<StatusEffectComponent>())
                {
                    entity.Remove<StatusEffectComponent>();
                }
            }
        }
    }
}