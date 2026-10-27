using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core;

/// <summary>
/// Renders the small Markdown subset rules text and written histories actually use - paragraphs, headings, <c>**bold**</c>,
/// <c>*italic*</c> and <c>_italic_</c>, <c>`code`</c>, bullet and numbered lists, <c>&gt;</c> quotes, rules, fenced code,
/// <c>[text](https://…)</c> links, pipe tables - into HTML. Everything is HTML-encoded first, so content from a pack or a
/// homebrew author can never inject markup.
/// </summary>
public static class TextFormatting
{
    private static readonly Regex Rule = new(@"^(?:-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);
    private static readonly Regex OrderedMark = new(@"^\s*\d{1,3}[.)]\s+", RegexOptions.Compiled);
    private static readonly Regex QuoteMark = new(@"^>\s?", RegexOptions.Compiled);
    private static readonly Regex CodeSpan = new(@"`([^`\n]+?)`", RegexOptions.Compiled);
    // Applied to text that is already HTML-encoded, so the address cannot break out of its attribute; web addresses only.
    private static readonly Regex MdLink = new(@"\[([^\[\]\n]+?)\]\((https?://[^\s()]+)\)", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<!\*)\*(?!\*)([^*\n]+?)\*(?!\*)", RegexOptions.Compiled);

    // _italic_ as the SRD writes trait names ("_Amphibious._"); not inside a word, so snake_case and file_names stay as written.
    private static readonly Regex BulletMark = new(@"^(?:[-*]|\d+\.)\s+", RegexOptions.Compiled);
    private static readonly Regex SummaryLink = new(@"\[\[([^\[\]|]+?)(?:\|([^\[\]]+?))?\]\]", RegexOptions.Compiled);
    private static readonly Regex UnderscoreItalic = new(@"(?<![\p{L}\p{N}_])_(?=\S)([^_\n]+?)(?<=\S)_(?![\p{L}\p{N}_])", RegexOptions.Compiled);

    public static string ToHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var html = new StringBuilder();
        var paragraph = new List<string>();
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];

            if (line.Length == 0)
            {
                Flush();
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith("|"))
            {
                Flush();
                var tableLines = new List<string>();
                while (i < lines.Count && lines[i].TrimStart().StartsWith("|"))
                {
                    tableLines.Add(lines[i].Trim());
                    i++;
                }

                html.Append(RenderTable(tableLines));
                continue;
            }

            if (line.TrimStart().StartsWith("```"))
            {
                Flush();
                i++;
                var code = new List<string>();
                while (i < lines.Count && !lines[i].TrimStart().StartsWith("```"))
                {
                    code.Add(lines[i]);
                    i++;
                }

                if (i < lines.Count) i++; // the closing fence; a block left open runs to the end
                html.Append("<pre><code>").Append(WebUtility.HtmlEncode(string.Join("\n", code))).Append("</code></pre>");
                continue;
            }

            if (Rule.IsMatch(line.Trim()))
            {
                Flush();
                html.Append("<hr>");
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith(">"))
            {
                Flush();
                var quoted = new List<string>();
                while (i < lines.Count && lines[i].TrimStart().StartsWith(">"))
                {
                    quoted.Add(QuoteMark.Replace(lines[i].TrimStart(), string.Empty).Trim());
                    i++;
                }

                // A blank quoted line starts a new paragraph inside the quote.
                var paragraphs = new List<List<string>> { new() };
                foreach (var quotedLine in quoted)
                {
                    if (quotedLine.Length == 0) paragraphs.Add(new List<string>());
                    else paragraphs[^1].Add(quotedLine);
                }

                html.Append("<blockquote>");
                foreach (var part in paragraphs.Where(x => x.Count > 0))
                {
                    html.Append("<p>").Append(string.Join("<br>", part.Select(Inline))).Append("</p>");
                }

                html.Append("</blockquote>");
                continue;
            }

            if (OrderedMark.IsMatch(line))
            {
                Flush();
                html.Append("<ol>");
                while (i < lines.Count && OrderedMark.IsMatch(lines[i]))
                {
                    html.Append("<li>").Append(Inline(OrderedMark.Replace(lines[i], string.Empty).Trim())).Append("</li>");
                    i++;
                }

                html.Append("</ol>");
                continue;
            }

            if (IsBullet(line))
            {
                Flush();
                html.Append("<ul>");
                while (i < lines.Count && IsBullet(lines[i]))
                {
                    html.Append("<li>").Append(Inline(lines[i].TrimStart().Substring(2))).Append("</li>");
                    i++;
                }

                html.Append("</ul>");
                continue;
            }

            if (line.StartsWith("#"))
            {
                Flush();
                var level = Math.Min(line.TakeWhile(c => c == '#').Count(), 4) + 2; // h3..h6 - the page owns h1/h2
                html.Append($"<h{level}>").Append(Inline(line.TrimStart('#').Trim())).Append($"</h{level}>");
                i++;
                continue;
            }

            paragraph.Add(line.Trim());
            i++;
        }

        Flush();
        return html.ToString();

        void Flush()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(string.Join("<br>", paragraph.Select(Inline))).Append("</p>");
            paragraph.Clear();
        }
    }

    private static bool IsBullet(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• ");
    }

    private static string RenderTable(List<string> rows)
    {
        var cells = rows
            .Where(r => !Regex.IsMatch(r, @"^\|?\s*:?-{2,}"))
            .Select(r => r.Trim('|').Split('|').Select(c => c.Trim()).ToList())
            .ToList();

        if (cells.Count == 0) return string.Empty;

        // The wrapper scrolls a wide table on its own instead of widening a phone screen.
        var sb = new StringBuilder("<div class=\"table-scroll\"><table class=\"rules-table\"><thead><tr>");
        foreach (var header in cells[0])
        {
            sb.Append("<th scope=\"col\">").Append(Inline(header)).Append("</th>");
        }

        sb.Append("</tr></thead><tbody>");
        foreach (var row in cells.Skip(1))
        {
            sb.Append("<tr>");
            foreach (var cell in row)
            {
                sb.Append("<td>").Append(Inline(cell)).Append("</td>");
            }

            sb.Append("</tr>");
        }

        return sb.Append("</tbody></table></div>").ToString();
    }

    /// <summary>HTML-encode, then apply code, links, bold and italic. Code goes first and is set aside, so what is written
    /// inside it (<c>*</c>, <c>_</c>) stays as written.</summary>
    public static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        var code = new List<string>();
        encoded = CodeSpan.Replace(encoded, m =>
        {
            code.Add(m.Groups[1].Value);
            return $"\u0003{code.Count - 1}\u0004";
        });
        encoded = MdLink.Replace(encoded, "<a href=\"$2\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>");
        encoded = Bold.Replace(encoded, "<strong>$1</strong>");
        encoded = Italic.Replace(encoded, "<em>$1</em>");
        encoded = UnderscoreItalic.Replace(encoded, "<em>$1</em>");
        return code.Count == 0 ? encoded : CodePlaceholder.Replace(encoded, m => "<code>" + code[int.Parse(m.Groups[1].Value)] + "</code>");
    }

    private static readonly Regex CodePlaceholder = new("\u0003(\\d+)\u0004", RegexOptions.Compiled);

    /// <summary>First sentence or so, for list rows and cards, with markdown removed: headings (labels, not content) are left
    /// out unless nothing else is written, bullets and emphasis lose their markers, and a wiki link shows its text. With
    /// <paramref name="keepLinks"/> the links stay as written ([[p1]]), for a summary that is resolved later: flattened to "p1" they
    /// could no longer be told from words, and a link cut in half by the length limit is dropped whole.</summary>
    public static string Summary(string? text, int maxLength = 140, bool keepLinks = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var content = lines.Where(l => !l.StartsWith('#')).ToList();
        var flat = string.Join(' ', (content.Count > 0 ? content : lines).Select(l => BulletMark.Replace(l.TrimStart('#').Trim(), string.Empty)));
        if (!keepLinks) flat = SummaryLink.Replace(flat, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
        flat = Italic.Replace(Bold.Replace(flat, "$1"), "$1");
        flat = UnderscoreItalic.Replace(flat, "$1").Replace("**", string.Empty);
        flat = Regex.Replace(flat, @"\s+", " ").Trim();
        if (flat.Length <= maxLength) return flat;
        var cut = flat.LastIndexOf(' ', maxLength);
        var shortened = flat.Substring(0, cut > 40 ? cut : maxLength);
        if (keepLinks)
        {
            var open = shortened.LastIndexOf("[[", StringComparison.Ordinal);
            if (open >= 0 && shortened.IndexOf("]]", open, StringComparison.Ordinal) < 0) shortened = shortened[..open];
        }

        return shortened.TrimEnd('.', ',', ';', ' ') + "…";
    }
}
