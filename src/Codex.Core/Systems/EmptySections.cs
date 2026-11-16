using System.Collections;
using System.Reflection;

namespace Codex.Core.Systems;

/// <summary>A section left empty in system.yaml (<c>derived:</c> with nothing under it, <c>lists:</c>, <c>ranked:</c>) reads as null, which
/// the rules would trip over. This puts an empty list or map in its place, all the way down, so an empty section means nothing there.</summary>
internal static class EmptySections
{
    private const string Namespace = "Codex.Plugin.Abstractions";

    private static readonly NullabilityInfoContext Nullability = new();

    public static void Fill(object? root)
    {
        if (root == null) return;
        Walk(root, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static void Walk(object value, HashSet<object> seen)
    {
        if (!seen.Add(value)) return;
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            var type = property.PropertyType;
            var current = property.GetValue(value);
            if (current == null)
            {
                // Only a list the type declares as always there: one declared nullable means "none given", which a rule may read.
                if (Nullability.Create(property).WriteState != NullabilityState.Nullable && type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                {
                    property.SetValue(value, Activator.CreateInstance(type));
                }

                continue;
            }

            Visit(current, seen);
        }
    }

    private static void Visit(object current, HashSet<object> seen)
    {
        switch (current)
        {
            case string:
                return;
            case IDictionary dictionary:
                foreach (var item in dictionary.Values) if (item != null) Visit(item, seen);
                return;
            case IEnumerable list:
                foreach (var item in list) if (item != null) Visit(item, seen);
                return;
        }

        if (current.GetType().Namespace is { } ns && ns.StartsWith(Namespace, StringComparison.Ordinal)) Walk(current, seen);
    }
}
