using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>A labelled field of an entry (a bold label at the start of a paragraph) or one of its degree sections.</summary>
public sealed record LabelledText(string Label, string Text);

/// <summary>
/// An entry as the parser reads it, before redaction: the header, the trait line, the unlabelled paragraphs, the labelled fields and the
/// degree sections, all normalized. <see cref="Render"/> joins them into the one description text the redaction rows and the pack hold.
/// </summary>
public sealed class ParsedEntry
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Category { get; set; }
    public bool Approved { get; set; }
    public int RowNumber { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The action icon in the header: 1, 2, 3, reaction or free. Null when the header has none.</summary>
    public string? Actions { get; set; }

    /// <summary>The words of the trait line. They wait for chapter 2: the system has no trait type yet, so nothing writes them.</summary>
    public List<string> Traits { get; set; } = new();

    public List<string> Paragraphs { get; set; } = new();
    public List<LabelledText> Fields { get; set; } = new();
    public List<LabelledText> Degrees { get; set; } = new();

    /// <summary>Every paragraph, field and degree section in source order. An empty label is a plain paragraph.</summary>
    public List<LabelledText> Parts { get; set; } = new();

    public string Render() =>
        string.Join('\n', Parts.Select(p => p.Label.Length == 0 ? p.Text : $"**{p.Label.TrimEnd('.', ':', ' ')}.** {p.Text}".TrimEnd()));
}

/// <summary>An entry after redaction: what the pack file holds.</summary>
public sealed class FinalEntry
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Category { get; set; }
    public bool Approved { get; set; }
    public int RowNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Stage 4. Reads the blocks of each taken range into entries: the header by the entry-header font, then the trait line, labelled fields and degree sections.
/// Text is normalized (see <see cref="SpanText"/>), hyphenation at line ends is joined, action icons become words or an <c>actions</c> value and
/// parenthesized page references are dropped. A paragraph is cut where a line ends short of the block's right edge or where a known field label
/// starts a line. Anything the reader cannot place (an unmapped icon, a control character) fails the run with a count.
/// </summary>
public static partial class EntriesStage
{
    private const double ShortLine = 12;

    /// <summary>How far a line must start to the right of its block's left edge to be a paragraph's first line (the books indent by about nine points).</summary>
    private const double Indent = 4;

    private static readonly string[] DegreeNames = { "critical success", "success", "failure", "critical failure" };

    /// <summary>Field labels that start a paragraph even when the line before it ran to the edge. Other bold runs start one only after a short line.</summary>
    private static readonly HashSet<string> KnownLabels = new(StringComparer.Ordinal)
    {
        "critical success", "success", "failure", "critical failure", "trigger", "requirements", "frequency", "effect", "cost", "prerequisites",
        "access", "special", "duration", "range", "area", "targets", "defense", "saving throw", "heightened",
    };

    private static readonly Dictionary<string, (string Actions, string Text)> Icons = new(StringComparer.Ordinal)
    {
        ["[one-action]"] = ("1", "[1 action]"),
        ["[two-actions]"] = ("2", "[2 actions]"),
        ["[three-actions]"] = ("3", "[3 actions]"),
        ["[reaction]"] = ("reaction", "[reaction]"),
        ["[free-action]"] = ("free", "[free action]"),
    };

    public static List<ParsedEntry> Run(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, string book)
    {
        var compounds = Compounds(blocks, ranges);
        var entries = new List<ParsedEntry>();
        foreach (var range in ranges)
        {
            var slice = blocks.Blocks.GetRange(range.Start, range.End - range.Start);
            entries.AddRange(range.Row.Parser == "rules" ? ParseRule(range, slice, book, compounds) : ParseEntries(range, slice, book, compounds));
        }

        // The redaction rows name an entry by id alone, so an id may not repeat even across types.
        var duplicate = entries.GroupBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new DegreesException($"{duplicate.Count()} entries ({string.Join(", ", duplicate.Select(e => e.Type).Distinct())}) share one id; ids come from names, so a name is repeated.");
        }

        return entries.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    private static List<ParsedEntry> ParseRule(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var name = range.Own ? range.Row.OwnName! : range.Row.PathOf(range)[^1];
        var entry = NewEntry(range, book, Clean(name), compounds);
        new BodyReader(entry, compounds).ReadAll(slice);
        return new List<ParsedEntry> { entry };
    }

    private static List<ParsedEntry> ParseEntries(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var result = new List<ParsedEntry>();
        var readers = new List<BodyReader>();
        var traitsOpen = false;
        foreach (var block in slice)
        {
            if (block.Role == "entry-header")
            {
                var header = Clean(string.Join(' ', block.Lines));
                string? actions = null;
                foreach (var (_, icon) in Icons)
                {
                    if (header.Contains(icon.Text, StringComparison.Ordinal))
                    {
                        actions = icon.Actions;
                        header = header.Replace(icon.Text, string.Empty, StringComparison.Ordinal);
                    }
                }

                var entry = NewEntry(range, book, Regex.Replace(header, " {2,}", " ").Trim(), compounds);
                entry.Actions = actions;
                result.Add(entry);
                readers.Add(new BodyReader(entry, compounds));
                traitsOpen = true;
                continue;
            }

            if (traitsOpen && block.Role == "tag")
            {
                result[^1].Traits.AddRange(Clean(string.Join(' ', block.Lines)).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                continue;
            }

            traitsOpen = false;
            readers[^1].Read(block);
        }

        foreach (var reader in readers)
        {
            reader.Finish();
        }

        return result;
    }

    private static ParsedEntry NewEntry(TakenRange range, string book, string name, HashSet<string> compounds)
    {
        var id = Slug(name);
        if (id.Length == 0)
        {
            throw new DegreesException($"Chapter {range.Row.Chapter}: an entry name has no letters or digits to make an id from.");
        }

        return new ParsedEntry
        {
            Id = id,
            Type = range.Row.Target,
            Folder = range.Row.FolderName,
            Chapter = range.Row.Chapter,
            Source = book + " > " + string.Join(" > ", range.Row.PathOf(range)),
            Category = range.Row.Category,
            Approved = range.Row.Approved,
            RowNumber = range.RowNumber,
            Name = name,
        };
    }

    /// <summary>Lowercase ASCII letters and digits, with a hyphen for each run of anything else. Diacritics fall away.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in SpanText.Normalize(name).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        return sb.ToString().Trim('-');
    }

    /// <summary>Normalizes one piece of text and checks it: page references go, icons are mapped, and nothing unreadable may remain.</summary>
    internal static string Clean(string text)
    {
        var t = SpanText.Normalize(text);
        foreach (var (token, icon) in Icons)
        {
            t = t.Replace(token, icon.Text, StringComparison.Ordinal);
        }

        t = PageRef().Replace(t, string.Empty);
        t = Regex.Replace(t, " {2,}", " ").Trim();

        // A space the extraction leaves before a stop, comma, semicolon or colon (after an italic run, say) is not in the book's text.
        t = SpaceBeforeStop().Replace(t, "$1");
        var unmapped = Regex.Matches(t, @"\[[a-z]+(?:-[a-z]+)*-actions?\]").Count(m => !Icons.Values.Any(i => i.Text == m.Value));
        var unreadable = t.Count(c => (char.IsControl(c) && c != '\n') || char.GetUnicodeCategory(c) == UnicodeCategory.PrivateUse);
        if (unmapped + unreadable > 0)
        {
            throw new DegreesException($"Entry text has {unmapped} unmapped icon token(s) and {unreadable} unreadable character(s).");
        }

        return t;
    }

    /// <summary>Hyphenated words seen inside a line anywhere in the taken text, so a hyphen at a line end is kept for them.</summary>
    private static HashSet<string> Compounds(BlocksResult blocks, IReadOnlyList<TakenRange> ranges)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var range in ranges)
        {
            for (var i = range.Start; i < range.End; i++)
            {
                foreach (var line in blocks.Blocks[i].Lines)
                {
                    // Matched on the raw line: only a real hyphen makes a compound. A dash (en or em) between two words is not one.
                    foreach (Match m in Compound().Matches(line))
                    {
                        set.Add(SpanText.Normalize(m.Value).ToLowerInvariant());
                    }
                }
            }
        }

        return set;
    }

    [GeneratedRegex(@"\s*\((?:see )?pages? \d+(?:\s*(?:-|,|and)\s*\d+)*\)", RegexOptions.IgnoreCase)]
    private static partial Regex PageRef();

    [GeneratedRegex(@" +([.,;:])")]
    private static partial Regex SpaceBeforeStop();

    [GeneratedRegex(@"\p{L}+[-‐‑]\p{L}+")]
    private static partial Regex Compound();

    /// <summary>Reads the body blocks of one entry into paragraphs, labelled fields and degree sections.</summary>
    private sealed class BodyReader
    {
        private readonly ParsedEntry _entry;
        private readonly HashSet<string> _compounds;
        private readonly List<(string Label, StringBuilder Text)> _parts = new();
        private bool _previousShort = true;
        private (int Page, int Column)? _previousPlace;

        public BodyReader(ParsedEntry entry, HashSet<string> compounds)
        {
            _entry = entry;
            _compounds = compounds;
        }

        public void ReadAll(List<Block> blocks)
        {
            foreach (var b in blocks)
            {
                Read(b);
            }

            Finish();
        }

        public void Read(Block block)
        {
            if (block.Role == "table" && block.Rows != null)
            {
                foreach (var row in block.Rows)
                {
                    Start(string.Empty);
                    Append(string.Join(" | ", row));
                }

                _previousShort = true;
                _previousPlace = (block.Page, block.Column);
                return;
            }

            var samePlace = _previousPlace == (block.Page, block.Column);
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var info = block.Info?[i] ?? new LineInfo(block.Left, block.Right, string.Empty);
                var text = block.Lines[i];
                var label = SpanText.Normalize(info.Label).TrimEnd('.', ':', ' ');
                // A first-line indent starts a paragraph even after a line that ran to the edge. A bullet's own wrapped lines are not read this way.
                var indented = _parts.Count > 0 && info.Left - block.Left > Indent && !_parts[^1].Text.ToString().StartsWith('•');
                var startsParagraph = _parts.Count == 0 || text.StartsWith('•') || indented || (i == 0 ? samePlace || _previousShort : _previousShort);
                if (label.Length > 0 && KnownLabels.Contains(label.ToLowerInvariant()))
                {
                    Start(label);
                    text = text[Math.Min(text.Length, info.Label.Length)..].TrimStart();
                }
                else if (startsParagraph)
                {
                    Start(string.Empty);
                }

                Append(text);
                _previousShort = info.Right < block.Right - ShortLine;
            }

            _previousPlace = (block.Page, block.Column);
        }

        private void Start(string label) => _parts.Add((label, new StringBuilder()));

        private void Append(string line)
        {
            var sb = _parts[^1].Text;
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

            // A hyphen at a line end: a word the book also writes with its hyphen inside a line keeps it, any other is a break in the word and goes.
            if (sb[^1] == '-' && sb.Length >= 2 && char.IsLetter(sb[^2]) && char.IsLower(text[0]))
            {
                if (!_compounds.Contains(Joined(sb, text)))
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

        public void Finish()
        {
            // A paragraph that follows a labelled one stays a paragraph of its own: it is cut by a short line, so it is the closing prose, not the label's text.
            foreach (var (label, sb) in _parts)
            {
                var text = Clean(sb.ToString());
                if (label.Length == 0)
                {
                    if (text.Length > 0)
                    {
                        _entry.Paragraphs.Add(text);
                        _entry.Parts.Add(new LabelledText(string.Empty, text));
                    }

                    continue;
                }

                var part = new LabelledText(label, text);
                (DegreeNames.Contains(label.ToLowerInvariant()) ? _entry.Degrees : _entry.Fields).Add(part);
                _entry.Parts.Add(part);
            }
        }
    }
}
