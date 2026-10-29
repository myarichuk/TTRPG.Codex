using Codex.Core;
using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>Wiki text as the viewer reads it: <c>[[Page]]</c> links to the pages they may open, stored mentions of
/// characters, places and items named for them, and secret blocks drawn for the DM or left out for everyone else.
/// Used wherever a page's text, or a place's, is shown.</summary>
public static class WikiRender
{
    /// <param name="canCreate">Links to missing pages offer to create them (the DM).</param>
    /// <param name="dm">The DM's view of the secret blocks; null renders what a player reads. For a player a link to a page
    /// they cannot open is plain text: styled as missing it would tell them a secret page is there.</param>
    public static string ToHtml(string? text, IReadOnlyCollection<WikiPageDocument> pages, MentionDirectory mentions, string campaignId, bool canCreate, DmView? dm = null)
    {
        var basePath = $"/campaigns/{campaignId}";
        return SecretRender.Html(text, run => WikiText.ToHtml(run,
            title =>
            {
                var slug = WikiText.Slugify(title);
                var page = pages.FirstOrDefault(p => p.Slug == slug || string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
                return page == null ? null : $"{basePath}/wiki/{page.Slug}";
            },
            canCreate && dm != null ? title => $"{basePath}/wiki/new?title={Uri.EscapeDataString(title)}" : null,
            (kind, id) => mentions.Html(kind, id, (k, i) => MentionLinks.Href(campaignId, k, i)),
            missingAsText: dm == null,
            createTitle: dm?.CreatePage), dm);
    }

    /// <summary>A line of text for a card: mentions named, Markdown, links and secret markers flattened, cut to <paramref name="max"/>
    /// characters. The DM's (<paramref name="forDm"/>) reads all of it; anyone else's leaves the secret blocks out.</summary>
    public static string Summary(string? text, MentionDirectory mentions, int max, bool forDm = false) =>
        TextFormatting.Summary(string.IsNullOrEmpty(text) ? text : mentions.Plain(forDm ? SecretText.WithoutMarkers(text) : SecretText.ForPlayers(text)), max);
}
