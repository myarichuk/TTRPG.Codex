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
    /// stores: type name -&gt; JSON *string*. Components are structs with public fields (not
    /// properties), so serialization must include fields explicitly. The value is deliberately a
    /// plain string rather than a <see cref="JsonElement"/> object graph: <c>ActorDocument</c> is
    /// persisted through RavenDB's client, which serializes documents with Newtonsoft.Json, not
    /// System.Text.Json - a JsonElement round-tripped through Newtonsoft comes back as whatever
    /// Newtonsoft happened to make of its public surface, not usable JSON. A JSON string survives
    /// any serializer unchanged, since it's just a string to all of them.
    /// </summary>
    public Dictionary<string, object> Snapshot(IEnumerable<object> components)
    {
        var snapshot = new Dictionary<string, object>();
        foreach (var component in components)
        {
            var type = component.GetType();
            snapshot[NameOf(type)] = JsonSerializer.Serialize(component, type, SerializerOptions);
        }

        return snapshot;
    }

    /// <summary>
    /// Rehydrates a State dictionary back into component instances, in whatever order the
    /// dictionary enumerates. A key that doesn't resolve to a registered type is skipped rather
    /// than throwing - the caller (ECS promotion, Phase 3) shows "Missing Content" for it, exactly
    /// like a dangling ability/blueprint reference. Handles a value stored as the JSON string
    /// <see cref="Snapshot"/> writes, a <see cref="JsonElement"/> (a snapshot built directly
    /// in-process, never round-tripped through a document store), or anything else by re-serializing
    /// it - so a snapshot dictionary built by hand in a test behaves the same as one loaded from Raven.
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

            var json = value switch
            {
                string s => s,
                JsonElement je => je.GetRawText(),
                _ => JsonSerializer.Serialize(value, SerializerOptions)
            };

            var component = JsonSerializer.Deserialize(json, type, SerializerOptions);
            if (component != null)
            {
                yield return component;
            }
        }
    }
}
