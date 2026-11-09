using System.Collections;
using System.Reflection;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Codex.Core.Systems;

/// <summary>
/// The keys in a YAML document that the type it is read into has no property for. The deserializers ignore them (a newer file must
/// still load in an older app), which also means a typo (<c>hitPoint:</c>) or a section a later version understands is silently
/// nothing. This names each one, so the loader can say so instead.
/// </summary>
public static class UnknownYamlKeys
{
    /// <summary>Each unknown key as a dotted path (<c>sheetRules.hitPoint</c>, <c>resources[2].maxkey</c>), in document order.
    /// A key under an unknown key is not listed again. An unreadable document gives none: the deserializer reports that.</summary>
    public static IReadOnlyList<string> Find(string yaml, Type type, INamingConvention naming)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return Array.Empty<string>();
        }

        var found = new List<string>();
        if (stream.Documents.Count > 0) Walk(stream.Documents[0].RootNode, type, "", naming, found);
        return found;
    }

    private static void Walk(YamlNode node, Type type, string path, INamingConvention naming, List<string> found)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(object) || type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return;

        if (DictionaryValueType(type) is { } valueType)
        {
            if (node is not YamlMappingNode map) return;
            foreach (var (key, value) in map.Children) Walk(value, valueType, Join(path, Scalar(key)), naming, found);
            return;
        }

        if (ListItemType(type) is { } itemType)
        {
            if (node is not YamlSequenceNode sequence) return;
            for (var i = 0; i < sequence.Children.Count; i++) Walk(sequence.Children[i], itemType, $"{path}[{i}]", naming, found);
            return;
        }

        if (node is not YamlMappingNode mapping) return;
        var properties = Properties(type, naming);
        foreach (var (key, value) in mapping.Children)
        {
            var name = Scalar(key);
            if (properties.TryGetValue(name, out var property)) Walk(value, property.PropertyType, Join(path, name), naming, found);
            else found.Add(Join(path, name));
        }
    }

    internal static Dictionary<string, PropertyInfo> Properties(Type type, INamingConvention naming)
    {
        var result = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0 || property.IsDefined(typeof(YamlIgnoreAttribute))
                || property.IsDefined(typeof(Codex.Plugin.Abstractions.NotInYamlAttribute))) continue;
            var alias = property.GetCustomAttribute<YamlMemberAttribute>()?.Alias;
            // A derived class may hide a base property ("new object? Equals"): the most derived one is what the deserializer fills.
            result.TryAdd(alias ?? naming.Apply(property.Name), property);
        }

        return result;
    }

    internal static Type? DictionaryValueType(Type type)
    {
        foreach (var candidate in Implemented(type))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() is var definition
                && (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)))
            {
                return candidate.GetGenericArguments()[1];
            }
        }

        return typeof(IDictionary).IsAssignableFrom(type) ? typeof(object) : null;
    }

    internal static Type? ListItemType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        foreach (var candidate in Implemented(type))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return candidate.GetGenericArguments()[0];
        }

        return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
    }

    private static IEnumerable<Type> Implemented(Type type) => type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces();

    private static string Scalar(YamlNode node) => node is YamlScalarNode { Value: { } value } ? value : node.ToString();

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}
