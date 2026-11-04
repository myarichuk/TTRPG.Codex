using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

internal sealed partial class BookMarkdownRenderer
{
    private const int MaxInlineDepth = 12;

    // How far an opener looks for its closer. Bounded, so a text of nothing but openers costs a window each, not the whole text.
    private const int ScanWindow = 2_000;

    private static readonly Regex MentionTarget = new(@"^(?<kind>actor|place|item):(?<id>\S+)$", Flags, Timeout);
    private static readonly Regex ImageTarget = new(@"^(?:slot|asset):[A-Za-z0-9_-]{1,64}$", Flags, Timeout);

    private string Inline(string s, int depth = 0)
    {
        if (depth > MaxInlineDepth)
        {
            Warn("nesting-too-deep");
            return Enc(s);
        }

        var sb = new StringBuilder(s.Length + 16);
        // A delimiter that found no closer once will find none later in this text either (the later ones see less), so a run of
        // unmatched "*" costs one scan, not one per star.
        var noCloser = new HashSet<(char, int)>();
        var n = s.Length;
        var i = 0;
        while (i < n)
        {
            var c = s[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < n && s[i + 1] == '\n')
                    {
                        sb.Append("<br>");
                        i += 2;
                    }
                    else if (i + 1 < n && char.IsAscii(s[i + 1]) && (char.IsPunctuation(s[i + 1]) || char.IsSymbol(s[i + 1])))
                    {
                        Esc(sb, s[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append('\\');
                        i++;
                    }

                    break;

                case '\n':
                    var trailing = 0;
                    while (trailing < sb.Length && sb[sb.Length - 1 - trailing] == ' ')
                    {
                        trailing++;
                    }

                    sb.Length -= trailing;
                    sb.Append(trailing >= 2 ? "<br>" : "\n");
                    i++;
                    break;

                case '`':
                    i = CodeSpan(s, i, sb);
                    break;

                case '!' when i + 1 < n && s[i + 1] == '[' && TryBracketed(s, i + 1, out var altText, out var imageTarget, out var imageEnd):
                    Image(altText, imageTarget, sb);
                    i = imageEnd;
                    break;

                case '[':
                    i = Bracket(s, i, depth, sb);
                    break;

                case '{' when i + 1 < n && s[i + 1] == '{':
                    i = Span(s, i, depth, sb);
                    break;

                case '*':
                case '_':
                case '~':
                    i = Emphasis(s, i, depth, noCloser, sb);
                    break;

                default:
                    Esc(sb, c);
                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    private static int Run(string s, int at, char c)
    {
        var end = at;
        while (end < s.Length && s[end] == c)
        {
            end++;
        }

        return end - at;
    }

    private static int BacktickClose(string s, int from, int run)
    {
        for (var j = from; j < s.Length;)
        {
            if (s[j] != '`')
            {
                j++;
                continue;
            }

            var r = Run(s, j, '`');
            if (r == run)
            {
                return j;
            }

            j += r;
        }

        return -1;
    }

    private static int CodeSpan(string s, int at, StringBuilder sb)
    {
        var run = Run(s, at, '`');
        var close = BacktickClose(s, at + run, run);
        if (close < 0)
        {
            sb.Append('`', run);
            return at + run;
        }

        sb.Append("<code>").Append(Enc(s[(at + run)..close].Replace('\n', ' ').Trim())).Append("</code>");
        return close + run;
    }

    private int Emphasis(string s, int at, int depth, HashSet<(char, int)> noCloser, StringBuilder sb)
    {
        var c = s[at];
        var available = Run(s, at, c);
        var run = c == '~' ? (available >= 2 ? 2 : 0) : Math.Min(available, 3);
        var after = at + run;

        // "snake_case", "Привет_мир": an underscore inside a word is a character, not a delimiter. Letters are Unicode letters.
        var intraword = c == '_' && at > 0 && char.IsLetterOrDigit(s[at - 1]);
        if (run == 0 || intraword || after >= s.Length || char.IsWhiteSpace(s[after]) || noCloser.Contains((c, run)))
        {
            sb.Append(c, available);
            return at + available;
        }

        var close = Closer(s, after, c, run);
        if (close < 0)
        {
            noCloser.Add((c, run));
            sb.Append(c, available);
            return at + available;
        }

        var inner = Inline(s[after..close], depth + 1);
        sb.Append(c == '~' ? "<s>" + inner + "</s>" : run switch
        {
            1 => "<em>" + inner + "</em>",
            2 => "<strong>" + inner + "</strong>",
            _ => "<strong><em>" + inner + "</em></strong>"
        });
        return close + run;
    }

    private static int Closer(string s, int from, char c, int run)
    {
        for (var j = from; j < s.Length;)
        {
            var d = s[j];
            if (d == '\\')
            {
                j += 2;
            }
            else if (d == '`')
            {
                var r = Run(s, j, '`');
                var close = BacktickClose(s, j + r, r);
                j = close < 0 ? j + r : close + r;
            }
            else if (d == c)
            {
                var r = Run(s, j, c);
                if (r == run && j > from && !char.IsWhiteSpace(s[j - 1]) && (c != '_' || j + r >= s.Length || !char.IsLetterOrDigit(s[j + r])))
                {
                    return j;
                }

                j += r;
            }
            else
            {
                j++;
            }
        }

        return -1;
    }

    // "[text](target)" at s[open] == '['. The text may hold balanced brackets; the target ends at its balanced ")" on the same line.
    private static bool TryBracketed(string s, int open, out string text, out string target, out int end)
    {
        text = target = string.Empty;
        end = open;
        var depth = 0;
        var close = -1;
        for (var j = open; j < Math.Min(s.Length, open + ScanWindow); j++)
        {
            if (s[j] == '\\')
            {
                j++;
            }
            else if (s[j] == '[')
            {
                depth++;
            }
            else if (s[j] == ']' && --depth == 0)
            {
                close = j;
                break;
            }
        }

        if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(')
        {
            return false;
        }

        var parens = 0;
        for (var j = close + 1; j < Math.Min(s.Length, close + ScanWindow) && s[j] != '\n'; j++)
        {
            if (s[j] == '(')
            {
                parens++;
            }
            else if (s[j] == ')' && --parens == 0)
            {
                text = s[(open + 1)..close];
                target = s[(close + 2)..j].Trim();
                end = j + 1;
                return true;
            }
        }

        return false;
    }

    private void Image(string alt, string rawTarget, StringBuilder sb)
    {
        // Only the first word is the target; the rest would be a title, which a book has no use for.
        var target = rawTarget.Split(' ', 2)[0];
        if (!ImageTarget.IsMatch(target))
        {
            Warn("image-dropped");
            sb.Append(Resolve(Enc(alt), asHtml: false));
            return;
        }

        var id = target[(target.IndexOf(':') + 1)..];
        var src = options.Image?.Invoke(id);
        if (src != null && src.StartsWith("data:image/", StringComparison.Ordinal) && !src.Any(ch => ch is '"' or '<' or '>' or '\'' || char.IsWhiteSpace(ch)))
        {
            sb.Append("<img class=\"art\" src=\"").Append(src).Append("\" alt=\"").Append(Resolve(Enc(alt), asHtml: false)).Append("\">");
            return;
        }

        sb.Append("<span class=\"slot\" data-slot=\"").Append(Enc(id)).Append("\"><span class=\"slot-caption\">").Append(Resolve(Enc(alt), asHtml: false)).Append("</span></span>");
    }

    private int Bracket(string s, int at, int depth, StringBuilder sb)
    {
        if (at + 1 < s.Length && s[at + 1] == '[')
        {
            var close = s.IndexOf("]]", at + 2, Math.Max(0, Math.Min(s.Length - at - 2, 220)), StringComparison.Ordinal);
            if (close > at + 2 && close - at < 220)
            {
                var inner = s[(at + 2)..close];
                if (!inner.Contains('[') && !inner.Contains('\n'))
                {
                    Link(inner, sb);
                    return close + 2;
                }
            }
        }
        else if (TryBracketed(s, at, out var text, out _, out var end))
        {
            // The words stay, the address does not: a book prints, and an address in it is a way to carry something out.
            Warn("link-dropped");
            sb.Append(Inline(text, depth + 1));
            return end;
        }

        sb.Append('[');
        return at + 1;
    }

    // [[Page]], [[Page|shown]], and stored mentions [[actor:id]], [[actor:id|Name]].
    private void Link(string inner, StringBuilder sb)
    {
        var pipe = inner.IndexOf('|');
        var target = (pipe < 0 ? inner : inner[..pipe]).Trim();
        var label = pipe < 0 ? null : inner[(pipe + 1)..].Trim();
        if (MentionTarget.Match(target) is { Success: true } mention)
        {
            // The label is never read: it is typed text, and the reader is not told a name it may not know.
            var kind = mention.Groups["kind"].Value;
            var name = options.Mention?.Invoke(kind, mention.Groups["id"].Value);
            if (string.IsNullOrWhiteSpace(name))
            {
                Warn("mention-hidden");
                return;
            }

            sb.Append("<span class=\"mention mention-").Append(kind).Append("\">").Append(Enc(name.Trim())).Append("</span>");
            return;
        }

        var shown = string.IsNullOrEmpty(label) ? target : label;
        if (shown.Length > 0)
        {
            sb.Append("<span class=\"wikilink\"><bdi>").Append(Enc(shown)).Append("</bdi></span>");
        }
    }

    // {{classes,#id,prop:value text}}
    private int Span(string s, int at, int depth, StringBuilder sb)
    {
        var level = 1;
        var close = -1;
        for (var j = at + 2; j < Math.Min(s.Length - 1, at + ScanWindow); j++)
        {
            if (s[j] == '{' && s[j + 1] == '{')
            {
                level++;
                j++;
            }
            else if (s[j] == '}' && s[j + 1] == '}' && --level == 0)
            {
                close = j;
                break;
            }
        }

        if (close < 0)
        {
            sb.Append('{');
            return at + 1;
        }

        var inner = s[(at + 2)..close];
        var space = inner.IndexOfAny([' ', '\n']);
        var attrs = space < 0 ? inner : inner[..space];
        var content = space < 0 ? string.Empty : inner[(space + 1)..].Trim();
        sb.Append("<span").Append(Attributes(attrs)).Append('>').Append(Inline(content, depth + 1)).Append("</span>");
        return close + 2;
    }
}
