using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Codex.ContentImport.Degrees;

/// <summary>A half-open character range in a normalized text.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// The text rules the redaction step shares with its hashes: one normalization, one sentence splitter, one hash. A row is keyed by the hash
/// and the length of a normalized span, so changing any rule here changes every row's key. <see cref="Version"/> is written in
/// <c>degrees-redactions.yaml</c> and in <c>degrees-books.yaml</c>; bump it, and every row, with any change here.
/// </summary>
public static class SpanText
{
    public const int Version = 1;

    /// <summary>Words that end in a period without ending a sentence. A single letter before the period never does either.</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase) { "vs", "eg", "ie", "cf", "approx", "max", "min" };

    private const string Closers = "\"')]";

    /// <summary>
    /// NFKC (which expands ligatures), straight quotes, a plain hyphen for every dash and minus, no soft hyphens, spaces and tabs collapsed
    /// to one space and a line break kept as the paragraph separator (one "\n", with no space beside it).
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var source = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(source.Length);
        var pendingSpace = false;
        var pendingBreak = false;
        foreach (var raw in source)
        {
            var c = raw switch
            {
                '‘' or '’' or '‚' or '‛' or '′' => '\'',
                '“' or '”' or '„' or '‟' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                _ => raw,
            };
            if (c == '­')
            {
                continue;
            }

            if (c is '\n' or '\r' || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                pendingBreak = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (sb.Length > 0)
            {
                if (pendingBreak)
                {
                    sb.Append('\n');
                }
                else if (pendingSpace)
                {
                    sb.Append(' ');
                }
            }

            pendingBreak = false;
            pendingSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>The sentences of a normalized text, in order. A paragraph break always ends one.</summary>
    public static List<TextSpan> Sentences(string text)
    {
        var result = new List<TextSpan>();
        var paragraphStart = 0;
        while (paragraphStart <= text.Length)
        {
            var paragraphEnd = text.IndexOf('\n', paragraphStart);
            if (paragraphEnd < 0)
            {
                paragraphEnd = text.Length;
            }

            SplitParagraph(text, paragraphStart, paragraphEnd, result);
            paragraphStart = paragraphEnd + 1;
        }

        return result;
    }

    /// <summary>The clauses of a sentence that has a semicolon inside it (each ends at its semicolon, the last at the sentence's end). Fewer than two: none.</summary>
    public static List<TextSpan> Clauses(string text, TextSpan sentence)
    {
        var result = new List<TextSpan>();
        var start = sentence.Start;
        for (var i = sentence.Start; i < sentence.End; i++)
        {
            if (text[i] == ';' && i + 1 < sentence.End && text[i + 1] == ' ')
            {
                result.Add(new TextSpan(start, i + 1 - start));
                start = i + 2;
            }
        }

        if (result.Count == 0)
        {
            return result;
        }

        result.Add(new TextSpan(start, sentence.End - start));
        return result;
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of an already normalized span.</summary>
    public static string Sha256(string normalizedSpan) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSpan))).ToLowerInvariant();

    private static void SplitParagraph(string text, int start, int end, List<TextSpan> result)
    {
        var from = start;
        for (var i = start; i < end; i++)
        {
            if (text[i] is not ('.' or '!' or '?'))
            {
                continue;
            }

            var last = i;
            while (last + 1 < end && Closers.Contains(text[last + 1]))
            {
                last++;
            }

            if (last + 1 < end)
            {
                // A boundary needs a space and then something that opens a sentence.
                if (text[last + 1] != ' ' || last + 2 >= end || !OpensSentence(text[last + 2]))
                {
                    continue;
                }
            }

            if (text[i] == '.' && IsAbbreviation(text, start, i))
            {
                continue;
            }

            result.Add(new TextSpan(from, last + 1 - from));
            from = last + 2;
            i = last;
        }

        if (from < end)
        {
            result.Add(new TextSpan(from, end - from));
        }
    }

    private static bool OpensSentence(char c) => char.IsUpper(c) || char.IsDigit(c) || c is '"' or '\'' or '(' or '[';

    private static bool IsAbbreviation(string text, int paragraphStart, int period)
    {
        var begin = period;
        while (begin > paragraphStart && char.IsLetter(text[begin - 1]))
        {
            begin--;
        }

        var word = text[begin..period];
        return word.Length == 1 || Abbreviations.Contains(word);
    }
}
