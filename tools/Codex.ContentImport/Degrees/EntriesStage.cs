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

    /// <summary>For an action: the key of the skill it belongs to, or null for a general skill action.</summary>
    public string? Skill { get; set; }

    /// <summary>For an action: the least proficiency it needs, <c>untrained</c> or <c>trained</c>.</summary>
    public string? Proficiency { get; set; }

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

    public string? Skill { get; set; }
    public string? Proficiency { get; set; }

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
                "actions" => ParseActions(range, slice, book, compounds),
                _ => ParseEntries(range, slice, book, compounds),
            });
        }

        // An action's id is its name's slug, so it can equal a feat's, a condition's or a rule's. The redaction rows name an entry by id alone, so an id may not repeat across
        // types: the action takes the suffix (see ActionIdSuffix). Two entries of one type with one id are still a mistake and fail below.
        var taken = entries.Where(e => e.Type != "action").Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var action in entries.Where(e => e.Type == "action" && taken.Contains(e.Id)))
        {
            action.Id += ActionIdSuffix;
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
        new BodyReader(entry, compounds, false, range.Row.TableRows).ReadAll(slice);
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
                readers.Add(new BodyReader(entry, compounds, range.Row.LevelWord != null, range.Row.TableRows));
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

/// <summary>
    /// The suffix an action's id takes when its name's slug is also the id of an entry of another type (a feat, a condition or a rule): <c>hide</c> and a feat of that name would
    /// otherwise share one id, and a redaction row names an entry by id alone. The suffix is a plain word, so the id stays readable and stable.
    /// </summary>
    public const string ActionIdSuffix = "-action";

    /// <summary>The labelled lines an action has a field of its own for (the system's action type, after its action cost, traits, skill and proficiency).</summary>
    private static readonly string[] ActionFieldKeys = { "frequency", "trigger", "requirements" };

    private static readonly Regex SkillTitle = new(@"^(?<name>.+?)\s+\([A-Za-z]{2,4}\)$", RegexOptions.CultureInvariant);
    private static readonly Regex GeneralTitle = new(@"\s\((?<rank>Trained|Untrained)\)$", RegexOptions.CultureInvariant);
    private static readonly Regex TrainedActionsTitle = new(@"\sTrained Actions?$", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingParenthesis = new(@"\s*\([^)]*\)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A box of examples set in from the column's edge under a bold title that names them (sample tasks by proficiency, a worked example). It is read past: it holds names and
    /// narrative, and none of the rules.
    /// </summary>
    private static readonly Regex ExampleTitle = new(@"^(?:Sample\s.+|.+\s(?:Tasks|Example))$", RegexOptions.CultureInvariant);

    /// <summary>True for a block that opens with a title line naming a box of examples: a short line in title case, ending in the word for sample tasks or an example, or starting with the word sample.</summary>
    internal static bool IsExampleBox(Block block)
    {
        var title = SpanText.Normalize(block.Lines[0]).Trim();
        return title.Length <= 60 && ExampleTitle.IsMatch(title) && title.Split(' ').All(IsNameWord);
    }

    /// <summary>The blocks of a slice that the actions parser reads past as boxes of examples: each box's first block, and the blocks the layout cut it into (body blocks at the same left edge).</summary>
    public static List<Block> ExampleBoxes(List<Block> slice)
    {
        var result = new List<Block>();
        Block? box = null;
        foreach (var b in slice)
        {
            if (b.Role == "body" && b.Lines.Count >= 2 && IsExampleBox(b))
            {
                box = b;
                result.Add(b);
            }
            else if (box != null && b.Role == "body" && Math.Abs(b.Left - box.Left) <= 1)
            {
                result.Add(b);
            }
            else
            {
                box = null;
            }
        }

        return result;
    }

    private enum ActionMode
    {
        Lead,
        Rule,
        Intro,
        Action,
        Past,
    }

    /// <summary>
    /// The <c>actions</c> parser, for a skills chapter. A range holds a sequence of headings, and each says what the blocks after it are: a heading in the section font is a skill
    /// (its prose is a rule entry, and the actions after it are untrained ones); an entry-header ending in "(Trained)" or "(Untrained)" introduces a general skill action (the text
    /// before the action's own heading is the start of its description); an entry-header ending in "Trained Action(s)" switches the skill's actions to trained, and the prose under it
    /// is read past (it only points to the general actions); any other entry-header, or a block-header heading in title case, is a subsection whose prose is a rule entry; a
    /// block-header heading with an action icon or a trait line below it is an action; a block-header in capitals without either is the caption of a table. Every block of the range
    /// must fall in one of these, or the run fails.
    /// </summary>
    private static List<ParsedEntry> ParseActions(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var row = range.Row;
        var result = new List<ParsedEntry>();
        ParsedEntry? open = null;
        BodyReader? reader = null;
        string? skill = null;
        string? place = null;
        var proficiency = "untrained";
        var rank = "untrained";
        var intro = new List<Block>();
        var mode = ActionMode.Lead;
        var traitsOpen = false;
        Block? box = null;
        var lead = false;
        var readers = new List<BodyReader>();
        BodyReader? skillReader = null;
        (ParsedEntry Entry, BodyReader Reader)? held = null;

        void Close()
        {
            open = null;
            reader = null;
            traitsOpen = false;
        }

        BodyReader NewReader(ParsedEntry entry, bool runOn)
        {
            var made = new BodyReader(entry, compounds, runOn, row.TableRows);
            readers.Add(made);
            return made;
        }

        void OpenRule(string name)
        {
            Close();
            var entry = NewEntry(range, book, name, compounds, "rule", "rules", row.Category, place);
            result.Add(entry);
            open = entry;
            reader = NewReader(entry, false);
            mode = ActionMode.Rule;
        }

        for (var i = 0; i < slice.Count; i++)
        {
            var b = slice[i];
            var text = Clean(string.Join(' ', b.Lines));
            if (box != null && b.Role != "body")
            {
                box = null;
            }

            // A table set in a box of its own between two blocks of one entry's text (a caption, then the table) is an entry of its own; the text that follows it goes on in the entry it interrupted.
            if (held is { } returning && b.Role is not ("body" or "table"))
            {
                open = returning.Entry;
                reader = returning.Reader;
                held = null;
            }

            switch (b.Role)
            {
                case "section":
                {
                    var m = SkillTitle.Match(text);
                    if (!m.Success)
                    {
                        throw new DegreesException($"Chapter {row.Chapter}: a heading in the section font on page {b.Page} is not a skill's name and key attribute.");
                    }

                    skill = Slug(m.Groups["name"].Value);
                    place = m.Groups["name"].Value;
                    proficiency = "untrained";
                    OpenRule(place);
                    skillReader = reader;
                    break;
                }

                case "entry-header":
                {
                    var general = GeneralTitle.Match(text);
                    if (general.Success)
                    {
                        Close();
                        skill = null;
                        place = null;
                        rank = general.Groups["rank"].Value.ToLowerInvariant();
                        intro.Clear();
                        mode = ActionMode.Intro;
                    }
                    else if (TrainedActionsTitle.IsMatch(text))
                    {
                        Close();
                        proficiency = "trained";
                        mode = ActionMode.Past;
                        skillReader?.EndParagraph();
                    }
                    else
                    {
                        OpenRule(text);
                    }

                    break;
                }

                case "block-header" when BlocksStage.IsActionHeader(slice, i):
                {
                    Close();
                    var header = text;
                    string? cost = null;
                    foreach (var (_, icon) in Icons)
                    {
                        if (header.Contains(icon.Text, StringComparison.Ordinal))
                        {
                            cost = icon.Actions;
                            header = header.Replace(icon.Text, string.Empty, StringComparison.Ordinal);
                        }
                    }

                    var entry = NewEntry(range, book, TitleCase(Regex.Replace(header, " {2,}", " ").Trim()), compounds, sourceTail: place);
                    entry.Actions = cost;
                    entry.Skill = skill;
                    entry.Proficiency = skill == null ? rank : proficiency;
                    result.Add(entry);
                    open = entry;
                    // An action's lines wrap with a hanging indent, as a feat's do.
                    reader = NewReader(entry, true);
                    foreach (var introBlock in intro)
                    {
                        reader.Read(introBlock, plain: true);
                    }

                    reader.EndParagraph();

                    intro.Clear();
                    mode = ActionMode.Action;
                    traitsOpen = true;
                    break;
                }

                case "block-header" when text.Any(char.IsLower):
                    OpenRule(TrailingParenthesis.Replace(text, string.Empty));
                    break;

                case "block-header":
                {
                    // The caption of a table, set in capitals: the text of the entry that holds the table, in title case.
                    if (reader == null)
                    {
                        throw new DegreesException($"Chapter {row.Chapter}: a table caption on page {b.Page} column {b.Column} comes before any entry that could hold it.");
                    }

                    if (open is { Type: "rule" })
                    {
                        // The caption of a table that interrupts a subsection's text: the table is an entry of its own (a rule), and the subsection goes on after it.
                        held = (open, reader);
                        var table = NewEntry(range, book, TitleCase(text), compounds, "rule", "rules", row.Category, place);
                        result.Add(table);
                        open = table;
                        reader = NewReader(table, false);
                    }
                    else
                    {
                        reader.Read(b with { Lines = new List<string> { TitleCase(text) }, Info = null });
                    }

                    break;
                }

                case "tag" when traitsOpen && open is { Type: "action" }:
                    open.Traits.AddRange(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()));
                    break;

                default:
                    if (b.Role == "body" && b.Lines.Count >= 2 && IsExampleBox(b))
                    {
                        box = b;
                        break;
                    }

                    if (box != null)
                    {
                        if (b.Role == "body" && Math.Abs(b.Left - box.Left) <= 1)
                        {
                            break; // the rest of an example box the layout cut into two blocks
                        }

                        box = null;
                    }

                    if (mode == ActionMode.Intro)
                    {
                        intro.Add(b);
                    }
                    else if (mode == ActionMode.Past)
                    {
                        // The prose under a skill's "Trained Action(s)" heading (what the skill needs to be trained for, and from which tradition) goes on in the skill's own rule entry.
                        skillReader?.Read(b);
                    }
                    else if (reader != null)
                    {
                        traitsOpen = false;
                        reader.Read(b);
                    }
                    else if (row.LeadName != null && b.Role == "rules" && !lead)
                    {
                        lead = true;
                        OpenRule(row.LeadName);
                        reader!.Read(b);
                    }
                    else if (b.Role != "body" && b.Role != "table")
                    {
                        throw new DegreesException($"Chapter {row.Chapter}: a {b.Role} block on page {b.Page} column {b.Column} comes before the first heading of the range, and the row names no lead rule for it.");
                    }

                    break;
            }
        }

        Close();
        foreach (var finished in readers)
        {
            finished.Finish();
        }

        foreach (var entry in result.Where(e => e.Type == "action"))
        {
            ExtractFields(entry, ActionFieldKeys);
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

    private static void ExtractFields(ParsedEntry entry, string[]? keys = null)
    {
        foreach (var key in keys ?? FeatFieldKeys)
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

    private static ParsedEntry NewEntry(TakenRange range, string book, string name, HashSet<string> compounds, string? type = null, string? folder = null, string? category = null, string? sourceTail = null)
    {
        var id = Slug(name);
        if (id.Length == 0)
        {
            throw new DegreesException($"Chapter {range.Row.Chapter}: an entry name has no letters or digits to make an id from.");
        }

        return new ParsedEntry
        {
            Id = id,
            Type = type ?? range.Row.Target,
            Folder = folder ?? range.Row.FolderName,
            Chapter = range.Row.Chapter,
            Source = book + " > " + string.Join(" > ", range.Row.PathOf(range)) + (sourceTail == null ? string.Empty : " > " + sourceTail),
            // An actions row's category is for the rule entries it also writes (an action has no category field).
            Category = type != null ? category : range.Row.Parser == "actions" ? null : range.Row.Category,
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
        private bool _runOn;
        private readonly List<(string Label, StringBuilder Text)> _parts = new();
        private bool _previousShort = true;
        private (int Page, int Column)? _previousPlace;

        /// <param name="runOn">
        /// True for a row whose entries run on in one column (the feats): a line of a labelled paragraph that wraps is set in from the edge (a hanging indent), and a block that
        /// the extraction cut off in the middle of a paragraph is not a paragraph of its own. Only a short line before it, a label or a first-line indent starts a paragraph.
        /// </param>
        /// <param name="tableRows">
        /// True for a row that says <see cref="TakeRow.TableRows"/>: a block whose lines mostly have two or more cells is a table the extraction could not rebuild, and is read one row to a line. A block that opens with a line that is only a bold label is read the same way, as in a run-on row.
        /// </param>
        public BodyReader(ParsedEntry entry, HashSet<string> compounds, bool runOn = false, bool tableRows = false)
        {
            _entry = entry;
            _compounds = compounds;
            _runOn = runOn;
            _tableRows = tableRows;
        }

        private readonly bool _tableRows;

        public void ReadAll(List<Block> blocks)
        {
            foreach (var b in blocks)
            {
                Read(b);
            }

            Finish();
        }

        /// <summary>
        /// Reads one block. <paramref name="plain"/> reads it as ordinary prose even in a run-on reader: the introduction a general action has before its heading is set like a rule's
        /// text (a first line set in, the rest at the edge), not like the action's own text with its hanging indents.
        /// </summary>
        public void Read(Block block, bool plain = false)
        {
            var saved = _runOn;
            _runOn = saved && !plain;
            try
            {
                ReadBlock(block);
            }
            finally
            {
                _runOn = saved;
            }
        }

        private void ReadBlock(Block block)
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

            if (_tableRows && block.Role == "body" && block.Info is { } cellInfo && block.Lines.Count >= 3 && cellInfo.Count(c => c.Cells != null) * 2 >= block.Lines.Count)
            {
                // Mostly multi-cell lines: a table that was not rebuilt, so one row to a line as the line reads (the gaps between cells are too small to cut cells by).
                for (var i = 0; i < block.Lines.Count; i++)
                {
                    Start(string.Empty);
                    Append(block.Lines[i]);
                }

                _previousShort = true;
                _previousPlace = (block.Page, block.Column);
                return;
            }

            var samePlace = _previousPlace == (block.Page, block.Column);
            // A block whose lines start at four or more different places is text set around a piece of art: its set-in lines are not first lines of paragraphs.
            var raggedLeft = block.Info != null && block.Info.Select(x => Math.Round(x.Left / 3)).Distinct().Count() >= 4;
            // A block that opens with a line that is only a bold label is a small table (a title row, then one row to a line) in a run-on row.
            var rowsBlock = (_runOn || _tableRows) && block.Info is { Count: > 1 } && block.Info[0].Label.Length > 0
                && SpanText.Normalize(block.Lines[0]).Trim() == SpanText.Normalize(block.Info[0].Label).Trim() && !KnownLabels.Contains(SpanText.Normalize(block.Info[0].Label).Trim().ToLowerInvariant());
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var info = block.Info?[i] ?? new LineInfo(block.Left, block.Right, string.Empty);
                var text = block.Lines[i];
                var label = SpanText.Normalize(info.Label).TrimEnd('.', ':', ' ');
                // A first-line indent starts a paragraph even after a line that ran to the edge. A bullet's own wrapped lines are not read this way.
                // A line that is only a page pointer, set in from the edge (a labelled line that ran on), continues the line before it; the pointer itself is dropped by Clean.
                var indented = _parts.Count > 0 && info.Left - block.Left > Indent && !_parts[^1].Text.ToString().StartsWith('•') && !(_runOn && PageRefLine().IsMatch(text)) && !raggedLeft;
                var startsParagraph = _parts.Count == 0 || text.StartsWith('•') || indented || (i == 0 ? (samePlace && !_runOn) || _previousShort : _previousShort);
                if (_runOn && _parts.Count > 0 && _parts[^1].Label.Length > 0 && !text.StartsWith('•'))
                {
                    // A labelled paragraph wraps with a hanging indent: its lines after the first are set in from the edge, and a line at the edge is the next paragraph.
                    startsParagraph = info.Left - block.Left <= Indent;
                }

                if ((_runOn || _tableRows) && startsParagraph && !indented && _parts.Count > 0 && text.Length > 0 && char.IsLower(text[0]) && !text.StartsWith('•')
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

        /// <summary>The text read so far ends a paragraph, whatever its last line looked like: the next block starts a new one.</summary>
        public void EndParagraph() => _previousShort = true;

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
