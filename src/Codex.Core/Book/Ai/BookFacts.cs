using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core.Book;

/// <summary>What a passage's facts are checked against: the names this edition gives its people and places (as its own source resolves them,
/// so the party edition's are the masks), and the game's own words (ability, condition and field labels), which a book may capitalise
/// without their being names.</summary>
public sealed record BookFactNames(IReadOnlyCollection<string> Names, IReadOnlyCollection<string> Vocabulary)
{
    public static readonly BookFactNames None = new([], []);
}

/// <summary>
/// The facts check of a rewrite that must keep the passage's facts (docs/BOOK_EXPORT.md 42, step 1): "As the book", "Shorter", "Tidy" and
/// "Read-aloud". Both ways: the reply may add no name and no figure the passage (or the text around it the model was shown) does not hold,
/// and may drop none the passage holds. All of it reads the passage as the model was sent it (its pieces as <c>{{block:N}}</c> markers),
/// never the stored text, so a name inside a secret is never counted, and never quoted back to the model on the retry.
/// <para>
/// Names are this edition's names, not capital letters (Hebrew has none). A name is matched word by word, tolerant of the languages' own
/// ways: Russian case endings on a stem (Вера, Веру, Верой; not "мирный" for Мир), Hebrew prefix letters (לוורה, ובמגדל, ל-אורן), Hebrew
/// points, English possessives. A name of several words is in the passage when all its words are, kept by the reply when any is ("Oren"
/// keeps "Oren the ferryman"), and added by the reply when all are there and none was before. Figures are digits and dice (2d6, 2к6);
/// a figure written out in words ("three", "три", "שלושה") keeps it.
/// </para>
/// </summary>
public static partial class BookFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"[\p{L}\p{Mn}]+(?:['’][\p{L}\p{Mn}]+)*", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\{\{block:\d{1,4}\}\}", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Marker();

    // Dice: 2d6, d20, 1d8+2 (the bonus is a figure of its own), in Latin or Cyrillic letters.
    [GeneratedRegex(@"(?<![\p{L}\p{Nd}])(?<n>\p{Nd}{0,3})[dDкКдД](?<s>\p{Nd}{1,3})(?![\p{L}\p{Nd}])", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Dice();

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "of", "and", "an", "at", "in", "on", "to", "by", "из", "на", "во", "של"
    };

    private const string HebrewPrefixes = "ובכלמשה";

    // Russian endings a stem may take: noun and adjective cases, both genders and the plural.
    private static readonly HashSet<string> RussianEndings = new(StringComparer.Ordinal)
    {
        "", "а", "я", "о", "е", "у", "ю", "ы", "и", "ь", "й", "ой", "ей", "ою", "ею", "ом", "ем", "ём", "ам", "ям", "ами", "ями", "ах", "ях",
        "ов", "ев", "ей", "ий", "ый", "ая", "яя", "ое", "ее", "ую", "юю", "ого", "его", "ому", "ему", "ым", "им", "ых", "их", "ыми", "ими"
    };

    private const string RussianFinals = "аяоеыиуюьй";

    /// <summary>What is wrong with <paramref name="reply"/>'s facts, in sentences for the model's retry; empty when it keeps them.
    /// <paramref name="passage"/> is the passage as sent (markers in place of pieces), <paramref name="context"/> the text around it the model
    /// was shown (a name or figure there is not invented).</summary>
    public static IReadOnlyList<string> Check(string passage, string context, string reply, BookFactNames facts, string language)
    {
        var problems = new List<string>();
        var source = Words(passage);
        var around = Words(passage + "\n" + context);
        var written = Words(reply);
        var names = facts.Names.Select(n => (Name: n.Trim(), Tokens: NameTokens(n))).Where(n => n.Tokens.Count > 0)
            .DistinctBy(n => string.Join(' ', n.Tokens)).ToList();

        // Dropped: a name the passage holds whole, of which the reply has no word.
        var dropped = names.Where(n => n.Tokens.All(t => Has(source, t)) && !n.Tokens.Any(t => Has(written, t)))
            // A name inside a longer one already counted ("Vera" in "Vera Saltmire") is not counted again.
            .Select(n => n.Name).ToList();
        dropped = dropped.Where(d => !dropped.Any(o => o != d && o.Contains(d, StringComparison.OrdinalIgnoreCase))).Take(6).ToList();
        if (dropped.Count > 0) problems.Add("It drops names the passage gives: " + string.Join(", ", dropped) + ". Keep every one of them, inflected as the language needs.");

        // Added: a name of the campaign's the reply has whole, of which neither the passage nor the text around it has a word.
        var added = names.Where(n => n.Tokens.All(t => Has(written, t)) && !n.Tokens.Any(t => Has(around, t))).Select(n => n.Name).Take(5).ToList();
        if (added.Count > 0) problems.Add("It names people or places the passage does not: " + string.Join(", ", added) + ". Use only names the passage gives.");

        // Capitalised words the passage never uses: names the model made up. The game's own words and the edition's names are not.
        var allowed = passage + "\n" + context + "\n" + string.Join("\n", facts.Vocabulary) + "\n" + string.Join("\n", facts.Names);
        var invented = BookChecks.NewNames(allowed, Marker().Replace(reply, "\n")).Where(w => !added.Any(a => a.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(5).ToList();
        if (invented.Count > 0) problems.Add("It introduces names that are not in the passage: " + string.Join(", ", invented) + ". Use only names the passage gives.");

        // Figures: none added, none dropped.
        var aroundNumbers = BookChecks.Numbers(passage + "\n" + context);
        foreach (var spelled in BookChecks.SpelledNumbers(passage + "\n" + context)) aroundNumbers.Add(spelled.ToString(CultureInfo.InvariantCulture));
        foreach (var (n, _) in DiceIn(passage + "\n" + context)) aroundNumbers.Add(n);
        var replyNumbers = BookChecks.Numbers(reply);
        var newNumbers = replyNumbers.Where(n => !aroundNumbers.Contains(n) && !IsPart(n, aroundNumbers))
            .Concat(BookChecks.SpelledNumbers(reply).Select(n => n.ToString(CultureInfo.InvariantCulture)).Where(n => !aroundNumbers.Contains(n)))
            .Where(n => n.Length > 0).Distinct().Take(5).ToList();
        if (newNumbers.Count > 0) problems.Add("It states numbers that are not in the passage: " + string.Join(", ", newNumbers) + ". Use only figures the passage gives.");

        var lost = Figures(passage).Where(f => !Kept(f, reply, written)).Take(6).ToList();
        if (lost.Count > 0) problems.Add("It drops figures the passage gives: " + string.Join(", ", lost) + ". Keep every figure, as digits.");
        return problems;
    }

    // "1,000" in the reply is "1" and "000" as well; a part of a figure the source has is not a new figure.
    private static bool IsPart(string n, HashSet<string> known) => n.All(c => c == '0') && known.Any(k => k.EndsWith(n, StringComparison.Ordinal));

    /// <summary>The words of a text, normalised: lower case, Hebrew points and other marks left out, ё as е, an English possessive cut.</summary>
    internal static List<string> Words(string text)
    {
        var plain = Marker().Replace(text, " ");
        var words = new List<string>();
        foreach (Match m in WordPattern().Matches(plain)) words.Add(Normalise(m.Value));
        return words;
    }

    private static string Normalise(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c == '’' ? '\'' : c);
        }

        var lower = sb.ToString().ToLowerInvariant().Replace('ё', 'е');
        if (lower.EndsWith("'s", StringComparison.Ordinal)) lower = lower[..^2];
        return lower.TrimEnd('\'');
    }

    internal static List<string> NameTokens(string name) =>
        Words(name).Where(t => t.Length >= 2 && !Stopwords.Contains(t)).Distinct().ToList();

    private static bool Has(List<string> words, string token) => words.Any(w => Matches(w, token));

    /// <summary>Whether a word of the text is the name's word <paramref name="token"/>, as the language writes it.</summary>
    internal static bool Matches(string word, string token)
    {
        if (word == token) return true;
        var script = token.FirstOrDefault(char.IsLetter);
        if (script is >= '֐' and <= '׿')
        {
            // Up to three prefix letters (ו, ה, ב, כ, ל, מ, ש) before the name. The word itself is tried first (above), so a name that
            // starts with one of them (מירה, לאה) is still matched whole.
            var extra = word.Length - token.Length;
            return extra is >= 1 and <= 3 && word.EndsWith(token, StringComparison.Ordinal) && word[..extra].All(c => HebrewPrefixes.Contains(c));
        }

        if (script is >= 'Ѐ' and <= 'ӿ')
        {
            var stem = token.Length > 3 && RussianFinals.Contains(token[^1]) ? token[..^1] : token;
            // A stem of two letters takes no ending: "Ия" is not "иных".
            if (stem.Length < 3) return false;
            return word.StartsWith(stem, StringComparison.Ordinal) && RussianEndings.Contains(word[stem.Length..]);
        }

        return false;
    }

    /// <summary>The figures of a passage a reply must keep: its numbers (list markers and block markers aside) and its dice.</summary>
    internal static List<string> Figures(string text)
    {
        var figures = new List<string>();
        var dice = DiceIn(text).ToList();
        figures.AddRange(dice.Select(d => d.Dice).Distinct());
        // A number that is only a die's part is kept with the die.
        var parts = new HashSet<string>(dice.SelectMany(d => DiceParts(d.Dice)), StringComparer.Ordinal);
        var stripped = Dice().Replace(text, " ");
        foreach (var n in Whole(stripped))
        {
            if (!parts.Contains(n) && !figures.Contains(n)) figures.Add(n);
        }

        return figures;
    }

    // The source's whole numbers (not their parts), as ASCII digits with separators removed.
    private static IEnumerable<string> Whole(string text)
    {
        var plain = Marker().Replace(text, " ");
        plain = Regex.Replace(plain, @"^[ \t]*\p{Nd}{1,3}[.)][ \t]+", string.Empty, RegexOptions.Multiline | RegexOptions.CultureInvariant, Timeout);
        foreach (Match m in Regex.Matches(plain, @"\p{Nd}+(?:[.,]\p{Nd}+)*", RegexOptions.CultureInvariant, Timeout))
        {
            var ascii = new string(m.Value.Where(c => c != ',').Select(c => char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c).ToArray());
            yield return ascii;
        }
    }

    private static IEnumerable<(string Number, string Dice)> DiceIn(string text)
    {
        foreach (Match m in Dice().Matches(Marker().Replace(text, " ")))
        {
            var n = Ascii(m.Groups["n"].Value);
            var s = Ascii(m.Groups["s"].Value);
            var dice = (n.Length == 0 ? "1" : n) + "d" + s;
            yield return (n.Length == 0 ? "1" : n, dice);
            yield return (s, dice);
        }
    }

    private static IEnumerable<string> DiceParts(string dice) => dice.Split('d');

    private static string Ascii(string digits) => new(digits.Select(c => char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c).ToArray());

    private static bool Kept(string figure, string reply, List<string> written)
    {
        if (figure.Contains('d'))
        {
            return DiceIn(reply).Any(d => d.Dice == figure);
        }

        if (Whole(Dice().Replace(reply, " ")).Contains(figure) || BookChecks.Numbers(reply).Contains(figure)) return true;
        if (!long.TryParse(figure, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
        // Written out in words.
        if (BookChecks.SpelledNumbers(reply).Contains(value)) return true;
        return written.Any(w => Spelled(w) == value);
    }

    private static readonly Dictionary<string, int> English = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["once"] = 1, ["twice"] = 2, ["single"] = 1, ["pair"] = 2, ["dozen"] = 12,
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8,
        ["ninth"] = 9, ["tenth"] = 10, ["eleventh"] = 11, ["twelfth"] = 12
    };

    // Russian stems of one to ten, ordinals too ("три", "трёх", "третий"), longest first.
    private static readonly (string Stem, int Value)[] Russian =
    [
        ("десят", 10), ("девят", 9), ("восем", 8), ("восьм", 8), ("седьм", 7), ("шест", 6), ("пят", 5), ("четверт", 4), ("четыр", 4),
        ("трет", 3), ("трое", 3), ("тро", 3), ("три", 3), ("тре", 3), ("втор", 2), ("двое", 2), ("дво", 2), ("два", 2), ("две", 2),
        ("дву", 2), ("перв", 1), ("один", 1), ("одн", 1), ("сем", 7)
    ];

    private static readonly Dictionary<string, int> Hebrew = new(StringComparer.Ordinal)
    {
        ["אחד"] = 1, ["אחת"] = 1, ["ראשון"] = 1, ["ראשונה"] = 1,
        ["שניים"] = 2, ["שתיים"] = 2, ["שני"] = 2, ["שתי"] = 2, ["שנייה"] = 2, ["שניה"] = 2,
        ["שלושה"] = 3, ["שלוש"] = 3, ["שלושת"] = 3, ["שלישי"] = 3, ["שלישית"] = 3,
        ["ארבעה"] = 4, ["ארבע"] = 4, ["ארבעת"] = 4, ["רביעי"] = 4, ["רביעית"] = 4,
        ["חמישה"] = 5, ["חמש"] = 5, ["חמשת"] = 5, ["חמישי"] = 5, ["חמישית"] = 5,
        ["שישה"] = 6, ["שש"] = 6, ["ששת"] = 6, ["שישי"] = 6, ["שישית"] = 6,
        ["שבעה"] = 7, ["שבע"] = 7, ["שבעת"] = 7, ["שביעי"] = 7, ["שביעית"] = 7,
        ["שמונה"] = 8, ["שמונת"] = 8, ["שמיני"] = 8, ["שמינית"] = 8,
        ["תשעה"] = 9, ["תשע"] = 9, ["תשעת"] = 9, ["תשיעי"] = 9, ["תשיעית"] = 9,
        ["עשרה"] = 10, ["עשר"] = 10, ["עשרת"] = 10, ["עשירי"] = 10, ["עשירית"] = 10
    };

    /// <summary>The number a word writes out, one to twelve, or null.</summary>
    internal static int? Spelled(string word)
    {
        if (English.TryGetValue(word, out var en)) return en;
        if (word.Length > 0 && word[0] is >= '֐' and <= '׿')
        {
            for (var cut = 0; cut <= 2 && cut < word.Length; cut++)
            {
                if (cut > 0 && !HebrewPrefixes.Contains(word[cut - 1])) break;
                if (Hebrew.TryGetValue(word[cut..], out var he)) return he;
            }

            return null;
        }

        foreach (var (stem, value) in Russian)
        {
            if (word.StartsWith(stem, StringComparison.Ordinal) && word.Length <= stem.Length + 4) return value;
        }

        return null;
    }
}
