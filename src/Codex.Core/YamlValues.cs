using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Codex.Core;

/// <summary>
/// YamlDotNet hands back <c>object</c> graphs as strings, <c>List&lt;object&gt;</c> and
/// <c>Dictionary&lt;object, object&gt;</c>. This normalises them into the shape the rest of the app
/// (and RavenDB, and System.Text.Json) can work with: <c>string</c>, <c>long</c>, <c>double</c>,
/// <c>bool</c>, <c>List&lt;object&gt;</c>, <c>Dictionary&lt;string, object&gt;</c>.
/// </summary>
public static class YamlValues
{
    public static Dictionary<string, object> NormalizeMap(IDictionary<string, object>? map)
    {
        var result = new Dictionary<string, object>();
        if (map == null) return result;

        foreach (var (key, value) in map)
        {
            var normalized = Normalize(value);
            if (normalized != null)
            {
                result[key] = normalized;
            }
        }

        return result;
    }

    public static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return ParseScalar(s);
            case bool or long or double or int:
                return value is int i ? (long)i : value;
            case IDictionary dictionary:
            {
                var map = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var normalized = Normalize(entry.Value);
                    if (normalized != null)
                    {
                        map[entry.Key?.ToString() ?? string.Empty] = normalized;
                    }
                }

                return map;
            }
            case IEnumerable enumerable:
                return enumerable.Cast<object?>().Select(Normalize).Where(v => v != null).Select(v => v!).ToList();
            default:
                return value;
        }
    }

    private static object ParseScalar(string s)
    {
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Length > 0 && s.Length < 19 && (char.IsDigit(s[0]) || (s[0] == '-' && s.Length > 1)))
        {
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !s.Contains('e') && !s.Contains('E')) return d;
        }

        return s;
    }

    /// <summary>
    /// A number out of a property bag, whatever it arrived as. RavenDB hands decimal numbers in an
    /// <c>object</c> back as its own lazy number type (a level saved as 4.0), which is neither a
    /// <c>double</c> nor a <c>string</c>, so anything unrecognised is read through its text.
    /// </summary>
    public static double? ToNumber(object? value) => value switch
    {
        null => null,
        bool => null,
        long l => l,
        int i => i,
        double d => d,
        float f => f,
        decimal m => (double)m,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        IEnumerable => null,
        _ => double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) ? text : null
    };

    /// <summary>
    /// Structural equality for two property-bag values, whatever store they came back from: numbers
    /// compare by value (a level saved as 4 and read back as 4.0 is the same level), maps and lists
    /// element by element, everything else by its text. Used to tell an edited sheet field from one
    /// the editor merely carried along.
    /// </summary>
    public static bool SameValue(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is bool lb) return right is bool rb && lb == rb;
        if (right is bool) return false;
        if (left is not string && right is not string && ToNumber(left) is { } ln && ToNumber(right) is { } rn) return ln.Equals(rn);

        if (left is IDictionary ld)
        {
            if (right is not IDictionary rd || ld.Count != rd.Count) return false;
            foreach (DictionaryEntry entry in ld)
            {
                if (!rd.Contains(entry.Key) || !SameValue(entry.Value, rd[entry.Key])) return false;
            }

            return true;
        }

        if (left is IEnumerable le and not string)
        {
            if (right is string || right is not IEnumerable re) return false;
            var l = le.Cast<object?>().ToList();
            var r = re.Cast<object?>().ToList();
            return l.Count == r.Count && l.Zip(r).All(pair => SameValue(pair.First, pair.Second));
        }

        return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Best-effort integer read from a normalised property bag.</summary>
    public static int? GetInt(IReadOnlyDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && ToNumber(value) is { } number ? (int)number : null;

    public static string? GetString(IReadOnlyDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) ? value?.ToString() : null;
}
