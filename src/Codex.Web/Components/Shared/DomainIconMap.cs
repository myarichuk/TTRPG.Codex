using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>Picks the second icon for a Library entry: a spell's school, a monster's creature type,
/// an item's category. Null when the entry has no such concept.</summary>
public static class DomainIconMap
{
    public static (string Domain, string Key, string Label)? Subject(ContentEntry entry)
    {
        string? Text(string key) => entry.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;

        switch (entry.Type.ToLowerInvariant())
        {
            case "spell" when Text("school") is { Length: > 0 } school:
                return ("school", school, school);
            case "monster" when Text("creatureType") is { Length: > 0 } creature:
                return ("creature", creature, creature);
            case "equipment" or "magic-item" when Text("category") is { Length: > 0 } category:
                return ("item", ItemKey(category), category);
            default:
                return null;
        }
    }

    internal static string ItemKey(string category)
    {
        var c = category.ToLowerInvariant();
        return c switch
        {
            _ when c.Contains("ammunition") => "ammunition",
            _ when c.Contains("weapon") => "weapon",
            _ when c.Contains("armor") => "armor",
            _ when c.Contains("ring") => "ring",
            _ when c.Contains("potion") || c.Contains("poison") => "potion",
            _ when c.Contains("wand") || c.Contains("staff") || c.Contains("foci") => "wand",
            _ when c.Contains("scroll") => "scroll",
            _ when c.Contains("tool") || c.Contains("instrument") || c.Contains("gaming") => "tool",
            _ when c.Contains("wondrous") => "wondrous",
            _ when c.Contains("mount") => "mount",
            _ => "gear",
        };
    }
}
