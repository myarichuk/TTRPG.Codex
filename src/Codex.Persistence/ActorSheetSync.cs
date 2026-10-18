using Codex.Core;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;

namespace Codex.Persistence;

/// <summary>
/// Keeps an actor's editable <see cref="ActorDocument.Sheet"/> and its runtime
/// <see cref="ActorDocument.State"/> (component snapshot) consistent. The sheet is the human face
/// (what a player types); the state is what the live table mutates (damage, healing, conditions).
/// Resources and attributes exist in both and this is the single place that copies them across.
/// </summary>
public static class ActorSheetSync
{
    /// <summary>
    /// Builds/updates the component snapshot from the sheet: attribute keys of the system become an
    /// <see cref="AttributesComponent"/>, resource keys (and their max keys) a
    /// <see cref="ResourcePoolComponent"/>. Components already in the state that the sheet doesn't
    /// describe (active effects, tags) are preserved.
    /// </summary>
    public static void ApplySheetToState(ActorDocument actor, SystemDefinition? system, ComponentRegistry registry)
    {
        var components = registry.Hydrate(actor.State).ToList();

        var pool = components.OfType<ResourcePoolComponent>().FirstOrDefault();
        if (pool.Pools == null) pool = new ResourcePoolComponent();

        var attributes = components.OfType<AttributesComponent>().FirstOrDefault();
        if (attributes.Values == null) attributes = new AttributesComponent();

        if (system != null)
        {
            foreach (var attribute in system.Attributes)
            {
                var value = YamlValues.GetInt(actor.Sheet, attribute.Key);
                if (value.HasValue) attributes.Set(attribute.Key, value.Value);
            }

            foreach (var resource in system.Resources)
            {
                var value = YamlValues.GetInt(actor.Sheet, resource.Key);
                if (value.HasValue) pool.Set(resource.Key, value.Value);
                if (resource.MaxKey != null)
                {
                    var max = YamlValues.GetInt(actor.Sheet, resource.MaxKey);
                    if (max.HasValue) pool.Set(resource.MaxKey, max.Value);
                }
            }
        }
        else
        {
            // No system schema: treat the conventional HP keys as the pool so the table still works.
            foreach (var key in new[] { "HP", "HP_Max", "TempHP" })
            {
                var value = YamlValues.GetInt(actor.Sheet, key);
                if (value.HasValue) pool.Set(key, value.Value);
            }
        }

        var rest = components.Where(c => c is not ResourcePoolComponent && c is not AttributesComponent).ToList();
        rest.Add(pool);
        if (attributes.Values.Count > 0) rest.Add(attributes);
        actor.State = registry.Snapshot(rest);
    }

    /// <summary>Copies live resource and attribute values from the state back onto the sheet, so a
    /// sheet opened after a fight shows the hit points the fight left behind.</summary>
    public static void ApplyStateToSheet(ActorDocument actor, SystemDefinition? system, ComponentRegistry registry)
    {
        foreach (var component in registry.Hydrate(actor.State))
        {
            switch (component)
            {
                case ResourcePoolComponent pool:
                    foreach (var (key, value) in pool.Pools)
                    {
                        actor.Sheet[key] = (long)value;
                    }

                    break;
                case AttributesComponent attributes:
                    foreach (var (key, value) in attributes.Values)
                    {
                        actor.Sheet[key] = (long)value;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Seeds a new actor's sheet from a content blueprint (a monster stat block): the blueprint's
    /// resources become sheet resources and its properties are copied as-is, so the sheet shows the
    /// stat block and the state gets a matching pool.
    /// </summary>
    public static void SeedFromBlueprint(ActorDocument actor, IActorDefinition blueprint, SystemDefinition? system, ComponentRegistry registry)
    {
        foreach (var (key, value) in blueprint.Properties)
        {
            actor.Sheet[key] = value;
        }

        foreach (var (key, value) in blueprint.Resources)
        {
            actor.Sheet[key] = (long)value;
        }

        ApplySheetToState(actor, system, registry);
    }

    /// <summary>Reads the current/max of the system's primary resource for display ("12/15 HP").</summary>
    public static (int Current, int Max, string Label) PrimaryResource(ActorDocument actor, SystemDefinition? system)
    {
        var resource = system?.PrimaryResource;
        var key = resource?.Key ?? "HP";
        var maxKey = resource?.MaxKey ?? "HP_Max";
        return (YamlValues.GetInt(actor.Sheet, key) ?? 0, YamlValues.GetInt(actor.Sheet, maxKey) ?? 0, resource?.Label ?? "HP");
    }
}
