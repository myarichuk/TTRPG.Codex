using DefaultEcs;
using DefaultEcs.System;
using Codex.Core.Components;

namespace Codex.Systems.DnD5e;

public struct DamageEvent
{
    public int Amount { get; set; }
}

public sealed class DamageSystem : AEntitySetSystem<float>
{
    public DamageSystem(World world)
        : base(world.GetEntities().With<DamageEvent>().With<ResourcePoolComponent>().AsSet())
    {
    }

    protected override void Update(float deltaTime, ReadOnlySpan<Entity> entities)
    {
        foreach (ref readonly var entity in entities)
        {
            var damage = entity.Get<DamageEvent>();
            var pool = entity.Get<ResourcePoolComponent>();

            pool.Modify("HP", -damage.Amount);

            entity.Remove<DamageEvent>();
        }
    }
}