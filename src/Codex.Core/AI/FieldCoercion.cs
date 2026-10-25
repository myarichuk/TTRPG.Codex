using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codex.Core.AI;

/// <summary>Turns the values a model wrote into the types the fields declare. Whatever cannot be made to fit
/// (an unknown option, a word where a number belongs) comes back null and is dropped rather than saved.</summary>
internal static class FieldCoercion
{
    private static readonly Regex JsonObject = new(@"\{[\s\S]*\}", RegexOptions.Compiled);

    /// <summary>The outermost JSON object in a reply, tolerating prose or code fences around it.</summary>
    public static JsonDocument ParseObject(string? text)
    {
        var match = JsonObject.Match(text ?? string.Empty);
        if (!match.Success)
        {
            throw new FormatException("The model did not return a JSON object.");
        }

        try
        {
            return JsonDocument.Parse(match.Value);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The model's JSON did not parse: " + ex.Message, ex);
        }
    }

    public static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <param name="canonical">Maps a value the model wrote in the reader's language back to the stored one (a translated
    /// "Воплощение" to "Evocation"); applied to dropdowns, tags and short text. Null leaves values as written.</param>
    public static object? Coerce(JsonElement value, FieldSpec spec, Func<string, string>? canonical = null)
    {
        canonical ??= v => v;
        switch (spec.Type)
        {
            case "Number":
                if (!TryNumber(value, out var number)) return null;
                if (spec.Min.HasValue) number = Math.Max(spec.Min.Value, number);
                if (spec.Max.HasValue) number = Math.Min(spec.Max.Value, number);
                return (long)Math.Round(number);
            case "Boolean":
                return value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : null,
                    _ => null
                };
            case "TagList":
                IEnumerable<string>? tags = value.ValueKind switch
                {
                    JsonValueKind.Array => value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!),
                    JsonValueKind.String => value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    _ => null
                };
                return tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Select(t => (object)canonical(t)).ToList();
            case "Dropdown":
            case "EntityReference":
                if (value.ValueKind != JsonValueKind.String) return null;
                var chosen = value.GetString()!.Trim();
                if (spec.Options == null || spec.Options.Count == 0) return canonical(chosen);
                return MatchOption(chosen, spec.Options) ?? MatchOption(canonical(chosen), spec.Options);
            case "Collection":
                return value.ValueKind == JsonValueKind.Array ? Rows(value) : null;
            case "Text":
                return value.ValueKind switch
                {
                    JsonValueKind.String => canonical(value.GetString()!.Trim()),
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                    _ => null
                };
            default:
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                    _ => null
                };
        }
    }

    public static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            number = value.GetDouble();
            return true;
        }

        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    // Options are "id" or "id (label)"; accept either the id or the label, case-insensitively.
    private static string? MatchOption(string chosen, IReadOnlyList<string> options)
    {
        foreach (var option in options)
        {
            var id = option.Split(" (")[0].Trim();
            var label = option.Contains(" (") ? option[(option.IndexOf(" (", StringComparison.Ordinal) + 2)..].TrimEnd(')') : id;
            if (string.Equals(id, chosen, StringComparison.OrdinalIgnoreCase) || string.Equals(label, chosen, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    // A collection is a list of small maps (a creature's actions: name / description); rows without a name are dropped.
    private static List<object>? Rows(JsonElement array)
    {
        var rows = new List<object>();
        foreach (var element in array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            var row = new Dictionary<string, object>();
            foreach (var prop in element.EnumerateObject())
            {
                object? cell = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()!.Trim(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var whole) ? (object)whole : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
                if (cell is not (null or ""))
                {
                    row[prop.Name] = cell;
                }
            }

            if (row.TryGetValue("name", out var name) && name is string { Length: > 0 })
            {
                rows.Add(row);
            }
        }

        return rows.Count == 0 ? null : rows;
    }
}
