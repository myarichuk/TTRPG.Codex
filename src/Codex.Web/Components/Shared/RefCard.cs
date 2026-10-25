using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>Reads the structured properties the reference cards draw from. A card shows only when
/// its entry has the fields it needs; otherwise the Library falls back to the generic detail.</summary>
public static class RefCard
{
    public static string? Text(ContentEntry entry, string key) =>
        entry.Properties.TryGetValue(key, out var v) && v is IConvertible && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString() : null;

    /// <summary>A row's cell as text; null when it is missing or not a plain value (a nested map or list has no text of its own,
    /// and its <c>ToString()</c> is a type name).</summary>
    public static string? Cell(IReadOnlyDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var v) && v is IConvertible && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString() : null;

    /// <summary>True for a value that reads as text: a string, number or flag, or a list of those.</summary>
    public static bool IsPlain(object? value) => value switch
    {
        null or IConvertible => true,
        IEnumerable<object> list => list.All(item => item is null or IConvertible),
        _ => false
    };

    public static int? Int(ContentEntry entry, string key) =>
        entry.Properties.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var n) ? n : null;

    public static bool Flag(ContentEntry entry, string key) =>
        entry.Properties.TryGetValue(key, out var v) && (v is true || string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase));

    public static List<string> List(ContentEntry entry, string key) =>
        entry.Properties.TryGetValue(key, out var v) && v is IEnumerable<object> items && v is not string
            ? items.OfType<IConvertible>().Select(o => o.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList()
            : new List<string>();

    public static List<Dictionary<string, object>> Rows(ContentEntry entry, string key) =>
        entry.Properties.TryGetValue(key, out var v) && v is IEnumerable<object> items
            ? items.OfType<IDictionary<string, object>>().Select(r => new Dictionary<string, object>(r)).ToList()
            : new List<Dictionary<string, object>>();

    public static string Modifier(int score)
    {
        var mod = (int)Math.Floor((score - 10) / 2.0);
        return mod >= 0 ? "+" + mod : "−" + -mod;
    }

    /// <summary>The description without its leading italic line and bold "Label:" lines, which the
    /// card header already shows. Paragraphs are separated by blank lines.</summary>
    public static string Body(string? description, params string[] metaLabels)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        var paragraphs = description.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
        while (paragraphs.Count > 0 && IsMeta(paragraphs[0], metaLabels))
        {
            paragraphs.RemoveAt(0);
        }

        return string.Join("\n\n", paragraphs);
    }

    private static bool IsMeta(string paragraph, string[] labels) =>
        (paragraph.StartsWith('*') && !paragraph.StartsWith("**") && paragraph.EndsWith('*') && !paragraph.Contains('\n'))
        || labels.Any(l => paragraph.StartsWith("**" + l, StringComparison.OrdinalIgnoreCase));
}
