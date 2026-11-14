using Codex.Core;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>What the link and members widgets show (docs/CREW_LINKS_DESIGN.md), worked out from the actors the viewer may see. Pure, so it is
/// tested without a page. An actor the viewer cannot see is never in the lists, so its name cannot leak through a widget.</summary>
public static class LinkWidgets
{
    /// <summary>One actor a widget names: its id and the name the viewer may read.</summary>
    public sealed record Choice(string Id, string Name);

    /// <summary>The actors the link's select offers: those of the campaign the viewer can see whose sheet <c>kind</c> is the link's kind, other than
    /// the sheet's own actor, by name. An actor that is already the link's value is offered even if retired or not of the kind, so what is stored shows.</summary>
    public static IReadOnlyList<Choice> Options(IEnumerable<ActorDocument> visible, SheetLinkDefinition link, string? selfId, string? current, bool viewerIsDm) =>
        visible
            .Where(a => a.Id != selfId && (a.Id == current || (!a.Retired && IsKind(a, link.Kind))))
            .Select(a => new Choice(a.Id, a.NameFor(viewerIsDm)))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The actor the link now names, as the viewer may see it; null when it names none or the viewer cannot see it (<see cref="Stored"/> says which).</summary>
    public static Choice? Chosen(IEnumerable<ActorDocument> visible, string? current, bool viewerIsDm) =>
        string.IsNullOrEmpty(current) ? null
            : visible.FirstOrDefault(a => a.Id == current) is { } found ? new Choice(found.Id, found.NameFor(viewerIsDm)) : null;

    /// <summary>The id the sheet stores for the link, or null when it stores none. A save keeps it whether or not the viewer can see that actor.</summary>
    public static string? Stored(IReadOnlyDictionary<string, object> sheet, string linkKey) => SheetLinks.ActorId(sheet, linkKey);

    /// <summary>The actors whose sheets link to <paramref name="selfId"/> (a link of their kind whose kind is <paramref name="selfKind"/>), among those the viewer can see.</summary>
    public static IReadOnlyList<Choice> Members(IEnumerable<ActorDocument> visible, SystemDefinition system, string selfKind, string selfId, bool viewerIsDm) =>
        visible
            .Where(a => a.Id != selfId && !a.Retired && LinksTo(system, a.Sheet, selfKind, selfId))
            .Select(a => new Choice(a.Id, a.NameFor(viewerIsDm)))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The sheet kind a sheet is, by the sheet's own <c>kind</c>.</summary>
    private static bool IsKind(ActorDocument actor, string kind) =>
        string.Equals(YamlValues.GetString(actor.Sheet, SheetModel.KindKey), kind, StringComparison.OrdinalIgnoreCase);

    private static bool LinksTo(SystemDefinition system, IReadOnlyDictionary<string, object> sheet, string targetKind, string targetId) =>
        SheetLayout.KindOf(system, sheet) is { } kind
        && kind.Links.Any(l => string.Equals(l.Kind, targetKind, StringComparison.OrdinalIgnoreCase) && SheetLinks.ActorId(sheet, l.Key) == targetId);
}
