using System.Net;
using Codex.Core;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>How the DM reads a text with secret blocks: the words on the blocks' labels, and on a link that creates a missing
/// page. A text rendered without one is rendered as a player reads it.</summary>
public sealed record DmView(string Dm, string Party, string Players, string CreatePage)
{
    /// <summary>The label of a block revealed to some characters, given their ids.</summary>
    public Func<IReadOnlyList<string>, string> Some { get; init; } = _ => Dm;

    /// <summary>When set, each block that can be revealed or hidden carries buttons that do it (see <see cref="SecretActions"/>).</summary>
    public SecretActions? Actions { get; init; }

    /// <param name="mentions">Names the characters a <c>:::for</c> block reveals to, as the reader knows them.</param>
    public static DmView From(Localizer l, MentionDirectory? mentions = null, bool actions = false) =>
        new(l["Common.Secret.Dm"], l["Common.Secret.Party"], l["Common.Secret.Players"], l["Campaign.Wiki.CreateThisPage"])
        {
            Some = ids => ids.Count == 0
                ? l["Common.Secret.NoOne"]
                : l["Common.Secret.Some", string.Join(", ", ids.Select(id => (mentions ?? MentionDirectory.Empty).Plain($"[[actor:{id}]]")))],
            Actions = actions ? new SecretActions(l["Common.Secret.Reveal"], l["Common.Secret.Hide"]) : null
        };
}

/// <summary>The words on the buttons a DM block carries in the reading view. The buttons are plain markup with
/// <c>data-secret-block</c> (the block's number) and <c>data-secret-to</c> (<c>party</c> or <c>dm</c>); the page listens for them.</summary>
public sealed record SecretActions(string Reveal, string Hide);

/// <summary>A text with <see cref="SecretText"/> blocks as HTML. For the DM each block is drawn as what it is (a DM secret, a
/// revealed secret, the players' version, a secret for some characters); for anyone else the text first goes through
/// <see cref="SecretText.ForPlayers(string?)"/>, so a page a repository has already redacted and one it has not come out the same.</summary>
public static class SecretRender
{
    /// <param name="render">Renders a run of ordinary text (Markdown, links, mentions) to HTML.</param>
    /// <param name="dm">The DM's labels; null renders the text a player reads.</param>
    /// <param name="newSince">For a player: blocks revealed after this are marked new. Null marks nothing.</param>
    public static string Html(string? text, Func<string, string> render, DmView? dm, DateTime? newSince = null, string newLabel = "")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (dm == null)
        {
            return ForPlayer(text, render, newSince, newLabel);
        }

        if (!SecretText.HasMarkers(text))
        {
            return render(text);
        }

        var blocks = SecretText.Blocks(text).ToDictionary(b => b.Block);
        // A block cut in two by one nested in it gets its buttons once, on its first part.
        var labelled = new HashSet<int>();
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
                SecretAudience.Some => ("secret-some", "user", dm.Some(segment.Knowers)),
                _ => ("secret-dm", "eye-off", dm.Dm)
            };
            var first = labelled.Add(segment.Block);
            html.Append($"<div class=\"secret-block {css}\"{(first ? $" id=\"secret-{segment.Block}\"" : "")} data-block=\"{segment.Block}\">")
                .Append($"<div class=\"secret-label\">{Icon(icon)}<span>{WebUtility.HtmlEncode(label)}</span>");
            if (dm.Actions is { } actions && first && blocks.TryGetValue(segment.Block, out var block) && !block.UnderDm)
            {
                html.Append(block.Marked switch
                {
                    SecretAudience.Dm => Button(segment.Block, "party", "eye", actions.Reveal),
                    SecretAudience.Party => Button(segment.Block, "dm", "eye-off", actions.Hide),
                    SecretAudience.Some => Button(segment.Block, "party", "eye", actions.Reveal) + Button(segment.Block, "dm", "eye-off", actions.Hide),
                    _ => string.Empty
                });
            }

            html.Append("</div>").Append(render(segment.Text)).Append("</div>");
        }

        return html.ToString();
    }

    // What a player reads. Blocks revealed since newSince are drawn apart and marked new; the rest renders as one text, so
    // Markdown that runs across a removed block (a list) reads as it did before there were any.
    private static string ForPlayer(string text, Func<string, string> render, DateTime? newSince, string newLabel)
    {
        var segments = SecretText.Parse(text).Where(s => SecretText.CanRead(s, null)).ToList();
        if (newSince is not { } since || !segments.Any(s => IsNew(s, since)))
        {
            return render(SecretText.ForPlayers(text));
        }

        var html = new System.Text.StringBuilder();
        var run = new List<string>();
        foreach (var segment in segments)
        {
            if (!IsNew(segment, since))
            {
                run.Add(segment.Text);
                continue;
            }

            Flush();
            html.Append("<div class=\"secret-new\">")
                .Append($"<div class=\"secret-label\">{Icon("sparkles")}<span>{WebUtility.HtmlEncode(newLabel)}</span></div>")
                .Append(render(segment.Text))
                .Append("</div>");
        }

        Flush();
        return html.ToString();

        void Flush()
        {
            var joined = string.Join("\n\n", run.Select(t => t.Trim('\n')).Where(t => t.Trim().Length > 0));
            if (joined.Length > 0) html.Append(render(joined));
            run.Clear();
        }
    }

    private static bool IsNew(SecretSegment segment, DateTime since) =>
        segment.Audience is SecretAudience.Party or SecretAudience.Some && segment.RevealedAt > since && segment.Text.Trim().Length > 0;

    private static string Button(int block, string to, string icon, string label) =>
        $"<button type=\"button\" class=\"secret-action\" data-secret-block=\"{block}\" data-secret-to=\"{to}\" data-testid=\"secret-{to}-{block}\">{Icon(icon)}<span>{WebUtility.HtmlEncode(label)}</span></button>";

    // The Lucide glyphs the labels use, inline: this markup is a string, not a component tree.
    private static string Icon(string name) => name switch
    {
        "eye" => Svg("<path d=\"M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>"),
        "users" => Svg("<path d=\"M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2\"/><circle cx=\"9\" cy=\"7\" r=\"4\"/><path d=\"M22 21v-2a4 4 0 0 0-3-3.87\"/><path d=\"M16 3.13a4 4 0 0 1 0 7.75\"/>"),
        "user" => Svg("<path d=\"M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2\"/><circle cx=\"12\" cy=\"7\" r=\"4\"/>"),
        "sparkles" => Svg("<path d=\"M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .963 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.963 0z\"/>"),
        _ => Svg("<path d=\"M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49\"/><path d=\"M14.084 14.158a3 3 0 0 1-4.242-4.242\"/><path d=\"M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143\"/><path d=\"m2 2 20 20\"/>")
    };

    private static string Svg(string body) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">{body}</svg>";
}
