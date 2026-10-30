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
}
