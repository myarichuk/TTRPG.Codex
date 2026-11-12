using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>
/// Shows a Library entry in the reader's language. The sheet's rules run on the original entries (their names are what other data refers to),
/// so anything the sheet prints about an entry (a pick, an item carried, a line of armor class, a rule message) goes through <see cref="Show"/> first.
/// </summary>
public static class EntryDisplay
{
    /// <summary>The entry with its translated name and text; the entry itself for English, for a language the pack has no translation in, and for
    /// homebrew (which has no pack, and may share an id with a pack entry that does).</summary>
    public static ContentEntry Show(this IContentRegistry registry, ContentEntry entry, string? culture) =>
        string.IsNullOrEmpty(entry.PackId) ? entry : registry.Localize(entry, culture);

    public static string Name(this IContentRegistry registry, ContentEntry entry, string? culture) => registry.Show(entry, culture).Name;

    /// <summary>
    /// The name and text of one row of an entry (a lineage, a feature's option) in the reader's language, for a row choice: the sheet stores
    /// the row's name as the entry writes it, and this finds the translated row in the same place (the same property, and the same index). The
    /// row as written when the language has no translation of that property or its rows no longer line up (a translation may drop rows).
    /// </summary>
    public static (string Name, string? Description) Row(this IContentRegistry registry, ContentEntry entry, string rowName, string? culture)
    {
        var shown = registry.Show(entry, culture);
        if (ReferenceEquals(shown, entry)) return (rowName, null);
        foreach (var (property, value) in entry.Properties)
        {
            if (!Rows(value, out var rows) || !shown.Properties.TryGetValue(property, out var other) || !Rows(other, out var translated)) continue;

            var index = rows.FindIndex(r => Named(r, rowName));
            if (index >= 0 && translated.Count == rows.Count) return (Text(translated[index], "name") ?? rowName, Text(translated[index], "description"));

            // One of a row's own options.
            for (var i = 0; i < rows.Count && i < translated.Count; i++)
            {
                if (!Rows(rows[i].TryGetValue("options", out var own) ? own : null, out var options)
                    || !Rows(translated[i].TryGetValue("options", out var theirs) ? theirs : null, out var translatedOptions)) continue;
                var at = options.FindIndex(r => Named(r, rowName));
                if (at >= 0 && translatedOptions.Count == options.Count) return (Text(translatedOptions[at], "name") ?? rowName, Text(translatedOptions[at], "description"));
            }
        }

        return (rowName, null);
    }

    private static bool Named(IDictionary<string, object> row, string name) => string.Equals(Text(row, "name"), name, StringComparison.OrdinalIgnoreCase);

    private static string? Text(IDictionary<string, object> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool Rows(object? value, out List<IDictionary<string, object>> rows)
    {
        rows = new List<IDictionary<string, object>>();
        if (value is not IEnumerable<object> list || value is string) return false;
        foreach (var item in list)
        {
            if (item is not IDictionary<string, object> row) return false;
            rows.Add(row);
        }

        return rows.Count > 0;
    }
}
