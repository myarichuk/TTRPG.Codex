using Codex.Core;

namespace Codex.Persistence;

/// <summary>
/// Which page a <c>[[Title]]</c> link points at. A link is stored as the slug of its text (<see cref="WikiPageDocument.Links"/>,
/// <see cref="FactLinks.Page"/>) and a page's slug never changes, so a renamed page is reached by its slug (links written to
/// its old name) or by its current title (links written to its new one). Rendering, backlinks, link counts and the lore about a
/// page all go through here, so a link that renders is also counted (R11).
/// </summary>
public static class WikiLinks
{
    /// <summary>Whether the stored link <paramref name="link"/> points at <paramref name="page"/>.</summary>
    public static bool Reaches(string link, WikiPageDocument page) =>
        link == page.Slug || link == WikiText.Slugify(page.Title);

    /// <summary>The page a stored link points at among <paramref name="pages"/>: the one with that slug first, else the one whose
    /// title it names; null when none does.</summary>
    public static WikiPageDocument? Target(string link, IEnumerable<WikiPageDocument> pages)
    {
        var list = pages as IReadOnlyCollection<WikiPageDocument> ?? pages.ToList();
        return list.FirstOrDefault(p => p.Slug == link) ?? list.FirstOrDefault(p => link == WikiText.Slugify(p.Title));
    }

    /// <summary>The page a <c>[[Title]]</c> link in some text points at.</summary>
    public static WikiPageDocument? TargetOfTitle(string title, IEnumerable<WikiPageDocument> pages) => Target(WikiText.Slugify(title), pages);

    /// <summary>The keys lore about <paramref name="page"/> may be stored under: a fact names it by its slug or its title.</summary>
    public static IEnumerable<string> FactKeys(WikiPageDocument page) =>
        new[] { FactLinks.Page(page.Slug), FactLinks.Page(WikiText.Slugify(page.Title)) }.Distinct(StringComparer.Ordinal);
}
