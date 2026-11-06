using System.Globalization;
using System.Text;

namespace Codex.Core.Book;

/// <summary>
/// The GM's own stylesheet, made safe to put in the book's page. The page already has no network and no script (its policy), so this is not
/// the only defence: it keeps the stylesheet from breaking out of its element, loading a face or a picture from anywhere, or growing without
/// bound, and it keeps everything else, escapes included, so CSS written for Homebrewery works as written.
/// <para>The stylesheet is read as the browser reads it (CSS Syntax Level 3 tokens: an escape is decoded before any name is looked at, so
/// <c>\75rl(</c> is <c>url(</c>), the tokens are checked, and the page gets only what was checked, written back out from the tokens: a token
/// with no escape as it was written, the rest re-escaped. A string is always re-escaped, so no <c>&lt;</c> is ever written: nothing can close
/// the style element.</para>
/// </summary>
public static class BookCss
{
    /// <summary>Room for a whole Homebrewery theme's stylesheet and a community design on top of it.</summary>
    public const int MaxLength = 512_000;

    // Functions that fetch what a string names (image("x.png"), image-set("x.png" 1x)) or run something: a whole call becomes "none".
    private static readonly HashSet<string> Banned = new(["expression", "src", "image", "image-set", "-webkit-image-set", "element", "-moz-element", "paint"],
        StringComparer.Ordinal);

    // Properties from old browsers that load code: the declaration is dropped.
    private static readonly HashSet<string> BannedProperties = new(["behavior", "-moz-binding"], StringComparer.Ordinal);

    // At-rules that fetch (or that make no sense in a page's second stylesheet): the rule is dropped.
    private static readonly HashSet<string> DroppedAtRules = new(["import", "namespace", "charset"], StringComparer.Ordinal);

    public static string Clean(string css, out IReadOnlyList<string> warnings)
    {
        var found = new List<string>();
        if (css.Length > MaxLength)
        {
            css = css[..MaxLength];
            found.Add("css-truncated");
        }

        var tokens = CssTokenizer.Tokenize(css);
        var output = new StringBuilder(css.Length + 64);
        var writer = new Writer(tokens, output, found);
        writer.Run();
        warnings = found.Distinct().ToList();
        return output.ToString();
    }

    private sealed class Writer(List<CssToken> tokens, StringBuilder sb, List<string> found)
    {
        private int i;
        private CssToken? last;
        private CssToken? lastSignificant;

        // Something was dropped since the last token written: the next is kept apart from the last, or the two could run together into what
        // was never written ("ur<l(" would become "url(").
        private bool cut;

        public void Run()
        {
            while (i < tokens.Count) Step(inFontFace: false);
        }

        private void Step(bool inFontFace)
        {
            var t = tokens[i];
            switch (t.Type)
            {
                case CssTokenType.AtKeyword when DroppedAtRules.Contains(t.Lower):
                    found.Add("css-at-rule-dropped");
                    SkipAtRule();
                    return;
                case CssTokenType.AtKeyword when t.Lower == "font-face":
                    FontFace();
                    return;
                case CssTokenType.Ident when BannedProperties.Contains(t.Lower) && IsProperty():
                    found.Add("css-rejected");
                    SkipDeclaration();
                    return;
                case CssTokenType.Function when t.Lower == "url":
                    UrlFunction(inFontFace);
                    return;
                case CssTokenType.Function when Banned.Contains(t.Lower):
                    found.Add("css-rejected");
                    SkipFunction();
                    Replace();
                    return;
                case CssTokenType.Url:
                    i++;
                    if (Allowed(t.Value, inFontFace)) Emit(t);
                    else
                    {
                        found.Add("css-url-dropped");
                        Replace();
                    }

                    return;
                case CssTokenType.BadUrl or CssTokenType.BadString or CssTokenType.Cdo or CssTokenType.Cdc:
                    i++;
                    cut = true;
                    return;
                case CssTokenType.Delim when t.Value is "<" or "\\":
                    i++;
                    cut = true;
                    return;
                default:
                    i++;
                    Emit(t);
                    return;
            }
        }

        private void Emit(CssToken t)
        {
            // One space where a comment or a dropped rule left two.
            if (t.Type == CssTokenType.Whitespace && last?.Type == CssTokenType.Whitespace) return;
            // A comment the browser saw between two tokens kept them apart ("a/**/b" is two words), as does anything dropped between them.
            if ((t.AfterComment || cut) && last != null && last.Type != CssTokenType.Whitespace && t.Type != CssTokenType.Whitespace) sb.Append("/**/");
            t.Write(sb);
            last = t;
            if (t.Type != CssTokenType.Whitespace) lastSignificant = t;
            cut = false;
        }

        // What was dropped from a value is "none", kept apart from what comes either side of it.
        private void Replace()
        {
            cut = true;
            Emit(CssToken.Synthetic(CssTokenType.Ident, "none"));
            cut = true;
        }

        // A name is a property where a declaration starts: after '{' or ';' (or at the start), with ':' next.
        private bool IsProperty()
        {
            if (lastSignificant != null && lastSignificant.Type is not (CssTokenType.LBrace or CssTokenType.Semicolon)) return false;
            var j = i + 1;
            while (j < tokens.Count && tokens[j].Type == CssTokenType.Whitespace) j++;
            return j < tokens.Count && tokens[j].Type == CssTokenType.Colon;
        }

        // url("…"): one string inside, checked. Anything else in it is not a URL the browser would load, but nothing is left to chance: dropped.
        private void UrlFunction(bool inFontFace)
        {
            var start = i;
            i++;
            var j = i;
            while (j < tokens.Count && tokens[j].Type == CssTokenType.Whitespace) j++;
            var str = j < tokens.Count && tokens[j].Type == CssTokenType.String ? tokens[j] : null;
            var k = str == null ? j : j + 1;
            while (k < tokens.Count && tokens[k].Type == CssTokenType.Whitespace) k++;
            var closed = k < tokens.Count && tokens[k].Type == CssTokenType.RParen;
            if (str != null && closed && Allowed(str.Value, inFontFace))
            {
                Emit(tokens[start]);
                Emit(str);
                Emit(tokens[k]);
                i = k + 1;
                return;
            }

            i = start;
            found.Add("css-url-dropped");
            SkipFunction();
            Replace();
        }

        private static bool Allowed(string url, bool inFontFace)
        {
            var u = url.Trim();
            if (u.StartsWith('#')) return true;
            if (u.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
            return inFontFace && (u.StartsWith("data:font/", StringComparison.OrdinalIgnoreCase) || u.StartsWith("data:application/font-", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("data:application/x-font-", StringComparison.OrdinalIgnoreCase));
        }

        // A face whose every source is a font written into the sheet (data:font/…) is kept; one that would fetch is dropped whole.
        private void FontFace()
        {
            var start = i;
            var end = AtRuleEnd(start);
            for (var j = start; j < end; j++)
            {
                var t = tokens[j];
                var bad = t.Type switch
                {
                    CssTokenType.Url => !Allowed(t.Value, inFontFace: true),
                    CssTokenType.BadUrl => true,
                    CssTokenType.Function when t.Lower == "url" => !UrlOk(j),
                    CssTokenType.Function => t.Lower is not ("format" or "local" or "tech" or "var"),
                    CssTokenType.AtKeyword => j != start,
                    CssTokenType.Delim => t.Value is "<" or "\\",
                    _ => false
                };
                if (bad)
                {
                    found.Add("css-font-dropped");
                    i = end;
                    cut = true;
                    return;
                }
            }

            Emit(tokens[i++]);
            while (i < end) Step(inFontFace: true);
        }

        private bool UrlOk(int j)
        {
            j++;
            while (j < tokens.Count && tokens[j].Type == CssTokenType.Whitespace) j++;
            if (j >= tokens.Count || tokens[j].Type != CssTokenType.String || !Allowed(tokens[j].Value, inFontFace: true)) return false;
            j++;
            while (j < tokens.Count && tokens[j].Type == CssTokenType.Whitespace) j++;
            return j < tokens.Count && tokens[j].Type == CssTokenType.RParen;
        }

        private void SkipAtRule()
        {
            i = AtRuleEnd(i);
            cut = true;
        }

        // From an at-keyword to the end of its rule: its ';', or the end of its block, or the end of the block it sits in.
        private int AtRuleEnd(int start)
        {
            var depth = 0;
            for (var j = start + 1; j < tokens.Count; j++)
            {
                switch (tokens[j].Type)
                {
                    case CssTokenType.Semicolon when depth == 0:
                        return j + 1;
                    case CssTokenType.LBrace or CssTokenType.LBracket or CssTokenType.LParen or CssTokenType.Function:
                        depth++;
                        break;
                    case CssTokenType.RBrace when depth == 0:
                        return j;
                    case CssTokenType.RBrace:
                        depth--;
                        if (depth == 0) return j + 1;
                        break;
                    case CssTokenType.RBracket or CssTokenType.RParen:
                        if (depth > 0) depth--;
                        break;
                }
            }

            return tokens.Count;
        }

        // From a property name to its ';' (taken) or the '}' that ends its block (left).
        private void SkipDeclaration()
        {
            cut = true;
            var depth = 0;
            for (i++; i < tokens.Count; i++)
            {
                switch (tokens[i].Type)
                {
                    case CssTokenType.Semicolon when depth == 0:
                        i++;
                        return;
                    case CssTokenType.RBrace when depth == 0:
                        return;
                    case CssTokenType.LBrace or CssTokenType.LBracket or CssTokenType.LParen or CssTokenType.Function:
                        depth++;
                        break;
                    case CssTokenType.RBrace or CssTokenType.RBracket or CssTokenType.RParen:
                        depth--;
                        break;
                }
            }
        }

        // A function token to its matching ')', or to the end of the sheet.
        private void SkipFunction()
        {
            var depth = 0;
            for (; i < tokens.Count; i++)
            {
                switch (tokens[i].Type)
                {
                    case CssTokenType.Function or CssTokenType.LParen or CssTokenType.LBracket or CssTokenType.LBrace:
                        depth++;
                        break;
                    case CssTokenType.RParen or CssTokenType.RBracket or CssTokenType.RBrace:
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            return;
                        }

                        break;
                }
            }
        }
    }
}

internal enum CssTokenType
{
    Ident, Function, AtKeyword, Hash, String, BadString, Url, BadUrl, Delim, Number, Percentage, Dimension, Whitespace, Cdo, Cdc,
    Colon, Semicolon, Comma, LBracket, RBracket, LParen, RParen, LBrace, RBrace
}

/// <summary>One token. <see cref="Raw"/> is the text as written; <see cref="Value"/> is what it means (escapes decoded): a name, a string's
/// contents, a URL, a delimiter. A dimension's number is <see cref="Raw"/> up to its unit, and its unit is <see cref="Value"/>.</summary>
internal sealed class CssToken
{
    public CssTokenType Type { get; init; }
    public string Raw { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string NumberRaw { get; init; } = string.Empty;
    public bool Escaped { get; init; }
    public bool AfterComment { get; set; }

    public string Lower => Value.ToLowerInvariant();

    public static CssToken Synthetic(CssTokenType type, string value) => new() { Type = type, Raw = value, Value = value };

    public void Write(StringBuilder sb)
    {
        switch (Type)
        {
            case CssTokenType.String:
                WriteString(sb, Value);
                return;
            case CssTokenType.Url when Escaped || Raw.Contains('<'):
                sb.Append("url(");
                WriteString(sb, Value);
                sb.Append(')');
                return;
            case CssTokenType.Whitespace:
                sb.Append(Raw.Contains('\n') ? "\n" : " ");
                return;
        }

        if (!Escaped)
        {
            sb.Append(Raw);
            return;
        }

        switch (Type)
        {
            case CssTokenType.Ident:
                WriteIdent(sb, Value);
                return;
            case CssTokenType.Function:
                WriteIdent(sb, Value);
                sb.Append('(');
                return;
            case CssTokenType.AtKeyword:
                sb.Append('@');
                WriteIdent(sb, Value);
                return;
            case CssTokenType.Hash:
                sb.Append('#');
                WriteName(sb, Value);
                return;
            case CssTokenType.Dimension:
                sb.Append(NumberRaw);
                // A unit that starts like an exponent ("e3") would be read back as part of the number.
                if (Value.Length > 1 && Value[0] is 'e' or 'E' && (char.IsAsciiDigit(Value[1]) || Value[1] is '+' or '-' && Value.Length > 2 && char.IsAsciiDigit(Value[2])))
                {
                    sb.Append(Value[0] == 'e' ? "\\65 " : "\\45 ");
                    WriteName(sb, Value[1..]);
                }
                else
                {
                    WriteIdent(sb, Value);
                }

                return;
            default:
                sb.Append(Raw);
                return;
        }
    }

    private static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c == 0x7F || c is '<' or '>' or '&') sb.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
            else sb.Append(c);
        }

        sb.Append('"');
    }

    private static bool IsNameChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' || c >= 0x80;

    // CSSOM's "serialize an identifier".
    private static void WriteIdent(StringBuilder sb, string value)
    {
        if (value == "-")
        {
            sb.Append("\\-");
            return;
        }

        for (var k = 0; k < value.Length; k++)
        {
            var c = value[k];
            if (c < 0x20 || c == 0x7F || c is '<' or '>' or '&' || (k == 0 && char.IsAsciiDigit(c)) || (k == 1 && char.IsAsciiDigit(c) && value[0] == '-'))
            {
                sb.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
            }
            else if (IsNameChar(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('\\').Append(c);
            }
        }
    }

    private static void WriteName(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7F || c is '<' or '>' or '&') sb.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
            else if (IsNameChar(c)) sb.Append(c);
            else sb.Append('\\').Append(c);
        }
    }
}

/// <summary>CSS Syntax Level 3's tokenizer (section 4), as browsers run it. Comments are not tokens: the token after one is marked.</summary>
internal static class CssTokenizer
{
    public static List<CssToken> Tokenize(string input)
    {
        var s = Preprocess(input);
        var list = new List<CssToken>();
        var p = 0;
        var comment = false;
        while (p < s.Length)
        {
            if (s[p] == '/' && p + 1 < s.Length && s[p + 1] == '*')
            {
                var end = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                p = end < 0 ? s.Length : end + 2;
                comment = true;
                continue;
            }

            var token = Next(s, ref p);
            token.AfterComment = comment;
            comment = false;
            list.Add(token);
        }

        return list;
    }

    private static string Preprocess(string input)
    {
        var sb = new StringBuilder(input.Length);
        for (var k = 0; k < input.Length; k++)
        {
            var c = input[k];
            if (c == '\r')
            {
                sb.Append('\n');
                if (k + 1 < input.Length && input[k + 1] == '\n') k++;
            }
            else if (c == '\f') sb.Append('\n');
            else if (c == '\0') sb.Append('�');
            else if (char.IsHighSurrogate(c) && k + 1 < input.Length && char.IsLowSurrogate(input[k + 1]))
            {
                sb.Append(c).Append(input[k + 1]);
                k++;
            }
            else if (char.IsSurrogate(c)) sb.Append('�');
            else sb.Append(c);
        }

        return sb.ToString();
    }

    private static char At(string s, int p) => p < s.Length ? s[p] : '\0';

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n';

    private static bool IsNameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || c >= 0x80;

    private static bool IsName(char c) => IsNameStart(c) || char.IsAsciiDigit(c) || c == '-';

    // §4.3.8: a backslash starts an escape unless a newline follows it (or nothing does).
    private static bool ValidEscape(string s, int p) => At(s, p) == '\\' && p + 1 < s.Length && s[p + 1] != '\n';

    // §4.3.9
    private static bool StartsIdent(string s, int p)
    {
        var c = At(s, p);
        if (c == '-') return IsNameStart(At(s, p + 1)) || At(s, p + 1) == '-' || ValidEscape(s, p + 1);
        if (IsNameStart(c)) return true;
        return c == '\\' && ValidEscape(s, p);
    }

    // §4.3.10
    private static bool StartsNumber(string s, int p)
    {
        var c = At(s, p);
        if (c is '+' or '-')
        {
            if (char.IsAsciiDigit(At(s, p + 1))) return true;
            return At(s, p + 1) == '.' && char.IsAsciiDigit(At(s, p + 2));
        }

        if (c == '.') return char.IsAsciiDigit(At(s, p + 1));
        return char.IsAsciiDigit(c);
    }

    private static CssToken Simple(CssTokenType type, string s, int start, int end) => new() { Type = type, Raw = s[start..end], Value = s[start..end] };

    private static CssToken Next(string s, ref int p)
    {
        var start = p;
        var c = s[p];
        if (IsWhitespace(c))
        {
            while (p < s.Length && IsWhitespace(s[p])) p++;
            return Simple(CssTokenType.Whitespace, s, start, p);
        }

        switch (c)
        {
            case '"' or '\'':
                return String(s, ref p);
            case '#':
                if (IsName(At(s, p + 1)) || ValidEscape(s, p + 1))
                {
                    p++;
                    var (name, escaped) = Name(s, ref p);
                    return new CssToken { Type = CssTokenType.Hash, Raw = s[start..p], Value = name, Escaped = escaped };
                }

                p++;
                return Simple(CssTokenType.Delim, s, start, p);
            case '(':
                p++;
                return Simple(CssTokenType.LParen, s, start, p);
            case ')':
                p++;
                return Simple(CssTokenType.RParen, s, start, p);
            case '[':
                p++;
                return Simple(CssTokenType.LBracket, s, start, p);
            case ']':
                p++;
                return Simple(CssTokenType.RBracket, s, start, p);
            case '{':
                p++;
                return Simple(CssTokenType.LBrace, s, start, p);
            case '}':
                p++;
                return Simple(CssTokenType.RBrace, s, start, p);
            case ',':
                p++;
                return Simple(CssTokenType.Comma, s, start, p);
            case ':':
                p++;
                return Simple(CssTokenType.Colon, s, start, p);
            case ';':
                p++;
                return Simple(CssTokenType.Semicolon, s, start, p);
            case '+' or '.':
                if (StartsNumber(s, p)) return Numeric(s, ref p);
                p++;
                return Simple(CssTokenType.Delim, s, start, p);
            case '-':
                if (StartsNumber(s, p)) return Numeric(s, ref p);
                if (At(s, p + 1) == '-' && At(s, p + 2) == '>')
                {
                    p += 3;
                    return Simple(CssTokenType.Cdc, s, start, p);
                }

                if (StartsIdent(s, p)) return IdentLike(s, ref p);
                p++;
                return Simple(CssTokenType.Delim, s, start, p);
            case '<':
                if (At(s, p + 1) == '!' && At(s, p + 2) == '-' && At(s, p + 3) == '-')
                {
                    p += 4;
                    return Simple(CssTokenType.Cdo, s, start, p);
                }

                p++;
                return Simple(CssTokenType.Delim, s, start, p);
            case '@':
                if (StartsIdent(s, p + 1))
                {
                    p++;
                    var (name, escaped) = Name(s, ref p);
                    return new CssToken { Type = CssTokenType.AtKeyword, Raw = s[start..p], Value = name, Escaped = escaped };
                }

                p++;
                return Simple(CssTokenType.Delim, s, start, p);
            case '\\':
                if (ValidEscape(s, p)) return IdentLike(s, ref p);
                p++;
                return Simple(CssTokenType.Delim, s, start, p);
        }

        if (char.IsAsciiDigit(c)) return Numeric(s, ref p);
        if (IsNameStart(c)) return IdentLike(s, ref p);
        p += char.IsHighSurrogate(c) && p + 1 < s.Length ? 2 : 1;
        return Simple(CssTokenType.Delim, s, start, p);
    }

    // §4.3.11: consumes a name; says whether any of it was an escape.
    private static (string Name, bool Escaped) Name(string s, ref int p)
    {
        var sb = new StringBuilder();
        var escaped = false;
        while (p < s.Length)
        {
            var c = s[p];
            if (IsName(c))
            {
                sb.Append(c);
                p++;
            }
            else if (ValidEscape(s, p))
            {
                p++;
                Escape(s, ref p, sb);
                escaped = true;
            }
            else
            {
                break;
            }
        }

        return (sb.ToString(), escaped);
    }

    // §4.3.7, after the backslash.
    private static void Escape(string s, ref int p, StringBuilder sb)
    {
        if (p >= s.Length)
        {
            sb.Append('�');
            return;
        }

        if (char.IsAsciiHexDigit(s[p]))
        {
            var value = 0;
            var n = 0;
            while (n < 6 && p < s.Length && char.IsAsciiHexDigit(s[p]))
            {
                value = value * 16 + Convert.ToInt32(s[p].ToString(), 16);
                p++;
                n++;
            }

            if (p < s.Length && IsWhitespace(s[p])) p++;
            if (value == 0 || value is >= 0xD800 and <= 0xDFFF || value > 0x10FFFF) sb.Append('�');
            else sb.Append(char.ConvertFromUtf32(value));
            return;
        }

        if (char.IsHighSurrogate(s[p]) && p + 1 < s.Length)
        {
            sb.Append(s, p, 2);
            p += 2;
            return;
        }

        sb.Append(s[p]);
        p++;
    }

    // §4.3.4
    private static CssToken IdentLike(string s, ref int p)
    {
        var start = p;
        var (name, escaped) = Name(s, ref p);
        if (string.Equals(name, "url", StringComparison.OrdinalIgnoreCase) && At(s, p) == '(')
        {
            p++;
            var q = p;
            while (q < s.Length && IsWhitespace(s[q])) q++;
            if (At(s, q) is '"' or '\'')
            {
                // url( followed by a string is a function; the whitespace before the string is its own token.
                return new CssToken { Type = CssTokenType.Function, Raw = s[start..p], Value = name, Escaped = escaped };
            }

            return Url(s, ref p, start, escaped);
        }

        if (At(s, p) == '(')
        {
            p++;
            return new CssToken { Type = CssTokenType.Function, Raw = s[start..p], Value = name, Escaped = escaped };
        }

        return new CssToken { Type = CssTokenType.Ident, Raw = s[start..p], Value = name, Escaped = escaped };
    }

    // §4.3.6, after "url(".
    private static CssToken Url(string s, ref int p, int start, bool escaped)
    {
        var sb = new StringBuilder();
        while (p < s.Length && IsWhitespace(s[p])) p++;
        while (true)
        {
            if (p >= s.Length) return new CssToken { Type = CssTokenType.Url, Raw = s[start..p] + ")", Value = sb.ToString(), Escaped = true };
            var c = s[p];
            if (c == ')')
            {
                p++;
                return new CssToken { Type = CssTokenType.Url, Raw = s[start..p], Value = sb.ToString(), Escaped = escaped };
            }

            if (IsWhitespace(c))
            {
                while (p < s.Length && IsWhitespace(s[p])) p++;
                if (p >= s.Length) return new CssToken { Type = CssTokenType.Url, Raw = s[start..p] + ")", Value = sb.ToString(), Escaped = true };
                if (s[p] == ')')
                {
                    p++;
                    return new CssToken { Type = CssTokenType.Url, Raw = s[start..p], Value = sb.ToString(), Escaped = true };
                }

                return BadUrl(s, ref p, start);
            }

            if (c is '"' or '\'' or '(' || c < 0x09 || c is (char)0x0B or (char)0x7F || c is >= (char)0x0E and <= (char)0x1F) return BadUrl(s, ref p, start);
            if (c == '\\')
            {
                if (!ValidEscape(s, p)) return BadUrl(s, ref p, start);
                p++;
                Escape(s, ref p, sb);
                escaped = true;
                continue;
            }

            sb.Append(c);
            p++;
        }
    }

    // §4.3.14
    private static CssToken BadUrl(string s, ref int p, int start)
    {
        while (p < s.Length)
        {
            if (s[p] == ')')
            {
                p++;
                break;
            }

            if (ValidEscape(s, p)) p += 2;
            else p++;
        }

        return new CssToken { Type = CssTokenType.BadUrl, Raw = s[start..Math.Min(p, s.Length)] };
    }

    // §4.3.5
    private static CssToken String(string s, ref int p)
    {
        var start = p;
        var quote = s[p++];
        var sb = new StringBuilder();
        while (p < s.Length)
        {
            var c = s[p];
            if (c == quote)
            {
                p++;
                return new CssToken { Type = CssTokenType.String, Raw = s[start..p], Value = sb.ToString() };
            }

            if (c == '\n')
            {
                // An unclosed string at a line's end is a bad string; the newline is the next token.
                return new CssToken { Type = CssTokenType.BadString, Raw = s[start..p] };
            }

            if (c == '\\')
            {
                if (p + 1 >= s.Length)
                {
                    p++;
                    continue;
                }

                if (s[p + 1] == '\n')
                {
                    p += 2;
                    continue;
                }

                p++;
                Escape(s, ref p, sb);
                continue;
            }

            sb.Append(c);
            p++;
        }

        // The end of the sheet closes a string.
        return new CssToken { Type = CssTokenType.String, Raw = s[start..p], Value = sb.ToString() };
    }

    // §4.3.3
    private static CssToken Numeric(string s, ref int p)
    {
        var start = p;
        if (At(s, p) is '+' or '-') p++;
        while (char.IsAsciiDigit(At(s, p))) p++;
        if (At(s, p) == '.' && char.IsAsciiDigit(At(s, p + 1)))
        {
            p += 2;
            while (char.IsAsciiDigit(At(s, p))) p++;
        }

        if (At(s, p) is 'e' or 'E')
        {
            var q = p + 1;
            if (At(s, q) is '+' or '-') q++;
            if (char.IsAsciiDigit(At(s, q)))
            {
                p = q;
                while (char.IsAsciiDigit(At(s, p))) p++;
            }
        }

        var number = s[start..p];
        if (StartsIdent(s, p))
        {
            var (unit, escaped) = Name(s, ref p);
            return new CssToken { Type = CssTokenType.Dimension, Raw = s[start..p], NumberRaw = number, Value = unit, Escaped = escaped };
        }

        if (At(s, p) == '%')
        {
            p++;
            return new CssToken { Type = CssTokenType.Percentage, Raw = s[start..p], NumberRaw = number, Value = number };
        }

        return new CssToken { Type = CssTokenType.Number, Raw = number, NumberRaw = number, Value = number };
    }
}
