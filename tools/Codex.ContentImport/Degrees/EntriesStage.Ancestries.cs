using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>What an ancestry's statistics box prints: its numbers, its boosts and flaw, the fixed languages, its traits and its special abilities.</summary>
public sealed class AncestryBox
{
    public int Hp { get; set; }
    public string Size { get; set; } = string.Empty;
    public int Speed { get; set; }

    /// <summary>Boost words in the engine's shape: an attribute key, <c>free</c>, or a leading minus for a flaw.</summary>
    public List<string> Boosts { get; } = new();

    /// <summary>The languages every member speaks, in lowercase.</summary>
    public List<string> Languages { get; } = new();

    /// <summary>How many more languages the member chooses, besides one for each point of a positive Intelligence modifier.</summary>
    public int ExtraLanguages { get; set; }

    /// <summary>The words the box lists the languages to choose from (read past: the language names are the languages chapter's).</summary>
    public string ChooseFrom { get; set; } = string.Empty;

    public List<string> Traits { get; } = new();

    /// <summary>The senses and abilities the box names, with the blocks of their text.</summary>
    public List<(string Name, List<Block> Body)> Specials { get; } = new();
}

/// <summary>A heritage as the blocks hold it: the heading and the blocks of its text.</summary>
public sealed class HeritageScan
{
    public Block Header { get; set; } = null!;
    public List<Block> Body { get; } = new();
}

/// <summary>An ancestry feat as the blocks hold it: the heading, the level band above it, its trait line blocks and the blocks of its text.</summary>
public sealed class FeatScan
{
    public Block Header { get; set; } = null!;
    public int Band { get; set; }
    public List<Block> Tags { get; } = new();
    public List<Block> Body { get; } = new();
}

/// <summary>
/// An ancestry's range sorted into its parts: the lore (read past), the statistics box, the heritages and the feats under their level bands. The lore holds the page's
/// opening text and every flavor child, and the sentence under the title of each of the two sections.
/// </summary>
public sealed class AncestryScan
{
    public List<Block> Flavor { get; } = new();
    public int LoreHeaders { get; set; }
    public List<Block> Box { get; } = new();
    public List<HeritageScan> Heritages { get; } = new();
    public List<FeatScan> Feats { get; } = new();
    public List<(Block Block, int Level)> Bands { get; } = new();
    public string? HeritagesTitle { get; set; }
    public string? FeatsTitle { get; set; }
}

public static partial class EntriesStage
{
    private static readonly Regex AncestryBand = new(@"^(?<n>\d+)(?:ST|ND|RD|TH) LEVEL$", RegexOptions.CultureInvariant);

    private static readonly Regex AncestryFeatHeader = new(@"^(?<name>.+?)\s+FEAT\s+(?<level>\d+)$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> AttributeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["strength"] = "str",
        ["dexterity"] = "dex",
        ["constitution"] = "con",
        ["intelligence"] = "int",
        ["wisdom"] = "wis",
        ["charisma"] = "cha",
    };

    private static string BlockText(Block block) => SpanText.Normalize(string.Join(' ', block.Lines)).Trim();

    /// <summary>
    /// Sorts the blocks of one ancestry's range. The statistics box is the blocks from its first label (<c>HIT POINTS</c>) to the end of that page's column: the book floats it at the top of the second column
    /// of one page, so what the first column began there goes on at the top of the next page, and the box sits between the two parts in reading order. Any block that fits none of the parts fails the run.
    /// </summary>
    public static AncestryScan ScanAncestry(List<Block> slice)
    {
        var scan = new AncestryScan();
        var mode = 'L'; // L lore, H heritages, F feats
        HeritageScan? heritage = null;
        FeatScan? feat = null;
        var band = 0;
        for (var i = 0; i < slice.Count; i++)
        {
            var b = slice[i];
            var text = BlockText(b);
            if (b.Role == "block-header" && text == "HIT POINTS")
            {
                if (scan.Box.Count > 0)
                {
                    throw new DegreesException("An ancestry's range holds two statistics boxes.");
                }

                var j = i;
                while (j < slice.Count && slice[j].Page == b.Page && slice[j].Column == b.Column)
                {
                    scan.Box.Add(slice[j]);
                    j++;
                }

                i = j - 1;
                continue;
            }

            switch (b.Role)
            {
                case "section":
                    if (text.EndsWith(" Heritages", StringComparison.OrdinalIgnoreCase) && mode == 'L')
                    {
                        mode = 'H';
                        scan.HeritagesTitle = Clean(text);
                    }
                    else if (text.EndsWith("Ancestry Feats", StringComparison.OrdinalIgnoreCase) && mode == 'H')
                    {
                        mode = 'F';
                        scan.FeatsTitle = Clean(text);
                    }
                    else
                    {
                        throw new DegreesException($"An ancestry's range holds a section title on page {b.Page} that is not the next of its two sections.");
                    }

                    break;
                case "entry-header" when mode == 'L':
                    scan.Flavor.Add(b);
                    scan.LoreHeaders++;
                    break;
                case "entry-header" when mode == 'H':
                    heritage = new HeritageScan { Header = b };
                    scan.Heritages.Add(heritage);
                    break;
                case "block-header" when mode == 'F':
                    if (AncestryBand.Match(text) is { Success: true } bandMatch)
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
                        throw new DegreesException($"An ancestry's feats hold a heading on page {b.Page}, column {b.Column}, that is neither a level band nor a feat's header.");
                    }

                    break;
                case "tag" when mode == 'F' && feat != null && feat.Body.Count == 0:
                    feat.Tags.Add(b);
                    break;
                case "rules" or "body" or "icon":
                    if (mode == 'F' && feat == null && scan.Bands.Count > 0)
                    {
                        // Text under a level band and before the next feat's header: a feat whose text flows past a band heading would lose it, with no count to see it.
                        throw new DegreesException($"An ancestry's feats hold a {b.Role} block on page {b.Page}, column {b.Column}, after a level band and before a feat's header.");
                    }

                    if (mode == 'L' || (mode == 'H' && heritage == null) || (mode == 'F' && feat == null))
                    {
                        scan.Flavor.Add(b); // the lore, and the sentence under a section's title
                    }
                    else if (mode == 'H')
                    {
                        heritage!.Body.Add(b);
                    }
                    else
                    {
                        feat!.Body.Add(b);
                    }

                    break;
                default:
                    throw new DegreesException($"An ancestry's range holds a {b.Role} block on page {b.Page}, column {b.Column}, where its {(mode == 'L' ? "lore" : mode == 'H' ? "heritages" : "feats")} have none.");
            }
        }

        if (scan.Box.Count == 0 || scan.HeritagesTitle == null || scan.FeatsTitle == null)
        {
            throw new DegreesException("An ancestry's range lacks its statistics box or one of its two sections.");
        }

        return scan;
    }

    internal static string HeaderWithoutIcons(string header)
    {
        var t = Clean(header);
        foreach (var (_, icon) in Icons)
        {
            t = t.Replace(icon.Text, string.Empty, StringComparison.Ordinal);
        }

        return Regex.Replace(t, " {2,}", " ").Trim();
    }

    private static string[] BoxLines(List<Block> body) =>
        body.SelectMany(b => b.Lines).Select(l => SpanText.Normalize(l).Trim()).Where(l => l.Length > 0).ToArray();

    /// <summary>Reads the statistics box: the seven labels in the book's order (the flaw is left out of an ancestry that has none), then a name and a text for each special ability.</summary>
    public static AncestryBox ParseAncestryBox(List<Block> box)
    {
        var pairs = new List<(string Label, List<Block> Body)>();
        foreach (var b in box)
        {
            if (b.Role == "block-header")
            {
                pairs.Add((BlockText(b), new List<Block>()));
            }
            else if (b.Role == "body" && pairs.Count > 0)
            {
                pairs[^1].Body.Add(b);
            }
            else
            {
                throw new DegreesException("An ancestry's statistics box holds a block that is neither a label nor the text under one.");
            }
        }

        var required = new List<string> { "HIT POINTS", "SIZE", "SPEED", "ATTRIBUTE BOOSTS" };
        var hasFlaw = pairs.Count > 4 && pairs[4].Label.StartsWith("ATTRIBUTE FLAW", StringComparison.Ordinal);
        if (hasFlaw)
        {
            required.Add(pairs[4].Label);
        }

        required.Add("LANGUAGES");
        required.Add("TRAITS");
        if (pairs.Count < required.Count || !pairs.Take(required.Count).Select(p => p.Label).SequenceEqual(required) || pairs.Take(required.Count).Any(p => p.Body.Count != 1))
        {
            throw new DegreesException("An ancestry's statistics box does not hold its labels in the book's order, each over one block of text.");
        }

        var result = new AncestryBox();
        string[] Text(int n) => BoxLines(pairs[n].Body);
        var where = $"page {box[0].Page}, column {box[0].Column}";
        var hp = Regex.Match(string.Join(' ', Text(0)), @"^\d+$");
        var speed = Regex.Match(string.Join(' ', Text(2)), @"^(?<n>\d+) feet$");
        if (!hp.Success || !speed.Success)
        {
            throw new DegreesException($"An ancestry's statistics box ({where}) does not give its hit points as a number or its speed in feet.");
        }

        result.Hp = int.Parse(hp.Value, CultureInfo.InvariantCulture);
        result.Size = string.Join(' ', Text(1));
        result.Speed = int.Parse(speed.Groups["n"].Value, CultureInfo.InvariantCulture);

        // "Constitution Wisdom Free", or "Two free attribute boosts".
        var boosts = string.Join(' ', Text(3));
        var words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4 };
        if (Regex.Match(boosts, @"^(?<n>one|two|three|four) free attribute boosts?$", RegexOptions.IgnoreCase) is { Success: true } many)
        {
            result.Boosts.AddRange(Enumerable.Repeat("free", words[many.Groups["n"].Value]));
        }
        else
        {
            foreach (var word in boosts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                result.Boosts.Add(AttributeKeys.TryGetValue(word, out var key) ? key : word.Equals("free", StringComparison.OrdinalIgnoreCase) ? "free" : throw new DegreesException("An ancestry's attribute boosts hold a word that is no attribute."));
            }
        }

        var next = 4;
        if (hasFlaw)
        {
            foreach (var word in string.Join(' ', Text(next)).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                result.Boosts.Add("-" + (AttributeKeys.TryGetValue(word, out var key) ? key : throw new DegreesException("An ancestry's attribute flaw is no attribute.")));
            }

            next++;
        }

        // The languages: the fixed ones, one to a line, then "Additional languages equal to [1 + ] your Intelligence modifier ... Choose from ...".
        var lines = Text(next);
        var additional = Array.FindIndex(lines, l => l.StartsWith("Additional languages", StringComparison.Ordinal));
        if (additional < 1 || lines[..additional].Any(l => l.Contains(' ')))
        {
            throw new DegreesException("An ancestry's languages are not a list of fixed languages and a line of additional ones.");
        }

        result.Languages.AddRange(lines[..additional].Select(l => l.ToLowerInvariant()));
        var rest = string.Join(' ', lines[additional..]);
        var count = Regex.Match(rest, @"^Additional languages equal to (?:(?<n>\d+) \+ )?your Intelligence modifier");
        if (!count.Success)
        {
            throw new DegreesException("An ancestry's additional languages are not 'equal to' the Intelligence modifier, with a number or not.");
        }

        result.ExtraLanguages = count.Groups["n"].Success ? int.Parse(count.Groups["n"].Value, CultureInfo.InvariantCulture) : 0; // the pattern holds digits only
        var choose = rest.IndexOf("Choose from", StringComparison.Ordinal);
        result.ChooseFrom = choose < 0 ? string.Empty : rest[choose..];

        result.Traits.AddRange(string.Join(' ', Text(next + 1)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()));
        foreach (var (label, body) in pairs.Skip(required.Count))
        {
            // An ability is one block of text under a name that is no feat's header or level band: a feat that the box's column goes on with is no ability of the ancestry.
            if (body.Count != 1 || AncestryBand.IsMatch(label) || AncestryFeatHeader.IsMatch(HeaderWithoutIcons(label)))
            {
                throw new DegreesException($"An ancestry's statistics box ({where}) holds an ability that has no text, more than one block of it, or the shape of a feat's header or a level band.");
            }

            result.Specials.Add((TitleCase(label), body));
        }

        return result;
    }

    /// <summary>The blocks an ancestries range reads past: its lore, the sentence under each section's title, and the list of languages the box offers (its words are hashed for the denylist with the other left-out text).</summary>
    public static List<Block> AncestryFlavor(List<Block> slice)
    {
        var scan = ScanAncestry(slice);
        var result = scan.Flavor.ToList();
        var box = ParseAncestryBox(scan.Box);
        if (box.ChooseFrom.Length > 0)
        {
            result.Add(scan.Box[0] with { Lines = new List<string> { box.ChooseFrom }, Info = null, Rows = null });
        }

        return result;
    }

    private static string Quoted(string text) => "\"" + text + "\"";

    private static string FlowList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Quoted)) + "]";

    /// <summary>
    /// The <c>ancestries</c> parser: one ancestry's whole range (its lore, its heritages, its ancestry feats and its statistics box) into an <c>ancestry</c> entry, <c>heritage</c> entries and <c>feat</c>
    /// entries. The lore is read past. An ancestry's description is its special abilities and nothing else. A heritage's name may end in a rarity in brackets, which is a trait. A feat's level
    /// must be the level band it sits under.
    /// </summary>
    private static List<ParsedEntry> ParseAncestries(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var scan = ScanAncestry(slice);
        var box = ParseAncestryBox(scan.Box);
        var result = new List<ParsedEntry>();
        var ancestry = NewEntry(range, book, Clean(range.Row.PathOf(range)[^1]), compounds);
        ancestry.Traits.AddRange(box.Traits);
        foreach (var (name, body) in box.Specials)
        {
            var scratch = new ParsedEntry { Chapter = ancestry.Chapter };
            var reader = new BodyReader(scratch, compounds, true);
            foreach (var block in body)
            {
                reader.Read(block);
            }

            reader.Finish();
            if (scratch.Parts.Count == 0 || scratch.Parts[0].Label.Length != 0)
            {
                throw new DegreesException($"The ability of an ancestry (page {body[0].Page}) does not start with plain text.");
            }

            var first = new LabelledText(name, scratch.Parts[0].Text);
            ancestry.Parts.Add(first);
            ancestry.Fields.Add(first);
            foreach (var more in scratch.Parts.Skip(1))
            {
                ancestry.Parts.Add(more);
            }
        }

        ancestry.Raw.Add(new LabelledText("hp", box.Hp.ToString(CultureInfo.InvariantCulture)));
        ancestry.Raw.Add(new LabelledText("size", Quoted(box.Size)));
        ancestry.Raw.Add(new LabelledText("speed", box.Speed.ToString(CultureInfo.InvariantCulture)));
        ancestry.Raw.Add(new LabelledText("boosts", FlowList(box.Boosts)));
        ancestry.Raw.Add(new LabelledText("languages", FlowList(box.Languages)));
        ancestry.Raw.Add(new LabelledText("extraLanguages", box.ExtraLanguages.ToString(CultureInfo.InvariantCulture)));
        if (box.Specials.Count > 0)
        {
            ancestry.Raw.Add(new LabelledText("specialAbilities", FlowList(box.Specials.Select(s => Slug(s.Name)))));
        }

        result.Add(ancestry);

        foreach (var h in scan.Heritages)
        {
            var title = Clean(string.Join(' ', h.Header.Lines));
            var traits = new List<string>();
            if (Regex.Match(title, @"^(?<name>.+?)\s*\((?<rarity>Uncommon|Rare|Unique)\)$", RegexOptions.CultureInvariant) is { Success: true } rarity)
            {
                title = rarity.Groups["name"].Value;
                traits.Add(rarity.Groups["rarity"].Value.ToLowerInvariant());
            }

            var entry = NewEntry(range, book, title, compounds, "heritage", "heritages", null, scan.HeritagesTitle);
            entry.Traits.AddRange(traits);
            entry.Owner = ancestry;
            var reader = new BodyReader(entry, compounds, false, false, null, true);
            foreach (var block in h.Body)
            {
                reader.Read(block);
            }

            reader.Finish();
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
                throw new DegreesException($"A feat of an ancestry (page {f.Header.Page}) has level {level} under the band for level {f.Band}.");
            }

            var entry = NewEntry(range, book, TitleCase(m.Groups["name"].Value), compounds, "feat", "feats", null, scan.FeatsTitle);
            entry.Level = level;
            entry.Actions = actions;
            entry.Owner = ancestry;
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
            result.Add(entry);
        }

        return result;
    }
}
