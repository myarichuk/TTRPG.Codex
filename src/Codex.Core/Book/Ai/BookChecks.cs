using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>
/// What a model's rewrite of a chapter is checked against before it may go in a book. All of it is deterministic and none of it asks a
/// model: the checks are what stand between a plausible paragraph and a printed book that says something the campaign never did.
/// A failed check is reported as a sentence the model is shown on its one retry, then the chapter falls back to the deterministic text.
/// </summary>
public static class BookChecks
{
    /// <summary>Bumped when a check is tightened: stored rewrites were accepted by the older checks and must be made again.</summary>
    public const string Version = "book-checks-1";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const RegexOptions Flags = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Marker = new(@"\{\{block:(?<n>\d{1,4})\}\}", Flags, Timeout);
    private static readonly Regex Number = new(@"\p{Nd}+(?:[.,]\p{Nd}+)*", Flags, Timeout);
    private static readonly Regex ListMarker = new(@"^[ \t]*\p{Nd}{1,3}[.)][ \t]+", Flags | RegexOptions.Multiline, Timeout);
    private static readonly Regex Url = new(@"(?:[a-z][a-z0-9+.-]{1,12}:/{1,2}|\bwww\.|\bdata:|\bjavascript:|\bmailto:)", Flags | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex Tag = new(@"</?[A-Za-z!][^>]{0,200}>", Flags, Timeout);
    private static readonly Regex Heading = new(@"^[ \t]{0,3}(#{1,6})[ \t]+(?<text>.+?)[ \t]*#*[ \t]*$", Flags | RegexOptions.Multiline, Timeout);
    private static readonly Regex Word = new(@"\p{L}[\p{L}\p{Mn}'’-]*", Flags, Timeout);

    /// <summary>The reasons the rewrite may not be used, empty when it passes. <paramref name="source"/> is what the model was given
    /// (see <see cref="BookWork.Prepare"/>), <paramref name="blocks"/> the markers it was told it could place.</summary>
    public static IReadOnlyList<string> Validate(string source, string output, int blockCount, string language)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return ["The reply was empty."];
        }

        // Nothing that fetches, runs or reaches outside the page.
        if (Url.IsMatch(output)) problems.Add("It contains a web address or a link scheme. Write no URLs.");
        if (Tag.IsMatch(output)) problems.Add("It contains an HTML tag. Write plain Markdown only.");
        if (output.Contains("![", StringComparison.Ordinal)) problems.Add("It contains an image. Write no images.");
        if (output.Split('\n').Any(l => SecretText.IsMarkerLine(l.TrimEnd('\r')))) problems.Add("It contains a line starting with ':::'. Write no secret-block markers.");
        if (output.Contains("[[", StringComparison.Ordinal)) problems.Add("It contains a [[mention]]. Write names as plain words.");
        if (Regex.Replace(output, @"\{\{block:\d{1,4}\}\}", "", RegexOptions.CultureInvariant, Timeout).Contains("{{", StringComparison.Ordinal))
        {
            problems.Add("It contains '{{'. The only double braces allowed are the {{block:N}} markers.");
        }

        // Blocks: each marker the model was given comes back once, and no other number is made up.
        var seen = new Dictionary<int, int>();
        foreach (Match m in Marker.Matches(output))
        {
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            seen[n] = seen.GetValueOrDefault(n) + 1;
        }

        for (var n = 1; n <= blockCount; n++)
        {
            var count = seen.GetValueOrDefault(n);
            if (count == 0) problems.Add($"The marker {{{{block:{n}}}}} is missing. Place every block exactly once.");
            else if (count > 1) problems.Add($"The marker {{{{block:{n}}}}} appears {count} times. Place every block exactly once.");
        }

        foreach (var n in seen.Keys.Where(n => n < 1 || n > blockCount).Order()) problems.Add($"{{{{block:{n}}}}} does not exist.");
        // A marker belongs on a line of its own: the renderer places it only there, so mid-sentence it would print as text.
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("{{block:", StringComparison.Ordinal) && !Regex.IsMatch(trimmed, @"^\{\{block:\d{1,4}\}\}$", RegexOptions.CultureInvariant, Timeout))
            {
                problems.Add("A {{block:N}} marker shares a line with other text. Put each marker on a line of its own.");
                break;
            }
        }

        // Headings the chapter had are the table of contents and the anchors: they stay as written.
        var outputHeadings = Headings(output).ToHashSet(StringComparer.Ordinal);
        foreach (var heading in Headings(source).Where(h => !outputHeadings.Contains(h)).Take(3))
        {
            problems.Add($"The heading \"{heading}\" was changed or dropped. Keep every heading exactly as it is.");
        }

        // Numbers: any figure in the rewrite is one the chapter already had (so the model cannot state a level, a date or a count).
        var allowed = Numbers(source);
        foreach (var spelled in SpelledNumbers(source)) allowed.Add(spelled.ToString(CultureInfo.InvariantCulture));
        var invented = Numbers(output).Where(n => !allowed.Contains(n)).Concat(SpelledNumbers(output).Select(n => n.ToString(CultureInfo.InvariantCulture)).Where(n => !allowed.Contains(n))).Distinct().Take(5).ToList();
        if (invented.Count > 0) problems.Add("It states numbers that are not in the source: " + string.Join(", ", invented) + ". Use only figures the source gives.");

        // Names: a capitalised word in the middle of a sentence that the source never uses is a name the model made up.
        var invention = NewNames(source, output).Take(5).ToList();
        if (invention.Count > 0) problems.Add("It introduces names that are not in the source: " + string.Join(", ", invention) + ". Use only names the source gives.");

        if (!InLanguage(output, language, out var share))
        {
            problems.Add($"It is not written in the book's language ({BookThemes.Primary(language)}); only about {share:P0} of its letters are in that script.");
        }

        var ratio = (double)output.Length / Math.Max(1, source.Length);
        if (source.Length >= 200 && ratio < 0.12) problems.Add("It is far shorter than the source. Condense, but keep what matters.");
        if (ratio > 2.5 && output.Length > 600) problems.Add("It is much longer than the source. Do not add material.");
        return problems;
    }

    /// <summary>Heading texts, unescaped and trimmed, as the compiler writes them with a backslash before each markup character.</summary>
    internal static IEnumerable<string> Headings(string markdown) =>
        Heading.Matches(markdown).Select(m => Unescape(m.Groups["text"].Value).Trim()).Where(t => t.Length > 0);

    private static string Unescape(string text) => Regex.Replace(text, @"\\(.)", "$1", RegexOptions.CultureInvariant | RegexOptions.Singleline, Timeout);

    /// <summary>The figures in a text as ASCII digits, so a number written in Arabic-Indic digits is the same number. List markers
    /// ("1.", "2)") and block markers are not figures.</summary>
    /// <summary>The style notes of the party edition may carry no fact the party does not have: no number and no name that the chapters
    /// the party reads do not hold. The notes are the DM's own words and go into every request, so a plot twist typed there would be
    /// written into a book the players hold.</summary>
    public static IReadOnlyList<string> StyleProblems(string style, string visibleText)
    {
        var problems = new List<string>();
        var names = NewNames(visibleText, style).Take(5).ToList();
        if (names.Count > 0) problems.Add("names the book does not hold: " + string.Join(", ", names));
        if (Numbers(style).Except(Numbers(visibleText)).Any()) problems.Add("numbers the book does not hold");
        return problems;
    }

    internal static HashSet<string> Numbers(string text)
    {
        var plain = Marker.Replace(ListMarker.Replace(text, string.Empty), string.Empty);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Number.Matches(plain))
        {
            var ascii = new string(m.Value.Select(c => char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c).ToArray());
            set.Add(ascii);
            // "1,000" and "1.5" also stand for their parts: a model that writes "1000" for "1,000" has not invented anything.
            set.Add(ascii.Replace(",", string.Empty));
            foreach (var part in ascii.Split(['.', ','])) set.Add(part);
        }

        return set;
    }

    private static readonly Dictionary<string, int> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
    };

    /// <summary>
    /// English figures written as words ("twelve", "thirty-one", "two hundred"), as numbers, so a model cannot state a level or a count
    /// by spelling it out past the digit check. One to nine alone are left alone: "one of them", "two-handed" and "no one" are not figures.
    /// Numbers in Russian or Hebrew words are not read (the digit check still covers their digits).
    /// </summary>
    internal static IEnumerable<long> SpelledNumbers(string text)
    {
        long total = 0, current = 0;
        var any = false;
        var compound = false;
        foreach (Match m in Word.Matches(text))
        {
            var w = m.Value.Trim('-', '\'', '’').ToLowerInvariant();
            // A compound like "thirty-one" arrives as one word with a hyphen.
            var parts = w.Split('-');
            var consumed = false;
            foreach (var part in parts)
            {
                if (Small.TryGetValue(part, out var value)) { current += value; any = true; consumed = true; compound |= value >= 10; }
                else if (part == "hundred") { current = (current == 0 ? 1 : current) * 100; any = true; consumed = true; compound = true; }
                else if (part == "thousand") { total += (current == 0 ? 1 : current) * 1000; current = 0; any = true; consumed = true; compound = true; }
                else if (part == "and" && any) { consumed = true; }
                else { consumed = false; break; }
            }

            if (consumed) continue;
            if (any && (compound || total + current >= 10)) yield return total + current;
            total = current = 0;
            any = false;
            compound = false;
        }

        if (any && (compound || total + current >= 10)) yield return total + current;
    }

    internal static IEnumerable<string> NewNames(string source, string output)
    {
        // The word less its last two letters (three to six kept): "Вера" and "Веры", "Солтмира" and "Солтмире" share a stem, which is
        // how a name inflected by case still counts as the same name. A rough rule, and tuned for recall: it errs toward letting a name pass.
        string Stem(string w) { var lower = w.ToLowerInvariant().Replace("’", "'"); return lower[..Math.Clamp(lower.Length - 2, Math.Min(3, lower.Length), 6)]; }
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Word.Matches(source)) known.Add(Stem(m.Value));
        var cleaned = Marker.Replace(output, " ");
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Word.Matches(cleaned))
        {
            var word = m.Value;
            if (word.Length < 3 || !char.IsUpper(word[0])) continue;
            // Start of a sentence, a line or a heading is capitalised for its own sake.
            var before = cleaned[..m.Index].TrimEnd(' ', '\t', '"', '“', '«', '(', '*', '_', '—', '–', '-');
            if (before.Length == 0 || before[^1] is '.' or '!' or '?' or '…' or '\n' or ':' or '#' or '>' or '•') continue;
            if (!known.Contains(Stem(word)) && reported.Add(word)) yield return word;
        }
    }

    internal static bool InLanguage(string text, string language, out double share)
    {
        var letters = 0;
        var inScript = 0;
        var primary = BookThemes.Primary(language);
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            var here = primary switch
            {
                "ru" => c is >= 'Ѐ' and <= 'ԯ',
                "he" => c is >= '֐' and <= '׿',
                _ => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'À' and <= 'ɏ'
            };
            if (here) inScript++;
        }

        share = letters == 0 ? 1 : (double)inScript / letters;
        return letters < 40 || share >= (primary == "en" ? 0.85 : 0.6);
    }
}
