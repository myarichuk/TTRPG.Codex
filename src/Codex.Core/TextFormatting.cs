using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core;

/// <summary>
/// Renders the small Markdown subset rules text actually uses - paragraphs, <c>**bold**</c>,
/// <c>*italic*</c> and <c>_italic_</c>, <c>- </c> bullet lists, pipe tables - into HTML. Everything is HTML-encoded
/// first, so content from a pack or a homebrew author can never inject markup.
/// </summary>
public static class TextFormatting
{
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<!\*)\*(?!\*)([^*\n]+?)\*(?!\*)", RegexOptions.Compiled);

    // _italic_ as the SRD writes trait names ("_Amphibious._"); not inside a word, so snake_case and file_names stay as written.
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

    /// <summary>HTML-encode, then apply bold/italic.</summary>
    public static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = Bold.Replace(encoded, "<strong>$1</strong>");
        encoded = Italic.Replace(encoded, "<em>$1</em>");
        encoded = UnderscoreItalic.Replace(encoded, "<em>$1</em>");
        return encoded;
    }

    /// <summary>First sentence or so, for list rows and cards, with markdown markers removed.</summary>
    public static string Summary(string? text, int maxLength = 140)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var flat = text.Replace("**", string.Empty).Replace('\n', ' ').Trim();
        flat = Regex.Replace(flat, @"\s+", " ");
        if (flat.Length <= maxLength) return flat;
        var cut = flat.LastIndexOf(' ', maxLength);
        return flat.Substring(0, cut > 40 ? cut : maxLength).TrimEnd('.', ',', ';') + "…";
    }
}
