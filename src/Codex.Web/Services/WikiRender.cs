using Codex.Core;
using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>Wiki text as the viewer reads it: <c>[[Page]]</c> links to the pages they may open, and stored mentions of
/// characters, places and items named for them. Used wherever a page's text, or a place's, is shown.</summary>
public static class WikiRender
{
    /// <param name="canCreate">Links to missing pages offer to create them (the DM).</param>
    public static string ToHtml(string? text, IReadOnlyCollection<WikiPageDocument> pages, MentionDirectory mentions, string campaignId, bool canCreate)
    {
        var basePath = $"/campaigns/{campaignId}";
        return WikiText.ToHtml(text,
            title =>
            {
                var slug = WikiText.Slugify(title);
                var page = pages.FirstOrDefault(p => p.Slug == slug || string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
                return page == null ? null : $"{basePath}/wiki/{page.Slug}";
            },
            canCreate ? title => $"{basePath}/wiki/new?title={Uri.EscapeDataString(title)}" : null,
            (kind, id) => mentions.Html(kind, id, (k, i) => MentionLinks.Href(campaignId, k, i)));
    }

    /// <summary>A line of text for a card: mentions named, Markdown and links flattened, cut to <paramref name="max"/> characters.</summary>
    public static string Summary(string? text, MentionDirectory mentions, int max) =>
        TextFormatting.Summary(string.IsNullOrEmpty(text) ? text : mentions.Plain(text), max);
}
