using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>One archetype as the blocks hold it: its section's title, the sentence under it (read past), the heading of its characters text and that text, and its feats (each a heading with its level, its trait line and its text).</summary>
public sealed class ArchetypeUnit
{
    public Block Section { get; set; } = null!;
    public List<Block> Flavor { get; } = new();
    public Block? CharactersHeader { get; set; }
    public List<Block> Characters { get; } = new();
    public List<FeatScan> Feats { get; } = new();
}

/// <summary>An archetypes range sorted into parts: the text before the first heading, the rules sections (headings before the first archetype), the archetypes, and the labels under pictures.</summary>
public sealed class ArchetypesScan
{
    public List<Block> Captions { get; } = new();
    public List<Block> Lead { get; } = new();
    public List<HeadedUnit> RuleUnits { get; } = new();
    public List<ArchetypeUnit> Archetypes { get; } = new();
}

public static partial class EntriesStage
{
    /// <summary>
    /// Sorts the blocks of the archetypes range. A heading in the section font starts an archetype (its first blocks are the sentence under the title, read past); the one heading in the entry-header font under it opens
    /// the text about multiclassing as it, and the feats follow that text, each a heading that ends in the word feat and its level, a trait line and its text. A heading in the entry-header font before the first archetype
    /// is a rules section. A block that fits no part, a second characters heading and a feat with no trait line fail the run.
    /// </summary>
    public static ArchetypesScan ScanArchetypes(List<Block> slice)
    {
        var scan = new ArchetypesScan();
        var units = SplitUnits(slice, scan.Captions);
        ArchetypeUnit? archetype = null;
        foreach (var unit in units)
        {
            if (unit.Header == null)
            {
                scan.Lead.AddRange(unit.Blocks);
                continue;
            }

            if (unit.Header.Role == "section")
            {
                archetype = new ArchetypeUnit { Section = unit.Header };
                archetype.Flavor.AddRange(unit.Blocks);
                scan.Archetypes.Add(archetype);
                continue;
            }

            if (archetype == null)
            {
                scan.RuleUnits.Add(unit);
                continue;
            }

            if (archetype.CharactersHeader != null)
            {
                throw new DegreesException($"An archetype's section (page {archetype.Section.Page}) holds a second heading in the entry-header font.");
            }

            archetype.CharactersHeader = unit.Header;
            FeatScan? feat = null;
            foreach (var b in unit.Blocks)
            {
                var text = BlockText(b);
                if (b.Role == "block-header" && AncestryFeatHeader.Match(HeaderWithoutIcons(text)) is { Success: true } header)
                {
                    feat = new FeatScan { Header = b, Band = int.Parse(header.Groups["level"].Value, CultureInfo.InvariantCulture) };
                    archetype.Feats.Add(feat);
                }
                else if (b.Role == "tag" && feat != null && feat.Body.Count == 0)
                {
                    feat.Tags.Add(b);
                }
                else if (b.Role is "rules" or "body" or "icon" && feat == null)
                {
                    archetype.Characters.Add(b);
                }
                else if (b.Role is "rules" or "body" or "icon")
                {
                    feat!.Body.Add(b);
                }
                else
                {
                    throw new DegreesException($"An archetype's feats hold a {b.Role} block on page {b.Page}, column {b.Column}, where they have none.");
                }
            }
        }

        if (scan.Archetypes.Any(a => a.CharactersHeader == null || a.Characters.Count == 0 || a.Feats.Count == 0 || a.Feats.Any(f => f.Tags.Count == 0 || f.Body.Count == 0)))
        {
            throw new DegreesException("An archetype has no text about multiclassing as it, or no feat, or a feat with no trait line or no text.");
        }

        return scan;
    }

    /// <summary>The blocks the archetypes parser reads past: the sentence under each archetype's title and the labels under pictures.</summary>
    public static List<Block> ArchetypeFlavor(List<Block> slice)
    {
        var scan = ScanArchetypes(slice);
        return scan.Archetypes.SelectMany(a => a.Flavor).Concat(scan.Captions).ToList();
    }

    /// <summary>
    /// A feat as its scan holds it: the name and the level of its heading (an action icon in it is the feat's action cost), its trait line, its text read in the run-on way, the labelled lines the feat type has fields for, an
    /// action it prints in its text set out as a class feature's is, and a list with no bold names as a list of dashes. The level must be the level band it sits under, when the scan has one.
    /// </summary>
    internal static ParsedEntry BuildFeat(TakenRange range, string book, FeatScan f, HashSet<string> compounds, string? sourceTail, bool lowerContinues, string what, bool hangingFromStart = false)
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
            throw new DegreesException($"A feat of {what} (page {f.Header.Page}) has level {level} under the band for level {f.Band}.");
        }

        var entry = NewEntry(range, book, TitleCase(m.Groups["name"].Value), compounds, "feat", "feats", null, sourceTail);
        entry.Level = level;
        entry.Actions = actions;
        foreach (var tag in f.Tags)
        {
            entry.Traits.AddRange(Clean(string.Join(' ', tag.Lines)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()));
        }

        var reader = new BodyReader(entry, compounds, true) { LowerContinues = lowerContinues, HangingFromStart = hangingFromStart };
        foreach (var block in f.Body)
        {
            reader.Read(block);
        }

        reader.Finish();
        ExtractFields(entry);
        SplitInlineAction(entry);
        DashBullets(entry);
        return entry;
    }

    /// <summary>A list of items with no bold name (each a bullet) is set out as the lists with names are: a dash.</summary>
    internal static void DashBullets(ParsedEntry entry)
    {
        for (var i = 0; i < entry.Parts.Count; i++)
        {
            if (entry.Parts[i].Label.Length == 0 && entry.Parts[i].Text.StartsWith("• ", StringComparison.Ordinal))
            {
                entry.Parts[i] = entry.Parts[i] with { Text = "- " + entry.Parts[i].Text[2..] };
            }
        }
    }

    /// <summary>
    /// The <c>archetypes</c> parser: the text before the first heading and each rules section (a sub-heading in the block-header font is a rule of its own) become <c>rule</c> entries, each archetype an <c>archetype</c> entry
    /// whose text is the text about multiclassing as it (the sentence under its title is read past), and each feat under it a <c>feat</c> entry that names the archetype. Its class is linked when the ids are final.
    /// </summary>
    private static List<ParsedEntry> ParseArchetypes(TakenRange range, List<Block> slice, string book, HashSet<string> compounds)
    {
        var scan = ScanArchetypes(slice);
        var result = new List<ParsedEntry>();
        if (scan.Lead.Count > 0)
        {
            result.Add(RuleEntry(range, book, Clean(range.Row.PathOf(range)[^1]), scan.Lead, compounds, null));
        }

        foreach (var unit in scan.RuleUnits)
        {
            foreach (var (name, blocks) in RuleSubunits(unit))
            {
                result.Add(RuleEntry(range, book, name, blocks, compounds, name));
            }
        }

        foreach (var a in scan.Archetypes)
        {
            var name = HeadingName(a.Section);
            var entry = NewEntry(range, book, name, compounds, "archetype", "archetypes", null, name);
            entry.Parts.AddRange(ReadFeature(name, 1, a.Characters, compounds, range.Row.Chapter).Parts);
            DashBullets(entry);
            result.Add(entry);
            foreach (var f in a.Feats)
            {
                var feat = BuildFeat(range, book, f, compounds, name, true, "an archetype", true);
                feat.ArchetypeOwner = entry;
                result.Add(feat);
            }
        }

        return result;
    }
}
