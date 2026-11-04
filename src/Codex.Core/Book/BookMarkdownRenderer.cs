using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>The block half of <see cref="BookMarkdown"/>: lines to elements. The inline half is in the other part of the class.</summary>
internal sealed partial class BookMarkdownRenderer(BookMarkdownOptions options)
{
    private const int MaxDepth = 8;
    private const int MaxParagraph = 50_000;
    private const int MaxWarnings = 50;
    private const int MaxHeading = 2_000;
    private const int MaxCell = 2_000;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Flags = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex Heading = new(@"^ {0,3}(?<hashes>#{1,6})[ ]+(?<text>.*?)(?:[ ]+#+)?[ ]*$", Flags, Timeout);
    private static readonly Regex Rule = new(@"^ {0,3}(?:-{3,}|\*{3,}|_{3,})[ ]*$", Flags, Timeout);
    private static readonly Regex Quote = new(@"^ {0,3}>[ ]?", Flags, Timeout);
    private static readonly Regex Fence = new(@"^ {0,3}`{3,}", Flags, Timeout);
    private static readonly Regex ListMarker = new(@"^(?<indent> *)(?<m>[-*+]|\d{1,3}[.)])[ ]+(?<rest>.*)$", Flags, Timeout);
    private static readonly Regex Mentions = new(@"\[\[\s*(?<kind>actor|place|item)\s*:(?<rest>.{0,400}?)\]\]", Flags | RegexOptions.IgnoreCase | RegexOptions.Singleline, Timeout);
    private static readonly Regex OpenMention = new(@"\[\[\s*(?:actor|place|item)\s*:[^\n]*", Flags | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex Sentinel = new("\u0001(\\d{1,6})\u0002", Flags, Timeout);
    private static readonly Regex Placeholder = new(@"^\{\{block:(?<n>\d{1,4})\}\}$", Flags, Timeout);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$", Flags, Timeout);
    private static readonly Regex FirstParagraph = new(@"^<p dir=""auto"">((?:(?!</p>).)*)</p>", Flags | RegexOptions.Singleline, Timeout);
    private static readonly Regex Tags = new(@"<[^>]+>", Flags, Timeout);

    private static readonly Regex ClassName = new(@"^[A-Za-z][A-Za-z0-9_-]{0,40}$", Flags, Timeout);
    private static readonly Regex SafeValue = new(@"^[A-Za-z0-9#%.,()+*/\- ]{1,80}$", Flags, Timeout);
    private static readonly Regex CssFunction = new(@"([A-Za-z-]+)\(", Flags, Timeout);

    // The properties a {{…}} may set. Nothing here positions an element out of its box, and values are checked for functions
    // below, so none of them can fetch a file.
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "color", "background-color", "background", "width", "min-width", "max-width",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "font-size", "font-weight", "font-style", "font-variant", "text-align", "text-indent", "text-transform", "text-decoration",
        "line-height", "letter-spacing", "vertical-align",
        "border", "border-top", "border-right", "border-bottom", "border-left", "border-color", "border-width", "border-style", "border-radius",
        "columns", "column-count", "column-gap", "float", "clear", "opacity"
    };

    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "rgb", "rgba", "hsl", "hsla", "calc", "min", "max", "clamp"
    };

    private readonly List<BookHeading> headings = [];
    private readonly ISet<string> usedIds = options.HeadingIds ?? new HashSet<string>(StringComparer.Ordinal);
    private readonly List<string> mentionNames = [];
    private readonly List<string> warnings = [];

    public IReadOnlyList<BookHeading> Headings => headings;
    public IReadOnlyList<string> Warnings => warnings;

    public string Document(string text)
    {
        if (text.Length > options.MaxLength)
        {
            Warn("text-truncated");
            text = text[..options.MaxLength];
        }

        var sb = new StringBuilder();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length > 0)
            {
                sb.Append(Blocks(DropMarkerLines(Mentions_(plain.ToString())).Split('\n'), 0));
                plain.Clear();
            }
        }

        text = Normalize(text);
        if (!SecretText.HasMarkers(text))
        {
            plain.Append(text);
        }
        else
        {
            // Each segment is rendered on its own: a secret's lines cannot close their own box (a stray "}}"), and what follows a
            // box cannot end up inside it. Without BoxSecrets what a stranger may not read is dropped even if the source kept it.
            foreach (var segment in SecretText.Parse(text))
            {
                if (segment.Audience is SecretAudience.Dm or SecretAudience.Some)
                {
                    if (!options.BoxSecrets)
                    {
                        Warn("secret-dropped");
                        continue;
                    }

                    Flush();
                    sb.Append("<div class=\"secret ").Append(segment.Audience == SecretAudience.Dm ? "secret-dm" : "secret-for").Append("\">")
                        .Append(Blocks(DropMarkerLines(Mentions_(segment.Text)).Split('\n'), 0)).Append("</div>");
                    continue;
                }

                plain.Append(segment.Text).Append('\n');
            }
        }

        Flush();
        return Resolve(sb.ToString(), asHtml: true);
    }

    /// <summary>The text shown as it is, escaped, when the renderer gave up on it.</summary>
    public string Fallback(string text)
    {
        Warn("render-failed");
        return "<p dir=\"auto\">" + Resolve(Enc(DropMarkerLines(Mentions_(Normalize(text)))).Replace("\n", "<br>"), asHtml: false) + "</p>";
    }

    // Every stored mention is settled here, once, over the whole text, before any parser sees it: the name the reader may have, or
    // nothing. Whatever path a text then takes (an image's alt, a code span, a paragraph too long to parse, a nesting cap), the
    // typed |label of a mention cannot be in it. A resolved name travels as a sentinel and is put back as markup at the end.
    private string Mentions_(string text)
    {
        text = Mentions.Replace(text, m =>
        {
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            var rest = m.Groups["rest"].Value;
            var pipe = rest.IndexOf('|');
            var id = (pipe < 0 ? rest : rest[..pipe]).Trim();
            var name = id.Length == 0 || id.Any(char.IsWhiteSpace) ? null : options.Mention?.Invoke(kind, id);
            if (string.IsNullOrWhiteSpace(name))
            {
                Warn("mention-hidden");
                return string.Empty;
            }

            mentionNames.Add(kind + "\u0000" + name.Trim());
            return $"\u0001{mentionNames.Count - 1}\u0002";
        });

        // A mention that never closes is cut to the end of its line, which is more than it needs and cannot leak.
        return OpenMention.Replace(text, _ =>
        {
            Warn("mention-hidden");
            return string.Empty;
        });
    }

    // Sentinels back to the name: as a span in markup, as plain escaped text where markup cannot go (an attribute, a heading's id).
    private string Resolve(string html, bool asHtml) => Sentinel.Replace(html, m =>
    {
        var entry = mentionNames[int.Parse(m.Groups[1].Value)];
        var split = entry.IndexOf('\u0000');
        var name = Enc(entry[(split + 1)..]);
        return asHtml ? $"<span class=\"mention mention-{entry[..split]}\"><bdi>{name}</bdi></span>" : name;
    });

    // A line that is a secret-block marker after a quote mark or an invisible character is not one the shared grammar knows, so it
    // would print as text; it is cut instead, which is what the reader's own check does to a marker it finds.
    private string DropMarkerLines(string text)
    {
        if (!text.Contains(":::", StringComparison.Ordinal))
        {
            return text;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (SecretText.IsMarkerLine(lines[i].TrimStart(' ', '>')))
            {
                lines[i] = string.Empty;
                Warn("marker-dropped");
            }
        }

        return string.Join('\n', lines);
    }

    // {{wide,monster : attributes only, no whitespace outside a quoted value, and no braces. A scanner, not a regex: a nested
    // quantifier here backtracked exponentially on "{{aaaa…a'" and one player's note took the whole build down.
    private static bool IsBlockOpen(string t, out string attrs)
    {
        attrs = string.Empty;
        if (t.Length < 3 || t[0] != '{' || t[1] != '{')
        {
            return false;
        }

        for (var i = 2; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '"')
            {
                if (t[i - 1] is not (':' or '='))
                {
                    return false;
                }

                var close = t.IndexOf('"', i + 1);
                if (close < 0 || t.AsSpan(i + 1, close - i - 1).ContainsAnyExcept(QuotedValue))
                {
                    return false;
                }

                i = close;
            }
            else if (char.IsWhiteSpace(c) || c is '\'' or '{' or '}')
            {
                return false;
            }
        }

        attrs = t[2..];
        return true;
    }

    private static readonly System.Buffers.SearchValues<char> QuotedValue =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_,-()#%. +*/");

    private void Warn(string code)
    {
        if (warnings.Count < MaxWarnings && !warnings.Contains(code))
        {
            warnings.Add(code);
        }
    }

    // Line endings to \n, tabs to spaces (list nesting counts columns), control characters out.
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    sb.Append('\n');
                    break;
                case LineSeparator:
                case ParagraphSeparator:
                    sb.Append('\n');
                    break;
                case '\t':
                    sb.Append("    ");
                    break;
                // Bidi overrides and embeddings reorder what a reader sees; marks (LRM, RLM, ALM) and isolates are kept for
                // mixed text. Zero-width space and the BOM would hide a marker from the check that looks for one.
                case >= '\u202A' and <= '\u202E':
                case >= '\u2066' and <= '\u2069':
                case '\u200B':
                case '\u2060':
                case '\uFEFF':
                    break;
                default:
                    if (c == '\n' || !char.IsControl(c))
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private string Blocks(IReadOnlyList<string> lines, int depth)
    {
        if (depth > MaxDepth)
        {
            Warn("nesting-too-deep");
            return "<p dir=\"auto\">" + Enc(string.Join(" ", lines).Trim()) + "</p>";
        }

        var sb = new StringBuilder();
        var n = lines.Count;
        var i = 0;
        while (i < n)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed == "\\page")
            {
                sb.Append("<div class=\"page-break\"></div>");
                i++;
            }
            else if (trimmed == "\\column")
            {
                sb.Append("<div class=\"column-break\"></div>");
                i++;
            }
            else if (Fence.IsMatch(line))
            {
                i = FencedCode(lines, i, sb);
            }
            else if (Heading.Match(line) is { Success: true } heading)
            {
                sb.Append(HeadingElement(heading.Groups["hashes"].Value.Length, heading.Groups["text"].Value));
                i++;
            }
            else if (Rule.IsMatch(line))
            {
                sb.Append("<hr>");
                i++;
            }
            else if (Quote.IsMatch(line))
            {
                var inner = new List<string>();
                while (i < n && Quote.IsMatch(lines[i]))
                {
                    inner.Add(Quote.Replace(lines[i], string.Empty, 1));
                    i++;
                }

                sb.Append("<blockquote>").Append(Blocks(inner, depth + 1)).Append("</blockquote>");
            }
            else if (Placeholder.Match(trimmed) is { Success: true } placeholder)
            {
                if (options.Blocks != null && options.Blocks.TryGetValue(int.Parse(placeholder.Groups["n"].Value), out var html))
                {
                    sb.Append(html);
                }
                else
                {
                    Warn("block-missing");
                }

                i++;
            }
            else if (IsBlockOpen(trimmed, out var openAttrs))
            {
                i = BlockDiv(lines, i, openAttrs, depth, sb);
            }
            else if (IsTableStart(lines, i))
            {
                i = Table(lines, i, sb);
            }
            else if (ListMarker.IsMatch(line))
            {
                i = List(lines, i, depth, sb);
            }
            else
            {
                i = Paragraph(lines, i, sb);
            }
        }

        return sb.ToString();
    }

    private bool StartsBlock(IReadOnlyList<string> lines, int i, bool interruptingParagraph)
    {
        var line = lines[i];
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || Heading.IsMatch(line) || Rule.IsMatch(line) || Quote.IsMatch(line) || Fence.IsMatch(line)
            || trimmed is "\\page" or "\\column" || IsBlockOpen(trimmed, out _) || Placeholder.IsMatch(trimmed) || IsTableStart(lines, i))
        {
            return true;
        }

        if (ListMarker.Match(line) is not { Success: true } marker)
        {
            return false;
        }

        // A numbered line only breaks a paragraph when it starts a list ("1."), so a wrapped "…in the year\n1482. The…" stays prose.
        var mark = marker.Groups["m"].Value;
        return !char.IsDigit(mark[0]) || mark.TrimEnd('.', ')') == "1" || !interruptingParagraph;
    }

    private int Paragraph(IReadOnlyList<string> lines, int start, StringBuilder sb)
    {
        var parts = new List<string> { lines[start].TrimStart() };
        var i = start + 1;
        while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]) && !StartsBlock(lines, i, true))
        {
            parts.Add(lines[i].TrimStart());
            i++;
        }

        parts[^1] = parts[^1].TrimEnd();
        var text = string.Join("\n", parts);
        sb.Append("<p dir=\"auto\">");
        if (text.Length > MaxParagraph)
        {
            Warn("paragraph-too-long");
            sb.Append(Enc(text));
        }
        else
        {
            sb.Append(Inline(text));
        }

        sb.Append("</p>");
        return i;
    }

    private string HeadingElement(int level, string raw)
    {
        var html = raw.Length > MaxHeading ? Enc(raw.Trim()) : Inline(raw.Trim());
        var plain = WebUtility.HtmlDecode(Tags.Replace(Resolve(html, asHtml: false), string.Empty)).Trim();
        var id = WikiText.Slugify(plain);
        var unique = id;
        for (var k = 2; !usedIds.Add(unique); k++)
        {
            unique = id + "-" + k;
        }

        headings.Add(new BookHeading(level, plain, unique));
        return $"<h{level} id=\"{Enc(unique)}\" dir=\"auto\">{html}</h{level}>";
    }

    private int FencedCode(IReadOnlyList<string> lines, int start, StringBuilder sb)
    {
        var code = new List<string>();
        var i = start + 1;
        while (i < lines.Count && !Fence.IsMatch(lines[i]))
        {
            code.Add(lines[i]);
            i++;
        }

        sb.Append("<pre><code>").Append(Enc(string.Join("\n", code))).Append("</code></pre>");
        return Math.Min(i + 1, lines.Count);
    }

    // {{wide,monster  … }}: the opening line holds only the attributes, the block ends at its matching line "}}".
    private int BlockDiv(IReadOnlyList<string> lines, int start, string attrs, int depth, StringBuilder sb)
    {
        var level = 1;
        var i = start + 1;
        while (i < lines.Count)
        {
            var t = lines[i].Trim();
            if (IsBlockOpen(t, out _))
            {
                level++;
            }
            else if (t == "}}" && --level == 0)
            {
                break;
            }

            i++;
        }

        if (level != 0)
        {
            Warn("block-unclosed");
        }

        var inner = lines.Skip(start + 1).Take(i - start - 1).ToList();
        sb.Append("<div").Append(Attributes(attrs)).Append('>').Append(Blocks(inner, depth + 1)).Append("</div>");
        return Math.Min(i + 1, lines.Count);
    }

    private static bool IsTableStart(IReadOnlyList<string> lines, int i) =>
        i + 1 < lines.Count && lines[i].Contains('|') && lines[i + 1].Contains('-') && lines[i + 1].Contains('|') && TableSeparator.IsMatch(lines[i + 1])
        && Cells(lines[i]).Count == Cells(lines[i + 1]).Count;

    private static List<string> Cells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }

        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal))
        {
            text = text[..^1];
        }

        var cells = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                current.Append('|');
                i++;
            }
            else if (text[i] == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(text[i]);
            }
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    private string Cell(string text) => text.Length > MaxCell ? Enc(text) : Inline(text);

    private int Table(IReadOnlyList<string> lines, int start, StringBuilder sb)
    {
        var head = Cells(lines[start]);
        var align = Cells(lines[start + 1]).Select(c => (c.StartsWith(':'), c.EndsWith(':')) switch
        {
            (true, true) => " style=\"text-align:center\"",
            (false, true) => " style=\"text-align:right\"",
            (true, false) => " style=\"text-align:left\"",
            _ => string.Empty
        }).ToList();

        sb.Append("<table><thead><tr>");
        for (var c = 0; c < head.Count; c++)
        {
            sb.Append("<th dir=\"auto\"").Append(align[c]).Append('>').Append(Cell(head[c])).Append("</th>");
        }

        sb.Append("</tr></thead><tbody>");
        var i = start + 2;
        while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|'))
        {
            var row = Cells(lines[i]);
            sb.Append("<tr>");
            for (var c = 0; c < head.Count; c++)
            {
                sb.Append("<td dir=\"auto\"").Append(align[c]).Append('>').Append(c < row.Count ? Cell(row[c]) : string.Empty).Append("</td>");
            }

            sb.Append("</tr>");
            i++;
        }

        sb.Append("</tbody></table>");
        return i;
    }

    private static int Width(string line)
    {
        var w = 0;
        while (w < line.Length && line[w] == ' ')
        {
            w++;
        }

        return w;
    }

    private int List(IReadOnlyList<string> lines, int start, int depth, StringBuilder sb)
    {
        var first = ListMarker.Match(lines[start]);
        var baseIndent = first.Groups["indent"].Length;
        var ordered = char.IsDigit(first.Groups["m"].Value[0]);
        var items = new List<List<string>>();
        var loose = false;
        var i = start;

        bool SameList(int at, out Match m)
        {
            m = ListMarker.Match(lines[at]);
            return m.Success && m.Groups["indent"].Length == baseIndent && char.IsDigit(m.Groups["m"].Value[0]) == ordered && !Rule.IsMatch(lines[at]);
        }

        while (i < lines.Count && SameList(i, out var marker))
        {
            var content = new List<string> { marker.Groups["rest"].Value };
            var indented = new List<int>();
            i++;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    var k = i;
                    while (k < lines.Count && string.IsNullOrWhiteSpace(lines[k]))
                    {
                        k++;
                    }

                    if (k < lines.Count && Width(lines[k]) > baseIndent)
                    {
                        content.AddRange(Enumerable.Repeat(string.Empty, k - i));
                        loose = true;
                        i = k;
                        continue;
                    }

                    break;
                }

                if (Width(line) > baseIndent)
                {
                    indented.Add(content.Count);
                    content.Add(line);
                    i++;
                }
                else if (!StartsBlock(lines, i, true) && !ListMarker.IsMatch(line))
                {
                    content.Add(line.TrimStart());
                    i++;
                }
                else
                {
                    break;
                }
            }

            if (indented.Count > 0)
            {
                var strip = indented.Min(ix => Width(content[ix]));
                foreach (var ix in indented)
                {
                    content[ix] = content[ix][strip..];
                }
            }

            items.Add(content);

            var next = i;
            while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next]))
            {
                next++;
            }

            if (next < lines.Count && next > i && SameList(next, out _))
            {
                loose = true;
                i = next;
            }
        }

        var tag = ordered ? "ol" : "ul";
        var startNumber = ordered && int.TryParse(first.Groups["m"].Value.TrimEnd('.', ')'), out var number) && number != 1 ? $" start=\"{number}\"" : string.Empty;
        sb.Append('<').Append(tag).Append(" dir=\"auto\"").Append(startNumber).Append('>');
        foreach (var item in items)
        {
            var html = Blocks(item, depth + 1);
            if (!loose)
            {
                html = FirstParagraph.Replace(html, "$1", 1);
            }

            sb.Append("<li dir=\"auto\">").Append(html).Append("</li>");
        }

        sb.Append("</").Append(tag).Append('>');
        return i;
    }

    // "wide,monster,#npc-3,color:red,width:50%" -> ` class="wide monster" id="npc-3" style="color:red;width:50%"`.
    // Anything that is not a plain class name, an id or an allowed property with a harmless value is dropped.
    private string Attributes(string raw)
    {
        var classes = new List<string>();
        var styles = new List<string>();
        string? id = null;
        foreach (var token in SplitAttributes(raw))
        {
            if (token.StartsWith('#'))
            {
                if (id == null && ClassName.IsMatch(token[1..]))
                {
                    id = "u-" + token[1..];
                    continue;
                }
            }
            else if (token.Contains(':'))
            {
                var colon = token.IndexOf(':');
                var property = token[..colon].Trim().ToLowerInvariant();
                var value = token[(colon + 1)..].Trim().Trim('"').Trim();
                if (Properties.Contains(property) && IsSafeValue(property, value))
                {
                    styles.Add(property + ":" + value);
                    continue;
                }
            }
            else if (ClassName.IsMatch(token) && !token.StartsWith("secret", StringComparison.OrdinalIgnoreCase))
            {
                classes.Add(token);
                continue;
            }

            Warn("attribute-dropped");
        }

        var sb = new StringBuilder();
        if (classes.Count > 0)
        {
            sb.Append(" class=\"").Append(Enc(string.Join(' ', classes))).Append('"');
        }

        if (id != null)
        {
            sb.Append(" id=\"").Append(Enc(id)).Append('"');
        }

        if (styles.Count > 0)
        {
            sb.Append(" style=\"").Append(Enc(string.Join(';', styles))).Append('"');
        }

        return sb.ToString();
    }

    private static bool IsSafeValue(string property, string value)
    {
        // No comment (it would swallow the declarations after it), no arithmetic outside calc(), no four-digit number (a 9999999px
        // block is thousands of pages), and no negative margin (text laid over other text).
        if (!SafeValue.IsMatch(value) || value.Contains("/*") || value.Contains("*/") || Regex.IsMatch(value, @"\d{4,}")
            || (value.IndexOfAny(['+', '*', '/']) >= 0 && !value.Contains("calc(", StringComparison.OrdinalIgnoreCase))
            || (property.StartsWith("margin", StringComparison.Ordinal) && Regex.IsMatch(value, @"(^|[\s(,])-\d")))
        {
            return false;
        }

        // A function other than a colour or arithmetic one (url, image-set, element, …) is how CSS fetches something.
        return CssFunction.Matches(value).All(m => Functions.Contains(m.Groups[1].Value));
    }

    private static IEnumerable<string> SplitAttributes(string raw)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in raw)
        {
            if (c == '"')
            {
                quoted = !quoted;
                current.Append(c);
            }
            else if (c == ',' && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString().Trim();
                }

                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    internal static string Enc(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            Esc(sb, c);
        }

        return sb.ToString();
    }

    private static void Esc(StringBuilder sb, char c)
    {
        switch (c)
        {
            case '&': sb.Append("&amp;"); break;
            case '<': sb.Append("&lt;"); break;
            case '>': sb.Append("&gt;"); break;
            case '"': sb.Append("&quot;"); break;
            case '\'': sb.Append("&#39;"); break;
            default: sb.Append(c); break;
        }
    }
}
