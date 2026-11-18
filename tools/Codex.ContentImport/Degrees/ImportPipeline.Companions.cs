using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>What the companions range holds, counted several ways from the document (see <see cref="ImportPipeline.CheckCompanionCounts"/>).</summary>
public sealed record CompanionCounts(int Bookmarks, int Headings, int CompanionHeadings, int SizeLines, int HitPointLines, int AttributeLines, int SupportLines, int ManeuverLines, int Companions, int Rules);

/// <summary>What the familiars range holds (see <see cref="ImportPipeline.CheckFamiliarCounts"/>): the abilities of each kind counted by bold names, by the shape of the text, and as entries.</summary>
public sealed record FamiliarCounts(int Bookmarks, int Headings, Dictionary<string, (int Bold, int Shape, int Entries)> Kinds, int Rules);

/// <summary>What one archetype holds (see <see cref="ImportPipeline.CheckArchetypeCounts"/>).</summary>
public sealed record ArchetypeCounts(string Name, int FeatHeaders, int TraitLines, int Markers, int Feats, int Dedications);

/// <summary>All the archetypes range holds: the bookmarks against the headings, and each archetype's counts.</summary>
public sealed record ArchetypesCounts(int Bookmarks, int Headings, int Rules, List<ArchetypeCounts> Archetypes);

public static partial class ImportPipeline
{
    private static List<SpineNode> Descendants(IReadOnlyList<SpineNode> spine, SpineNode of)
    {
        var at = spine.ToList().FindIndex(n => ReferenceEquals(n, of));
        return spine.Skip(at + 1).TakeWhile(n => n.Depth > of.Depth).Where(n => !n.Sidebar).ToList();
    }

    private static bool Labelled(LineInfo? info, string label) => info != null && SpanText.Normalize(info.Label).Trim() == label;

    /// <summary>
    /// For the companions range, from the document and not from the parser's text handling: every bookmark below the section against the headings (the section font, the entry-header font and a heading in the block-header
    /// font that has neither an icon nor a feat's shape; a row may say in <c>countsDiffer</c> why they differ); the headings that open a companion against the lines labelled Size, Hit Points, the attribute modifiers, the support benefit
    /// and the advanced maneuver, and against the companions parsed. Any difference fails the run.
    /// </summary>
    public static CompanionCounts? CheckCompanionCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var range = ranges.FirstOrDefault(r => !r.Own && r.Row.Parser == "companions");
        if (range == null)
        {
            return null;
        }

        var slice = range.BlocksIn(blocks);
        var units = EntriesStage.SplitUnits(slice, new List<Block>());
        var bookmarks = Descendants(spine, range.Node).Count;
        var headings = slice.Count(b => b.Role is "section" or "entry-header" || EntriesStage.IsPlainSubheading(b));
        var companionUnits = units.Count(EntriesStage.IsCompanionUnit);
        int Lines(string label) => units.Where(EntriesStage.IsCompanionUnit).SelectMany(u => u.Blocks).SelectMany(b => b.Role == "body" && b.Info != null ? b.Info : new List<LineInfo>()).Count(i => Labelled(i, label));
        var companions = parsed.Count(e => e.Type == "companion");
        var counts = new CompanionCounts(bookmarks, headings, companionUnits, Lines("Size"), Lines("Hit Points"), slice.SelectMany(b => b.Role == "body" && b.Info != null ? b.Info : new List<LineInfo>()).Count(i => Labelled(i, "Str")), Lines("Support Benefit"), Lines("Advanced Maneuver"), companions,
            parsed.Count(e => e.Type == "rule" && e.RowNumber == range.RowNumber));
        var same = new[] { counts.SizeLines, counts.HitPointLines, counts.AttributeLines, counts.SupportLines, counts.ManeuverLines, counts.Companions }.All(n => n == counts.CompanionHeadings);
        var bookmarksAgree = counts.Bookmarks == counts.Headings || (!string.IsNullOrWhiteSpace(range.Row.CountsDiffer) && counts.Headings > counts.Bookmarks);
        if (!same || !bookmarksAgree || counts.Companions == 0 || counts.Rules != counts.Headings - counts.CompanionHeadings + 1)
        {
            throw new DegreesException($"Chapter {range.Row.Chapter}: the companions' counts differ: {counts.Bookmarks} bookmarks and {counts.Headings} headings; {counts.CompanionHeadings} headings that open a companion, {counts.SizeLines} size lines, " +
                $"{counts.HitPointLines} hit point lines, {counts.AttributeLines} attribute lines, {counts.SupportLines} support benefit lines, {counts.ManeuverLines} advanced maneuver lines and {counts.Companions} entries; {counts.Rules} rules for {counts.Headings - counts.CompanionHeadings} other headings and the lead.");
        }

        return counts;
    }

    private static readonly Regex AbilityShape = new(@"^[A-Z][A-Za-z’'-]*(?: [A-Za-z’'-]+){0,3}: \S", RegexOptions.CultureInvariant);

    /// <summary>
    /// For the familiars range: every bookmark below the section against the headings; and, for each section the row names as a list of abilities, the abilities counted by the bold name at a line's start, by the shape of the
    /// line (a name of up to four words and a colon), and by the entries parsed of that kind. The names an ability's own sentence gives of the pet abilities (no bold name, no colon) are no abilities.
    /// </summary>
    public static FamiliarCounts? CheckFamiliarCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var range = ranges.FirstOrDefault(r => !r.Own && r.Row.Parser == "familiars");
        if (range == null)
        {
            return null;
        }

        var slice = range.BlocksIn(blocks);
        var units = EntriesStage.SplitUnits(slice, new List<Block>());
        var bookmarks = Descendants(spine, range.Node).Count;
        var headings = units.Count(u => u.Header != null);
        var kinds = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);
        foreach (var unit in units.Where(u => u.Header != null && range.Row.AbilityKinds.ContainsKey(EntriesStage.HeadingName(u.Header))))
        {
            var kind = range.Row.AbilityKinds[EntriesStage.HeadingName(unit.Header!)];
            var lines = unit.Blocks.SelectMany(b => b.Lines.Select((l, i) => (Line: SpanText.Normalize(l).Trim(), Info: b.Info?[i]))).ToList();
            var bold = lines.Count(l => l.Info != null && l.Info.Label.EndsWith(':'));
            var shape = lines.Count(l => AbilityShape.IsMatch(l.Line));
            var entries = parsed.Count(e => e.Type == "familiar-ability" && e.Raw.Any(r => r.Label == "kind" && r.Text == kind));
            kinds[kind] = (bold, shape, entries);
        }

        var counts = new FamiliarCounts(bookmarks, headings, kinds.ToDictionary(k => k.Key, k => k.Value), parsed.Count(e => e.Type == "rule" && e.RowNumber == range.RowNumber));
        if (bookmarks != headings || kinds.Count != range.Row.AbilityKinds.Count || kinds.Values.Any(k => k.Item1 == 0 || k.Item1 != k.Item2 || k.Item1 != k.Item3))
        {
            throw new DegreesException($"Chapter {range.Row.Chapter}: the familiars' counts differ: {bookmarks} bookmarks and {headings} headings; " +
                string.Join("; ", kinds.Select(k => $"{k.Key}: {k.Value.Item1} bold names, {k.Value.Item2} lines of the shape, {k.Value.Item3} entries")) + ".");
        }

        return counts;
    }

    /// <summary>
    /// For the archetypes range: the bookmarks of the section's children against the rules sections and the archetypes read, and their children against the sub-headings of the rules and the archetypes' headings about
    /// multiclassing (compared by title, whatever the case); and, for each archetype, its feat headers (the block-header font, ending in the word feat and a level) against its trait lines against the lines that end in the
    /// word and a level, against the feats parsed, with exactly one dedication feat. Any difference fails the run.
    /// </summary>
    public static ArchetypesCounts? CheckArchetypeCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var range = ranges.FirstOrDefault(r => !r.Own && r.Row.Parser == "archetypes");
        if (range == null)
        {
            return null;
        }

        var where = $"Chapter {range.Row.Chapter}";
        var slice = range.BlocksIn(blocks);
        var scan = EntriesStage.ScanArchetypes(slice);
        var marker = new Regex(@"\sFEAT\s+\d+$", RegexOptions.CultureInvariant);
        var kids = spine.Skip(spine.ToList().FindIndex(n => ReferenceEquals(n, range.Node)) + 1).TakeWhile(n => n.Depth > range.Node.Depth).Where(n => !n.Sidebar).ToList();
        var children = kids.Where(k => k.Depth == range.Node.Depth + 1).ToList();
        var grandchildren = kids.Where(k => k.Depth == range.Node.Depth + 2).ToList();
        var subheadings = scan.RuleUnits.Sum(u => EntriesStage.RuleSubunits(u).Count - 1);
        var names = scan.Archetypes.Select(a => EntriesStage.Slug(EntriesStage.HeadingName(a.Section))).ToList();
        var childNames = children.Skip(scan.RuleUnits.Count).Select(k => EntriesStage.Slug(k.Title)).ToList();
        var characters = scan.Archetypes.Select(a => EntriesStage.Slug(EntriesStage.HeadingName(a.CharactersHeader!))).ToList();
        var grandNames = grandchildren.Skip(subheadings).Select(k => EntriesStage.Slug(k.Title)).ToList();
        if (children.Count != scan.RuleUnits.Count + scan.Archetypes.Count || !names.SequenceEqual(childNames) || grandchildren.Count != subheadings + scan.Archetypes.Count || !characters.SequenceEqual(grandNames))
        {
            throw new DegreesException($"{where}: the archetypes' bookmarks differ from their headings ({children.Count} bookmarks for {scan.RuleUnits.Count} rules sections and {scan.Archetypes.Count} archetypes; " +
                $"{grandchildren.Count} below them for {subheadings} sub-headings and {scan.Archetypes.Count} texts about multiclassing; or the titles differ).");
        }

        var result = new List<ArchetypeCounts>();
        var at = -1;
        var counted = new List<(int Headers, int Tags, int Markers)>();
        foreach (var b in slice)
        {
            if (b.Role == "section")
            {
                counted.Add((0, 0, 0));
                at++;
            }

            if (at < 0)
            {
                continue;
            }

            var (h, t, m) = counted[at];
            counted[at] = (h + (b.Role == "block-header" && marker.IsMatch(" " + EntriesStage.HeaderWithoutIcons(string.Join(' ', b.Lines))) ? 1 : 0), t + (b.Role == "tag" ? 1 : 0),
                m + b.Lines.Count(l => marker.IsMatch(SpanText.Normalize(l))));
        }

        for (var k = 0; k < scan.Archetypes.Count; k++)
        {
            var name = EntriesStage.HeadingName(scan.Archetypes[k].Section);
            var mine = parsed.Where(e => e.Type == "feat" && e.ArchetypeOwner != null && e.ArchetypeOwner.Name == name).ToList();
            var dedications = mine.Count(e => e.Traits.Contains("dedication"));
            var count = new ArchetypeCounts(name, counted[k].Headers, counted[k].Tags, counted[k].Markers, mine.Count, dedications);
            if (count.FeatHeaders == 0 || count.FeatHeaders != count.TraitLines || count.FeatHeaders != count.Markers || count.FeatHeaders != count.Feats || count.Dedications != 1 || !string.Join(' ', scan.Archetypes[k].Feats[0].Tags.SelectMany(t => t.Lines)).Contains("DEDICATION", StringComparison.Ordinal))
            {
                throw new DegreesException($"{where}: an archetype's counts differ: {count.FeatHeaders} feat headers, {count.TraitLines} trait lines, {count.Markers} level markers, {count.Feats} entries and {count.Dedications} dedication feats (the first feat must be the one).");
            }

            result.Add(count);
        }

        return new ArchetypesCounts(children.Count, scan.RuleUnits.Count + scan.Archetypes.Count, parsed.Count(e => e.Type == "rule" && e.RowNumber == range.RowNumber), result);
    }
}
