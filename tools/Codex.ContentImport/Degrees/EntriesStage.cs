using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
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

    /// <summary>For a row with several level words (<c>spell|cantrip</c>): the one this header has, in lowercase.</summary>
    public string? Kind { get; set; }

    /// <summary>For a spell whose header gives a range of action costs: the cost as a cast time ("1 to 3 actions"), since the action dropdown names one cost.</summary>
    public string? VariableCost { get; set; }

    /// <summary>For an entry of a row with groups (<see cref="TakeRow.Groups"/>): the title of the group heading it sits under, as the book prints it.</summary>
    public string? Group { get; set; }

    /// <summary>For a heritage or an ancestry feat the <c>ancestries</c> parser read: the ancestry entry it sits under. A heritage names that ancestry's id once the ids are final (a name that is also a trait id takes a suffix).</summary>
    [JsonIgnore]
    public ParsedEntry? Owner { get; set; }

    /// <summary>For a feat of a versatile heritage: the heritage its trait line names. The trait key is the heritage's final name, so a heritage a redaction row renames takes its feats' trait with it.</summary>
    [JsonIgnore]
    public ParsedEntry? TraitOwner { get; set; }

    /// <summary>For a class feat: the slug of its class. A class feat whose id another feat has takes it as a suffix (several classes have a feat of one name).</summary>
    public string? ClassKey { get; set; }

    /// <summary>For a spell: the traditions its labelled line lists, in lowercase, in the book's order.</summary>
    public List<string> Traditions { get; set; } = new();

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

    /// <summary>The entry's tags (a weapon's <c>weapons</c>, which the Strike action reads).</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Properties a table gives an item that are not text to redact: the field key and the YAML value as the pack writes it (a bare number, a quoted string, a flow list or map).
    /// They come from the statistics of a table row and hold no prose.
    /// </summary>
    public List<LabelledText> Raw { get; set; } = new();

    /// <summary>For a class or a subclass: its feature rows (a name, the level it applies from, its text and the grants to the sheet's ranked lists), in source order.</summary>
    public List<ClassFeature> Features { get; set; } = new();

    public string Render() => RenderParts(Parts);

    public static string RenderParts(IEnumerable<LabelledText> parts) =>
        string.Join('\n', parts.Select(p =>
            p.Label.Length == 0 ? p.Text
            // A bold run-in heading that starts with a bullet is a list item.
            : p.Label.StartsWith("• ", StringComparison.Ordinal) ? $"- **{p.Label[2..].TrimEnd('.', ':', ' ')}.** {p.Text}".TrimEnd()
            : $"**{p.Label.TrimEnd('.', ':', ' ')}.** {p.Text}".TrimEnd()));

    /// <summary>The key of a redaction field that names one feature row's text (<c>feature:</c> and the slug of its name).</summary>
    public static string FeatureField(ClassFeature feature) => "feature:" + EntriesStage.Slug(feature.Name);

    /// <summary>The text fields the redaction rows may name: the name and description, any extracted field (its key is the field name), and each feature row's text (<see cref="FeatureField"/>).</summary>
    public Dictionary<string, string> RedactionFields()
    {
        var fields = new Dictionary<string, string> { ["name"] = Name, ["description"] = Render() };
        foreach (var e in Extracted)
        {
            fields[e.Label] = e.Text;
        }

        foreach (var f in Features)
        {
            if (!fields.TryAdd(FeatureField(f), f.Render()))
            {
                throw new DegreesException("Two feature rows of one entry share a name, so a redaction row could not tell them apart.");
            }
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

    public string? Kind { get; set; }
    public List<string> Traditions { get; set; } = new();

    public string? Actions { get; set; }
    public List<string> Traits { get; set; } = new();

    public string? Skill { get; set; }
    public string? Proficiency { get; set; }

    /// <summary>The extracted labelled fields after redaction, by the target type's field key, in the type's order.</summary>
    public List<LabelledText> Extracted { get; set; } = new();

    public List<string> Tags { get; set; } = new();

    /// <summary>A table's statistics for an item, by field key, as the YAML value the pack writes (see <see cref="ParsedEntry.Raw"/>).</summary>
    public List<LabelledText> Raw { get; set; } = new();

    /// <summary>A class's or a subclass's feature rows after redaction.</summary>
    public List<FinalFeature> Features { get; set; } = new();
}

/// <summary>A feature row of a class or a subclass as the parser reads it: the book's own heading (a name and the level it is gained at), its text and the grants it makes to the sheet's ranked lists.</summary>
public sealed class ClassFeature
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The level the feature applies from; 1 when the heading names none.</summary>
    public int Level { get; set; } = 1;

    public List<LabelledText> Parts { get; set; } = new();

    /// <summary>Grants to ranked lists, each one the flow-map YAML of an <c>adds</c> row.</summary>
    public List<string> Adds { get; set; } = new();

    /// <summary>When a row says the feature's heading in other words (<c>featureNames</c>): the book's own heading, whose words are reserved names (with their plurals) for the denylist.</summary>
    public string? BookName { get; set; }

    public string Render() => ParsedEntry.RenderParts(Parts);
}

/// <summary>A feature row after redaction: what the pack file holds.</summary>
public sealed record FinalFeature(string Name, int Level, string Description, List<string> Adds);

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
        "critical success", "success", "failure", "critical failure", "trigger", "requirements", "frequency", "effect", "cost", "prerequisites", "prerequisite",
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

    /// <summary>
    /// Several classes have a feat of one name: each class feat of such a group takes its class as a suffix. A plain id that has shipped (<paramref name="shipped"/>, id to folder) belongs to the feat it was shipped for,
    /// the one of the class whose row came first, so a later class that prints the same name takes its suffix and the earlier id never changes; a general feat of the same id keeps it against every class feat.
    /// </summary>
    public static void SuffixClassFeats(List<ParsedEntry> entries, IReadOnlyDictionary<string, string>? shipped)
    {
        foreach (var same in entries.Where(e => e.Type == "feat").GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList())
        {
            // The entry that shipped is the earliest row's in the folder that holds the id, whatever its kind; a later general feat of the id is left as it is, and the run fails below on the two ids.
            var keeper = shipped != null && shipped.TryGetValue(same.Key, out var shippedFolder) && same.Any(e => e.Folder == shippedFolder)
                ? same.Where(e => e.Folder == shippedFolder).OrderBy(e => e.RowNumber).First()
                : null;
            foreach (var classFeat in same.Where(e => e.ClassKey != null && e != keeper))
            {
                classFeat.Id += "-" + classFeat.ClassKey;
            }
        }
    }

    public static List<ParsedEntry> Run(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, string book, IReadOnlyDictionary<string, string>? shipped = null, IReadOnlyList<SpineNode>? spine = null)
    {
        var compounds = Compounds(blocks, ranges);
        var entries = new List<ParsedEntry>();
        foreach (var range in ranges)
        {
            var slice = range.BlocksIn(blocks);
            entries.AddRange(range.Row.Parser switch
            {
                "rules" => ParseRule(range, slice, book, compounds),
                "items" when range.Row.ItemKind == "gear" => ParseGear(range, ranges.Where(r => r.RowNumber == range.RowNumber).ToList(), blocks, spine ?? throw new DegreesException("The items parser needs the book's bookmarks."), book, compounds),
                "items" => ParseItems(range, slice, blocks, spine ?? throw new DegreesException("The items parser needs the book's bookmarks."), book, compounds),
                "glossary" => ParseGlossary(range, slice, book, compounds),
                "actions" => ParseActions(range, slice, book, compounds),
                "ancestries" => ParseAncestries(range, slice, book, compounds),
                "versatile" => ParseVersatile(range, slice, book, compounds),
                "backgrounds" => ParseBackgrounds(range, slice, book, compounds),
                "classes" => ParseClasses(range, slice, book, compounds),
                // The tradition lists make no entry: they are read past, and counted against the spells (ImportPipeline.CheckSpellCounts).
                "spelllists" => new List<ParsedEntry>(),
                _ => ParseEntries(range, slice, book, compounds),
            });
            if (range.Row.Sidebars.Count > 0 && range.PathNumber == 1)
            {
                entries.AddRange(ParseSidebars(range, blocks, spine ?? throw new DegreesException("A row that takes a sidebar needs the book's bookmarks."), book, compounds));
            }
        }

        // An action's id is its name's slug, so it can equal a feat's, a condition's or a rule's. The redaction rows name an entry by id alone, so an id may not repeat across
        // types: the action takes the suffix (see ActionIdSuffix). Two entries of one type with one id are still a mistake and fail below.
        // The same holds for a spell, named like a feat, a condition, a trait, a rule or an action more than once. Which of two entries that share a slug keeps it: the one whose folder holds
        // that id in the pack already committed (`shipped`, id to folder), since an id that has shipped is never renamed, whatever a later chapter brings. With nothing shipped under the id,
        // the entry of the type that is neither an action nor a spell keeps it, and an action keeps it against a spell. Every other entry of the group takes the suffix of its type.
        // Several classes have a feat of one name, and a class feat may be named like a general feat: each class feat in such a group takes its class as a suffix (every other kind of repeat still fails below).
        SuffixClassFeats(entries, shipped);

        foreach (var group in entries.GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Select(e => e.Type).Distinct().Count() > 1).ToList())
        {
            var folders = group.Select(e => e.Folder).Distinct().ToList();
            string? keeper = shipped != null && shipped.TryGetValue(group.Key, out var folder) && folders.Contains(folder) ? folder : null;
            if (keeper == null)
            {
                // Nothing shipped under the id (or no pack to read): what shipping order would give. The entry of the earliest take-list row keeps the slug (the chapters are taken in the order they
                // were shipped); within one row a rule or any other type keeps it against an action, and an action against a spell. So a run reads the same ids with or without a pack folder.
                keeper = group.OrderBy(e => e.RowNumber).ThenBy(e => e.Type == "spell" ? 2 : e.Type == "action" ? 1 : 0).ThenBy(e => e.Type, StringComparer.Ordinal).First().Folder;
            }

            foreach (var entry in group.Where(e => keeper != null && e.Folder != keeper))
            {
                entry.Id += entry.Type switch { "action" => ActionIdSuffix, "spell" => SpellIdSuffix, var other => "-" + other };
            }
        }

        // A heritage names its ancestry by id, and that id is final only now (an ancestry named like a shipped trait took a suffix above).
        foreach (var heritage in entries.Where(e => e.Type == "heritage" && e.Owner != null))
        {
            heritage.Raw.Add(new LabelledText("ancestry", "\"" + heritage.Owner!.Id + "\""));
        }

        // A subclass names its class by id too, final only now (a class named like a shipped trait took a suffix above).
        foreach (var subclass in entries.Where(e => e.Type == "subclass" && e.Owner != null))
        {
            subclass.Raw.Insert(0, new LabelledText("class", "\"" + subclass.Owner!.Id + "\""));
        }

        // The redaction rows name an entry by id alone, so an id may not repeat even across types.
        var duplicate = entries.GroupBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new DegreesException($"{duplicate.Count()} entries ({string.Join(", ", duplicate.Select(e => e.Type).Distinct())}) share one id ({duplicate.Key}); ids come from names, so a name is repeated.");
        }

        return entries.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    private static List<ParsedEntry> ParseRule(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var name = range.Own ? range.Row.OwnName ?? range.Row.PathOf(range)[^1] : range.Row.PathOf(range)[^1];
        if (range.Row.EntryNames.TryGetValue(range.Row.PathOf(range)[^1], out var renamed))
        {
            name = renamed;
        }

        var entry = NewEntry(range, book, Clean(name), compounds);
        if (range.Row.ReadPast)
        {
            // A section whose text the row reads past (its blocks are claimed, so they are not left out text, and no entry is made).
            return new List<ParsedEntry>();
        }

        var reader = new BodyReader(entry, compounds, false, range.Row.TableRows, null, range.Row.Subheadings, range.Row.ProseBoxes);
        var skipping = false;
        foreach (var block in slice)
        {
            // A stat table is the items rows' (see StatTables), and the caption under a picture of an item is no text.
            if (block.Role == StatTables.Role || (range.Row.ArtLabels && IsArtLabel(block)))
            {
                continue;
            }

            if (range.Row.FoldChildren && block.Role == "entry-header")
            {
                // A child section whose title is in the entry-header font is a paragraph of the entry (its title, then its text), or is read past when the row names it.
                var title = Clean(string.Join(' ', block.Lines));
                skipping = range.Row.ReadPastChildren.Any(t => t.Equals(title, StringComparison.OrdinalIgnoreCase));
                if (!skipping)
                {
                    reader.EndParagraph();
                    reader.Read(block with { Role = "body", Lines = new List<string> { title }, Info = null });
                    reader.EndParagraph();
                }

                continue;
            }

            if (skipping)
            {
                continue;
            }

            if (range.Row.Subheadings && block.Role == "block-header")
            {
                // A heading that has no bookmark (a sub-subsection, an activity's name): a paragraph of its own, in title case. A heading with a bracket or a parenthesis in it is a format
                // line (an example of how a stat block is set out) and stays as printed.
                var text = Clean(string.Join(' ', block.Lines));
                reader.EndParagraph();
                reader.Read(block with { Role = "body", Lines = new List<string> { text.Contains('[') || text.Contains('(') ? text : TitleCase(text) }, Info = null });
                reader.EndParagraph();
                continue;
            }

            reader.Read(block);
            if (range.Row.Subheadings && block.Role == "tag")
            {
                reader.EndParagraph(); // the trait line under a heading is a paragraph of its own
            }
        }

        reader.Finish();
        return new List<ParsedEntry> { entry };
    }

    private static List<ParsedEntry> ParseEntries(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var row = range.Row;
        var spell = row.Target == "spell";
        var result = new List<ParsedEntry>();
        var readers = new List<BodyReader>();
        var traitsOpen = false;
        var hasBoxes = row.LevelWord != null && (row.Boxes.Count > 0 || row.ReadPastBoxes > 0);
        var inBox = false;
        BodyReader? boxReader = null;
        var readPast = 0;
        string? group = null;
        foreach (var block in slice)
        {
            if (row.Groups && block.Role is "section" or "entry-header")
            {
                // The name of a group of entries (a class's focus spells, a domain, an order, a school): each entry after it says it, up to the next heading.
                group = Clean(string.Join(' ', block.Lines));
                traitsOpen = false;
                continue;
            }

            if (row.IsHeader(block))
            {
                inBox = false;
                boxReader = null;
                var header = Clean(string.Join(' ', block.Lines));
                string? actions = null;
                string? variableCost = null;
                if (spell && Regex.Match(header, @"\[(?<a>[^\]]+)\]\s+(?<w>TO|OR)\s+\[(?<b>[^\]]+)\]", RegexOptions.CultureInvariant) is { Success: true } range2)
                {
                    // A cost that runs from one number of actions to another ("[1 action] TO [3 actions]") has no one action cost: the dropdown stays empty and the cost is the spell's cast time.
                    string Count(string icon) => Icons.Values.FirstOrDefault(i => i.Text == $"[{icon}]").Actions ?? icon;
                    variableCost = $"{Count(range2.Groups["a"].Value)} {range2.Groups["w"].Value.ToLowerInvariant()} {Count(range2.Groups["b"].Value)} actions";
                    header = header.Replace(range2.Value, string.Empty, StringComparison.Ordinal);
                }

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
                string? kind = null;
                if (row.LevelWord != null)
                {
                    // "NAME [icon] WORD 3": the name is set in capitals, the level is the number after the row's word (or one of its words: spell, cantrip).
                    var m = Regex.Match(header, @"^(?<name>.+?)\s+" + row.LevelPattern + @"\s+(?<level>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    level = int.Parse(m.Groups["level"].Value, CultureInfo.InvariantCulture);
                    kind = row.LevelWords.Length > 1 || spell ? m.Groups["word"].Value.ToLowerInvariant() : null;
                    header = TitleCase(m.Groups["name"].Value);
                }

                var entry = NewEntry(range, book, header, compounds);
                entry.Actions = actions;
                entry.Level = level;
                entry.Kind = kind;
                entry.VariableCost = variableCost;
                entry.Group = row.Groups ? group : null;
                result.Add(entry);
                readers.Add(new BodyReader(entry, compounds, row.LevelWord != null, row.TableRows, spell ? SpellStatLabels : null));
                traitsOpen = true;
                continue;
            }

            // A heading with no entry's shape starts a box (a table or a block of rules set among the entries): the row names it and takes it as a rule entry, or counts it as read past.
            if (hasBoxes && !inBox && block.Role == (row.HeaderRole ?? "entry-header"))
            {
                var title = TitleCase(Clean(string.Join(' ', block.Lines)));
                inBox = true;
                traitsOpen = false;
                if (row.Boxes.Contains(title, StringComparer.OrdinalIgnoreCase))
                {
                    var rule = NewEntry(range, book, title, compounds, "rule", "rules", row.Category);
                    result.Add(rule);
                    boxReader = new BodyReader(rule, compounds, false, row.TableRows, null, true);
                    readers.Add(boxReader);
                }
                else
                {
                    readPast++;
                    boxReader = null;
                }

                continue;
            }

            if (inBox)
            {
                if (boxReader != null)
                {
                    if (block.Role == (row.HeaderRole ?? "entry-header"))
                    {
                        // A heading inside the box (a stat block's own name): a paragraph of its own, in title case.
                        boxReader.EndParagraph();
                        boxReader.Read(block with { Role = "body", Lines = new List<string> { TitleCase(Clean(string.Join(' ', block.Lines))) }, Info = null });
                        boxReader.EndParagraph();
                    }
                    else
                    {
                        boxReader.Read(block);
                        if (block.Role == "tag")
                        {
                            boxReader.EndParagraph(); // the trait line is a paragraph of its own
                        }
                    }
                }

                continue;
            }

            if (traitsOpen && block.Role == "tag")
            {
                var words = Clean(string.Join(' ', block.Lines)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // A trait line set in capitals (the feats' tag line) is a list of trait keys.
                result[^1].Traits.AddRange(row.LevelWord != null ? words.Select(w => w.ToLowerInvariant()) : words);
                continue;
            }

            // A lone line in capitals, set well in from the column's edge where a header's left edge is (a label of the page's art), is not entry text.
            if (row.LevelWord != null && IsStrayLabel(block, slice, row))
            {
                continue;
            }

            traitsOpen = false;
            readers[^1].Read(block);
        }

        if (hasBoxes && readPast != row.ReadPastBoxes)
        {
            throw new DegreesException($"Chapter {row.Chapter}, path {range.PathNumber}: the range holds {readPast} box(es) the row does not take, but the row says it reads {row.ReadPastBoxes} past.");
        }

        foreach (var reader in readers)
        {
            reader.Finish();
        }

        if (row.LevelWord != null)
        {
            foreach (var entry in result.Where(e => e.Type == row.Target))
            {
                if (spell)
                {
                    ExtractSpellFields(entry);
                }
                else
                {
                    ExtractFields(entry);
                }
            }
        }

        return result;
    }

    /// <summary>The labels of a spell's stat lines and its heightened entries, in lowercase. A bold run that opens a spell's text line and is none of these (a creature's attack, an art label) is not a label.</summary>
    private static readonly HashSet<string> SpellStatLabels = new(StringComparer.Ordinal)
    {
        "traditions", "cast", "cost", "trigger", "requirements", "range", "area", "target", "targets", "defense", "duration", "heightened",
        // A ritual's own lines, and the lines a class's focus spells open with (the muse of a composition, the lesson of a hex, the patron of a hex cantrip): the last three stay in the description.
        "secondary casters", "primary check", "secondary checks", "muse", "lesson", "patron",
        // The older label of a spell's saving throw, which two focus spells print in place of Defense.
        "saving throw",
    };

    /// <summary>The labels a ritual's stat line puts after a semicolon. A bold run of one of them can be cut by a line break or a stray run, so the semicolon before one is the cut whatever the runs say.</summary>
    private static readonly string[] RitualLabels = { "Secondary Casters", "Secondary Checks", "Primary Check" };

    /// <summary>The spell type's fields beside its header and traditions, in the system's order: the field key and the labels that fill it.</summary>
    private static readonly (string Key, string[] Labels)[] SpellFieldLabels =
    {
        ("castTime", new[] { "cast" }), ("cost", new[] { "cost" }), ("trigger", new[] { "trigger" }), ("requirements", new[] { "requirements" }),
        ("secondaryCasters", new[] { "secondary casters" }), ("primaryCheck", new[] { "primary check" }), ("secondaryChecks", new[] { "secondary checks" }),
        ("range", new[] { "range" }), ("area", new[] { "area" }), ("targets", new[] { "targets", "target" }), ("defense", new[] { "defense", "saving throw" }), ("duration", new[] { "duration" }),
    };

    internal static readonly string[] TraditionKeys = { "arcane", "divine", "occult", "primal" };

    /// <summary>A label's key for the stat-label test: lowercase, and without the parenthesized part of a heightened entry's label ("Heightened (+1)").</summary>
    private static string StatKey(string label)
    {
        var open = label.IndexOf('(', StringComparison.Ordinal);
        return (open > 0 ? label[..open] : label).Trim().ToLowerInvariant();
    }

    /// <summary>Takes the spell's traditions and the labelled lines the type has a field of its own for out of its parts.</summary>
    private static void ExtractSpellFields(ParsedEntry entry)
    {
        if (entry.Group != null)
        {
            entry.Extracted.Add(new LabelledText("focusGroup", entry.Group));
        }

        var traditions = entry.Parts.Where(p => p.Label.Length > 0 && StatKey(p.Label.TrimEnd('.', ':', ' ')) == "traditions").ToList();
        if (traditions.Count > 1)
        {
            throw new DegreesException($"A spell of chapter {entry.Chapter} has the traditions label more than once.");
        }

        if (traditions.Count == 1)
        {
            entry.Traditions.AddRange(traditions[0].Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToLowerInvariant()));
            if (entry.Traditions.Any(t => !TraditionKeys.Contains(t)))
            {
                throw new DegreesException($"A spell of chapter {entry.Chapter} lists a tradition that is not one of the four.");
            }

            entry.Parts.Remove(traditions[0]);
            entry.Fields.Remove(traditions[0]);
        }

        foreach (var (key, labels) in SpellFieldLabels)
        {
            var found = entry.Parts.Where(p => p.Label.Length > 0 && labels.Contains(StatKey(p.Label.TrimEnd('.', ':', ' ')))).ToList();
            if (found.Count > 1 || (key == "castTime" && entry.VariableCost != null && found.Count > 0))
            {
                throw new DegreesException($"A spell of chapter {entry.Chapter} has the label of '{key}' more than once, or twice with a range of action costs in its header.");
            }

            if (key == "castTime" && entry.VariableCost != null)
            {
                entry.Extracted.Add(new LabelledText(key, entry.VariableCost));
            }
            else if (found.Count == 1)
            {
                entry.Extracted.Add(new LabelledText(key, found[0].Text));
                entry.Parts.Remove(found[0]);
                entry.Fields.Remove(found[0]);
            }
        }
    }

/// <summary>
    /// The suffix an action's id takes when its name's slug is also the id of an entry of another type (a feat, a condition or a rule): <c>hide</c> and a feat of that name would
    /// otherwise share one id, and a redaction row names an entry by id alone. The suffix is a plain word, so the id stays readable and stable.
    /// </summary>
    public const string ActionIdSuffix = "-action";

    /// <summary>The suffix a spell's id takes when its name's slug is the id of an entry of any other type (see <see cref="ActionIdSuffix"/>).</summary>
    public const string SpellIdSuffix = "-spell";

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

    /// <summary>The indexes (in <paramref name="slice"/>) of the blocks inside a box of an <c>entries</c> row with a level word, whether the row takes the box or reads it past: the heading and what follows up to the next entry.</summary>
    public static HashSet<int> BoxIndexes(TakeRow row, List<Block> slice)
    {
        var result = new HashSet<int>();
        if (row.LevelWord == null || (row.Boxes.Count == 0 && row.ReadPastBoxes == 0))
        {
            return result;
        }

        var inBox = false;
        for (var i = 0; i < slice.Count; i++)
        {
            if (row.IsHeader(slice[i]))
            {
                inBox = false;
            }
            else if (!inBox && slice[i].Role == (row.HeaderRole ?? "entry-header"))
            {
                inBox = true;
            }

            if (inBox)
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>The blocks of the boxes an <c>entries</c> row with a level word reads past: its boxes that are not among the row's named ones (see <see cref="TakeRow.Boxes"/>), heading included.</summary>
    public static List<Block> ReadPastBoxBlocks(TakeRow row, List<Block> slice)
    {
        var result = new List<Block>();
        if (row.LevelWord == null || (row.Boxes.Count == 0 && row.ReadPastBoxes == 0))
        {
            return result;
        }

        var inBox = false;
        var taken = false;
        foreach (var block in slice)
        {
            if (row.IsHeader(block))
            {
                inBox = false;
            }
            else if (!inBox && block.Role == (row.HeaderRole ?? "entry-header"))
            {
                inBox = true;
                taken = row.Boxes.Contains(TitleCase(Clean(string.Join(' ', block.Lines))), StringComparer.OrdinalIgnoreCase);
            }

            if (inBox && !taken)
            {
                result.Add(block);
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
        if (double.IsNaN(edge))
        {
            // A column with no header on it (the middle of a spell that runs on from the column before): the edge is where the column's other text starts.
            edge = slice.Where(b => b.Page == block.Page && b.Column == block.Column && !ReferenceEquals(b, block) && b.Role == "body" && b.Lines.Count > 1).Select(b => b.Left).DefaultIfEmpty(double.NaN).Min();
        }

        return !double.IsNaN(edge) && block.Left - edge > 10;
    }

    /// <summary>The labels a feat has a field of its own for (the system's feat type): label in lowercase, then the field key. Every other label stays in the description.</summary>
    private static readonly string[] FeatFieldKeys = { "prerequisites", "frequency", "trigger", "requirements", "cost", "special" };

    private static void ExtractFields(ParsedEntry entry, string[]? keys = null)
    {
        foreach (var key in keys ?? FeatFieldKeys)
        {
            // The book prints the singular label on one feat; it is the same field.
            var found = entry.Parts.Where(p => p.Label.Length > 0 && (p.Label.TrimEnd('.', ':', ' ').Equals(key, StringComparison.OrdinalIgnoreCase)
                || (key == "prerequisites" && p.Label.TrimEnd('.', ':', ' ').Equals("prerequisite", StringComparison.OrdinalIgnoreCase)))).ToList();
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
            Source = SpanText.Normalize(book + " > " + string.Join(" > ", range.Row.PathOf(range)) + (sourceTail == null ? string.Empty : " > " + sourceTail)),
            // An actions row's or a spells row's category is for the rule entries it also writes (an action or a spell has no category field).
            Category = type != null ? category : range.Row.Parser == "actions" || range.Row.Boxes.Count > 0 ? null : range.Row.Category,
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

    private static readonly Regex ActionOptionLine = new(@"^\[(?:one-action|two-actions|three-actions)\] ", RegexOptions.CultureInvariant);

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
        /// <param name="statLabels">
        /// For a spell: the labels (in lowercase, without the parenthesis of a heightened entry) that open a paragraph and may also stand in the middle of a line after a semicolon, as
        /// the second label does in a line of the shape "Label value; Label value". Any other bold run at a line start that is not a bullet's heading is plain text (the attack lines of a battle form).
        /// </param>
        /// <param name="bodyRunOn">
        /// True for a reader whose body-font blocks (a stat block's lines, the example of how a spell is set out) are set ragged-right with hanging indents, as a feat's text is: they are read in the
        /// run-on way, while the justified rules-font text around them is read as ever.
        /// </param>
        /// <param name="proseBoxes">
        /// True for a row that says <see cref="TakeRow.ProseBoxes"/>: a block that opens with a line that is only a bold label is a box of prose (a heading, then ragged lines of text), not a small table, so its lines run on.
        /// </param>
        public BodyReader(ParsedEntry entry, HashSet<string> compounds, bool runOn = false, bool tableRows = false, HashSet<string>? statLabels = null, bool bodyRunOn = false, bool proseBoxes = false)
        {
            _entry = entry;
            _compounds = compounds;
            _runOn = runOn;
            _tableRows = tableRows;
            _stat = statLabels;
            _bodyRunOn = bodyRunOn;
            _proseBoxes = proseBoxes;
        }

        private readonly bool _bodyRunOn;

        private readonly bool _proseBoxes;

        private readonly bool _tableRows;
        private readonly HashSet<string>? _stat;

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
            _runOn = (saved || (_bodyRunOn && block.Role == "body")) && !plain;
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
            var rowsBlock = !_proseBoxes && (_runOn || _tableRows) && block.Info is { Count: > 1 } && block.Info[0].Label.Length > 0
                && SpanText.Normalize(block.Lines[0]).Trim() == SpanText.Normalize(block.Info[0].Label).Trim() && !KnownLabels.Contains(SpanText.Normalize(block.Info[0].Label).Trim().ToLowerInvariant());
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var info = block.Info?[i] ?? new LineInfo(block.Left, block.Right, string.Empty);
                var text = block.Lines[i];
                var rawLabel = info.Label;
                if (_stat != null && rawLabel.Length > 0 && !rawLabel.StartsWith("• ", StringComparison.Ordinal)
                    && !_stat.Contains(StatKey(SpanText.Normalize(rawLabel))) && !KnownLabels.Contains(SpanText.Normalize(rawLabel).TrimEnd('.', ':', ' ').ToLowerInvariant()))
                {
                    rawLabel = string.Empty; // a bold run that is no label of a spell's text
                }

                var label = SpanText.Normalize(rawLabel).TrimEnd('.', ':', ' ');
                // A first-line indent starts a paragraph even after a line that ran to the edge. A bullet's own wrapped lines are not read this way.
                // A line that is only a page pointer, set in from the edge (a labelled line that ran on), continues the line before it; the pointer itself is dropped by Clean.
                var indented = _parts.Count > 0 && info.Left - block.Left > Indent && !_parts[^1].Text.ToString().StartsWith('•') && !(_runOn && PageRefLine().IsMatch(text)) && !raggedLeft
                    // A spell's text has no first-line indents: a line set in from the edge under a paragraph with no label of its own is the hanging indent of an inset (a reaction, an affliction).
                    && !(_stat != null && _parts[^1].Label.Length == 0);
                var startsParagraph = _parts.Count == 0 || text.StartsWith('•') || indented || (i == 0 ? (samePlace && !_runOn) || _previousShort : _previousShort);
                var optionLine = _stat != null && ActionOptionLine.IsMatch(text); // a spell's option for one number of actions opens its own paragraph
                if (_runOn && _parts.Count > 0 && _parts[^1].Label.Length > 0 && !text.StartsWith('•'))
                {
                    // A labelled paragraph wraps with a hanging indent: its lines after the first are set in from the edge, and a line at the edge is the next paragraph.
                    // In text set ragged or slanted (the lines start at four or more places) a hanging indent means nothing: only a line set short before this one ends the paragraph.
                    startsParagraph = raggedLeft ? _previousShort : info.Left - block.Left <= Indent;
                }

                // A list item's wrapped lines are set in beyond its bullet; a line back at the bullet's own edge, with no bullet, after a line that ended a sentence, is the paragraph that follows the list.
                // Only for the rows that set LowerContinues (the class feats of chapters 8b and after), so the earlier chapters' entries read as they did.
                var afterItem = _parts.Count > 0 && (_parts[^1].Label.StartsWith("• ", StringComparison.Ordinal) || _parts[^1].Text.ToString().StartsWith('•'));
                if (LowerContinues && afterItem && !text.StartsWith('•') && !(label.Length > 0 && rawLabel.StartsWith("• ", StringComparison.Ordinal)) && _bulletLeft is { } edge && Math.Abs(info.Left - edge) < 2
                    && _parts[^1].Text.ToString().TrimEnd() is { Length: > 0 } itemTail && ".!?".Contains(itemTail[^1]))
                {
                    startsParagraph = true;
                }

                if (text.StartsWith('•') || rawLabel.StartsWith("• ", StringComparison.Ordinal))
                {
                    _bulletLeft = info.Left;
                }

                if ((_runOn || _tableRows) && startsParagraph && (!indented || LowerContinues) && _parts.Count > 0 && text.Length > 0 && char.IsLower(text[0]) && !text.StartsWith('•')
                    && _parts[^1].Text.ToString().TrimEnd() is { Length: > 0 } tail && !".!?:;\"”)".Contains(tail[^1]))
                {
                    startsParagraph = false; // a line set short by the layout, not by the end of a sentence: the text goes on
                }
                // A box of prose or a formula in a body block (a row with proseBoxes) wraps with a hanging indent: a line that goes on with an operator, or starts with one, or starts in lowercase after a line
                // that does not end a sentence, is the same line (a formula is never cut at its plus sign).
                if (_proseBoxes && block.Role == "body" && _parts.Count > 0 && text.Length > 0 && !text.StartsWith('•') && _parts[^1].Label.Length == 0
                    && _parts[^1].Text.ToString().TrimEnd() is { Length: > 0 } before
                    && (text.StartsWith("+ ", StringComparison.Ordinal) || before[^1] is '+' or '=' || (char.IsLower(text[0]) && !".!?:;\"”)".Contains(before[^1]))))
                {
                    startsParagraph = false;
                }

                if (rowsBlock && !(_stat != null && _parts.Count > 0 && text.Length > 0 && char.IsLower(text[0])))
                {
                    startsParagraph = true; // a table's rows: one line each, whatever the line before it ran to
                }

                if (optionLine)
                {
                    startsParagraph = true;
                }

                if (label.Length > 0 && (KnownLabels.Contains(label.ToLowerInvariant()) || (_stat != null && _stat.Contains(StatKey(label)))))
                {
                    Start(label);
                    text = text[Math.Min(text.Length, rawLabel.Length)..].TrimStart();
                    if (_stat != null && StatKey(label) == "heightened" && !label.Contains('(', StringComparison.Ordinal) && text.StartsWith('(') && text.IndexOf(')', StringComparison.Ordinal) is var close and > 0)
                    {
                        // The rank in a heightened entry's label ("Heightened (+1)") is set in the plain face in some entries: it is part of the label all the same.
                        _parts[^1] = (label + " " + text[..(close + 1)], _parts[^1].Text);
                        text = text[(close + 1)..].TrimStart();
                    }
                }
                else if (_runOn && startsParagraph && label.Length > 0 && text.StartsWith(rawLabel, StringComparison.Ordinal) && text.Length > rawLabel.Length)
                {
                    // A bold run-in heading at the start of a paragraph (a pet's statistics, say) is a label like the known ones.
                    Start(label);
                    text = text[rawLabel.Length..].TrimStart();
                }
                else if (startsParagraph)
                {
                    Start(string.Empty);
                }

                if (_stat != null && info.Bold != null)
                {
                    // A second label in the middle of a line, after a semicolon (a line of the shape "Label value; Label value"): the line is cut there and the label starts a field of its own.
                    foreach (var run in info.Bold)
                    {
                        var marker = "; " + run;
                        var at = text.IndexOf(marker, StringComparison.Ordinal);
                        if (at < 0 || !_stat.Contains(StatKey(SpanText.Normalize(run))))
                        {
                            continue;
                        }

                        Append(text[..at]);
                        Start(SpanText.Normalize(run).TrimEnd('.', ':', ' '));
                        text = text[(at + marker.Length)..].TrimStart();
                    }
                }

                Append(text);
                _previousShort = info.Right < block.Right - ShortLine;
            }

            _previousPlace = (block.Page, block.Column);
        }

        /// <summary>The text read so far ends a paragraph, whatever its last line looked like: the next block starts a new one.</summary>
        public void EndParagraph() => _previousShort = true;

        private double? _bulletLeft;

        private void Start(string label) => _parts.Add((label, new StringBuilder()));

        /// <summary>
        /// A line set in from the block's edge that starts in lowercase, after a sentence that has not ended, goes on the sentence (text set around a piece of art is pushed in line by line). Set for the class
        /// feats of chapters 8b and after, so the earlier chapters' entries read as they did.
        /// </summary>
        public bool LowerContinues { get; init; }

        private void Append(string line)
        {
            JoinLine(_parts[^1].Text, line, _compounds);
        }

        /// <summary>
        /// A ritual's stat line puts its second and third labels after a semicolon (<c>Cost ...; Secondary Casters 1</c>, <c>Primary Check ...; Secondary Checks ...</c>). The bold runs that
        /// mark them are sometimes cut by a line break or read as a run of one word, so the cut is made here, on the text: a stat part is split at a semicolon that comes before one of
        /// the ritual's labels, and each label starts a part of its own.
        /// </summary>
        private void SplitRitualLabels()
        {
            for (var i = 0; i < _parts.Count; i++)
            {
                var (label, sb) = _parts[i];
                if (label.Length == 0 || !_stat!.Contains(StatKey(label)))
                {
                    continue;
                }

                // The first label in the text, whichever of the three it is; the rest of the text is a part of its own that the loop reaches next.
                var cuts = RitualLabels.Select(r => (Label: r, At: sb.ToString().IndexOf("; " + r + " ", StringComparison.Ordinal))).Where(c => c.At >= 0).ToList();
                if (cuts.Count == 0)
                {
                    continue;
                }

                var text = sb.ToString();
                var (ritual, at) = cuts.OrderBy(c => c.At).First();
                _parts[i] = (label, new StringBuilder(text[..at]));
                _parts.Insert(i + 1, (ritual, new StringBuilder(text[(at + 3 + ritual.Length)..])));
            }
        }

        public void Finish()
        {
            if (_stat != null)
            {
                SplitRitualLabels();
            }

            // A paragraph that follows a labelled one stays a paragraph of its own: it is cut by a short line, so it is the closing prose, not the label's text.
            foreach (var (label, sb) in _parts)
            {
                var text = Clean(sb.ToString());
                if (_stat != null && label.Length > 0 && _stat.Contains(StatKey(label)))
                {
                    text = text.TrimEnd(';', ' '); // a stat line that goes on with another label ends in the semicolon between them
                }

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
