using System.Net;
using Codex.Core;
using Codex.Localization;

namespace Codex.Web.Services;

/// <summary>How the DM reads a text with secret blocks: the words on the blocks' labels, and on a link that creates a missing
/// page. A text rendered without one is rendered as a player reads it.</summary>
public sealed record DmView(string Dm, string Party, string Players, string CreatePage)
{
    public static DmView From(Localizer l) =>
        new(l["Common.Secret.Dm"], l["Common.Secret.Party"], l["Common.Secret.Players"], l["Campaign.Wiki.CreateThisPage"]);
}

/// <summary>A text with <see cref="SecretText"/> blocks as HTML. For the DM each block is drawn as what it is (a DM secret, a
/// revealed secret, the players' version); for anyone else the text first goes through <see cref="SecretText.ForPlayers"/>,
/// so a page a repository has already redacted and one it has not come out the same.</summary>
public static class SecretRender
{
    /// <param name="render">Renders a run of ordinary text (Markdown, links, mentions) to HTML.</param>
    /// <param name="dm">The DM's labels; null renders the text a player reads.</param>
    public static string Html(string? text, Func<string, string> render, DmView? dm)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (dm == null)
        {
            return render(SecretText.ForPlayers(text));
        }

        if (!SecretText.HasMarkers(text))
        {
            return render(text);
        }

        var html = new System.Text.StringBuilder();
        foreach (var segment in SecretText.Parse(text))
        {
            if (segment.Text.Trim().Length == 0)
            {
                continue;
            }

            if (segment.Audience == SecretAudience.Everyone)
            {
                html.Append(render(segment.Text));
                continue;
            }

            var (css, icon, label) = segment.Audience switch
            {
                SecretAudience.Party => ("secret-party", "eye", dm.Party),
                SecretAudience.Players => ("secret-players", "users", dm.Players),
                _ => ("secret-dm", "eye-off", dm.Dm)
            };
            html.Append($"<div class=\"secret-block {css}\" data-block=\"{segment.Block}\">")
                .Append($"<div class=\"secret-label\">{Icon(icon)}<span>{WebUtility.HtmlEncode(label)}</span></div>")
                .Append(render(segment.Text))
                .Append("</div>");
        }

        return html.ToString();
    }

    // The two Lucide glyphs the labels use, inline: this markup is a string, not a component tree.
    private static string Icon(string name) => name switch
    {
        "eye" => Svg("<path d=\"M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>"),
        "users" => Svg("<path d=\"M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2\"/><circle cx=\"9\" cy=\"7\" r=\"4\"/><path d=\"M22 21v-2a4 4 0 0 0-3-3.87\"/><path d=\"M16 3.13a4 4 0 0 1 0 7.75\"/>"),
        _ => Svg("<path d=\"M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49\"/><path d=\"M14.084 14.158a3 3 0 0 1-4.242-4.242\"/><path d=\"M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143\"/><path d=\"m2 2 20 20\"/>")
    };

    private static string Svg(string body) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">{body}</svg>";
}
