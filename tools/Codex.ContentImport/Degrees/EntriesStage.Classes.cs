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
    public List<FeatScan> Feats { get; } = new();
    public List<(Block Block, int Level)> Bands { get; } = new();
    public string? FeatsTitle { get; set; }

    /// <summary>The blocks of the printed list of the class's feats (each line a name and a level), and how many headings of it the range holds.</summary>
    public List<Block> ByName { get; } = new();

    public int ByNameHeaders { get; set; }
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

    private static readonly string[] InitialLabels = { "PERCEPTION", "SAVING THROWS", "SKILLS", "ATTACKS", "DEFENSES", "CLASS DC" };

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
        var band = 0;
        foreach (var b in slice)
        {
            var text = BlockText(b);
            switch (b.Role)
            {
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
                case "block-header" when mode == 'B':
                    if (text == "INITIAL PROFICIENCIES")
                    {
                        pending = 'I';
                    }
                    else if (text.EndsWith(" ADVANCEMENT", StringComparison.Ordinal))
                    {
                        pending = 'A';
                    }
                    else if (InitialLabels.Contains(text, StringComparer.Ordinal) && scan.Initial.All(i => i.Label != text))
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
                    else if (text.EndsWith("Feats", StringComparison.Ordinal) && mode == 'C')
                    {
                        mode = 'F';
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
                case "body" when mode == 'F' && scan.ByNameHeaders > 0 && IsByNameList(b):
                    scan.ByName.Add(b);
                    break;
                case "rules" or "body" or "icon":
                    if (mode == 'B')
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
                        }
                        else if (pending == 'L')
                        {
                            scan.Initial[^1].Body.Add(b);
                        }
                        else
                        {
                            scan.Flavor.Add(b); // flavor text, the box's explanation, the introduction of the proficiencies
                        }
                    }
                    else if (mode == 'C')
                    {
                        if (feature == null)
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

        if (scan.StatsBox == null || scan.AdvancementTable == null || scan.Initial.Count != InitialLabels.Length || scan.FeatsTitle == null || scan.Features.Count == 0)
        {
            throw new DegreesException("A class's range lacks its statistics box, its proficiencies, its advancement table, its features or its feats.");
        }

        if (row.SubclassFeature != null && scan.Subclasses.Count == 0)
        {
            throw new DegreesException("The row names a feature that holds subclasses, and the class has none.");
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
            if (Regex.IsMatch(line, @"^(?:Trained|Expert|Master|Legendary) in\b", RegexOptions.CultureInvariant) || statements.Count == 0)
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
        var w = Regex.Replace(what.Trim().ToLowerInvariant(), @"^your\s+", string.Empty);
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
                var m = Regex.Match(s, @"^(?<rank>Trained|Expert|Master|Legendary) in (?<what>.+)$", RegexOptions.CultureInvariant);
                if (!m.Success)
                {
                    throw new DegreesException($"A statement of a class's initial proficiencies ({label}) does not open with a rank and 'in'.");
                }

                var rank = m.Groups["rank"].Value.ToLowerInvariant();
                var what = m.Groups["what"].Value;
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
                    else if (!Regex.IsMatch(what, @"^(?:your choice of |one or more skills determined by your )", RegexOptions.CultureInvariant))
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
    internal static List<string> SentenceGrants(string text, TakeRow row, out int notModelled)
    {
        notModelled = 0;
        var adds = new List<string>();
        foreach (var raw in Regex.Split(text.Replace('\n', ' '), @"(?<=[.!?])\s+(?=[A-Z])"))
        {
            foreach (var part in Regex.Split(raw.Trim(), @",\s+and (?=your proficiency rank)", RegexOptions.IgnoreCase))
            {
                var p = part.Trim().TrimEnd('.', '!', '?', ' ').Trim();
                if (!Regex.IsMatch(p, @"\bproficiency\b|\byou(?:'re| are) trained in\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    continue;
                }

                var rank = string.Empty;
                string? things = null;
                if (Regex.Match(p, @"^(?:[Aa]nd )?[Yy]our proficiency ranks? for (?<what>.+?) increases? to (?<rank>expert|master|legendary)$", RegexOptions.CultureInvariant) is { Success: true } a)
                {
                    (rank, things) = (a.Groups["rank"].Value, a.Groups["what"].Value);
                }
                else if (Regex.Match(p, @"^Your proficiency ranks? increases? to (?<rank>expert|master|legendary) for all (?<what>.+)$", RegexOptions.CultureInvariant) is { Success: true } b)
                {
                    (rank, things) = (b.Groups["rank"].Value, b.Groups["what"].Value);
                }
                else if (Regex.Match(p, @"^You gain (?<rank>expert|master|legendary) proficiency in (?<what>.+)$", RegexOptions.CultureInvariant) is { Success: true } c)
                {
                    (rank, things) = (c.Groups["rank"].Value, c.Groups["what"].Value);
                }
                else if (Regex.Match(p, @"^You(?:'re| are) trained in (?<what>.+?)(?: and one of the following.*)?$", RegexOptions.CultureInvariant) is { Success: true } d)
                {
                    (rank, things) = ("trained", d.Groups["what"].Value);
                }
                else if (Regex.IsMatch(p, @"\bwhen you gain\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    continue; // a later grant of a subclass: read by LaterGrants
                }
                else if (p.Contains("group", StringComparison.Ordinal) || Regex.IsMatch(p, @"\bhave (?:trained|expert|master|legendary) proficiency\b", RegexOptions.CultureInvariant))
                {
                    // A weapon group the player picks, or a condition on a rank held (the sheet models neither).
                    notModelled++;
                    continue;
                }
                else
                {
                    throw new DegreesException("A feature's sentence about a proficiency has a shape the reader does not know, and it is not about a weapon group.");
                }

                var parts = RankedThings(things!).Select(t => rank == "trained" && Regex.IsMatch(t, @"^[A-Z][a-z]+$", RegexOptions.CultureInvariant) ? "skill:" + Slug(t) : t).ToList();
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
    internal static List<(string Feature, string Rank, string What)> LaterGrants(string text) =>
        Regex.Matches(text.Replace('\n', ' '), @"[Ww]hen you gain (?<f>[^,]+), you also gain (?<r>expert|master|legendary) proficiency in (?<w>[^,.]+?)(?=,|\.|$)", RegexOptions.CultureInvariant)
            .Select(m => (m.Groups["f"].Value.Trim(), m.Groups["r"].Value, m.Groups["w"].Value.Trim())).ToList();

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
                result.Add((level, row.AdvancementNames.FirstOrDefault(kv => NormalizeAdvancementName(kv.Key) == norm) is { Value: { } alias } ? NormalizeAdvancementName(alias) : norm));
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
    public static void CheckAdvancement(List<(int Level, string Name)> table, IEnumerable<ClassFeature> features, string className)
    {
        var rows = features.Select(f => (f.Level, Name: NormalizeAdvancementName(f.Name))).ToList();
        var inTable = table.ToHashSet();
        var missing = rows.Where(r => !inTable.Contains(r)).ToList();
        var extra = table.Where(t => !rows.Contains(t) && !rows.Any(r => r.Name == t.Name && r.Level < t.Level)).ToList();
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

    private static ClassFeature ReadFeature(string name, int level, IEnumerable<Block> blocks, HashSet<string> compounds, string chapter)
    {
        var scratch = new ParsedEntry { Chapter = chapter };
        BodyReader NewReader() => new(scratch, compounds, false, false, null, true);
        var reader = NewReader();
        foreach (var block in blocks)
        {
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
        var read = ReadFeature(TitleCase(Clean(string.Join(' ', s.Header.Lines))), 1, s.Body, compounds, chapter);
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
                feature = ReadFeature(f.Name, f.Level, f.Body, compounds, cls.Chapter);
                feature.Adds = SentenceGrants(feature.Render(), row, out _);
            }

            cls.Features.Add(feature);
        }

        CheckAdvancement(ParseAdvancement(scan.AdvancementTable!, row), cls.Features, cls.Name);
        var result = new List<ParsedEntry> { cls };

        foreach (var s in scan.Subclasses)
        {
            var title = TitleCase(Clean(string.Join(' ', s.Header.Lines)));
            var entry = NewEntry(range, book, title, compounds, "subclass", "subclasses", null, scan.Subclasses.Count > 0 ? row.SubclassFeature : null);
            entry.Owner = cls;
            var (_, rules) = ReadSubclass(s, compounds, cls.Chapter);
            var text = rules.Render();
            rules.Adds = SentenceGrants(text, row, out _);
            entry.Features.Add(rules);
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

        foreach (var f in scan.Feats)
        {
            var header = Clean(string.Join(' ', f.Header.Lines));
            string? actions = null;
            foreach (var (_, icon) in Icons)
            {
                if (header.Contains(icon.Text, StringComparison.Ordinal))
                {
                    actions = icon.Actions;
                    header = header.Replace(icon.Text, string.Empty, StringComparison.Ordinal);
                }
            }

            var m = AncestryFeatHeader.Match(Regex.Replace(header, " {2,}", " ").Trim());
            var level = int.Parse(m.Groups["level"].Value, CultureInfo.InvariantCulture);
            if (level != f.Band)
            {
                throw new DegreesException($"A feat of a class (page {f.Header.Page}) has level {level} under the band for level {f.Band}.");
            }

            var entry = NewEntry(range, book, TitleCase(m.Groups["name"].Value), compounds, "feat", "feats", null, scan.FeatsTitle);
            entry.Level = level;
            entry.Actions = actions;
            entry.ClassKey = Slug(cls.Name);
            foreach (var tag in f.Tags)
            {
                entry.Traits.AddRange(Clean(string.Join(' ', tag.Lines)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()));
            }

            var reader = new BodyReader(entry, compounds, true);
            foreach (var block in f.Body)
            {
                reader.Read(block);
            }

            reader.Finish();
            ExtractFields(entry);
            SplitInlineAction(entry);
            result.Add(entry);
        }

        return result;
    }

    private static readonly Regex InlineAction = new(@"^\[(?<icon>[^\]]+)\] \((?<traits>[^)]+)\) Trigger (?<trigger>.+?); Effect (?<effect>.+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A feat that gives a reaction of its own prints it in running text: a bold name, the icon, the traits in parentheses, then Trigger and Effect. It is set out as the class features' actions are: the name with its action word,
    /// a traits line, and a labelled line each.
    /// </summary>
    private static void SplitInlineAction(ParsedEntry entry)
    {
        for (var i = 0; i < entry.Parts.Count; i++)
        {
            var part = entry.Parts[i];
            if (part.Label.Length == 0 || InlineAction.Match(part.Text) is not { Success: true } m)
            {
                continue;
            }

            entry.Parts.RemoveAt(i);
            entry.Parts.InsertRange(i, new[]
            {
                new LabelledText(part.Label.TrimEnd('.', ':', ' ') + " (" + m.Groups["icon"].Value + ")", string.Empty),
                new LabelledText("Traits", string.Join(' ', m.Groups["traits"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()))),
                new LabelledText("Trigger", m.Groups["trigger"].Value),
                new LabelledText("Effect", m.Groups["effect"].Value),
            });
            return;
        }
    }

    private static string TitleLabel(string upper) => upper == "CLASS DC" ? "Class DC" : char.ToUpperInvariant(upper[0]) + upper[1..].ToLowerInvariant();
}
