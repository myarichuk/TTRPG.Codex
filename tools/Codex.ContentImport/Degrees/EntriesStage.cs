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

    /// <summary>The level in the header, for a row whose headers carry one (<see cref="TakeRow.LevelWord"/>).</summary>
    public int? Level { get; set; }

    /// <summary>The words of the trait line. They wait for chapter 2: the system has no trait type yet, so nothing writes them.</summary>
    public List<string> Traits { get; set; } = new();

    public List<string> Paragraphs { get; set; } = new();
    public List<LabelledText> Fields { get; set; } = new();
    public List<LabelledText> Degrees { get; set; } = new();

    /// <summary>Every paragraph, field and degree section in source order. An empty label is a plain paragraph.</summary>
    public List<LabelledText> Parts { get; set; } = new();

    /// <summary>
    /// Labelled fields the target type has a field of its own for (a feat's prerequisites, say): the type's field key and the text, in source order. They are taken out of
    /// <see cref="Parts"/>, so <see cref="Render"/> does not repeat them.
    /// </summary>
    public List<LabelledText> Extracted { get; set; } = new();

    public string Render() =>
        string.Join('\n', Parts.Select(p =>
            p.Label.Length == 0 ? p.Text
            // A bold run-in heading that starts with a bullet is a list item.
            : p.Label.StartsWith("• ", StringComparison.Ordinal) ? $"- **{p.Label[2..].TrimEnd('.', ':', ' ')}.** {p.Text}".TrimEnd()
            : $"**{p.Label.TrimEnd('.', ':', ' ')}.** {p.Text}".TrimEnd()));

    /// <summary>The text fields the redaction rows may name: the name and description, and any extracted field (its key is the field name).</summary>
    public Dictionary<string, string> RedactionFields()
    {
        var fields = new Dictionary<string, string> { ["name"] = Name, ["description"] = Render() };
        foreach (var e in Extracted)
        {
            fields[e.Label] = e.Text;
        }

        return fields;
    }
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

    /// <summary>The level, the action cost and the trait keys, for a type that has them (a feat).</summary>
    public int? Level { get; set; }

    public string? Actions { get; set; }
    public List<string> Traits { get; set; } = new();

    /// <summary>The extracted labelled fields after redaction, by the target type's field key, in the type's order.</summary>
    public List<LabelledText> Extracted { get; set; } = new();
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
            var slice = range.BlocksIn(blocks);
            entries.AddRange(range.Row.Parser switch
            {
                "rules" => ParseRule(range, slice, book, compounds),
                "glossary" => ParseGlossary(range, slice, book, compounds),
                _ => ParseEntries(range, slice, book, compounds),
            });
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
            if (range.Row.IsHeader(block))
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

                header = Regex.Replace(header, " {2,}", " ").Trim();
                int? level = null;
                if (range.Row.LevelWord != null)
                {
                    // "NAME [icon] WORD 3": the name is set in capitals, the level is the number after the row's word.
                    var m = Regex.Match(header, @"^(?<name>.+?)\s+" + Regex.Escape(range.Row.LevelWord) + @"\s+(?<level>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    level = int.Parse(m.Groups["level"].Value, CultureInfo.InvariantCulture);
                    header = TitleCase(m.Groups["name"].Value);
                }

                var entry = NewEntry(range, book, header, compounds);
                entry.Actions = actions;
                entry.Level = level;
                result.Add(entry);
                readers.Add(new BodyReader(entry, compounds, range.Row.LevelWord != null));
                traitsOpen = true;
                continue;
            }

            if (traitsOpen && block.Role == "tag")
            {
                var words = Clean(string.Join(' ', block.Lines)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // A trait line set in capitals (the feats' tag line) is a list of trait keys.
                result[^1].Traits.AddRange(range.Row.LevelWord != null ? words.Select(w => w.ToLowerInvariant()) : words);
                continue;
            }

            // A lone line in capitals, set well in from the column's edge where a header's left edge is (a label of the page's art), is not entry text.
            if (range.Row.LevelWord != null && IsStrayLabel(block, slice, range.Row))
            {
                continue;
            }

            traitsOpen = false;
            readers[^1].Read(block);
        }

        foreach (var reader in readers)
        {
            reader.Finish();
        }

        if (range.Row.LevelWord != null)
        {
            foreach (var entry in result)
            {
                ExtractFields(entry);
            }
        }

        return result;
    }

    internal static bool IsStrayLabel(Block block, List<Block> slice, TakeRow row)
    {
        if (block.Role != "body" || block.Lines.Count != 1 || block.Lines[0].Any(char.IsLower) || !block.Lines[0].Any(char.IsLetter))
        {
            return false;
        }

        var edge = slice.Where(b => b.Page == block.Page && b.Column == block.Column && row.IsHeader(b)).Select(b => b.Left).DefaultIfEmpty(double.NaN).Min();
        return !double.IsNaN(edge) && block.Left - edge > 10;
    }

    /// <summary>The labels a feat has a field of its own for (the system's feat type): label in lowercase, then the field key. Every other label stays in the description.</summary>
    private static readonly string[] FeatFieldKeys = { "prerequisites", "frequency", "trigger", "requirements", "cost", "special" };

    private static void ExtractFields(ParsedEntry entry)
    {
        foreach (var key in FeatFieldKeys)
        {
            var found = entry.Parts.Where(p => p.Label.Length > 0 && p.Label.TrimEnd('.', ':', ' ').Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count > 1)
            {
                throw new DegreesException($"An entry of chapter {entry.Chapter} has the field label for '{key}' more than once.");
            }

            if (found.Count == 1)
            {
                entry.Extracted.Add(new LabelledText(key, found[0].Text));
                entry.Parts.Remove(found[0]);
                entry.Fields.Remove(found[0]);
            }
        }
    }

    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal) { "a", "an", "and", "of", "the", "to", "in", "on", "for", "or", "at", "by" };

    /// <summary>True for a word of a name in title case: it starts with a capital, or it is a small joining word.</summary>
    public static bool IsNameWord(string word) => word.Length > 0 && (char.IsUpper(word[0]) || SmallWords.Contains(word));

    /// <summary>A name the book sets in capitals, in title case: each word and each part of a hyphenated word begins with a capital, except a small joining word that is not first.</summary>
    public static string TitleCase(string upper)
    {
        var words = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((w, i) =>
        {
            var lower = w.ToLowerInvariant();
            return i > 0 && SmallWords.Contains(lower)
                ? lower
                : string.Join('-', lower.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
        }));
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
            foreach (var i in range.Indexes)
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

    [GeneratedRegex(@"^\((?:see )?pages? \d+(?:\s*(?:-|,|and)\s*\d+)*\)[.,;:]?$", RegexOptions.IgnoreCase)]
    private static partial Regex PageRefLine();

    [GeneratedRegex(@" +([.,;:])")]
    private static partial Regex SpaceBeforeStop();

    [GeneratedRegex(@"\p{L}+[-‐‑]\p{L}+")]
    private static partial Regex Compound();

    /// <summary>Reads the body blocks of one entry into paragraphs, labelled fields and degree sections.</summary>
    private sealed class BodyReader
    {
        private readonly ParsedEntry _entry;
        private readonly HashSet<string> _compounds;
        private readonly bool _runOn;
        private readonly List<(string Label, StringBuilder Text)> _parts = new();
        private bool _previousShort = true;
        private (int Page, int Column)? _previousPlace;

        /// <param name="runOn">
        /// True for a row whose entries run on in one column (the feats): a line of a labelled paragraph that wraps is set in from the edge (a hanging indent), and a block that
        /// the extraction cut off in the middle of a paragraph is not a paragraph of its own. Only a short line before it, a label or a first-line indent starts a paragraph.
        /// </param>
        public BodyReader(ParsedEntry entry, HashSet<string> compounds, bool runOn = false)
        {
            _entry = entry;
            _compounds = compounds;
            _runOn = runOn;
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
            // A block that opens with a line that is only a bold label is a small table (a title row, then one row to a line) in a run-on row.
            var rowsBlock = _runOn && block.Info is { Count: > 1 } && block.Info[0].Label.Length > 0
                && SpanText.Normalize(block.Lines[0]).Trim() == SpanText.Normalize(block.Info[0].Label).Trim() && !KnownLabels.Contains(SpanText.Normalize(block.Info[0].Label).Trim().ToLowerInvariant());
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var info = block.Info?[i] ?? new LineInfo(block.Left, block.Right, string.Empty);
                var text = block.Lines[i];
                var label = SpanText.Normalize(info.Label).TrimEnd('.', ':', ' ');
                // A first-line indent starts a paragraph even after a line that ran to the edge. A bullet's own wrapped lines are not read this way.
                // A line that is only a page pointer, set in from the edge (a labelled line that ran on), continues the line before it; the pointer itself is dropped by Clean.
                var indented = _parts.Count > 0 && info.Left - block.Left > Indent && !_parts[^1].Text.ToString().StartsWith('•') && !(_runOn && PageRefLine().IsMatch(text));
                var startsParagraph = _parts.Count == 0 || text.StartsWith('•') || indented || (i == 0 ? (samePlace && !_runOn) || _previousShort : _previousShort);
                if (_runOn && _parts.Count > 0 && _parts[^1].Label.Length > 0 && !text.StartsWith('•'))
                {
                    // A labelled paragraph wraps with a hanging indent: its lines after the first are set in from the edge, and a line at the edge is the next paragraph.
                    startsParagraph = info.Left - block.Left <= Indent;
                }

                if (_runOn && startsParagraph && !indented && _parts.Count > 0 && text.Length > 0 && char.IsLower(text[0]) && !text.StartsWith('•')
                    && _parts[^1].Text.ToString().TrimEnd() is { Length: > 0 } tail && !".!?:;\"”)".Contains(tail[^1]))
                {
                    startsParagraph = false; // a line set short by the layout, not by the end of a sentence: the text goes on
                }
                if (rowsBlock)
                {
                    startsParagraph = true; // a table's rows: one line each, whatever the line before it ran to
                }

                if (label.Length > 0 && KnownLabels.Contains(label.ToLowerInvariant()))
                {
                    Start(label);
                    text = text[Math.Min(text.Length, info.Label.Length)..].TrimStart();
                }
                else if (_runOn && startsParagraph && label.Length > 0 && text.StartsWith(info.Label, StringComparison.Ordinal) && text.Length > info.Label.Length)
                {
                    // A bold run-in heading at the start of a paragraph (a pet's statistics, say) is a label like the known ones.
                    Start(label);
                    text = text[info.Label.Length..].TrimStart();
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
            JoinLine(_parts[^1].Text, line, _compounds);
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
