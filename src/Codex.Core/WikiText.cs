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

    /// <summary>The link targets (titles, untrimmed duplicates removed) a body mentions.</summary>
    public static IReadOnlyList<string> ExtractLinks(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return Array.Empty<string>();
        }

        return Link.Matches(body)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
    /// </summary>
    public static string ToHtml(string? body, Func<string, string?> resolve, Func<string, string?>? createHref = null)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var links = new List<(string Title, string Label)>();
        var withTokens = Link.Replace(body, m =>
        {
            var title = m.Groups[1].Value.Trim();
            var label = m.Groups[2].Success ? m.Groups[2].Value.Trim() : title;
            links.Add((title, label));
            return $"\u0001{links.Count - 1}\u0002";
        });

        var html = TextFormatting.ToHtml(withTokens);
        for (var i = 0; i < links.Count; i++)
        {
            var (title, label) = links[i];
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
