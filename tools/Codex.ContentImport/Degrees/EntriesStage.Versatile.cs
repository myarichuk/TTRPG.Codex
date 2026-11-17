using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// One versatile heritage as the blocks hold it, or the introduction that comes before the first of them in a section that holds several. The lore (and every sentence under a title) is kept apart as <see cref="Flavor"/>.
/// </summary>
public sealed class VersatileUnit
{
    public List<Block> Flavor { get; } = new();

    /// <summary>The captions of the unit's pictures: read past in every unit (an introduction's too), never part of its text.</summary>
    public List<Block> Captions { get; } = new();

    public int LoreHeaders { get; set; }
    public HeritageScan? Heritage { get; set; }
    public string? FeatsTitle { get; set; }
    public int LineageSections { get; set; }
    public List<FeatScan> Feats { get; } = new();
    public List<(Block Block, int Level)> Bands { get; } = new();
}

/// <summary>A versatile heritages range sorted into units. A unit with no heritage is only legal as the first of several: it is the section's own introduction.</summary>
public sealed class VersatileScan
{
    public List<VersatileUnit> Units { get; } = new();
}

public static partial class EntriesStage
{
    private static readonly Regex VersatileHeritageHeader = new(@"^(?<name>.+?)\s*\((?:(?<rarity>Uncommon|Rare|Unique) )?Heritage\)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Sorts the blocks of one range (a section after its title block). A section title that is not a feats title starts a unit; a heading that ends in "(Heritage)" (with a rarity before it, or not) is a unit's heritage, and the
    /// blocks before it are its lore. After a heading that ends in "Feats" come the feats: a "Lineages" heading starts the feats of 1st level that sit under no level band, then each level band and its feats. A block that fits
    /// no part fails the run, and so does text under a band before the next feat's header.
    /// </summary>
    public static VersatileScan ScanVersatile(List<Block> slice)
    {
        var scan = new VersatileScan();
        var unit = new VersatileUnit();
        scan.Units.Add(unit);
        var mode = 'L'; // L lore, H heritage, F feats
        FeatScan? feat = null;
        var band = 0;
        foreach (var b in slice)
        {
            var text = BlockText(b);
            if (IsArtLabel(b))
            {
                unit.Captions.Add(b); // the caption of a picture: read past, and hashed with the lore
                continue;
            }

            switch (b.Role)
            {
                case "section" when !text.EndsWith(" Feats", StringComparison.OrdinalIgnoreCase):
                    unit = new VersatileUnit();
                    scan.Units.Add(unit);
                    mode = 'L';
                    feat = null;
                    band = 0;
                    break;
                case "section" or "entry-header" when text.EndsWith(" Feats", StringComparison.OrdinalIgnoreCase) && mode == 'H':
                    mode = 'F';
                    unit.FeatsTitle = Clean(text);
                    break;
                case "entry-header" when mode == 'L' && VersatileHeritageHeader.IsMatch(Clean(text)):
                    mode = 'H';
                    unit.Heritage = new HeritageScan { Header = b };
                    break;
                case "entry-header" when mode == 'L':
                    unit.Flavor.Add(b);
                    unit.LoreHeaders++;
                    break;
                case "entry-header" when mode == 'F' && text.Equals("Lineages", StringComparison.OrdinalIgnoreCase):
                    // The feats of the lineages are 1st-level feats that sit under no level band.
                    unit.LineageSections++;
                    band = 1;
                    feat = null;
                    break;
                case "block-header" when mode == 'F':
                    if (AncestryBand.Match(text) is { Success: true } bandMatch)
                    {
                        band = int.Parse(bandMatch.Groups["n"].Value, CultureInfo.InvariantCulture);
                        unit.Bands.Add((b, band));
                        feat = null;
                    }
                    else if (AncestryFeatHeader.IsMatch(HeaderWithoutIcons(text)))
                    {
                        feat = new FeatScan { Header = b, Band = band };
                        unit.Feats.Add(feat);
                    }
                    else
                    {
                        throw new DegreesException($"A versatile heritage's feats hold a heading on page {b.Page}, column {b.Column}, that is neither a level band nor a feat's header.");
                    }

                    break;
                case "tag" when mode == 'F' && feat != null && feat.Body.Count == 0:
                    feat.Tags.Add(b);
                    break;
                case "rules" or "body" or "icon":
                    if (mode == 'F' && feat == null && unit.Bands.Count > 0)
                    {
                        throw new DegreesException($"A versatile heritage's feats hold a {b.Role} block on page {b.Page}, column {b.Column}, after a level band and before a feat's header.");
                    }

                    if (mode == 'L' || (mode == 'H' && unit.Heritage == null) || (mode == 'F' && feat == null))
                    {
                        unit.Flavor.Add(b);
                    }
                    else if (mode == 'H')
                    {
                        unit.Heritage!.Body.Add(b);
                    }
                    else
                    {
                        feat!.Body.Add(b);
                    }

                    break;
                default:
                    throw new DegreesException($"A versatile heritage's range holds a {b.Role} block on page {b.Page}, column {b.Column}, where its {(mode == 'L' ? "lore" : mode == 'H' ? "heritage" : "feats")} have none.");
            }
        }

        // The first unit holds the section's own introduction when more units follow it (a section of several heritages); a section of one heritage has it as the heritage's lore.
        foreach (var (u, k) in scan.Units.Select((u, k) => (u, k)))
        {
            var intro = k == 0 && scan.Units.Count > 1;
            if (u.Heritage == null != intro || (!intro && u.FeatsTitle == null) || (intro && (u.Feats.Count > 0 || u.FeatsTitle != null)))
            {
                throw new DegreesException("A versatile heritages range holds a part with no heritage that is not the introduction of a section of several, or a heritage with no feats section.");
            }
        }

        return scan;
    }

    /// <summary>The blocks a versatile heritages range reads past: the lore, the sentence under each section's title and the captions of its pictures (hashed as left-out text with the other lore). The introduction of a section of several is a rule, not read past.</summary>
    public static List<Block> VersatileFlavor(List<Block> slice) =>
        ScanVersatile(slice).Units.SelectMany(u => u.Heritage != null ? u.Flavor.Concat(u.Captions) : u.Captions).ToList();

    /// <summary>
    /// The <c>versatile</c> parser: the heritages of a section (one or several) into <c>heritage</c> entries that name no ancestry (any ancestry may take one), their feats into <c>feat</c> entries whose trait line
    /// carries the heritage's key, and the introduction of a section of several into one <c>rule</c> entry. The lore is read past. A feat's level must be the level band it sits under, and a lineage feat sits under none: it is of 1st level.
    /// </summary>
    private static List<ParsedEntry> ParseVersatile(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var scan = ScanVersatile(slice);
        var result = new List<ParsedEntry>();
        foreach (var unit in scan.Units)
        {
            if (unit.Heritage == null)
            {
                var rule = NewEntry(range, book, Clean(range.Row.PathOf(range)[^1]), compounds, "rule", "rules", range.Row.Category);
                // The section's introduction is set around a picture, so its lines are ragged: every line is read as part of the paragraph it is in (runOn), as a feat's text is.
                var introReader = new BodyReader(rule, compounds, true, false, null, true);
                foreach (var block in unit.Flavor)
                {
                    introReader.Read(block);
                }

                introReader.Finish();
                result.Add(rule);
                continue;
            }

            var title = Clean(string.Join(' ', unit.Heritage.Header.Lines));
            var traits = new List<string>();
            var match = VersatileHeritageHeader.Match(title);
            title = match.Groups["name"].Value;
            if (match.Groups["rarity"].Success)
            {
                traits.Add(match.Groups["rarity"].Value.ToLowerInvariant());
            }

            var heritage = NewEntry(range, book, title, compounds, "heritage", "heritages", null, null);
            heritage.Traits.AddRange(traits);
            var reader = new BodyReader(heritage, compounds, false, false, null, true);
            foreach (var block in unit.Heritage.Body)
            {
                reader.Read(block);
            }

            reader.Finish();
            result.Add(heritage);

            foreach (var f in unit.Feats)
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
                    throw new DegreesException($"A feat of a versatile heritage (page {f.Header.Page}) has level {level} under the band for level {f.Band}.");
                }

                var entry = NewEntry(range, book, TitleCase(m.Groups["name"].Value), compounds, "feat", "feats", null, "Feats");
                entry.Level = level;
                entry.Actions = actions;
                entry.TraitOwner = heritage;
                foreach (var tag in f.Tags)
                {
                    entry.Traits.AddRange(Clean(string.Join(' ', tag.Lines)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()));
                }

                var featReader = new BodyReader(entry, compounds, true);
                foreach (var block in f.Body)
                {
                    featReader.Read(block);
                }

                featReader.Finish();
                ExtractFields(entry);
                result.Add(entry);
            }
        }

        return result;
    }
}
