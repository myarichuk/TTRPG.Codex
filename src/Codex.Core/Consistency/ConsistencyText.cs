using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Consistency;

/// <summary>Builds issues with the id scheme in one place, so no check spells it its own way.</summary>
internal static class Make
{
    /// <summary><c>check:kind:entityId:field[:detail]</c>. Readable on purpose: the model cites ids, and a person debugs with them.</summary>
    public static string Id(string check, string kind, string entityId, string field, string? detail = null) =>
        detail == null ? $"{check}:{kind}:{Part(entityId)}:{Part(field)}" : $"{check}:{kind}:{Part(entityId)}:{Part(field)}:{Part(detail)}";

    /// <summary>The longest part of an id that is kept whole.</summary>
    public const int MaxPart = 80;

    /// <summary>
    /// One part of an id as it is written: letters, digits and <c>- _ . : / @ +</c> only, anything else (a space, a line break, a quote, a bracket, a
    /// backtick, the <c>~</c> that numbers duplicates) as <c>-</c>, and at most <see cref="MaxPart"/> long (cut, with a short hash of the whole so two long
    /// parts stay apart). An id is shown to the model and kept in lists, and some details come from text a person typed (a mention's id, a property value, an
    /// item id on a sheet), so no id can carry a sentence. Ids made of document ids and fixed words are unchanged.
    /// </summary>
    public static string Part(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var clean = new StringBuilder(Math.Min(text.Length, MaxPart + 9));
        foreach (var c in text)
        {
            clean.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/' or '@' or '+' ? c : '-');
        }

        if (clean.Length <= MaxPart) return clean.ToString();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..6].ToLowerInvariant();
        return clean.ToString(0, MaxPart).TrimEnd('-') + "-" + hash;
    }

    public static Issue Issue(string check, IssueSeverity severity, string kind, string entityId, string field, string? detail, string code,
        IReadOnlyList<string> args, string message, string fixHint, FixVia fix) =>
        new(Id(check, kind, entityId, field, detail), check, severity, kind, entityId, field, code, args, message, fixHint, $"{kind}:{entityId}", fix);

    /// <summary>A name as it is quoted in a message: control characters and line breaks are folded away (a title is text a person typed,
    /// and a message is read by a model).</summary>
    public static string Quote(string? text)
    {
        var flat = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        return flat.Length > 80 ? flat[..80] + "…" : flat;
    }
}

/// <summary>One word of a text, found by <see cref="TextNorm.Tokenize"/>.</summary>
/// <param name="Norm">The word as titles are compared: Unicode-compatibility normalised, lower case, apostrophes dropped.</param>
/// <param name="Start">Where it starts in the text.</param>
/// <param name="End">Where it ends (exclusive).</param>
/// <param name="Capitalized">Whether it was written with a capital first letter.</param>
internal readonly record struct Token(string Norm, int Start, int End, bool Capitalized);

/// <summary>
/// How titles are compared for the "did you mean" of a broken link and for names written in plain text that should have been links.
/// Words are runs of letters and digits; a leading article (the, a, an) is ignored; a word written twice in a row ("the the") counts
/// once. Deterministic and language-light on purpose: it knows English articles only, and other languages match word for word.
/// </summary>
internal static class TextNorm
{
    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal) { "the", "a", "an" };

    public static bool IsArticle(string norm) => Articles.Contains(norm);

    private static bool IsApostrophe(char c) => c is '\'' or '’' or '‘' or 'ʼ';

    public static List<Token> Tokenize(string? text) => Tokenize(text, null);

    /// <summary>The words of <paramref name="text"/>. <paramref name="words"/>, when given, keeps each written word's compared form, so a word met again
    /// (in this text or another of the same run of a check) is looked up instead of normalised again; the forms are the same either way.</summary>
    public static List<Token> Tokenize(string? text, WordForms? words)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(text)) return tokens;
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            var start = i;
            var apostrophes = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsLetterOrDigit(c)) i++;
                else if (IsApostrophe(c) && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1])) { apostrophes = true; i++; } // "men's" is one word
                else break;
            }

            // The word is its letters and digits: an apostrophe inside it is left out.
            var norm = apostrophes ? Form(Letters(text, start, i)) : words != null ? words.Of(text.AsSpan(start, i - start)) : Form(text.Substring(start, i - start));
            tokens.Add(new Token(norm, start, i, char.IsUpper(text[start])));
        }

        return tokens;
    }

    /// <summary>A written word as titles are compared: Unicode-compatibility normalised, lower case.</summary>
    internal static string Form(string word) => word.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    private static string Letters(string text, int start, int end)
    {
        var word = new StringBuilder(end - start);
        for (var k = start; k < end; k++)
        {
            if (char.IsLetterOrDigit(text[k])) word.Append(text[k]);
        }

        return word.ToString();
    }

    /// <summary>The words of a title, compared: no leading articles, no word twice in a row.</summary>
    public static IReadOnlyList<string> Words(string? title)
    {
        var words = new List<string>();
        foreach (var token in Tokenize(title))
        {
            if (words.Count == 0 && Articles.Contains(token.Norm)) continue;
            if (words.Count > 0 && words[^1] == token.Norm) continue;
            words.Add(token.Norm);
        }

        // Every leading article goes ("the the a Mere"), not just the first: the first loop keeps skipping while nothing is kept yet.
        return words;
    }

    public static string Canonical(string? title) => string.Join(' ', Words(title));

    /// <summary>
    /// The runs of words of a text that are written one after the other: a gap of punctuation, or of more than one line break, ends a run,
    /// so a name is never found across a full stop or a paragraph. Inside a run, a word written twice in a row is kept once, and
    /// <c>Starts</c> / <c>Ends</c> give each kept word's place in the text.
    /// </summary>
    public static List<List<Token>> Runs(string text) => Runs(text, null);

    /// <inheritdoc cref="Runs(string)"/>
    public static List<List<Token>> Runs(string text, WordForms? words)
    {
        var runs = new List<List<Token>>();
        List<Token>? run = null;
        var previous = default(Token);
        foreach (var token in Tokenize(text, words))
        {
            var joined = run != null && IsPlainGap(text, previous.End, token.Start);
            if (!joined)
            {
                run = new List<Token>();
                runs.Add(run);
            }

            if (run!.Count > 0 && run[^1].Norm == token.Norm)
            {
                run[^1] = run[^1] with { End = token.End };
            }
            else
            {
                run.Add(token);
            }

            previous = token;
        }

        return runs;
    }

    // Only blanks between two words, with at most one line break: "Mere of\nDead Men" is one name, a paragraph break is not.
    private static bool IsPlainGap(string text, int from, int to)
    {
        var lineBreaks = 0;
        for (var i = from; i < to; i++)
        {
            if (!char.IsWhiteSpace(text[i])) return false;
            if (text[i] == '\n') lineBreaks++;
        }

        return to > from && lineBreaks <= 1;
    }
}

/// <summary>The compared forms of the written words one check has met (<see cref="TextNorm.Tokenize(string?, WordForms?)"/>): a cache made for one run of
/// a check and dropped with it, never shared between threads or runs. A form depends on the written word alone, so a cached form is the form.</summary>
internal sealed class WordForms
{
    private readonly Dictionary<string, string> forms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> bySpan;

    public WordForms() => bySpan = forms.GetAlternateLookup<ReadOnlySpan<char>>();

    public string Of(ReadOnlySpan<char> written)
    {
        if (bySpan.TryGetValue(written, out var form)) return form;
        var word = written.ToString();
        return forms[word] = TextNorm.Form(word);
    }
}

/// <summary>A text of the snapshot a text check reads: whose it is, which field, and what kind of change could fix a problem in it.</summary>
internal sealed record TextField(string Kind, string EntityId, string Field, string Text, FixVia Fix);

internal static class TextFields
{
    /// <summary>Every free-text field of the snapshot that is not empty, in a stable order: pages, then actors, places, facts, each by id.</summary>
    public static IEnumerable<TextField> Of(WorldSnapshot s)
    {
        foreach (var p in s.Pages)
        {
            if (!string.IsNullOrEmpty(p.Body)) yield return new TextField("page", p.Id, "body", p.Body, FixVia.Page);
        }

        foreach (var a in s.Actors)
        {
            if (!string.IsNullOrEmpty(a.Biography)) yield return new TextField("actor", a.Id, "biography", a.Biography, FixVia.ActorBiography);
            if (!string.IsNullOrEmpty(a.DmNotes)) yield return new TextField("actor", a.Id, "dmNotes", a.DmNotes, FixVia.ReportOnly);
        }

        foreach (var p in s.Places)
        {
            if (!string.IsNullOrEmpty(p.Description)) yield return new TextField("place", p.Id, "description", p.Description, FixVia.Place);
        }

        foreach (var f in s.Facts)
        {
            if (!string.IsNullOrEmpty(f.Summary)) yield return new TextField("fact", f.Id, "summary", f.Summary, FixVia.Fact);
            if (!string.IsNullOrEmpty(f.Details)) yield return new TextField("fact", f.Id, "details", f.Details, FixVia.Fact);
        }
    }

    /// <summary>The text with every <c>[[...]]</c> span and every secret-block marker line blanked out, same length, so a position in
    /// the result is a position in the original.</summary>
    public static string WithoutLinksAndMarkers(string text)
    {
        var chars = text.ToCharArray();
        foreach (Match m in Regex.Matches(text, @"\[\[[^\[\]]*\]\]")) Blank(chars, m.Index, m.Length);
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            if (SecretText.IsMarkerLine(line.TrimEnd('\r'))) Blank(chars, offset, line.Length);
            offset += line.Length + 1;
        }

        return new string(chars);
    }

    private static void Blank(char[] chars, int start, int length)
    {
        for (var i = start; i < start + length && i < chars.Length; i++)
        {
            if (chars[i] != '\n') chars[i] = ' ';
        }
    }

    /// <summary>The stored mentions a text holds, as (kind, id): <c>[[actor:id]]</c> and <c>[[place:id]]</c>, with or without a label,
    /// outside secret-block marker lines (a <c>:::for</c> line is the secret check's to read).</summary>
    public static IEnumerable<(string Kind, string Id)> Mentions(string text)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (SecretText.IsMarkerLine(line)) continue;
            foreach (Match m in MentionPattern.Matches(line))
            {
                if (seen.Add((m.Groups[1].Value, m.Groups[2].Value))) yield return (m.Groups[1].Value, m.Groups[2].Value);
            }
        }
    }

    private static readonly Regex MentionPattern = new(@"\[\[(actor|place):([^\]\s|]+)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);
}
