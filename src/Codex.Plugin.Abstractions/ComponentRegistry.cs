using System.Text.Json;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// The type-name &lt;-&gt; Type map every system plugin's <c>RegisterComponents</c> populates
/// (1.5). This is what lets an actor's persisted component snapshot (a plain JSON-friendly
/// dictionary, since that's all a document store can hold) round-trip back into real ECS
/// component instances - and what lets a snapshot naming a component nobody registered anymore
/// (its owning content pack or plugin was removed) be skipped instead of crashing the hydration.
/// </summary>
public class ComponentRegistry
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { IncludeFields = true };

    public HashSet<Type> RegisteredComponents { get; } = new();

    private readonly Dictionary<string, Type> _typesByName = new();

    public void Register<T>()
    {
        var type = typeof(T);
        RegisteredComponents.Add(type);
        _typesByName[NameOf(type)] = type;
    }

    /// <summary>The stable key a component type is snapshotted/looked up under.</summary>
    public static string NameOf(Type type) => type.FullName ?? type.Name;

    public Type? Resolve(string typeName) => _typesByName.GetValueOrDefault(typeName);

    /// <summary>
    /// Snapshots live component instances into the dictionary shape <c>ActorDocument.State</c>
    /// stores: type name -&gt; JSON. Components are structs with public fields (not properties),
    /// so serialization must include fields explicitly.
    /// </summary>
    public Dictionary<string, object> Snapshot(IEnumerable<object> components)
    {
        var snapshot = new Dictionary<string, object>();
        foreach (var component in components)
        {
            var type = component.GetType();
            snapshot[NameOf(type)] = JsonSerializer.SerializeToElement(component, type, SerializerOptions);
        }

        return snapshot;
    }

    /// <summary>
    /// Rehydrates a State dictionary back into component instances, in whatever order the
    /// dictionary enumerates. A key that doesn't resolve to a registered type is skipped rather
    /// than throwing - the caller (ECS promotion, Phase 3) shows "Missing Content" for it, exactly
    /// like a dangling ability/blueprint reference.
    /// </summary>
    public IEnumerable<object> Hydrate(IReadOnlyDictionary<string, object> snapshot)
    {
        foreach (var (name, value) in snapshot)
        {
            var type = Resolve(name);
            if (type == null)
            {
                continue;
            }

            var element = value is JsonElement je ? je : JsonSerializer.SerializeToElement(value, SerializerOptions);
            var component = element.Deserialize(type, SerializerOptions);
            if (component != null)
            {
                yield return component;
            }
        }
    }
}
