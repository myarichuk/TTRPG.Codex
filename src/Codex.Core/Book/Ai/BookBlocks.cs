using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>A piece of a chapter that code built and a model may only place: a boxed secret now, a stat block or a table when there are
/// any. The model sees <c>{{block:N}}</c> and a label, never the content, so nothing in it passes through prose.</summary>
public sealed record BookBlock(int Number, string Label, string Html);

/// <summary>One chapter ready to hand to a model: text with every name written out and every secret and block replaced by a marker.</summary>
public sealed record BookWorkText(string Text, IReadOnlyList<BookBlock> Blocks);

public static class BookWork
{
    private static readonly Regex Mention = new(@"\[\[(?<kind>actor|place|item):(?<id>[^\]\s|]+)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Prepares a chapter's Markdown for a model. A stored mention becomes the name its audience may be told (or nothing), so the
    /// model sees names in clear text, can inflect them, and is never handed an id. Every secret block (<c>:::dm</c>, <c>:::for</c>) is
    /// cut out and replaced by <c>{{block:N}}</c> on a line of its own: its words are not sent to the model at all, and come back as the
    /// same box the deterministic book prints. <paramref name="render"/> is the renderer's options for that box (names resolved,
    /// <see cref="BookMarkdownOptions.BoxSecrets"/> on).
    /// </summary>
    public static BookWorkText Prepare(string markdown, Func<string, string, string?>? mention, BookMarkdownOptions render)
    {
        var blocks = new List<BookBlock>();
        var text = new StringBuilder();
        foreach (var segment in SecretText.Parse(markdown.ReplaceLineEndings("\n")))
        {
            if (segment.Audience is SecretAudience.Dm or SecretAudience.Some)
            {
                var marker = segment.Audience == SecretAudience.Dm
                    ? ":::dm"
                    : ":::for " + string.Join(" ", segment.Knowers.Select(k => "[[actor:" + k + "]]"));
                var box = BookMarkdown.Render(marker + "\n" + segment.Text.TrimEnd('\n') + "\n:::", new BookMarkdownOptions
                {
                    BoxSecrets = true, Mention = render.Mention, Image = render.Image
                });
                var number = blocks.Count + 1;
                blocks.Add(new BookBlock(number, segment.Audience == SecretAudience.Dm ? "GM secret" : "secret for some players", box.Html));
                text.Append("\n{{block:").Append(number).Append("}}\n\n");
                continue;
            }

            text.Append(segment.Text);
            if (!segment.Text.EndsWith('\n')) text.Append('\n');
        }

        var named = Mention.Replace(text.ToString(), m => mention?.Invoke(m.Groups["kind"].Value, m.Groups["id"].Value) is { Length: > 0 } name ? EscapeName(name) : string.Empty);
        return new BookWorkText(named.Trim(), blocks);
    }

    private static string EscapeName(string name) => name.ReplaceLineEndings(" ").Replace("[", "(").Replace("]", ")").Replace("{", "(").Replace("}", ")").Replace(":::", " ").Trim();
}
