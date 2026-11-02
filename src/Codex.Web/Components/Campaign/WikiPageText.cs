using Codex.Core;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Web.Components.Campaign;

/// <summary>How a wiki page is named on a card and on its own page: its kind's word and icon, its tags, and when the
/// reader last had something revealed on it.</summary>
public static class WikiPageText
{
    public static string KindLabel(Localizer l, WikiPageKind kind) => l[$"Campaign.Wiki.Kind.{kind}"];

    /// <summary>"Region › Area › Town": where a place sits, the place itself left out.</summary>
    public static string PlacePath((RegionDocument Region, LocationDocument Place) on) =>
        string.Join(" › ", PlaceTree.Ancestors(on.Region.Locations, on.Place).Select(p => p.Name).Prepend(on.Region.Name));

    public static string KindIcon(WikiPageKind kind) => kind switch
    {
        WikiPageKind.Person => "user",
        WikiPageKind.Place => "map-pin",
        WikiPageKind.Faction => "shield",
        WikiPageKind.Quest => "scroll",
        _ => "book-open"
    };

    /// <summary>A page's tags less the one that only repeats its kind: place pages and the world builder's pages are tagged
    /// "place", "faction" and so on, which the kind already says (and the kind filter already filters by).</summary>
    public static IEnumerable<string> ShownTags(Localizer l, WikiPageDocument page)
    {
        if (page.Kind == WikiPageKind.General) return page.Tags;
        var echoes = new[] { KindLabel(l, page.Kind), l[$"WorldBuilder.Tag.{KindTagKey(page.Kind)}"] };
        return page.Tags.Where(t => !echoes.Contains(t.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    private static string KindTagKey(WikiPageKind kind) => kind == WikiPageKind.Person ? "npc" : kind.ToString().ToLowerInvariant();

    /// <summary>When the newest of the blocks the reader may read on a page was revealed, if any says.</summary>
    public static DateTime? LatestReveal(WikiPageDocument page) =>
        SecretText.Parse(page.Body).Where(s => s.Audience is SecretAudience.Party or SecretAudience.Some).Max(s => s.RevealedAt);
}
