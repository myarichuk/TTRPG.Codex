using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>Resolves the actor a link points to into that actor's sheet (docs/CREW_LINKS_DESIGN.md). The hosts that have the campaign's
/// actors supply one; every other caller of the engine passes none, and then a link is <see cref="LinkState.Unknown"/>.</summary>
public interface ILinkedSheets
{
    /// <summary>The sheet of the actor with this id, or null when it has none to give: the actor is deleted (in the trash), gone, or not in the
    /// campaign. A deleted actor's sheet is never handed out, so what it held cannot reach a member; the link is <see cref="LinkState.Unknown"/>.</summary>
    IReadOnlyDictionary<string, object>? Sheet(string actorId);

    /// <summary>Why <see cref="Sheet"/> has none, for the member's sheet to say. The default cannot tell a deleted actor from a missing one.</summary>
    LinkedActorStatus Status(string actorId) => Sheet(actorId) == null ? LinkedActorStatus.NotFound : LinkedActorStatus.Available;
}

/// <summary>What a host knows about the actor a link names.</summary>
public enum LinkedActorStatus
{
    /// <summary>The actor's sheet is supplied.</summary>
    Available,

    /// <summary>The actor is in the trash. Restoring it brings back what the link made.</summary>
    Deleted,

    /// <summary>No such actor in the campaign.</summary>
    NotFound
}

/// <summary>What the engine knows about one link of a sheet. <see cref="Unknown"/> is not <see cref="None"/>: the sheet names a linked actor, but
/// no <see cref="ILinkedSheets"/> was supplied, so what the link brings cannot be told, and nothing it owns may be swept or clamped.</summary>
public enum LinkState
{
    /// <summary>The sheet has no <c>link__{key}</c>: nothing is linked.</summary>
    None,

    /// <summary>The sheet names an actor and the resolver supplied its sheet. The sheet may be of another kind than the link declares: that is a known, empty link.</summary>
    Known,

    /// <summary>The sheet names an actor, and no resolver was supplied, or the resolver has no sheet for it (the actor is deleted or gone).</summary>
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

    /// <summary>Whether a sheet key holds a link's actor id.</summary>
    public static bool IsLinkKey(string key) => key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    /// <summary>A copy of the sheet without its <c>link__*</c> keys: an actor id belongs to one campaign, so a sheet that leaves it (a vault
    /// character joining a campaign) keeps none.</summary>
    public static Dictionary<string, object> WithoutLinks(IReadOnlyDictionary<string, object> sheet) =>
        sheet.Where(p => !IsLinkKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>Known only when the sheet names an actor and the host supplied that actor's sheet. A link to an actor the host cannot give (deleted,
    /// gone) is unknown, like one with no host: what it made is kept, and restoring the actor brings it back.</summary>
    public static LinkState State(IReadOnlyDictionary<string, object> sheet, string linkKey, ILinkedSheets? linked) =>
        ActorId(sheet, linkKey) is not { } id ? LinkState.None : linked?.Sheet(id) == null ? LinkState.Unknown : LinkState.Known;

    /// <summary>Whether any link of the kind is <see cref="LinkState.Unknown"/> on this sheet.</summary>
    public static bool AnyUnknown(SheetKindDefinition? kind, IReadOnlyDictionary<string, object> sheet, ILinkedSheets? linked) =>
        kind != null && kind.Links.Any(l => State(sheet, l.Key, linked) == LinkState.Unknown);

    /// <summary>Why a host has no sheet for a link: null when the link names none, the sheet is supplied, or no host was supplied (nothing is known
    /// then); else the actor is deleted or not found.</summary>
    public static LinkedActorStatus? Gone(IReadOnlyDictionary<string, object> sheet, string linkKey, ILinkedSheets? linked) =>
        linked != null && ActorId(sheet, linkKey) is { } id && linked.Sheet(id) == null ? linked.Status(id) : null;

    /// <summary>The sheet a known link points to: null when there is no link, it is unknown, the actor is missing, or the actor's sheet is not of the
    /// kind the link declares (judged by the sheet's own <c>kind</c>, so a sheet with none is not taken for the system's first kind).</summary>
    public static IReadOnlyDictionary<string, object>? Linked(SheetKindDefinition? kind, IReadOnlyDictionary<string, object> sheet, string linkKey, ILinkedSheets? linked)
    {
        if (linked == null || kind?.Links.FirstOrDefault(l => l.Key == linkKey) is not { } link || ActorId(sheet, linkKey) is not { } id) return null;
        if (linked.Sheet(id) is not { } found) return null;
        return string.Equals(YamlValues.GetString(found, SheetModel.KindKey), link.Kind, StringComparison.OrdinalIgnoreCase) ? found : null;
    }
}
