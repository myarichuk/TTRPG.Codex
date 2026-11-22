using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>A class feature as the blocks hold it: the heading (a name and the level it is gained at) and the blocks of its text.</summary>
public sealed class ClassFeatureScan
{
    public Block Header { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; } = 1;
    public List<Block> Body { get; } = new();
}

/// <summary>A subclass (a choice made inside the class) as the blocks hold it: the heading and the blocks of its text.</summary>
public sealed class SubclassScan
{
    public Block Header { get; set; } = null!;
    public List<Block> Body { get; } = new();
}

/// <summary>A section among the class features that is a rule of its own (a witch's lessons): its title, its introduction, and each child heading with its text.</summary>
public sealed class RuleSectionScan
{
    public Block Title { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public List<Block> Intro { get; } = new();
    public List<SubclassScan> Tiers { get; } = new();
}

/// <summary>
/// A class's range sorted into its parts: the flavor and the explanations that are read past, the statistics box, the initial proficiencies, the advancement table, the class features
/// (with the subclasses a feature holds) and the class feats under their level bands.
/// </summary>
public sealed class ClassScan
{
    /// <summary>Every block the range reads past: the flavor children and their text, the box's explanation, the introductions of the proficiencies, the features and the feats.</summary>
    public List<Block> Flavor { get; } = new();

    /// <summary>The flavor headings only (the bookmark children that come before the class features), for the count.</summary>
    public int LoreHeaders { get; set; }

    public Block? StatsBox { get; set; }
    public List<(string Label, List<Block> Body)> Initial { get; } = new();
    public Block? AdvancementTable { get; set; }
    public List<ClassFeatureScan> Features { get; } = new();
    public List<SubclassScan> Subclasses { get; } = new();

    /// <summary>The introduction of the row's subclass section (a witch's patrons, a wizard's schools), which belongs to the class feature that names the choice.</summary>
    public List<Block> SubclassIntro { get; } = new();

    public List<RuleSectionScan> RuleSections { get; } = new();
    public List<FeatScan> Feats { get; } = new();
    public List<(Block Block, int Level)> Bands { get; } = new();
    public string? FeatsTitle { get; set; }

    /// <summary>The blocks of the printed list of the class's feats (each line a name and a level), and how many headings of it the range holds.</summary>
    public List<Block> ByName { get; } = new();

    public int ByNameHeaders { get; set; }

    /// <summary>For a spellcaster: how many "spells per day" table headings the features hold, and the blocks of the table (its two halves, the ranks 1 to 4 and 5 to 10, and the notes under them).</summary>
    public int SpellTableHeaders { get; set; }

    public List<Block> SpellTable { get; } = new();
}

/// <summary>What a class's statistics box prints: the key attribute (or a choice of two, or one more option the subclasses give) and the hit points gained at each level.</summary>
public sealed class ClassBox
{
    /// <summary>The attribute keys the class may take as its key, joined with a vertical bar when there is a choice.</summary>
    public string KeyAttribute { get; set; } = string.Empty;

    /// <summary>True when the box also allows an option the class's subclasses give.</summary>
    public bool OtherOption { get; set; }

    public int Hp { get; set; }
}

/// <summary>What the initial proficiencies give: the saves it starts trained in, the extra skills, and the grants to ranked lists (flow-map YAML).</summary>
public sealed class InitialProficiencies
{
    public List<string> TrainedSaves { get; } = new();
    public int ExtraSkills { get; set; }
    public List<string> Adds { get; } = new();

    /// <summary>The statements in the book's order, by category, as the feature's text says them.</summary>
    public List<(string Label, string Text)> Statements { get; } = new();
}

public static partial class EntriesStage
{
    private static readonly Regex ClassFeatureHeader = new(@"^(?<name>.+?)(?:\s+(?<n>\d+)(?:st|nd|rd|th))?$", RegexOptions.CultureInvariant);

    /// <summary>The start of a run of a subclass's text that is gained at a level: a name of two capitalized words and the level in parentheses, then a colon (<c>Second Doctrine (3rd):</c>).</summary>
    private static readonly Regex LevelledMarker = new(@"(?<![A-Za-z])(?<name>[A-Z][a-z]+ [A-Z][a-z]+) \((?<n>\d+)(?:st|nd|rd|th)\):\s*", RegexOptions.CultureInvariant);

    /// <summary>
    /// Cuts a subclass's paragraphs at the markers of the runs it gains at a later level. What comes before the first marker, and the run of the 1st level, stay with the subclass's own row (as paragraphs);
    /// each run after the 1st level is a row of its own: its name, its level and its text.
    /// </summary>
    public static (List<LabelledText> Kept, List<(string Name, int Level, string Text)> Rows) SplitLevelledParagraphs(IEnumerable<LabelledText> parts)
    {
        var kept = new List<LabelledText>();
        var rows = new List<(string, int, string)>();
        foreach (var part in parts)
        {
            var markers = part.Label.Length == 0 ? LevelledMarker.Matches(part.Text) : null;
            if (markers is not { Count: > 0 })
            {
                kept.Add(part);
                continue;
            }

            if (markers[0].Index > 0)
            {
                kept.Add(part with { Text = part.Text[..markers[0].Index].Trim() });
            }

            for (var i = 0; i < markers.Count; i++)
            {
                var end = i + 1 < markers.Count ? markers[i + 1].Index : part.Text.Length;
                var level = int.Parse(markers[i].Groups["n"].Value, CultureInfo.InvariantCulture);
                var text = part.Text[(markers[i].Index + markers[i].Length)..end].Trim();
                if (level > 1)
                {
                    rows.Add((markers[i].Groups["name"].Value, level, text));
                }
                else
                {
                    kept.Add(new LabelledText(markers[i].Groups["name"].Value + " (1st)", text));
                }
            }
        }

        return (kept, rows);
    }

    private static readonly string[] InitialLabels = { "PERCEPTION", "SAVING THROWS", "SKILLS", "ATTACKS", "DEFENSES", "CLASS DC" };

    /// <summary>The seventh label of a spellcaster's initial proficiencies: the spell attack modifier and the spell DC, which share one rank.</summary>
    private const string SpellsLabel = "SPELLS";

    private static readonly string[] RankWords = { "trained", "expert", "master", "legendary" };

    /// <summary>
    /// Sorts the blocks of one class's range. The statistics box and the proficiencies come before the features (in the entry-header and block-header fonts), the features follow the
    /// section title <c>Class Features</c>, and the feats follow the section title that ends in <c>Feats</c>. A block that fits none of the parts fails the run.
    /// </summary>
    public static ClassScan ScanClass(List<Block> slice, TakeRow row)
    {
        var scan = new ClassScan();
        var mode = 'B'; // B before the features, C the class features, F the feats
        var pending = 'N'; // N nothing, S statistics box, I proficiencies' introduction, L the body of an initial label, A advancement table
        ClassFeatureScan? feature = null;
        SubclassScan? subclass = null;
        FeatScan? feat = null;
        var sect = 'N'; // N no section, S the row's subclass section, R a rule section (among the class features)
        RuleSectionScan? ruleSection = null;
        var band = 0;
        var lastWasTable = false;
        var lastWasAdvancement = false;
        var listTitleSeen = false;
        foreach (var b in slice)
        {
            var text = BlockText(b);
            var afterTable = lastWasTable;
            lastWasTable = false;
            var afterAdvancement = lastWasAdvancement;
            lastWasAdvancement = false;
            var afterListTitle = listTitleSeen;
            listTitleSeen = false;
            switch (b.Role)
            {
                // The printed list's column labels may sit alone in a block between its title and its body (a column break): they belong to the list.
                case "body" when afterListTitle && Regex.IsMatch(text, @"^Feat\s+Level$", RegexOptions.CultureInvariant):
                    scan.ByName.Add(b);
                    break;
                case "entry-header" when mode == 'B':
                    if (text == "KEY ATTRIBUTE HIT POINTS")
                    {
                        pending = 'S';
                    }
                    else
                    {
                        scan.Flavor.Add(b);
                        scan.LoreHeaders++;
                        pending = 'N';
                    }

                    break;
                case "entry-header" when mode == 'C' && sect == 'S':
                    subclass = new SubclassScan { Header = b }; // a patron, a school: a heading in the entry-header font under the row's subclass section
                    scan.Subclasses.Add(subclass);
                    break;
                case "entry-header" when mode == 'C' && sect == 'R':
                    ruleSection!.Tiers.Add(new SubclassScan { Header = b });
                    break;
                case "entry-header" when mode == 'C':
                    var m = ClassFeatureHeader.Match(Clean(text));
                    feature = new ClassFeatureScan
                    {
                        Header = b,
                        Name = m.Groups["name"].Value,
                        Level = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 1,
                    };
                    scan.Features.Add(feature);
                    subclass = null;
                    break;
                case "block-header" when mode == 'B' || (mode == 'C' && IsClassBoxHeader(text)):
                    // The boxes of the proficiencies and the advancement table sit in a column of their own and may come after the section title of the features (a page's second column): their headings mean the same there.
                    if (text == "INITIAL PROFICIENCIES")
                    {
                        pending = 'I';
                    }
                    else if (text.EndsWith(" ADVANCEMENT", StringComparison.Ordinal))
                    {
                        pending = 'A';
                    }
                    else if ((InitialLabels.Contains(text, StringComparer.Ordinal) || text == SpellsLabel) && scan.Initial.All(i => i.Label != text))
                    {
                        scan.Initial.Add((text, new List<Block>()));
                        pending = 'L';
                    }
                    else
                    {
                        throw new DegreesException($"A class's range holds a heading on page {b.Page}, column {b.Column}, that is none of its statistics, proficiency labels or advancement table.");
                    }

                    break;
                case "block-header" when mode == 'C':
                    if (feature == null)
                    {
                        throw new DegreesException($"A class's features hold a heading on page {b.Page}, column {b.Column}, before the first feature.");
                    }

                    if (text.EndsWith(" FEATS BY NAME", StringComparison.Ordinal))
                    {
                        scan.ByNameHeaders++; // the printed list of the class's feats may sit among the features (a page's column of the spellcasters' classes): the same independent count
                        listTitleSeen = true;
                        break;
                    }

                    if (Regex.IsMatch(text, @" SPELLS PER DAY[*†]*$", RegexOptions.CultureInvariant))
                    {
                        scan.SpellTableHeaders++; // a spellcaster's table of cantrips and spell slots: read as a table, not as the feature's text (its title may carry a note's mark)
                        break;
                    }

                    if (sect != 'N')
                    {
                        throw new DegreesException($"A class's section holds a heading in the block-header font on page {b.Page}, column {b.Column}, which the reader does not expect there.");
                    }

                    if (row.SubclassFeature != null && Slug(feature.Name) == Slug(row.SubclassFeature) && !text.Contains('['))
                    {
                        subclass = new SubclassScan { Header = b };
                        scan.Subclasses.Add(subclass);
                    }
                    else if (subclass != null)
                    {
                        throw new DegreesException($"A class's subclass holds an action heading on page {b.Page}, column {b.Column}.");
                    }
                    else
                    {
                        feature.Body.Add(b); // an action the feature gives: its heading, trait line and text are part of the feature
                    }

                    break;
                case "block-header" when mode == 'F':
                    if (text.EndsWith(" FEATS BY NAME", StringComparison.Ordinal))
                    {
                        scan.ByNameHeaders++; // the printed list of the class's feats (name and level): the independent count, not an entry
                        listTitleSeen = true;
                    }
                    else if (AncestryBand.Match(text) is { Success: true } bandMatch)
                    {
                        band = int.Parse(bandMatch.Groups["n"].Value, CultureInfo.InvariantCulture);
                        scan.Bands.Add((b, band));
                        feat = null;
                    }
                    else if (AncestryFeatHeader.IsMatch(HeaderWithoutIcons(text)))
                    {
                        feat = new FeatScan { Header = b, Band = band };
                        scan.Feats.Add(feat);
                    }
                    else
                    {
                        throw new DegreesException($"A class's feats hold a heading on page {b.Page}, column {b.Column}, that is neither a level band nor a feat's header.");
                    }

                    break;
                case "section":
                    if (text == "Class Features" && mode == 'B')
                    {
                        mode = 'C';
                        pending = 'N';
                    }
                    else if (mode == 'C' && row.SubclassSection != null && text == row.SubclassSection)
                    {
                        sect = 'S';
                        subclass = null;
                    }
                    else if (mode == 'C' && row.RuleSections.Contains(text))
                    {
                        sect = 'R';
                        subclass = null;
                        ruleSection = new RuleSectionScan { Title = b, Name = text };
                        scan.RuleSections.Add(ruleSection);
                    }
                    else if (text.EndsWith("Feats", StringComparison.Ordinal) && mode == 'C')
                    {
                        mode = 'F';
                        sect = 'N';
                        subclass = null;
                        scan.FeatsTitle = Clean(text);
                    }
                    else
                    {
                        throw new DegreesException($"A class's range holds a section title on page {b.Page} that is not the next of its two sections.");
                    }

                    break;
                case "tag" when mode == 'C' && feature != null:
                    if (subclass != null)
                    {
                        throw new DegreesException($"A class's subclass holds a trait line on page {b.Page}, column {b.Column}.");
                    }

                    feature.Body.Add(b);
                    break;
                case "tag" when mode == 'F' && feat != null && feat.Body.Count == 0:
                    feat.Tags.Add(b);
                    break;
                case "body" when mode != 'B' && scan.ByNameHeaders > 0 && IsByNameList(b):
                    scan.ByName.Add(b);
                    break;
                case "body" when afterAdvancement && scan.AdvancementTable != null && b.Role == "body" && b.Page == scan.AdvancementTable.Page && b.Column == scan.AdvancementTable.Column:
                    // The advancement table's block was cut in two (a row runs over): the second part goes on the first.
                    scan.AdvancementTable = scan.AdvancementTable with
                    {
                        Lines = scan.AdvancementTable.Lines.Concat(b.Lines).ToList(),
                        Info = scan.AdvancementTable.Info == null || b.Info == null ? null : scan.AdvancementTable.Info.Concat(b.Info).ToList(),
                    };
                    lastWasAdvancement = true;
                    break;
                case "rules" or "body" or "icon":
                    if (mode == 'B' || (mode == 'C' && pending != 'N' && b.Role == "body"))
                    {
                        if (pending == 'S' && b.Role == "body" && scan.StatsBox == null)
                        {
                            scan.StatsBox = b;
                            pending = 'N';
                        }
                        else if (pending == 'A' && b.Role == "body" && scan.AdvancementTable == null)
                        {
                            scan.AdvancementTable = b;
                            pending = 'N';
                            lastWasAdvancement = true;
                        }
                        else if (pending == 'L')
                        {
                            scan.Initial[^1].Body.Add(b);
                        }
                        else
                        {
                            scan.Flavor.Add(b); // flavor text, the box's explanation, the introduction of the proficiencies
                        }

                        if (mode == 'C')
                        {
                            pending = 'N'; // a box among the features: one block of text under its heading
                        }
                    }
                    else if (mode == 'C')
                    {
                        if (b.Role == "body" && scan.SpellTableHeaders > 0 && IsSpellTableBlock(b, afterTable))
                        {
                            scan.SpellTable.Add(b);
                            lastWasTable = true;
                        }
                        else if (sect == 'S' && subclass == null)
                        {
                            scan.SubclassIntro.Add(b); // the section's introduction: rules of the choice (the feature that names the section gets them)
                        }
                        else if (sect == 'R')
                        {
                            if (ruleSection!.Tiers.Count == 0)
                            {
                                ruleSection.Intro.Add(b);
                            }
                            else
                            {
                                ruleSection.Tiers[^1].Body.Add(b);
                            }
                        }
                        else if (feature == null)
                        {
                            scan.Flavor.Add(b); // the introduction of the features
                        }
                        else if (subclass != null)
                        {
                            subclass.Body.Add(b);
                        }
                        else
                        {
                            feature.Body.Add(b);
                        }
                    }
                    else if (feat == null)
                    {
                        if (scan.Bands.Count > 0)
                        {
                            throw new DegreesException($"A class's feats hold a {b.Role} block on page {b.Page}, column {b.Column}, after a level band and before a feat's header.");
                        }

                        scan.Flavor.Add(b); // the introduction of the feats
                    }
                    else
                    {
                        feat.Body.Add(b);
                    }

                    break;
                default:
                    throw new DegreesException($"A class's range holds a {b.Role} block on page {b.Page}, column {b.Column}, where its {(mode == 'B' ? "statistics" : mode == 'C' ? "features" : "feats")} have none.");
            }
        }

        if (scan.StatsBox == null || scan.AdvancementTable == null || scan.Initial.Count(i => i.Label != SpellsLabel) != InitialLabels.Length || scan.FeatsTitle == null || scan.Features.Count == 0)
        {
            throw new DegreesException($"A class's range (from page {slice.FirstOrDefault()?.Page}) lacks its statistics box, its proficiencies, its advancement table, its features or its feats " +
                $"(box {scan.StatsBox != null}, table {scan.AdvancementTable != null}, {scan.Initial.Count(i => i.Label != SpellsLabel)} of {InitialLabels.Length} proficiency labels, feats {scan.FeatsTitle != null}, {scan.Features.Count} features).");
        }

        if ((row.SubclassFeature != null || row.SubclassSection != null) && scan.Subclasses.Count == 0)
        {
            throw new DegreesException("The row names a feature or a section that holds subclasses, and the class has none.");
        }

        if (row.RuleSections.Count != scan.RuleSections.Count || scan.RuleSections.Any(r => r.Tiers.Count == 0 || r.Tiers.Any(t => t.Body.Count == 0)))
        {
            throw new DegreesException("The row names rule sections among the class features, and the class's range does not hold each of them with its children and their text.");
        }

        return scan;
    }

    private static readonly Regex ByNameLine = new(@"^(?<name>[A-Z].*?)\s+(?<level>\d{1,2})$", RegexOptions.CultureInvariant);

    /// <summary>A block of a list of feats: at least ten lines, each a name and a level (the first may be the column labels). No feat's text has that shape.</summary>
    private static bool IsByNameList(Block block) =>
        block.Lines.Count >= 10 && block.Lines.Skip(block.Lines[0].StartsWith("Feat", StringComparison.Ordinal) ? 1 : 0).All(l => ByNameLine.IsMatch(SpanText.Normalize(l).Trim()));

    /// <summary>The (name id, level) pairs of the printed list of a class's feats.</summary>
    public static List<(string Id, int Level)> ByNamePairs(ClassScan scan) =>
        scan.ByName.SelectMany(b => b.Lines).Select(l => ByNameLine.Match(SpanText.Normalize(l).Trim())).Where(m => m.Success)
            .Select(m => (Slug(m.Groups["name"].Value), int.Parse(m.Groups["level"].Value, CultureInfo.InvariantCulture))).ToList();

    private static string AttributeKeyOf(string word) =>
        AttributeKeys.TryGetValue(word, out var key) ? key : throw new DegreesException("A class's key attribute names a word that is no attribute.");

    /// <summary>Reads the box's first line: two cells, the key attribute and the hit points. The lines under it explain them and are read past.</summary>
    public static ClassBox ParseClassBox(Block box)
    {
        if (box.Info is not { Count: > 0 } info || info[0].Cells is not { Count: 2 } cells)
        {
            throw new DegreesException($"A class's statistics box (page {box.Page}) does not hold its key attribute and hit points in two cells.");
        }

        var result = new ClassBox();
        var keys = new List<string>();
        foreach (var word in SpanText.Normalize(cells[0].Text).Trim().Split(" or ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("other", StringComparison.Ordinal))
            {
                result.OtherOption = true;
            }
            else
            {
                keys.Add(AttributeKeyOf(word));
            }
        }

        if (keys.Count == 0)
        {
            throw new DegreesException($"A class's statistics box (page {box.Page}) names no key attribute.");
        }

        result.KeyAttribute = string.Join('|', keys);
        var hp = Regex.Match(SpanText.Normalize(cells[1].Text).Trim(), @"^(?<n>\d+) plus your Constitution modifier$", RegexOptions.CultureInvariant);
        if (!hp.Success)
        {
            throw new DegreesException($"A class's statistics box (page {box.Page}) does not give its hit points as a number plus the Constitution modifier.");
        }

        result.Hp = int.Parse(hp.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (!BlockText(box).Contains("attribute boost to", StringComparison.Ordinal))
        {
            throw new DegreesException($"A class's statistics box (page {box.Page}) does not say what attribute boost the class gives.");
        }

        return result;
    }

    /// <summary>One statement of the proficiencies: the lines of a label, a line that does not open with a rank continuing the line before it.</summary>
    private static List<string> InitialStatements(List<Block> body)
    {
        var statements = new List<string>();
        foreach (var line in body.SelectMany(b => b.Lines).Select(l => Clean(l)).Where(l => l.Length > 0))
        {
            if (Regex.IsMatch(line, @"^(?:Untrained|Trained|Expert|Master|Legendary) in\b", RegexOptions.CultureInvariant) || statements.Count == 0)
            {
                statements.Add(line);
            }
            else
            {
                statements[^1] += " " + line;
            }
        }

        return statements;
    }

    /// <summary>Where a word the book uses for something with a proficiency rank lands: the sheet's ranked list and its entry keys (none for a list of one).</summary>
    private static (string List, string[] Entries)[] RankTargets(string what, TakeRow row)
    {
        var w = Regex.Replace(Regex.Replace(what.Trim().ToLowerInvariant(), @"^(?:your|the)\s+", string.Empty), @"\s+statistics?$", string.Empty);
        if (w == "perception")
        {
            return new[] { ("perception", Array.Empty<string>()) };
        }

        if (Regex.IsMatch(w, @"^[a-z' ]*class dc$"))
        {
            return new[] { ("classDc", Array.Empty<string>()) };
        }

        if (Regex.Match(w, @"^(?<s>[a-z]+?)(?: saves?| saving throws?)?$") is { Success: true } save
            && row.SaveAttributes.FirstOrDefault(kv => kv.Key.Equals(save.Groups["s"].Value, StringComparison.OrdinalIgnoreCase)) is { Value: { } attribute })
        {
            return new[] { ("saves", new[] { attribute }) };
        }

        if (Regex.IsMatch(w, @"^spell attack(?: modifier| roll)?s?$"))
        {
            return new[] { ("spellAttack", Array.Empty<string>()) };
        }

        if (Regex.IsMatch(w, @"^spell dcs?$"))
        {
            return new[] { ("spellDc", Array.Empty<string>()) };
        }

        // The class's own DC said without the word class (a ranger's, a spellcaster's is the spell DC above).
        if (Regex.IsMatch(w, @"^[a-z']+ dc$"))
        {
            return new[] { ("classDc", Array.Empty<string>()) };
        }

        return w switch
        {
            "all armor" => new[] { ("armor", new[] { "unarmored", "light", "medium", "heavy" }) },
            "unarmored defense" or "unarmored" => new[] { ("armor", new[] { "unarmored" }) },
            "light armor" or "light" => new[] { ("armor", new[] { "light" }) },
            "medium armor" or "medium" => new[] { ("armor", new[] { "medium" }) },
            "heavy armor" or "heavy" => new[] { ("armor", new[] { "heavy" }) },
            "simple weapons" => new[] { ("weapons", new[] { "simple" }) },
            "martial weapons" => new[] { ("weapons", new[] { "martial" }) },
            "advanced weapons" => new[] { ("weapons", new[] { "advanced" }) },
            "unarmed attacks" or "unarmed" => new[] { ("weapons", new[] { "unarmed" }) },
            _ => throw new DegreesException("A class's proficiency names something that is no ranked list of the sheet, no saving throw of the row and no skill."),
        };
    }

    /// <summary>The words of a list of things that share one rank: "A, B, and C", "A and B", with "as well as for" joining the last.</summary>
    private static string[] RankedThings(string what) =>
        Regex.Split(Regex.Replace(Regex.Replace(what, @",?\s+as well as for\s+", ", "), @",?\s+and\s+", ", "), @",\s*").Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

    /// <summary>Grants for a rank over several things, grouped by ranked list: <c>{ to: armor, entries: [light, medium], rank: expert }</c>.</summary>
    private static List<string> RankGrants(IEnumerable<string> things, string rank, TakeRow row)
    {
        var grouped = new List<(string List, List<string> Entries)>();
        foreach (var thing in things)
        {
            foreach (var (list, entries) in RankTargets(thing, row))
            {
                var at = grouped.FindIndex(g => g.List == list);
                if (at < 0)
                {
                    grouped.Add((list, new List<string>()));
                    at = grouped.Count - 1;
                }

                grouped[at].Entries.AddRange(entries.Where(e => !grouped[at].Entries.Contains(e)));
            }
        }

        return grouped.Select(g => "{ to: " + g.List + (g.Entries.Count > 0 ? ", entries: [" + string.Join(", ", g.Entries) + "]" : string.Empty) + ", rank: " + rank + " }").ToList();
    }

    /// <summary>Reads the six labels of the initial proficiencies. A statement of a shape the reader does not know fails the run.</summary>
    public static InitialProficiencies ParseInitial(List<(string Label, List<Block> Body)> labels, TakeRow row)
    {
        var result = new InitialProficiencies();
        var byRank = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (label, body) in labels)
        {
            var statements = InitialStatements(body);
            if (statements.Count == 0)
            {
                throw new DegreesException("A class's initial proficiencies hold a label with no statement.");
            }

            result.Statements.Add((label, string.Join(' ', statements.Select(s => s.TrimEnd('.') + "."))));
            foreach (var s in statements)
            {
                var m = Regex.Match(s, @"^(?<rank>Untrained|Trained|Expert|Master|Legendary) in (?<what>.+)$", RegexOptions.CultureInvariant);
                if (!m.Success)
                {
                    throw new DegreesException($"A statement of a class's initial proficiencies ({label}) does not open with a rank and 'in'.");
                }

                var rank = m.Groups["rank"].Value.ToLowerInvariant();
                var what = m.Groups["what"].Value;
                if (rank == "untrained" || Regex.IsMatch(what, @"^the favored weapon of your deity\b", RegexOptions.CultureInvariant))
                {
                    continue; // an untrained statement gives nothing; a favored weapon is the faith's (the sheet has no weapon of its own in a ranked list): the text keeps both
                }
                if (label == "SKILLS")
                {
                    var extra = Regex.Match(what, @"\bequal to (?<n>\d+) plus your Intelligence\b", RegexOptions.CultureInvariant);
                    if (extra.Success)
                    {
                        result.ExtraSkills = int.Parse(extra.Groups["n"].Value, CultureInfo.InvariantCulture);
                    }
                    else if (Regex.IsMatch(what, @"^[A-Z][a-z]+$", RegexOptions.CultureInvariant) && rank == "trained")
                    {
                        result.Adds.Add("{ to: skills, entries: [" + Slug(what) + "], rank: trained }");
                    }
                    else if (!Regex.IsMatch(what, @"^(?:your choice of |one (?:or more )?skills? determined by your )", RegexOptions.CultureInvariant))
                    {
                        throw new DegreesException("A statement of a class's trained skills has a shape the reader does not know.");
                    }

                    continue;
                }

                if (label == "SAVING THROWS" && rank == "trained")
                {
                    result.TrainedSaves.AddRange(RankTargets(what, row).SelectMany(t => t.Entries));
                    continue;
                }

                if (!byRank.TryGetValue(rank, out var things))
                {
                    byRank[rank] = things = new List<string>();
                }

                things.Add(what);
            }
        }

        foreach (var rank in RankWords)
        {
            if (byRank.TryGetValue(rank, out var things))
            {
                result.Adds.AddRange(RankGrants(things, rank, row));
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the grants a feature's own sentences make: <c>Your proficiency rank for X increases to R</c>, <c>You gain R proficiency in X</c>, <c>Your proficiency ranks increase to R for all X</c>, and
    /// (for a subclass) <c>You are trained in X</c>. A sentence that talks of a proficiency in another shape fails the run, unless it speaks of a weapon group (a choice the sheet does not model).
    /// </summary>
    public static List<string> SentenceGrants(string text, TakeRow row, out int notModelled)
    {
        notModelled = 0;
        var adds = new List<string>();
        // A paragraph's bold label (a name and a full stop) is not part of its first sentence.
        foreach (var raw in Regex.Split(Regex.Replace(text.Replace('\n', ' '), @"\*\*[^*]+?\*\*\s*", string.Empty), @"(?<=[.!?])\s+(?=[A-Z])"))
        {
            foreach (var part in Regex.Split(raw.Trim(), @",\s+and (?=your proficiency rank|you have (?:expert|master|legendary) proficiency in)", RegexOptions.IgnoreCase))
            {
                var p = part.Trim().TrimEnd('.', '!', '?', ' ').Trim();
                if (!Regex.IsMatch(p, @"\bproficiency\b|\byou(?:'re| are) trained in\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    continue;
                }

                var rank = string.Empty;
                string? things = null;
                if (Regex.Match(p, @"^(?:[Aa]nd )?[Yy]our proficiency ranks? (?:for|in) (?<what>.+?) increases? to (?<rank>expert|master|legendary)$", RegexOptions.CultureInvariant) is { Success: true } a)
                {
                    (rank, things) = (a.Groups["rank"].Value, a.Groups["what"].Value);
                }
                else if (Regex.Match(p, @"^Your proficiency ranks? increases? to (?<rank>expert|master|legendary) for all (?<what>.+)$", RegexOptions.CultureInvariant) is { Success: true } b)
                {
                    (rank, things) = (b.Groups["rank"].Value, b.Groups["what"].Value);
                }
                else if (Regex.Match(p, @"^You gain (?<rank>expert|master|legendary) proficiency (?:in|with) (?<what>.+)$", RegexOptions.CultureInvariant) is { Success: true } c)
                {
                    (rank, things) = (c.Groups["rank"].Value, c.Groups["what"].Value);
                }
                else if (Regex.Match(p, @"^You(?:'re| are) trained in (?<what>.+?)(?: and one of the following.*)?$", RegexOptions.CultureInvariant) is { Success: true } d)
                {
                    (rank, things) = ("trained", d.Groups["what"].Value);
                }
                else if (Regex.Match(p, @"^[Yy]ou have (?<rank>expert|master|legendary) proficiency in (?<what>.+)$", RegexOptions.CultureInvariant) is { Success: true } e)
                {
                    (rank, things) = (e.Groups["rank"].Value, e.Groups["what"].Value);
                }
                else if (Regex.IsMatch(p, @"\b(?:when|if) you gain\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    continue; // a later grant of a subclass: read by LaterGrants
                }
                else if (p.Contains("group", StringComparison.Ordinal) || p.Contains("favored weapon", StringComparison.Ordinal) || p.Contains("increase your proficiency rank", StringComparison.Ordinal) || Regex.IsMatch(p, @"\bhave (?:trained|expert|master|legendary) proficiency\b", RegexOptions.CultureInvariant))
                {
                    // A weapon group the player picks, or a condition on a rank held (the sheet models neither).
                    notModelled++;
                    continue;
                }
                else if (Regex.IsMatch(p, @"^If you have [a-z' ]+, your proficiency ranks? (?:for|in) .+ increases? to (?:expert|master|legendary)$", RegexOptions.CultureInvariant))
                {
                    // A rank that depends on something the character may or may not have (spells a feat gives): the sheet holds no such condition, so the sentence stays text.
                    notModelled++;
                    continue;
                }
                else
                {
                    throw new DegreesException("A feature's sentence about a proficiency has a shape the reader does not know, and it is not about a weapon group.");
                }

                // The faith's favored weapon is the faith's (the sheet has no weapon of its own in a ranked list): a sentence that names it among other things grants the others.
                var named = RankedThings(things!);
                notModelled += named.Count(t => t.Contains("favored weapon", StringComparison.Ordinal));
                var parts = named.Where(t => !t.Contains("favored weapon", StringComparison.Ordinal))
                    .Select(t => rank == "trained" && Regex.IsMatch(t, @"^[A-Z][a-z]+$", RegexOptions.CultureInvariant) ? "skill:" + Slug(t) : t).ToList();
                var skills = parts.Where(t => t.StartsWith("skill:", StringComparison.Ordinal)).Select(t => t[6..]).ToList();
                if (skills.Count > 0)
                {
                    adds.Add("{ to: skills, entries: [" + string.Join(", ", skills) + "], rank: " + rank + " }");
                }

                adds.AddRange(RankGrants(parts.Where(t => !t.StartsWith("skill:", StringComparison.Ordinal)), rank, row));
            }
        }

        return adds;
    }

    /// <summary>The grants a subclass makes at a later level (<c>When you gain F, you also gain R proficiency in X</c>): the class feature's name, the rank and what.</summary>
    public static List<(string Feature, string Rank, string What)> LaterGrants(string text) =>
        Regex.Matches(text.Replace('\n', ' '), @"(?:[Ww]hen|[Ii]f) you gain (?<f>[^,]+), you also gain (?<r>expert|master|legendary) proficiency in (?<w>[^,.]+?(?:,? and (?!when |if )[^,.]+?)?)(?=,|\.|$)", RegexOptions.CultureInvariant)
            .Select(m => (Regex.Replace(Regex.Replace(m.Groups["f"].Value.Trim(), @"^the\s+", string.Empty), @"\s+class feature$", string.Empty), m.Groups["r"].Value, m.Groups["w"].Value.Trim())).ToList();

    /// <summary>
    /// What a row maps a name to: the map's key is the name (compared without case, and for the advancement table as the table's names are normalized) or, for a name that a row must not spell
    /// (a word the setting coined), the lowercase hex SHA-256 of the lowercase name, so the take-list never holds it.
    /// </summary>
    private static string? MappedName(IReadOnlyDictionary<string, string> map, string name, bool normalized = false)
    {
        var lower = name.ToLowerInvariant();
        var hash = Denylist.Hash(lower);
        foreach (var (key, value) in map)
        {
            if (normalized ? NormalizeAdvancementName(key) == lower : key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (key.Length == 64 && key.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static string NormalizeAdvancementName(string name)
    {
        var n = Regex.Replace(SpanText.Normalize(name).Trim().ToLowerInvariant(), @"\s+\d+d\d+$", string.Empty);
        n = Regex.Replace(n, @"\s+", " ");
        return n.EndsWith('s') ? n[..^1] : n;
    }

    /// <summary>Reads the advancement table into (level, name) rows, the names normalized the same way for the table and the feature rows.</summary>
    public static List<(int Level, string Name)> ParseAdvancement(Block table, TakeRow row)
    {
        if (table.Info == null || table.Info.Count != table.Lines.Count)
        {
            throw new DegreesException($"A class's advancement table (page {table.Page}) has lines and cells that do not match.");
        }

        var rows = new List<(int Level, string Text)>();
        for (var i = 0; i < table.Lines.Count; i++)
        {
            var line = SpanText.Normalize(table.Lines[i]).Trim();
            if (table.Info[i].Cells is { Count: 2 } cells && int.TryParse(cells[0].Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var level))
            {
                rows.Add((level, SpanText.Normalize(cells[1].Text).Trim()));
            }
            else if (rows.Count > 0)
            {
                rows[^1] = (rows[^1].Level, rows[^1].Text + " " + line);
            }
        }

        var result = new List<(int, string)>();
        foreach (var (level, text) in rows)
        {
            foreach (var name in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var norm = NormalizeAdvancementName(name);
                result.Add((level, MappedName(row.AdvancementNames, norm, true) is { } alias ? NormalizeAdvancementName(alias) : norm));
            }
        }

        if (result.Count == 0 || rows.Select(r => r.Level).Distinct().Count() != rows.Count || rows.Count != 20)
        {
            throw new DegreesException($"A class's advancement table (page {table.Page}) does not hold one row for each of the 20 levels.");
        }

        return result;
    }

    /// <summary>
    /// Compares the table's (level, name) rows with the feature rows': every feature row must be in the table at its level, and any other table row must be a feature the class gained at an earlier
    /// level again (a feat every second level). Any other difference fails the run.
    /// </summary>
    public static void CheckAdvancement(List<(int Level, string Name)> table, IEnumerable<ClassFeature> features, string className, bool spellRankRows = false, IEnumerable<(int Level, string Name)>? subclassRows = null)
    {
        // The rows the class's subclasses gain at a level (each subclass the same ones) are named once in the table, as the class's own features are.
        var rows = features.Select(f => (f.Level, Name: NormalizeAdvancementName(f.Name))).Concat(subclassRows ?? Enumerable.Empty<(int, string)>()).ToList();
        var inTable = table.ToHashSet();
        var missing = rows.Where(r => !inTable.Contains(r)).ToList();
        // A spellcaster's table also says at which level a new spell rank comes; those rows are no features and are checked against the spells-per-day table.
        var extra = table.Where(t => !rows.Contains(t) && !(spellRankRows && IsSpellRankRow(t.Name)) && !rows.Any(r => r.Name == t.Name && r.Level < t.Level)).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            throw new DegreesException($"The advancement table of the {className} and its feature rows differ: {missing.Count} feature rows are not in the table at their level, and {extra.Count} table rows are no feature of an earlier level again.");
        }
    }

    private static string FeatureLabel(Block header)
    {
        var text = Clean(string.Join(' ', header.Lines));
        var icon = Regex.Match(text, @"\[(?<i>[^\]]+)\]");
        var name = TitleCase(HeaderWithoutIcons(text));
        return icon.Success ? name + " (" + icon.Groups["i"].Value + ")" : name;
    }

    private static ClassFeature ReadFeature(string name, int level, IEnumerable<Block> blocks, HashSet<string> compounds, string chapter, bool bodyStartsParagraph = false)
    {
        var scratch = new ParsedEntry { Chapter = chapter };
        BodyReader NewReader() => new(scratch, compounds, false, false, null, true);
        var reader = NewReader();
        foreach (var block in blocks)
        {
            // A subclass's labelled lines (a body block of bold labels) come after its flavor paragraph, which may run to the edge of the column: the block starts a paragraph of its own.
            if (bodyStartsParagraph && block.Role == "body")
            {
                reader.Finish();
                reader = NewReader();
            }

            if (block.Role == "block-header")
            {
                reader.Finish();
                scratch.Parts.Add(new LabelledText(FeatureLabel(block), string.Empty));
                reader = NewReader();
            }
            else if (block.Role == "tag")
            {
                reader.Finish();
                scratch.Parts.Add(new LabelledText("Traits", string.Join(' ', Clean(string.Join(' ', block.Lines)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()))));
                reader = NewReader();
            }
            else
            {
                reader.Read(block);
            }
        }

        reader.Finish();
        if (scratch.Parts.Count == 0)
        {
            throw new DegreesException($"A class feature (level {level}) has no text.");
        }

        // A paragraph the extraction cut at the foot of a column goes on at the top of the next one, in a block of its own: it starts in lowercase, and the paragraph before it has no full stop.
        var parts = new List<LabelledText>();
        foreach (var p in scratch.Parts)
        {
            if (parts.Count > 0 && p.Label.Length == 0 && parts[^1].Label.Length == 0 && p.Text.Length > 0 && char.IsLower(p.Text[0]) && !Regex.IsMatch(parts[^1].Text, @"[.!?:;]$"))
            {
                parts[^1] = parts[^1] with { Text = parts[^1].Text + " " + p.Text };
            }
            else
            {
                parts.Add(p);
            }
        }

        return new ClassFeature { Name = name, Level = level, Parts = parts };
    }

    private static string FlowStrings(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Quoted)) + "]";

    /// <summary>The paragraphs of a subclass's text: the first is its flavor, and the rest are its rules.</summary>
    private static (string Flavor, ClassFeature Rules) ReadSubclass(SubclassScan s, HashSet<string> compounds, string chapter)
    {
        var read = ReadFeature(TitleCase(Clean(string.Join(' ', s.Header.Lines))), 1, s.Body, compounds, chapter, true);
        if (read.Parts.Count < 2 || read.Parts[0].Label.Length != 0)
        {
            throw new DegreesException($"A subclass (page {s.Header.Page}) does not hold a flavor paragraph and its rules.");
        }

        var flavor = read.Parts[0].Text;
        read.Parts = read.Parts.Skip(1).ToList();
        return (flavor, read);
    }

    /// <summary>The blocks a class's range reads past (flavor, explanations and introductions, the proficiencies feature's own text, and the flavor paragraph of each subclass).</summary>
    public static List<Block> ClassFlavor(List<Block> slice, TakeRow row)
    {
        var scan = ScanClass(slice, row);
        var result = scan.Flavor.ToList();
        result.AddRange(scan.Features.Where(f => IsInitialFeature(f.Name)).SelectMany(f => f.Body));
        var compounds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in scan.Subclasses)
        {
            var (flavor, _) = ReadSubclass(s, compounds, "classes");
            result.Add(s.Header with { Role = "rules", Lines = new List<string> { flavor }, Info = null, Rows = null });
        }

        return result;
    }

    /// <summary>A heading of the boxes the class sets before its features: the proficiencies (and the label of each) and the advancement table.</summary>
    private static bool IsClassBoxHeader(string text) =>
        text == "INITIAL PROFICIENCIES" || text == SpellsLabel || InitialLabels.Contains(text, StringComparer.Ordinal) || text.EndsWith(" ADVANCEMENT", StringComparison.Ordinal);

    private static bool IsInitialFeature(string name) => name.Equals("Initial Proficiencies", StringComparison.OrdinalIgnoreCase);

    private static string LowerWords(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()));

    /// <summary>
    /// The <c>classes</c> parser: one class's whole range into a <c>class</c> entry (its statistics, its initial proficiencies and one feature row for each class feature), the <c>subclass</c> entries of
    /// the feature the row names, and the class's feats under their level bands as <c>feat</c> entries. The flavor is read past. The advancement table must agree with the feature rows.
    /// </summary>
    private static List<ParsedEntry> ParseClasses(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var row = range.Row;
        var scan = ScanClass(slice, row);
        var box = ParseClassBox(scan.StatsBox!);
        var initial = ParseInitial(scan.Initial, row);
        var cls = NewEntry(range, book, Clean(row.PathOf(range)[^1]), compounds);
        cls.Raw.Add(new LabelledText("hp", box.Hp.ToString(CultureInfo.InvariantCulture)));
        cls.Raw.Add(new LabelledText("keyAttribute", Quoted(box.KeyAttribute)));
        cls.Raw.Add(new LabelledText("boosts", FlowStrings(new[] { box.KeyAttribute })));
        if (initial.TrainedSaves.Count > 0)
        {
            cls.Raw.Add(new LabelledText("savingThrows", FlowStrings(initial.TrainedSaves)));
        }

        if (initial.ExtraSkills > 0)
        {
            cls.Raw.Add(new LabelledText("extraSkills", initial.ExtraSkills.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var f in scan.Features)
        {
            ClassFeature feature;
            if (IsInitialFeature(f.Name))
            {
                feature = new ClassFeature { Name = f.Name, Level = f.Level, Adds = initial.Adds.ToList() };
                feature.Parts.AddRange(initial.Statements.Select(s => new LabelledText(TitleLabel(s.Label), s.Text)));
            }
            else
            {
                var plain = MappedName(row.FeatureNames, f.Name);
                // The introduction of the subclass section (the patrons', the schools') is part of the feature that makes the choice.
                var body = row.SubclassSection != null && row.SubclassFeature != null && Slug(f.Name) == Slug(row.SubclassFeature) ? f.Body.Concat(scan.SubclassIntro).ToList() : f.Body;
                feature = ReadFeature(plain ?? f.Name, f.Level, body, compounds, cls.Chapter);
                if (!ReferenceEquals(body, f.Body))
                {
                    feature.Parts = TidySubclassParts(feature.Parts);
                }

                feature.BookName = plain == null ? null : f.Name;
                feature.Uses = DailyCounter(feature.Name, feature.Render());
                try
                {
                    feature.Adds = SentenceGrants(feature.Render(), row, out _);
                    if (row.GrantsByName && !row.ChoiceFeatures.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        feature.Adds.AddRange(NamedGrants(feature.Render()));
                    }
                }
                catch (DegreesException ex)
                {
                    throw new DegreesException($"{ex.Message} (class feature at page {f.Header.Page}, column {f.Header.Column})");
                }
            }

            cls.Features.Add(feature);
        }

        var advancement = ParseAdvancement(scan.AdvancementTable!, row);
        var spellcaster = scan.Initial.Any(i => i.Label == SpellsLabel);
        if (spellcaster != scan.SpellTableHeaders > 0 || scan.SpellTableHeaders > 1)
        {
            throw new DegreesException($"The {cls.Name} has {scan.SpellTableHeaders} spells-per-day tables and {(spellcaster ? "a" : "no")} spell proficiency line: a spellcaster has one of each.");
        }

        if (spellcaster)
        {
            var spellRows = ParseSpellTable(scan.SpellTable, cls.Name);
            CheckSpellRankRows(advancement, spellRows, cls.Features, cls.Name);
            AddSpellcastingFields(cls, ReadSpellcasting(cls.Features, spellRows, box.KeyAttribute, cls.Name, row.TraditionFromSubclass), spellRows);
        }

        var result = new List<ParsedEntry> { cls };
        var subclassRows = new List<(int Level, string Name)>();
        var subclassRowSets = new List<HashSet<(int Level, string Name)>>();

        foreach (var s in scan.Subclasses)
        {
            var title = TitleCase(Clean(string.Join(' ', s.Header.Lines)));
            var entry = NewEntry(range, book, title, compounds, "subclass", "subclasses", null, scan.Subclasses.Count > 0 ? row.SubclassFeature ?? row.SubclassSection : null);
            entry.Owner = cls;
            var (_, rules) = ReadSubclass(s, compounds, cls.Chapter);
            // A paragraph that opens with a name and a level in parentheses (a doctrine gained at the 3rd level) is a feature row of its own, at that level; the one at the 1st level stays in the row.
            var (keptParts, levelled) = SplitLevelledParagraphs(rules.Parts);
            rules.Parts = row.SubclassSection != null ? TidySubclassParts(keptParts) : keptParts;
            var tradition = TraditionOf(rules.Parts);
            if (row.TraditionFromSubclass != (tradition != null))
            {
                throw new DegreesException($"The {cls.Name}'s subclass on page {s.Header.Page} {(tradition == null ? "names no tradition" : "names a tradition")}, and the row says its subclasses {(row.TraditionFromSubclass ? "decide" : "do not decide")} it.");
            }

            if (tradition != null)
            {
                entry.Raw.Add(new LabelledText("tradition", Quoted(tradition)));
            }

            var text = rules.Render();
            rules.Adds = SentenceGrants(text, row, out _);
            if (row.GrantsByName)
            {
                rules.Adds.AddRange(NamedGrants(text));
                rules.Adds.AddRange(LabelledGrants(rules.Parts));
            }

            entry.Features.Add(rules);
            var own = new HashSet<(int, string)>();
            foreach (var (name, level, part) in levelled)
            {
                var rowFeature = new ClassFeature { Name = name, Level = level, Parts = new List<LabelledText> { new(string.Empty, part) } };
                rowFeature.Adds = SentenceGrants(rowFeature.Render(), row, out _);
                if (row.GrantsByName)
                {
                    rowFeature.Adds.AddRange(NamedGrants(rowFeature.Render()));
                }

                entry.Features.Add(rowFeature);
                own.Add((rowFeature.Level, NormalizeAdvancementName(rowFeature.Name)));
                text += "\n" + part;
            }

            subclassRowSets.Add(own);
            if (Regex.Match(text, @"You can choose (?<a>[A-Z][a-z]+) as your key attribute", RegexOptions.CultureInvariant) is { Success: true } key)
            {
                entry.Raw.Add(new LabelledText("keyAttribute", Quoted(AttributeKeyOf(key.Groups["a"].Value))));
            }

            foreach (var (featureName, rank, what) in LaterGrants(text))
            {
                var at = cls.Features.FirstOrDefault(f => Slug(f.Name) == Slug(featureName)) ?? throw new DegreesException($"A subclass (page {s.Header.Page}) gains a proficiency when the class gains a feature that has no row.");
                entry.Features.Add(new ClassFeature
                {
                    Name = title + " " + at.Name,
                    Level = at.Level,
                    Parts = new List<LabelledText> { new(string.Empty, $"Your proficiency rank for {what} increases to {rank}.") },
                    Adds = RankGrants(RankedThings(what), rank, row),
                });
            }

            result.Add(entry);
        }

        if (subclassRowSets.Any(set => !set.SetEquals(subclassRowSets[0])))
        {
            throw new DegreesException($"The subclasses of the {cls.Name} do not gain the same levelled rows, so the advancement table could not name them once.");
        }

        CheckAdvancement(advancement, cls.Features, cls.Name, spellcaster, subclassRowSets.FirstOrDefault());

        // A section among the features that is a rule of its own (a witch's lessons): one rule entry, the children's titles as paragraphs.
        foreach (var section in scan.RuleSections)
        {
            var rule = NewEntry(range, book, section.Name, compounds, "rule", "rules", row.RuleSectionCategory, "Class Features");
            var ruleReader = new BodyReader(rule, compounds, false, false, null, true);
            foreach (var block in section.Intro)
            {
                ruleReader.Read(block);
            }

            foreach (var tier in section.Tiers)
            {
                ruleReader.EndParagraph();
                ruleReader.Read(tier.Header with { Role = "body", Lines = new List<string> { TitleCase(Clean(string.Join(' ', tier.Header.Lines))) }, Info = null });
                ruleReader.EndParagraph();
                foreach (var block in tier.Body)
                {
                    ruleReader.Read(block);
                }
            }

            ruleReader.Finish();
            result.Add(rule);
        }

        foreach (var f in scan.Feats)
        {
            var entry = BuildFeat(range, book, f, compounds, scan.FeatsTitle, row.GrantsByName, "a class");
            entry.ClassKey = Slug(cls.Name);
            result.Add(entry);
        }

        return result;
    }

    private static readonly Regex InlineAction = new(@"^\[(?<icon>[^\]]+)\] (?:\((?<traits>[^)]+)\) )?(?<rest>(?:Frequency|Trigger|Requirements|Effect) .+)$", RegexOptions.CultureInvariant);

    private static readonly Regex InlineActionField = new(@";\s+(?=(?:Frequency|Trigger|Requirements|Effect) )", RegexOptions.CultureInvariant);

    /// <summary>
    /// A feat that gives an action of its own prints it in running text: a bold name, the icon, the traits in parentheses, then its labelled lines (Frequency, Trigger, Requirements, Effect) after semicolons.
    /// It is set out as the class features' actions are: the name with its action word, a traits line, and a labelled line each. An action's Effect is required. A list item that is a Trigger and its Effect
    /// (a bullet) is two labelled lines.
    /// </summary>
    public static void SplitInlineAction(ParsedEntry entry)
    {
        // The layout may cut an action's line after the semicolon that ends its frequency: the Effect label then opens the next part, and it is the same action.
        for (var i = 0; i + 1 < entry.Parts.Count; i++)
        {
            if (entry.Parts[i].Label.Length > 0 && entry.Parts[i + 1].Label.TrimEnd('.', ':', ' ') == "Effect" && entry.Parts[i].Text.EndsWith(';')
                && InlineAction.IsMatch(entry.Parts[i].Text + " Effect x"))
            {
                entry.Parts[i] = entry.Parts[i] with { Text = entry.Parts[i].Text + " Effect " + entry.Parts[i + 1].Text };
                entry.Parts.RemoveAt(i + 1);
            }
        }

        for (var i = 0; i < entry.Parts.Count; i++)
        {
            var part = entry.Parts[i];
            if (part.Label.StartsWith("• ", StringComparison.Ordinal) && part.Label[2..].TrimEnd('.', ':', ' ') == "Trigger" && InlineActionField.Split(part.Text) is { Length: 2 } pair && pair[1].StartsWith("Effect ", StringComparison.Ordinal))
            {
                entry.Parts[i] = part with { Text = pair[0] };
                entry.Parts.Insert(i + 1, new LabelledText("Effect", pair[1]["Effect ".Length..]));
                i++;
                continue;
            }

            if (part.Label.Length == 0 || InlineAction.Match(part.Text) is not { Success: true } m)
            {
                continue;
            }

            var fields = InlineActionField.Split(m.Groups["rest"].Value).Select(f => (Label: f[..f.IndexOf(' ', StringComparison.Ordinal)], Text: f[(f.IndexOf(' ', StringComparison.Ordinal) + 1)..])).ToList();
            if (fields.All(f => f.Label != "Effect"))
            {
                continue;
            }

            entry.Parts.RemoveAt(i);
            var set = new List<LabelledText> { new(part.Label.TrimEnd('.', ':', ' ') + " (" + m.Groups["icon"].Value + ")", string.Empty) };
            if (m.Groups["traits"].Success)
            {
                set.Add(new("Traits", string.Join(' ', m.Groups["traits"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()))));
            }

            set.AddRange(fields.Select(f => new LabelledText(f.Label, f.Text)));
            entry.Parts.InsertRange(i, set);
            i += set.Count - 1;
        }
    }

    private static string TitleLabel(string upper) => upper == "CLASS DC" ? "Class DC" : char.ToUpperInvariant(upper[0]) + upper[1..].ToLowerInvariant();
}
