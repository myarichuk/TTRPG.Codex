using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// One row of <c>Data/degrees-chapters.yaml</c>: the bookmark paths taken, how they are parsed, and what they become. A chapter may have several
/// rows (the list of conditions is parsed as entries, its two rule sections as rules). Nothing outside a taken path is read into an entry,
/// and dropped sections are never named: they are absent.
/// </summary>
public sealed class TakeRow
{
    public string Chapter { get; set; } = string.Empty;
    public string Book { get; set; } = string.Empty;
    public string Parser { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;

    /// <summary>The pack folder for the entries; by default the target type's name in the plural.</summary>
    public string? Folder { get; set; }

    /// <summary>The value of the entry's <c>category</c> field, in our own words, when the target type has one.</summary>
    public string? Category { get; set; }

    public bool Approved { get; set; }

    /// <summary>The most columns a page of this row may have. Two unless stated.</summary>
    public int Columns { get; set; } = 2;

    /// <summary>
    /// A page of the row (its number) and the x of its one gutter, in points. Only for a page whose text holds a wider empty band than the real gutter (a feat set around a piece of art, with a gap in every
    /// line), so the gutter search would cut the page there. The page then has two columns, cut at that x. A page the row does not cover fails the run.
    /// </summary>
    public Dictionary<int, double> GutterAt { get; set; } = new();

    /// <summary>Bookmark paths, top level first. Each path is a section or an entry.</summary>
    public List<List<string>> Paths { get; set; } = new();

    /// <summary>
    /// Bookmark paths whose OWN text is taken: what sits between the section's title and its first child section (which this row, or another, takes on its own path).
    /// Used by the <c>rules</c> parser, one entry per path, named by <see cref="OwnName"/>. It is never dropped silently: a section's intro is either here or fails nothing, so
    /// leaving it out is a decision the take-list shows.
    /// </summary>
    public List<List<string>> Own { get; set; } = new();

    /// <summary>
    /// For a <c>rules</c> row: the names, in our own words, of entries whose section's title would clash with another entry's id (a section called Spellcasting under the services, as the spells' chapter has one),
    /// by the section's last title. Any other section keeps its title.
    /// </summary>
    public Dictionary<string, string> EntryNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name, in our own words, of the entry made from an <see cref="Own"/> path (its title is not the entry's name). Defaults to the path's last title.</summary>
    public string? OwnName { get; set; }

    /// <summary>
    /// For an <c>entries</c> row: why the bookmark children of a taken section (minus the sidebar bookmarks) and the entry-header-font blocks of its range may disagree, in our
    /// own words. Without it, a disagreement fails the run (see <see cref="ImportPipeline.CheckEntryCounts"/>). Empty when they agree.
    /// </summary>
    public string? CountsDiffer { get; set; }

    /// <summary>
    /// A page number: the section's last lines sit on the page its next section starts on, above that section's own text, because the bookmarks end the section a page early.
    /// The blocks taken there are the first of each column when it starts at the page's top edge (see <see cref="TakeStage"/>).
    /// </summary>
    public int? SpillsOnto { get; set; }

    /// <summary>The block role an entry header is set in. Entry-header by default; a chapter whose headers share the block-header font says so here.</summary>
    public string? HeaderRole { get; set; }

    /// <summary>
    /// For an <c>entries</c> row whose headers end in a word and a number (the type's word and the entry's level): that word. A header block then needs this shape, and the range
    /// starts at the first one (what comes before it is read past, see <see cref="TakenRange.LeadStart"/>).
    /// </summary>
    public string? LevelWord { get; set; }

    /// <summary>The words <see cref="LevelWord"/> names: one, or several joined with a vertical bar (<c>spell|cantrip</c>) for a type whose header word says what kind of entry it is.</summary>
    public string[] LevelWords => (LevelWord ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The regular-expression group that matches any of the <see cref="LevelWords"/> (named <c>word</c>), whatever their case.</summary>
    public string LevelPattern => "(?<word>" + string.Join('|', LevelWords.Select(Regex.Escape)) + ")";

    /// <summary>
    /// For an <c>entries</c> row with a level word: the titles, in our own words, of the boxes the range holds that are not entries (a heading in the entry-header font that has not the
    /// entries' shape, and the blocks after it up to the next entry). Each becomes one <c>rule</c> entry (the row's <see cref="Category"/>), named by the title in title case.
    /// </summary>
    public List<string> Boxes { get; set; } = new();

    /// <summary>
    /// For an <c>entries</c> row with a level word: how many boxes the range holds that the row does not take (they are read past, and hashed for the denylist). Any other count fails the run,
    /// so a box that is not named is never dropped silently.
    /// </summary>
    public int ReadPastBoxes { get; set; }

    /// <summary>
    /// For a <c>rules</c> row: a heading in the block-header font inside the range (a sub-subsection that has no bookmark, or an activity's name) is a paragraph of the entry, in title case,
    /// and its trait line is a paragraph of its own. Without it such a heading is a block the parser does not claim, and the run fails.
    /// </summary>
    public bool Subheadings { get; set; }

    /// <summary>
    /// The bookmark paths of this row whose section goes on, over a page break, above the title of the next section (the pages between are sidebars, or the bookmark ends a page early). For any other
    /// path that fails the run, since the end of its range is then unknown; for these the range runs to the next title, which is right when the blocks in between are in reading order.
    /// </summary>
    public List<List<string>> RunsToTitle { get; set; } = new();

    public bool RunsToTitleFor(IReadOnlyList<string> path) => RunsToTitle.Any(p => p.SequenceEqual(path, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The bookmark paths whose next section starts on a page of its own that opens with no block of that section's title (the book's class summaries open with a list of entries): the section ends where its
    /// own last page does. For any other path a missing title still fails the run, since the end of the range is then unknown.
    /// </summary>
    public List<List<string>> EndsAtPage { get; set; } = new();

    public bool EndsAtPageFor(IReadOnlyList<string> path) => EndsAtPage.Any(p => p.SequenceEqual(path, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// For the <c>actions</c> parser: the name, in our own words, of the one rule entry made from the section's own prose, the text between the start of the range and its first
    /// skill or subsection heading. Without it that text must be a table (the proficiency table of the general skill actions), which is read past.
    /// </summary>
    public string? LeadName { get; set; }

    /// <summary>
    /// For a <c>rules</c> row: the range ends at the first heading in the block-header font (the caption of a table). The blocks from there to the end of the section (the page's
    /// overview tables) are read past, and are not counted as left out text because they sit in a taken section.
    /// </summary>
    public bool TailFromHeader { get; set; }

    /// <summary>
    /// A block that opens with a line that is only a bold label (a table's title row) is a small table, one row to a line, as it is for a row with a level word.
    /// For a row that has none (the skills chapter's tables of difficulty classes and income).
    /// </summary>
    public bool TableRows { get; set; }

    /// <summary>
    /// For an <c>entries</c> row with a level word whose section has child sections that group its entries (the focus spells of a class, a domain, an order, a school): a heading in the
    /// section font or the entry-header font is the name of a group, and each entry that follows it says that group in its <c>focusGroup</c> field. The row's range starts at the first
    /// group heading, and the section's bookmark descendants are counted against these headings.
    /// </summary>
    public bool Groups { get; set; }

    /// <summary>
    /// For an <c>entries</c> row with a level word: the title of a heading in the block-header font, anywhere in the book, under which a table lists every entry of the row with its rank
    /// (<c>rank name</c> one to a line). It is the entries' independent count: the (name, rank) pairs of the list must equal those of the entries.
    /// </summary>
    public string? ListHeading { get; set; }

    /// <summary>
    /// For an <c>items</c> row: what the row's entries are (<c>armor</c>, <c>shield</c> or <c>weapon</c>). The row's own <see cref="Paths"/> are the sections that describe the items (a description
    /// is a paragraph that opens with the item's name and a colon); the item's statistics are the rows of the stat tables.
    /// </summary>
    public string? ItemKind { get; set; }

    /// <summary>For an <c>items</c> row: the sections whose pages hold the row's stat tables. Only these pages are read for tables (see <see cref="StatTables"/>).</summary>
    public List<List<string>> Tables { get; set; } = new();

    /// <summary>
    /// For an <c>items</c> row: the column label lists a header row of its stat tables may carry, after the name column (each begins with <c>Price</c>). A header with any other labels is not a table.
    /// Each label must be named by a bookmark of the chapter (see <see cref="ImportPipeline.CheckTableLabels"/>), so the columns are counted against the document.
    /// </summary>
    public List<List<string>> TableColumns { get; set; } = new();

    /// <summary>
    /// For an <c>items</c> row: names of table rows (and descriptions) that are not made into entries, each with the reason in <see cref="CountsDiffer"/>. A name that matches no row or description fails the run.
    /// </summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>
    /// For an <c>items</c> row: names of table rows the book has no description for. Each becomes an entry from its row alone (its description is its trait line and notes). A name that has a description,
    /// or is not a row, fails the run.
    /// </summary>
    public List<string> Undescribed { get; set; } = new();

    /// <summary>
    /// For an items row of kind <c>gear</c>: the sections whose table is set in two panels side by side (see <see cref="GearTables"/>); the right panel reads as the rows that follow the left one.
    /// </summary>
    public List<List<string>> TwoUp { get; set; } = new();

    /// <summary>
    /// For an items row of kind <c>gear</c>: the paths of the row whose text before the first description (the section's rules for its items, and a sentence that says what follows) is one
    /// <c>rule</c> entry of the category <see cref="RuleCategory"/>, named by the path's last title. A path not named here has its lead read past, and counted.
    /// </summary>
    public List<List<string>> LeadRules { get; set; } = new();

    /// <summary>
    /// For a <c>rules</c> row: the bookmark paths of sidebars that are taken as rules in spite of the rule that sidebars stay out (a sidebar of game rules that a section's items depend on). Each is one
    /// <c>rule</c> entry of the row's <see cref="Category"/>, named by the bookmark's title without its <c>Sidebar:</c> prefix, whose text is the sidebar's blocks.
    /// </summary>
    public List<List<string>> Sidebars { get; set; } = new();

    /// <summary>
    /// For an <c>items</c> row: names of descriptions that have no row of their own (the kinds of ammunition, whose prices sit under the weapons that fire them). They become the paragraphs of one <c>rule</c> entry,
    /// <see cref="RuleName"/>, of the category <see cref="RuleCategory"/>.
    /// </summary>
    public List<string> RuleDescriptions { get; set; } = new();

    public string? RuleName { get; set; }

    public string? RuleCategory { get; set; }

    /// <summary>For an <c>items</c> row or a <c>rules</c> row among pictures of items: the body lines set in capitals alone (a label under a picture) are skipped, and the run prints how many.</summary>
    public bool ArtLabels { get; set; }

    /// <summary>
    /// For a <c>rules</c> row with subheadings: a block of body text that opens with a bold heading line is a box of prose (the weapon rules' boxes on damage dice), not a small table, so its ragged lines run on.
    /// </summary>
    public bool ProseBoxes { get; set; }

    /// <summary>For a <c>rules</c> row: the section's text is read past, no entry is made (its blocks are claimed, so they are not counted as left out).</summary>
    public bool ReadPast { get; set; }

    /// <summary>
    /// For a <c>rules</c> row: the child sections of the path's section, whose titles are in the entry-header font, are paragraphs of the one entry (the title, then the text). The bookmark children are
    /// counted against those titles (see <see cref="ImportPipeline.CheckFoldCounts"/>). The names in <see cref="ReadPastChildren"/> are children whose text is left out of the entry.
    /// </summary>
    public bool FoldChildren { get; set; }

    public List<string> ReadPastChildren { get; set; } = new();

    /// <summary>
    /// Pages of the row where a sidebar fills a whole column, so the text that opens the next column is the page's own going on from the page before and not the sidebar's continuation (the blocks stage
    /// flags any column that opens with body text after one that ended inside a sidebar).
    /// </summary>
    public List<int> SidebarFillsColumn { get; set; } = new();

    /// <summary>
    /// For a <c>classes</c> row: the title of the class feature whose children (headings in the block-header font, with no action icon) are the class's subclasses. Each becomes a <c>subclass</c> entry.
    /// Left out, the class has none, and such a heading fails the run.
    /// </summary>
    public string? SubclassFeature { get; set; }

    /// <summary>For a <c>classes</c> row: the attribute key (<c>con</c>) each saving throw's name is on, by name, as the book's own rules say it. A saving throw the table lacks fails the run.</summary>
    public Dictionary<string, string> SaveAttributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For a <c>classes</c> row: a name the class's advancement table prints that the feature's heading says otherwise, by the table's name (the book names one feature two ways). The reason goes in
    /// <see cref="CountsDiffer"/>. Any other difference between the table and the feature rows fails the run.
    /// </summary>
    public Dictionary<string, string> AdvancementNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For a <c>classes</c> row: a class feature's heading that the pack says in other words, by the book's heading (a name the setting coined for a thing the rules need: the secret language of a class).
    /// The advancement table's own name for it is mapped by <see cref="AdvancementNames"/>; the book's bookmark is still compared by its own name.
    /// </summary>
    public Dictionary<string, string> FeatureNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For a <c>classes</c> row: true when a feature's sentences and a subclass's labelled lines that name a feat or a spell grant it to the sheet's lists by its id (<c>You gain the N feat</c>, <c>You learn
    /// the n composition spell</c>, an order's or a muse's feat and spell). The grants of the fighter and the rogue (8a) are not read this way, so their entries stay as they were.
    /// </summary>
    public bool GrantsByName { get; set; }

    /// <summary>
    /// For a <c>classes</c> row: the title of a section among the class features (in the section font, not a feature's heading) whose entry-header children are the class's subclasses (a witch's patrons, a
    /// wizard's schools). The section's introduction is part of the feature named by <see cref="SubclassFeature"/>, which the section's choice belongs to.
    /// </summary>
    public string? SubclassSection { get; set; }

    /// <summary>
    /// For a <c>classes</c> row: titles of sections among the class features that are rules of their own and no feature (a witch's lessons): the section, with its children's titles as paragraphs, is one
    /// <c>rule</c> entry of the row's <c>ruleCategory</c>. The bookmark children are counted against the children's titles.
    /// </summary>
    public List<string> RuleSections { get; set; } = new();

    /// <summary>For a <c>classes</c> row: the category of the <c>rule</c> entries of <see cref="RuleSections"/>.</summary>
    public string? RuleSectionCategory { get; set; }

    /// <summary>
    /// For a <c>classes</c> row: class features whose text lists the options of a choice (a wizard's thesis topics, each a heading in the block-header font). Their sentences make no grant by name (an option
    /// that says "You gain the N feat" is one choice of several, not a grant of the class), and the headings are counted against the feature's bookmark children.
    /// </summary>
    public List<string> ChoiceFeatures { get; set; } = new();

    /// <summary>
    /// For a <c>classes</c> row: true when the class casts spells of a tradition its subclass decides (a witch's patron). The class entry then has no tradition, every subclass names exactly one, and the
    /// spell lists read the subclass's.
    /// </summary>
    public bool TraditionFromSubclass { get; set; }

    /// <summary>
    /// For a <c>familiars</c> row: the titles of the sections whose text is a list of abilities (each opens with a bold name and a colon), by the kind each list gives its entries. A section not named here is a rule.
    /// </summary>
    public Dictionary<string, string> AbilityKinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for the row that holds the spells the four tradition lists name: its level words include <c>spell</c>. Only these are counted against the lists.</summary>
    public bool OnTraditionLists => LevelWords.Contains("spell", StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="block"/> is an entry header of this row.</summary>
    public bool IsHeader(Block block) =>
        block.Role == (HeaderRole ?? "entry-header")
        && (LevelWord == null || LevelHeader().IsMatch(SpanText.Normalize(string.Join(' ', block.Lines))));

    private Regex? _levelHeader;

    private Regex LevelHeader() => _levelHeader ??= new Regex(@"\s" + LevelPattern + @"\s+\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The bookmark path of a taken range, whichever list it came from.</summary>
    public List<string> PathOf(TakenRange range) => range.Own ? Own[range.PathNumber - Paths.Count - 1] : Paths[range.PathNumber - 1];

    public string FolderName => Folder ?? (Target.EndsWith('y') ? Target[..^1] + "ies" : Target + "s");
}

/// <summary>The planned bookmark paths of one book (see <see cref="TakeList.Planned"/>).</summary>
public sealed class PlannedRow
{
    public string Book { get; set; } = string.Empty;
    public List<List<string>> Paths { get; set; } = new();
}

public sealed partial class TakeList
{
    public static readonly string[] Parsers = { "entries", "statblocks", "table", "glossary", "rules", "actions", "spelllists", "items", "ancestries", "versatile", "backgrounds", "classes", "companions", "familiars", "archetypes" };

    /// <summary>The parsers that exist. Any other parser in the list fails the run, so a row cannot silently take nothing.</summary>
    public static readonly string[] BuiltParsers = { "entries", "rules", "glossary", "actions", "spelllists", "items", "ancestries", "versatile", "backgrounds", "classes", "companions", "familiars", "archetypes" };

    public List<TakeRow> Rows { get; set; } = new();

    /// <summary>
    /// Sections of a book that a later chapter will take. A planned path is not read, parsed or approved: it only tells the denylist generator that the section is not
    /// left out, so its words are not hashed as if they were setting material. Anything on neither list is left out.
    /// </summary>
    public List<PlannedRow> Planned { get; set; } = new();

    private static readonly IDeserializer Strict = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public static TakeList Parse(string yaml)
    {
        TakeList list;
        try
        {
            list = Strict.Deserialize<TakeList>(yaml) ?? new TakeList();
        }
        catch (Exception)
        {
            throw new DegreesException("degrees-chapters.yaml is not valid YAML of the expected shape.");
        }

        list.Validate();
        return list;
    }

    public static TakeList Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-chapters.yaml"));

    private sealed class SystemTypes
    {
        public List<SystemType> EntityTypes { get; set; } = new();
    }

    private sealed class SystemType
    {
        public string Type { get; set; } = string.Empty;
        public string? Folder { get; set; }
    }

    /// <summary>
    /// Checks the rows' targets against the system's own <c>entityTypes</c>: each target must be a declared type, and the pack folder must be that type's
    /// <c>folder</c>, because the loader types a folder by the system's declaration and treats any other name as an undeclared type that shows nowhere.
    /// </summary>
    public void CheckAgainstSystem(string systemYaml)
    {
        SystemTypes system;
        try
        {
            system = BooksConfig.Deserializer.Deserialize<SystemTypes>(systemYaml) ?? new SystemTypes();
        }
        catch (Exception)
        {
            throw new DegreesException("system.yaml could not be read for its entity types.");
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            var type = system.EntityTypes.FirstOrDefault(t => t.Type == Rows[i].Target)
                ?? throw new DegreesException($"Take-list row {i + 1}: the system declares no entity type for the target.");
            if ((type.Folder ?? type.Type) != Rows[i].FolderName)
            {
                throw new DegreesException($"Take-list row {i + 1}: the pack folder is not the target type's folder in the system.");
            }

            // The actions parser also writes the rules text between its actions as rule entries, so the system must declare that type, in its default folder.
            var ruleType = system.EntityTypes.FirstOrDefault(t => t.Type == "rule");
            if (Rows[i].Parser == "actions" && (ruleType == null || (ruleType.Folder ?? ruleType.Type) != "rules"))
            {
                throw new DegreesException($"Take-list row {i + 1}: the actions parser also writes rule entries, and the system declares no rule type with the folder rules.");
            }

            // The ancestries parser also writes the heritages and the ancestry feats of the ancestries it reads.
            // The versatile parser writes heritages, their feats and the introduction of a section of several as a rule; the backgrounds parser writes backgrounds and the introduction as a rule.
            if (Rows[i].Parser is "ancestries" or "versatile" or "backgrounds" or "classes" or "companions" or "familiars" or "archetypes")
            {
                var needed = Rows[i].Parser switch
                {
                    "archetypes" => new[] { ("feat", "feats"), ("rule", "rules") },
                    "classes" => new[] { ("subclass", "subclasses"), ("feat", "feats") },
                    "ancestries" => new[] { ("heritage", "heritages"), ("feat", "feats") },
                    "versatile" => new[] { ("heritage", "heritages"), ("feat", "feats"), ("rule", "rules") },
                    _ => new[] { ("rule", "rules") },
                };
                foreach (var (also, folder) in needed)
                {
                    var declared = system.EntityTypes.FirstOrDefault(t => t.Type == also);
                    if (declared == null || (declared.Folder ?? declared.Type) != folder)
                    {
                        throw new DegreesException($"Take-list row {i + 1}: the ancestries parser also writes {also} entries, and the system declares no {also} type with the folder {folder}.");
                    }
                }
            }
        }
    }

    public IEnumerable<TakeRow> For(string book) => Rows.Where(r => r.Book == book);

    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var other = Rows.GroupBy(r => r.FolderName, StringComparer.Ordinal).FirstOrDefault(g => g.Select(r => r.Target).Distinct().Count() > 1);
        if (other != null)
        {
            throw new DegreesException("Take-list: two different targets share one pack folder, so their entries would merge into one file.");
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            var n = i + 1;
            if (!Key().IsMatch(r.Chapter) || !Key().IsMatch(r.Target) || !(r.Folder == null || Key().IsMatch(r.Folder)))
            {
                throw new DegreesException($"Take-list row {n}: chapter, target and folder must be lowercase keys.");
            }

            if (!DegreesOptions.Books.Contains(r.Book))
            {
                throw new DegreesException($"Take-list row {n}: book must be one of {string.Join(", ", DegreesOptions.Books)}.");
            }

            if (!Parsers.Contains(r.Parser))
            {
                throw new DegreesException($"Take-list row {n}: parser must be one of {string.Join(", ", Parsers)}.");
            }

            if (r.Columns is < 1 or > 4)
            {
                throw new DegreesException($"Take-list row {n}: columns must be 1 to 4.");
            }

            if ((r.Paths.Count == 0 && r.Own.Count == 0) || r.Paths.Any(p => p.Count == 0 || p.Any(string.IsNullOrWhiteSpace)))
            {
                throw new DegreesException($"Take-list row {n}: it needs at least one bookmark path (or an own-text path), and no empty title.");
            }

            foreach (var path in r.Paths)
            {
                if (!seen.Add(r.Book + "\n" + string.Join("\n", path.Select(t => t.Trim().ToLowerInvariant()))))
                {
                    throw new DegreesException($"Take-list row {n}: a bookmark path is taken twice.");
                }
            }

            if ((r.HeaderRole != null || r.LevelWord != null) && r.Parser != "entries")
            {
                throw new DegreesException($"Take-list row {n}: headerRole and levelWord belong to the entries parser.");
            }

            if (r.LevelWord != null && r.HeaderRole == null)
            {
                throw new DegreesException($"Take-list row {n}: levelWord needs a headerRole.");
            }

            if (r.Own.Count > 0 && r.Parser != "rules")
            {
                throw new DegreesException($"Take-list row {n}: own-text paths belong to the rules parser.");
            }

            // Without an ownName, an own-text entry is named by its path's last title; with several own-text paths that is the only way to name each one.
            if (r.Own.Any(p => p.Count == 0 || p.Any(string.IsNullOrWhiteSpace)) || (r.Own.Count > 1 && !string.IsNullOrWhiteSpace(r.OwnName)))
            {
                throw new DegreesException($"Take-list row {n}: an own-text path needs titles, and a row with several of them takes no ownName (each is named by its last title).");
            }

            if ((r.Subheadings && r.Parser != "rules") || ((r.Boxes.Count > 0 || r.ReadPastBoxes > 0) && (r.Parser != "entries" || r.LevelWord == null)))
            {
                throw new DegreesException($"Take-list row {n}: subheadings belong to the rules parser, and boxes and readPastBoxes to an entries row with a levelWord.");
            }

            if ((r.Groups || r.ListHeading != null) && (r.Parser != "entries" || r.LevelWord == null || r.Target != "spell"))
            {
                throw new DegreesException($"Take-list row {n}: groups and listHeading belong to an entries row of spells with a levelWord.");
            }

            var itemRow = r.Parser == "items";
            if ((r.ArtLabels && !itemRow && r.Parser != "rules") || ((r.ReadPast || r.FoldChildren || r.ProseBoxes || r.ReadPastChildren.Count > 0) && r.Parser != "rules") || (r.ReadPastChildren.Count > 0 && !r.FoldChildren))
            {
                throw new DegreesException($"Take-list row {n}: artLabels belong to the items and rules parsers, readPast and foldChildren to the rules parser, and readPastChildren to a foldChildren row.");
            }

            if (!itemRow && (r.Undescribed.Count > 0 || r.RuleDescriptions.Count > 0 || r.RuleName != null || r.RuleCategory != null))
            {
                throw new DegreesException($"Take-list row {n}: undescribed, ruleDescriptions, ruleName and ruleCategory belong to the items parser.");
            }

            if (r.RuleDescriptions.Count > 0 && (string.IsNullOrWhiteSpace(r.RuleName) || string.IsNullOrWhiteSpace(r.RuleCategory)))
            {
                throw new DegreesException($"Take-list row {n}: ruleDescriptions need a ruleName and a ruleCategory.");
            }

            if (itemRow != (r.ItemKind != null) || (!itemRow && (r.Tables.Count > 0 || r.TableColumns.Count > 0 || r.Exclude.Count > 0))
                || (itemRow && (r.ItemKind is not ("armor" or "shield" or "weapon" or "gear") || r.Tables.Count == 0 || r.TableColumns.Count == 0
                    || r.TableColumns.Any(c => c.Count < 2 || c[0] != "Price" || c.Any(string.IsNullOrWhiteSpace)))))
            {
                throw new DegreesException($"Take-list row {n}: itemKind, tables and tableColumns (each list starting with Price) belong to the items parser, which needs all three and an itemKind of armor, shield, weapon or gear.");
            }

            if (((r.TwoUp.Count > 0 || r.LeadRules.Count > 0) && r.ItemKind != "gear") || (r.Sidebars.Count > 0 && r.Parser != "rules")
                || (r.LeadRules.Count > 0 && string.IsNullOrWhiteSpace(r.RuleCategory)) || r.Sidebars.Any(p => p.Count == 0 || p.Any(string.IsNullOrWhiteSpace)))
            {
                throw new DegreesException($"Take-list row {n}: twoUp and leadRules belong to an items row of kind gear (leadRules needs a ruleCategory), and sidebars to the rules parser, each with titles.");
            }

            // An option that names a path or a title the row does not have would do nothing, silently: it fails here.
            bool Same(List<string> a, List<string> b) => a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
            if (r.LeadRules.Any(p => !r.Paths.Any(q => Same(p, q))) || r.TwoUp.Any(p => !r.Tables.Any(q => Same(p, q))))
            {
                throw new DegreesException($"Take-list row {n}: a leadRules path must be one of the row's paths, and a twoUp path one of its tables.");
            }

            if (r.EntryNames.Count > 0 && (r.Parser != "rules" || r.EntryNames.Any(e => string.IsNullOrWhiteSpace(e.Value) || !r.Paths.Concat(r.Own).Any(p => p[^1].Equals(e.Key, StringComparison.OrdinalIgnoreCase)))))
            {
                throw new DegreesException($"Take-list row {n}: entryNames belongs to the rules parser, each key being the last title of one of the row's paths or own-text paths, with a name for a value.");
            }

            if (r.Exclude.Count > 0 && string.IsNullOrWhiteSpace(r.CountsDiffer))
            {
                throw new DegreesException($"Take-list row {n}: exclude needs a countsDiffer reason.");
            }

            if ((r.LeadName != null && r.Parser != "actions") || (r.TailFromHeader && r.Parser != "rules") || (r.TableRows && r.Parser == "glossary"))
            {
                throw new DegreesException($"Take-list row {n}: leadName belongs to the actions parser, tailFromHeader to the rules parser, and tableRows to a parser that reads blocks into paragraphs.");
            }

            foreach (var path in r.Own)
            {
                if (!seen.Add("own\n" + r.Book + "\n" + string.Join("\n", path.Select(t => t.Trim().ToLowerInvariant()))))
                {
                    throw new DegreesException($"Take-list row {n}: an own-text path is taken twice.");
                }
            }
        }

        foreach (var planned in Planned)
        {
            if (!DegreesOptions.Books.Contains(planned.Book) || planned.Paths.Count == 0 || planned.Paths.Any(p => p.Count == 0 || p.Any(string.IsNullOrWhiteSpace)))
            {
                throw new DegreesException("Take-list: a planned entry needs a book and bookmark paths with no empty title.");
            }

            if (planned.Paths.Any(p => Rows.Any(r => r.Book == planned.Book && r.Paths.Any(t => t.SequenceEqual(p, StringComparer.OrdinalIgnoreCase)))))
            {
                throw new DegreesException("Take-list: a planned path is also taken.");
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex Key();
}
