using Codex.Plugin.Abstractions;

namespace Codex.Core;

public static class ContentNames
{
    /// <summary>
    /// The name of the actor blueprint <paramref name="fullId"/> ("system:id") as a reader of <paramref name="culture"/> sees it:
    /// translated when the blueprint is a Library entry (a monster) a translation pack covers, the pack's own name otherwise.
    /// Null when no loaded pack has it.
    /// </summary>
    public static string? ActorName(this IContentRegistry registry, string fullId, string? culture)
    {
        var actor = registry.GetActor(fullId);
        if (actor == null)
        {
            return null;
        }

        var type = actor.Metadata.TryGetValue("entryType", out var value) ? value?.ToString() : null;
        var entry = type == null ? null : registry.GetEntry(actor.SystemId, type, actor.Id);
        return entry == null ? actor.Name : registry.Localize(entry, culture).Name;
    }
}
