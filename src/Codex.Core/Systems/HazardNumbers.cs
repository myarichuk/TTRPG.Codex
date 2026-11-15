using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>
/// The numbers of a hazard entry as one placement uses them (HAZARDS_DESIGN.md 3.1): the entry's base properties, with the fields of one of its own
/// scaled rows (<c>higherLevels</c>) laid over them. The table reads a hazard's numbers only through <see cref="For"/>, so a placement made for one
/// party level does not change when the party grows.
/// </summary>
public static class HazardNumbers
{
    /// <summary>The <c>levels</c> of each scaled row the entry has, in the order written.</summary>
    public static IReadOnlyList<string> Rows(ContentEntry entry, ContentTypeDefinition type) =>
        RowsOf(entry, type).Select(r => Text(r, "levels")).Where(l => l.Length > 0).ToList();

    /// <summary>The entry's properties with the row named <paramref name="levels"/> laid over them. A null or unknown row gives the base numbers.</summary>
    public static IReadOnlyDictionary<string, object> For(ContentEntry entry, ContentTypeDefinition type, string? levels)
    {
        var numbers = new Dictionary<string, object>(entry.Properties, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(levels)) return numbers;
        var row = RowsOf(entry, type).FirstOrDefault(r => Text(r, "levels") == levels.Trim());
        if (row == null) return numbers;
        foreach (var (key, value) in row)
        {
            if (!string.Equals(key, "levels", StringComparison.Ordinal) && value != null) numbers[key] = value;
        }

        return numbers;
    }

    /// <summary>What is wrong inside an entry's collection rows: a scaled row naming a key that is no field of the type (or one that only differs in case,
    /// which would be ignored without a word), and an other-way row with no name.</summary>
    public static IEnumerable<string> Problems(ContentEntry entry, ContentTypeDefinition type)
    {
        if (type.Hazard is not { } hazard) yield break;
        var fields = type.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var row in RowsOf(entry, type))
        {
            foreach (var key in row.Keys.Where(k => k != "levels" && !fields.Contains(k)))
            {
                var near = fields.FirstOrDefault(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
                yield return near != null
                    ? $"scaled row {Text(row, "levels")} names '{key}'; the field is '{near}', so the row would be ignored."
                    : $"scaled row {Text(row, "levels")} names '{key}', which is not a field of {type.Type}.";
            }
        }

        foreach (var row in Rows(entry, hazard.Methods))
        {
            if (Text(row, "name").Length == 0) yield return "an other-way row has no name.";
        }
    }

    private static IEnumerable<IDictionary<string, object?>> RowsOf(ContentEntry entry, ContentTypeDefinition type) => Rows(entry, type.Hazard?.HigherLevels);

    private static IEnumerable<IDictionary<string, object?>> Rows(ContentEntry entry, string? field)
    {
        if (string.IsNullOrEmpty(field) || !entry.Properties.TryGetValue(field, out var value) || value is not System.Collections.IEnumerable items || value is string) yield break;
        foreach (var item in items)
        {
            if (item is IDictionary<string, object> typed) yield return typed.ToDictionary(p => p.Key, p => (object?)p.Value);
            else if (item is IDictionary<object, object> loose)
            {
                yield return loose.ToDictionary(p => Convert.ToString(p.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, p => (object?)p.Value);
            }
        }
    }

    private static string Text(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;
}
