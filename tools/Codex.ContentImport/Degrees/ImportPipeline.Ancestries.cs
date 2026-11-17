using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>What one ancestry's range holds, counted two or three ways from the document (see <see cref="ImportPipeline.CheckAncestryCounts"/>).</summary>
public sealed record AncestryCounts(string Name, int LoreBookmarks, int LoreHeadings, int HeritageBookmarks, int HeritageHeadings, int Heritages, int BandBookmarks, int Bands, int FeatHeaders, int TraitLines, int Markers, int Feats);

public static partial class ImportPipeline
{
    /// <summary>
    /// For every taken <c>ancestries</c> range, from the document and not from the parser's text handling:
    /// the bookmark children of the ancestry other than its two sections against the lore headings it reads past; the children of its heritages section against the heritage headings and
    /// the heritage entries parsed; the children of its feats section (the level bands) against the level bands found; and the feat headers against the trait lines against the lines
    /// that end in the word and a level, against the feat entries parsed. Every band must hold a feat. Any difference fails the run: the book prints no list of its ancestry feats to compare
    /// them with, so a row's <c>countsDiffer</c> only says so and gives no way out.
    /// </summary>
    public static List<AncestryCounts> CheckAncestryCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var result = new List<AncestryCounts>();
        var marker = new Regex(@"\sFEAT\s+\d+$", RegexOptions.CultureInvariant);
        List<SpineNode> Children(SpineNode of) => spine.Where(n => n.Path.Length == of.Path.Length + 1 && n.Path.Take(of.Path.Length).SequenceEqual(of.Path) && !n.Sidebar).ToList();
        foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "ancestries"))
        {
            var node = range.Node;
            var slice = range.BlocksIn(blocks);
            var scan = EntriesStage.ScanAncestry(slice);
            var kids = Children(node);
            var heritages = kids.Where(k => k.Title.EndsWith(" Heritages", StringComparison.OrdinalIgnoreCase)).ToList();
            var feats = kids.Where(k => k.Title.EndsWith("Ancestry Feats", StringComparison.OrdinalIgnoreCase)).ToList();
            if (heritages.Count != 1 || feats.Count != 1)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the ancestry has {heritages.Count} heritages sections and {feats.Count} feats sections among its bookmark children, not one of each.");
            }

            var markers = slice.SelectMany(b => b.Lines).Count(l => marker.IsMatch(SpanText.Normalize(l)));
            // A trait line that wraps is two tag blocks in a row, so a run of them is one.
            var traitLines = Enumerable.Range(0, slice.Count).Count(k => slice[k].Role == "tag" && (k == 0 || slice[k - 1].Role != "tag"));
            var headers = slice.Count(b => b.Role == "block-header" && marker.IsMatch(" " + EntriesStage.HeaderWithoutIcons(string.Join(' ', b.Lines))));
            var mine = parsed.Where(e => e.Owner != null && e.Owner.Name == node.Title).ToList();
            var count = new AncestryCounts(node.Title, kids.Count - 2, scan.LoreHeaders, Children(heritages[0]).Count, scan.Heritages.Count, mine.Count(e => e.Type == "heritage"),
                Children(feats[0]).Count, scan.Bands.Count, headers, traitLines, markers, mine.Count(e => e.Type == "feat"));
            var emptyBands = scan.Bands.Count(b => scan.Feats.All(f => f.Band != b.Level));
            if (count.LoreBookmarks != count.LoreHeadings || count.HeritageBookmarks != count.HeritageHeadings || count.HeritageHeadings != count.Heritages
                || count.BandBookmarks != count.Bands || emptyBands != 0 || count.FeatHeaders != count.TraitLines || count.FeatHeaders != count.Markers || count.FeatHeaders != count.Feats)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: an ancestry's counts differ: {count.LoreBookmarks} lore bookmarks and {count.LoreHeadings} lore headings; {count.HeritageBookmarks} heritage bookmarks, " +
                    $"{count.HeritageHeadings} headings and {count.Heritages} entries; {count.BandBookmarks} level bookmarks, {count.Bands} bands ({emptyBands} with no feat); {count.FeatHeaders} feat headers, {count.TraitLines} trait lines, {count.Markers} level markers and {count.Feats} entries.");
            }

            result.Add(count);
        }

        return result;
    }

    /// <summary>
    /// The book's table of contents names its ancestries with the page each starts on, in the front matter: the names it lists before the first versatile heritage, as a set, against the ancestries parsed.
    /// The one printed list of the ancestries, so the independent count of them. A run with no ancestries parsed has nothing to compare, and returns 0.
    /// </summary>
    public static int CheckAncestryList(BlocksResult blocks, IReadOnlyList<ParsedEntry> parsed)
    {
        var ancestries = parsed.Where(e => e.Type == "ancestry").Select(e => EntriesStage.Slug(e.Name)).ToHashSet(StringComparer.Ordinal);
        if (ancestries.Count == 0)
        {
            return 0;
        }

        var contents = blocks.Blocks.Where(b => b.Page <= 6 && b.Role == "body").Select(b => SpanText.Normalize(string.Join(' ', b.Lines)))
            .Where(t => Regex.IsMatch(t, @"Versatile Heritages\s*\.{4,}")).ToList();
        if (contents.Count != 1)
        {
            throw new DegreesException($"The table of contents with the ancestries was found {contents.Count} times in the front matter, so the list of ancestries is not known.");
        }

        var listed = new List<string>();
        foreach (Match m in Regex.Matches(contents[0], @"(?<name>[A-Z][^.]*?)\s*\.{4,}\s*\d+", RegexOptions.CultureInvariant))
        {
            var name = m.Groups["name"].Value.Trim();
            if (name.Equals("Versatile Heritages", StringComparison.Ordinal))
            {
                break;
            }

            listed.Add(EntriesStage.Slug(name));
        }

        if (!listed.ToHashSet(StringComparer.Ordinal).SetEquals(ancestries) || listed.Count != ancestries.Count)
        {
            throw new DegreesException($"The table of contents lists {listed.Count} ancestries before the versatile heritages, and the parser read {ancestries.Count}; as sets they differ ({listed.Except(ancestries).Count()} listed and not parsed, {ancestries.Except(listed).Count()} parsed and not listed).");
        }

        return listed.Count;
    }
}
