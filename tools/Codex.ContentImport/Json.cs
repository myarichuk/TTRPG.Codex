using System.Globalization;
using System.Text.Json;

namespace Codex.ContentImport;

/// <summary>Null-tolerant helpers over <see cref="JsonElement"/> - the 5e-bits data is regular but not uniform.</summary>
internal static class Json
{
    public static JsonElement? Get(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? Str(this JsonElement element, string property)
    {
        var value = element.Get(property);
        if (value == null) return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static string? Name(this JsonElement element, string property) => element.Get(property)?.Str("name");

    public static int? Int(this JsonElement element, string property)
    {
        var value = element.Get(property);
        if (value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i)) return i;
        if (value is { ValueKind: JsonValueKind.Number } d) return (int)Math.Round(d.GetDouble());
        if (value is { ValueKind: JsonValueKind.String } s && int.TryParse(s.GetString(), out var parsed)) return parsed;
        return null;
    }

    public static double? Dbl(this JsonElement element, string property)
    {
        var value = element.Get(property);
        return value is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
    }

    public static bool? Bool(this JsonElement element, string property)
    {
        var value = element.Get(property);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static IEnumerable<JsonElement> Items(this JsonElement element, string property)
    {
        var value = element.Get(property);
        return value is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    /// <summary>A text field that may be a string or an array of paragraphs.</summary>
    public static string? Text(this JsonElement element, string property)
    {
        var value = element.Get(property);
        if (value == null) return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.String => Clean(value.Value.GetString()),
            JsonValueKind.Array => Clean(string.Join("\n\n", value.Value.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)))),
            _ => null
        };
    }

    /// <summary>Names of an array of references, or the strings of an array of strings.</summary>
    public static List<string> Names(this JsonElement element, string property) =>
        element.Items(property)
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.Str("name"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();

    /// <summary>Tidies the PDF-extraction artefacts the upstream text carries: indented
    /// continuation lines and hyphenated line breaks like "ex- pended".</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim());
        var joined = string.Join("\n", lines);
        joined = System.Text.RegularExpressions.Regex.Replace(joined, @"(\w)- (\w)", "$1$2");
        return joined.Trim();
    }

    public static string FormatCr(double cr) => cr switch
    {
        0.125 => "1/8",
        0.25 => "1/4",
        0.5 => "1/2",
        _ => cr.ToString(CultureInfo.InvariantCulture)
    };
}
