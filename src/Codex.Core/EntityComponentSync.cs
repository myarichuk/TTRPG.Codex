using System.Linq;
using System.Reflection;
using DefaultEcs;

namespace Codex.Core;

/// <summary>
/// Bridges boxed component instances - which is all <c>ComponentRegistry.Snapshot</c>/<c>Hydrate</c>
/// can produce, since a document store only knows the component's runtime <see cref="Type"/>, not
/// its compile-time generic parameter - to DefaultEcs's <c>Entity.Set&lt;T&gt;</c>/<c>Get&lt;T&gt;</c>/
/// <c>Has&lt;T&gt;</c>, which are generic methods that can't otherwise be invoked with only a
/// runtime <see cref="Type"/> in hand. Used by <c>CampaignRuntime</c> to promote a persisted
/// <c>ActorDocument.State</c> snapshot into live ECS components and back (3.1).
/// </summary>
public static class EntityComponentSync
{
    private static readonly MethodInfo SetMethod = typeof(Entity).GetMethods()
        .Single(m => m.Name == nameof(Entity.Set) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

    private static readonly MethodInfo HasMethod = typeof(Entity).GetMethod(nameof(Entity.Has))!;
    private static readonly MethodInfo GetMethod = typeof(Entity).GetMethod(nameof(Entity.Get))!;

    public static void SetBoxed(Entity entity, object component)
    {
        var boxedEntity = (object)entity;
        SetMethod.MakeGenericMethod(component.GetType()).Invoke(boxedEntity, new[] { component });
    }

    /// <summary>Every live component on <paramref name="entity"/> whose type is in <paramref name="registeredTypes"/>.</summary>
    public static IEnumerable<object> SnapshotAll(Entity entity, IEnumerable<Type> registeredTypes)
    {
        var boxedEntity = (object)entity;
        foreach (var type in registeredTypes)
        {
            var has = (bool)HasMethod.MakeGenericMethod(type).Invoke(boxedEntity, null)!;
            if (!has)
            {
                continue;
            }

            var value = GetMethod.MakeGenericMethod(type).Invoke(boxedEntity, null);
            if (value != null)
            {
                yield return value;
            }
        }
    }
}
