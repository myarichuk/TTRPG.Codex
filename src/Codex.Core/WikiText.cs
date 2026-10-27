using System.Net;
using System.Text.RegularExpressions;

namespace Codex.Core;

/// <summary>
/// The <c>[[Page title]]</c> / <c>[[Page title|shown as]]</c> link syntax of the campaign wiki.
/// Rendering goes through <see cref="TextFormatting"/> so a page body gets the same bold, lists
/// and tables as rules text and can never inject markup; links are swapped in afterwards.
/// </summary>
public static class WikiText
{
    private static readonly Regex Link = new(@"\[\[([^\[\]|]+?)(?:\|([^\[\]]+?))?\]\]", RegexOptions.Compiled);

    // A stored mention ("[[actor:id]]", "[[place:id]]", "[[item:type/id]]"), which shares the brackets but is not a page link.
    private static readonly Regex MentionToken = new(@"^(actor|place|item):\S+$", RegexOptions.Compiled);

    private static bool IsMention(string target) => MentionToken.IsMatch(target.Trim());

    /// <summary>Whether a body holds a stored mention of a character, place or item.</summary>
    public static bool HasMentions(string? body) => !string.IsNullOrEmpty(body) && Link.Matches(body).Any(m => IsMention(m.Groups[1].Value));

    /// <summary>The link targets (titles, untrimmed duplicates removed) a body mentions.</summary>
    public static IReadOnlyList<string> ExtractLinks(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return Array.Empty<string>();
        }

        return Link.Matches(body)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(t => t.Length > 0 && !IsMention(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The body with each page link replaced by its label, stored mentions kept: for text that leaves the wiki (a
    /// character's history), where page links don't render.</summary>
    public static string WithoutPageLinks(string? body) =>
        string.IsNullOrEmpty(body)
            ? string.Empty
            : Link.Replace(body, m => IsMention(m.Groups[1].Value) && !m.Groups[2].Success
                ? m.Value
                : (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value).Trim());

    public static string Slugify(string title)
    {
        var chars = title.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "page" : slug;
    }

    /// <summary>
    /// Renders a page body to HTML. <paramref name="resolve"/> maps a link title to its href, or
    /// null when the page doesn't exist (or the reader may not see it); missing links render as
    /// a <c>wiki-missing</c> span, optionally with a create link when <paramref name="createHref"/> is given.
    /// <paramref name="mention"/> turns a stored mention (kind "actor", "place" or "item", and its id) into markup for
    /// the reader; without it a mention shows as plain text.
    /// </summary>
    public static string ToHtml(string? body, Func<string, string?> resolve, Func<string, string?>? createHref = null, Func<string, string, string>? mention = null)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var links = new List<(string Title, string Label)>();
        var withTokens = Link.Replace(body, m =>
        {
            var title = m.Groups[1].Value.Trim();
            if (IsMention(title) && !m.Groups[2].Success)
            {
                links.Add((title, string.Empty));
                return $"\u0001{links.Count - 1}\u0002";
            }

            var label = m.Groups[2].Success ? m.Groups[2].Value.Trim() : title;
            links.Add((title, label));
            return $"\u0001{links.Count - 1}\u0002";
        });

        var html = TextFormatting.ToHtml(withTokens);
        for (var i = 0; i < links.Count; i++)
        {
            var (title, label) = links[i];
            if (label.Length == 0 && IsMention(title))
            {
                var colon = title.IndexOf(':');
                html = html.Replace($"\u0001{i}\u0002", mention?.Invoke(title[..colon], title[(colon + 1)..]) ?? string.Empty);
                continue;
            }

            var encodedLabel = WebUtility.HtmlEncode(label);
            var href = resolve(title);
            string replacement;
            if (href != null)
            {
                replacement = $"<a class=\"wiki-link\" href=\"{WebUtility.HtmlEncode(href)}\">{encodedLabel}</a>";
            }
            else
            {
                var create = createHref?.Invoke(title);
                replacement = create != null
                    ? $"<a class=\"wiki-link wiki-missing\" href=\"{WebUtility.HtmlEncode(create)}\" title=\"Create this page\">{encodedLabel}</a>"
                    : $"<span class=\"wiki-missing\">{encodedLabel}</span>";
            }

            html = html.Replace($"\u0001{i}\u0002", replacement);
        }

        return html;
    }
}
