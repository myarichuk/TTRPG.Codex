using System.Text;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// What the glossary parser found in the blocks of one range: the trait definitions (the bold term and its text, page pointers removed), the trait lines that
/// only point elsewhere (a page list, or a short mark and a page: the trait is defined in another section or book), and how many times the word "(trait)"
/// occurs in the range at all. The last number is read without looking at entry starts, so it is the second count the first two must add up to.
/// </summary>
/// <param name="Dropped">The lines read past and not kept: the index, the other glossary entries and the pointer-only trait lines. The denylist generator hashes their capitalized words.</param>
public sealed record GlossaryScan(List<(string Name, string Text)> Definitions, int Pointers, int TraitTokens, List<string> Dropped);

/// <summary>
/// The <c>glossary</c> parser. The book's glossary and index are one alphabetical list. An entry starts at a line that begins with a bold term; the lines after it
/// belong to it until the next bold term, or until a line ends in a list of page numbers (the sub-entries of the index follow such a line in the same, unbolded
/// style). Only an entry whose term is followed by the plain word "(trait)" and some text is a trait definition. Every other entry, the index, is read past and not kept.
/// </summary>
public static partial class EntriesStage
{
    private const string TraitMark = "(trait)";

    /// <summary>A list of page numbers at the end of a line, after a sentence stop or on its own: <c>10, 15, 414–415</c>.</summary>
    [GeneratedRegex(@"(?:^|(?<=[.!?)\]”""’]))\s*(?:[A-Z]{2,5} )?\d+(?:[-–]\d+)?(?:,\s*(?:[A-Z]{2,5} )?\d+(?:[-–]\d+)?)*$")]
    private static partial Regex PageList();

    /// <summary>A trait line with nothing but pages. A page may follow a short mark (the other book's abbreviation).</summary>
    [GeneratedRegex(@"^(?:[A-Z]{2,5} )?\d+(?:[-–]\d+)?(?:,\s*(?:[A-Z]{2,5} )?\d+(?:[-–]\d+)?)*$")]
    private static partial Regex OnlyPages();

    /// <summary>Appends one line to the text so far: the join rules of every parser (a dash or a hyphen at a line end, then the next word).</summary>
    internal static void JoinLine(StringBuilder sb, string line, HashSet<string> compounds)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (sb.Length == 0)
        {
            sb.Append(text);
            return;
        }

        // A dash at a line end (en or em) joins with no space: the word after it follows straight on.
        if (sb[^1] is '–' or '—')
        {
            sb.Append(text);
            return;
        }

        // A hyphen after a number at a line end ("100-" then "foot") is the hyphen of the compound, which stays, with no space after it. A suspended hyphen ("2- and 3-action") is not joined.
        if (sb[^1] == '-' && sb.Length >= 2 && char.IsDigit(sb[^2]) && char.IsLower(text[0]) && !(text.Split(' ')[0] is "and" or "or" or "to"))
        {
            sb.Append(text);
            return;
        }

        // A hyphen at a line end: a word the book also writes with its hyphen inside a line keeps it, any other is a break in the word and goes.
        if (sb[^1] == '-' && sb.Length >= 2 && char.IsLetter(sb[^2]) && char.IsLower(text[0]))
        {
            if (!compounds.Contains(Joined(sb, text)))
            {
                sb.Length--;
            }

            sb.Append(text);
            return;
        }

        sb.Append(' ').Append(text);
    }

    /// <summary>The hyphenated word a line break would split, written with its hyphen: the end of the text so far and the start of the next line.</summary>
    private static string Joined(StringBuilder sb, string next)
    {
        var start = sb.Length - 1;
        while (start > 0 && char.IsLetter(sb[start - 1]))
        {
            start--;
        }

        var end = 0;
        while (end < next.Length && char.IsLetter(next[end]))
        {
            end++;
        }

        return (sb.ToString(start, sb.Length - start) + next[..end]).ToLowerInvariant();
    }

    /// <summary>Reads the glossary lines of the blocks, in reading order, into trait definitions (see <see cref="GlossaryScan"/>).</summary>
    public static GlossaryScan ScanGlossary(IEnumerable<Block> blocks, HashSet<string> compounds)
    {
        var definitions = new List<(string, string)>();
        var pointers = 0;
        var tokens = 0;
        var dropped = new List<string>();
        string? name = null;
        StringBuilder? text = null;

        void Close()
        {
            if (name != null)
            {
                definitions.Add((name, text!.ToString()));
            }

            name = null;
            text = null;
        }

        // Cuts a trailing page list off a line. True when there was one: the entry ends there.
        static bool StripPages(ref string line)
        {
            var m = PageList().Match(line);
            if (!m.Success)
            {
                return false;
            }

            line = line[..m.Index].TrimEnd();
            return true;
        }

        foreach (var block in blocks)
        {
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var line = block.Lines[i];
                var label = block.Info?[i].Label ?? string.Empty;
                tokens += line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w == TraitMark);
                if (label.Trim().Length > 0)
                {
                    Close();
                    var after = line.StartsWith(label, StringComparison.Ordinal) ? line[label.Length..].TrimStart() : string.Empty;
                    if (!after.StartsWith(TraitMark, StringComparison.Ordinal) || (after.Length > TraitMark.Length && after[TraitMark.Length] != ' '))
                    {
                        dropped.Add(line);
                        continue;
                    }

                    var rest = after[TraitMark.Length..].Trim();
                    if (rest.Length == 0 || OnlyPages().IsMatch(rest))
                    {
                        pointers++;
                        dropped.Add(line);
                        continue;
                    }

                    var ended = StripPages(ref rest);
                    name = label.Trim();
                    text = new StringBuilder();
                    JoinLine(text, rest, compounds);
                    if (ended)
                    {
                        Close();
                    }
                }
                else if (name != null)
                {
                    var rest = line;
                    var ended = StripPages(ref rest);
                    JoinLine(text!, rest, compounds);
                    if (ended)
                    {
                        Close();
                    }
                }
                else
                {
                    dropped.Add(line);
                }
            }
        }

        Close();
        return new GlossaryScan(definitions, pointers, tokens, dropped);
    }

    private static List<ParsedEntry> ParseGlossary(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var result = new List<ParsedEntry>();
        foreach (var (rawName, rawText) in ScanGlossary(slice, compounds).Definitions)
        {
            var name = Clean(rawName);
            if (name.Length == 0)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}: a glossary term is empty after cleaning.");
            }

            name = char.ToUpperInvariant(name[0]) + name[1..];
            var text = Clean(rawText);
            if (text.Length == 0)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}: glossary entry number {result.Count + 1} has no text after cleaning.");
            }

            var entry = NewEntry(range, book, name, compounds);
            entry.Paragraphs.Add(text);
            entry.Parts.Add(new LabelledText(string.Empty, text));
            result.Add(entry);
        }

        return result;
    }
}
