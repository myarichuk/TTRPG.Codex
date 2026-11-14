using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>Resolves the actor a link points to into that actor's sheet (docs/CREW_LINKS_DESIGN.md). The hosts that have the campaign's
/// actors supply one; every other caller of the engine passes none, and then a link is <see cref="LinkState.Unknown"/>.</summary>
public interface ILinkedSheets
{
    /// <summary>The sheet of the actor with this id, or null when there is no such actor (it was deleted, or is not in the campaign).</summary>
    IReadOnlyDictionary<string, object>? Sheet(string actorId);
}

/// <summary>What the engine knows about one link of a sheet. <see cref="Unknown"/> is not <see cref="None"/>: the sheet names a linked actor, but
/// no <see cref="ILinkedSheets"/> was supplied, so what the link brings cannot be told, and nothing it owns may be swept or clamped.</summary>
public enum LinkState
{
    /// <summary>The sheet has no <c>link__{key}</c>: nothing is linked.</summary>
    None,

    /// <summary>The sheet names an actor and a resolver was supplied. The actor may still be missing (the resolver gives null): that is a known, empty link.</summary>
    Known,

    /// <summary>The sheet names an actor, and no resolver was supplied.</summary>
    Unknown
}

/// <summary>Reads the links of a sheet: the id it stores, the state of each link, and the linked sheet.</summary>
public static class SheetLinks
{
    /// <summary>The sheet key that holds a link's actor id: <c>link__{key}</c>.</summary>
    public const string KeyPrefix = "link__";

    public static string KeyOf(string linkKey) => KeyPrefix + linkKey;

    /// <summary>The actor id the sheet stores for a link, or null when it stores none.</summary>
    public static string? ActorId(IReadOnlyDictionary<string, object> sheet, string linkKey) =>
        YamlValues.GetString(sheet, KeyOf(linkKey)) is { Length: > 0 } id ? id : null;

    public static LinkState State(IReadOnlyDictionary<string, object> sheet, string linkKey, ILinkedSheets? linked) =>
        ActorId(sheet, linkKey) == null ? LinkState.None : linked == null ? LinkState.Unknown : LinkState.Known;

    /// <summary>Whether any link of the kind is <see cref="LinkState.Unknown"/> on this sheet.</summary>
    public static bool AnyUnknown(SheetKindDefinition? kind, IReadOnlyDictionary<string, object> sheet, ILinkedSheets? linked) =>
        linked == null && kind != null && kind.Links.Any(l => ActorId(sheet, l.Key) != null);

    /// <summary>The sheet a known link points to: null when there is no link, it is unknown, the actor is missing, or the actor's sheet is not of the
    /// kind the link declares (judged by the sheet's own <c>kind</c>, so a sheet with none is not taken for the system's first kind).</summary>
    public static IReadOnlyDictionary<string, object>? Linked(SheetKindDefinition? kind, IReadOnlyDictionary<string, object> sheet, string linkKey, ILinkedSheets? linked)
    {
        if (linked == null || kind?.Links.FirstOrDefault(l => l.Key == linkKey) is not { } link || ActorId(sheet, linkKey) is not { } id) return null;
        if (linked.Sheet(id) is not { } found) return null;
        return string.Equals(YamlValues.GetString(found, SheetModel.KindKey), link.Kind, StringComparison.OrdinalIgnoreCase) ? found : null;
    }
}
