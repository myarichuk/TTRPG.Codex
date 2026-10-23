using System.Globalization;
using System.Text;

namespace Codex.Localization;

/// <summary>
/// A small ICU-style formatter: <c>{0}</c>, <c>{0:N0}</c> and
/// <c>{0, plural, one{# round} few{# rounds} other{# rounds}}</c> (<c>#</c> is the number).
/// <c>{{</c> and <c>}}</c> are literal braces. A missing "two"/"few"/"many" branch falls back to "other".
/// </summary>
public static class MessageFormatter
{
    public static string Format(string template, CultureInfo culture, params object?[] args)
    {
        var language = SupportedCultures.Normalize(culture.Name);
        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
            if (c != '{') { sb.Append(c); i++; continue; }

            var end = MatchingBrace(template, i);
            if (end < 0) { sb.Append(template, i, template.Length - i); break; }

            sb.Append(Placeholder(template.AsSpan(i + 1, end - i - 1), language, culture, args));
            i = end + 1;
        }
        return sb.ToString();
    }

    private static int MatchingBrace(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string Placeholder(ReadOnlySpan<char> body, string language, CultureInfo culture, object?[] args)
    {
        var text = body.ToString();
        var comma = text.IndexOf(',');
        var colon = text.IndexOf(':');
        var indexEnd = new[] { comma, colon }.Where(x => x >= 0).DefaultIfEmpty(text.Length).Min();

        if (!int.TryParse(text.AsSpan(0, indexEnd).Trim(), out var index) || index < 0 || index >= args.Length)
            return "{" + text + "}";

        var arg = args[index];

        if (comma >= 0 && (colon < 0 || comma < colon))
        {
            var rest = text[(comma + 1)..].TrimStart();
            if (rest.StartsWith("plural", StringComparison.Ordinal))
            {
                var branches = rest[(rest.IndexOf(',') + 1)..];
                return Plural(branches, arg, language, culture, args);
            }
        }

        var format = colon >= 0 ? text[(colon + 1)..] : null;
        return arg is IFormattable f ? f.ToString(format, culture) : arg?.ToString() ?? "";
    }

    private static string Plural(string branches, object? arg, string language, CultureInfo culture, object?[] args)
    {
        var number = arg switch
        {
            null => 0d,
            IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
            _ => 0d,
        };
        var category = PluralRules.Category(language, number);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < branches.Length)
        {
            while (i < branches.Length && char.IsWhiteSpace(branches[i])) i++;
            var open = branches.IndexOf('{', i);
            if (open < 0) break;
            var name = branches[i..open].Trim();
            var close = MatchingBrace(branches, open);
            if (close < 0) break;
            map[name] = branches[(open + 1)..close];
            i = close + 1;
        }

        // A language missing the exact category falls back to "other", then to whatever branch exists, so text never vanishes.
        if (!map.TryGetValue(category, out var chosen) && !map.TryGetValue("other", out chosen))
        {
            if (map.Count == 0) return "";
            chosen = map.Values.First();
        }

        // "#" is the number; it is replaced before the other arguments go in, so a "#" inside one of them is left alone.
        // {0}, {1}... inside a branch are the message's own arguments.
        var shown = number.ToString("N0", culture);
        return Format(chosen.Replace("#", shown, StringComparison.Ordinal), culture, args);
    }
}
