using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>What one versatile heritages range holds, counted from the bookmarks and from the blocks (see <see cref="ImportPipeline.CheckVersatileCounts"/>).</summary>
public sealed record VersatileCounts(string Name, int LoreBookmarks, int LoreHeadings, int HeritageBookmarks, int Heritages, int FeatsBookmarks, int LineageBookmarks, int Lineages, int BandBookmarks, int Bands, int FeatHeaders, int TraitLines, int Markers, int Feats);

/// <summary>What the backgrounds range holds, counted four ways (see <see cref="ImportPipeline.CheckBackgroundCounts"/>).</summary>
public sealed record BackgroundCounts(int Headers, int BoostSentences, int FeatSentences, int Entries);

public static partial class ImportPipeline
{
    private static readonly Regex VersatileLoreTitle = new(@"^(?:You might|Others probably)\.\.\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VersatileHeritageTitle = new(@"\((?:(?:Uncommon|Rare|Unique) )?Heritage\)$", RegexOptions.CultureInvariant);
    private static readonly Regex LevelBandTitle = new(@"^\d+(?:st|nd|rd|th) Level$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// For every taken <c>versatile</c> range, from the document: its bookmarks below the range (the lore children, the heritage heading, the feats sections, the lineages sections and the level bands) against what
    /// the scan found of each; the feat headers against the trait lines against the lines that end in the word and a level, against the feats parsed (and every band holds a feat). The section that introduces the kinds
    /// of versatile heritage ("In This Book") has one child for each range, (the feats heading of every heritage is found by the scan, which fails without one; the book's bookmarks name only some of those headings, so the bookmarks may be fewer, never more), which is the independent count of them, and the heritages and feats parsed in all must be the sums of the ranges'. Any difference fails the run.
    /// </summary>
    public static List<VersatileCounts> CheckVersatileCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var result = new List<VersatileCounts>();
        var marker = new Regex(@"\sFEAT\s+\d+$", RegexOptions.CultureInvariant);
        var mine = ranges.Where(r => !r.Own && r.Row.Parser == "versatile").ToList();
        foreach (var range in mine)
        {
            var node = range.Node;
            var below = spine.Where(n => n.Path.Length > node.Path.Length && n.Path.Take(node.Path.Length).SequenceEqual(node.Path) && !n.Sidebar).ToList();
            var slice = range.BlocksIn(blocks);
            var scan = EntriesStage.ScanVersatile(slice);
            var units = scan.Units.Where(u => u.Heritage != null).ToList();
            var markers = slice.SelectMany(b => b.Lines).Count(l => marker.IsMatch(SpanText.Normalize(l)));
            var traitLines = Enumerable.Range(0, slice.Count).Count(k => slice[k].Role == "tag" && (k == 0 || slice[k - 1].Role != "tag"));
            var headers = slice.Count(b => b.Role == "block-header" && marker.IsMatch(" " + EntriesStage.HeaderWithoutIcons(string.Join(' ', b.Lines))));
            var count = new VersatileCounts(node.Title, below.Count(n => VersatileLoreTitle.IsMatch(n.Title)), units.Sum(u => u.LoreHeaders),
                below.Count(n => VersatileHeritageTitle.IsMatch(n.Title)), units.Count, below.Count(n => n.Title.EndsWith(" Feats", StringComparison.OrdinalIgnoreCase)),
                below.Count(n => n.Title.Equals("Lineages", StringComparison.OrdinalIgnoreCase)), units.Sum(u => u.LineageSections),
                below.Count(n => LevelBandTitle.IsMatch(n.Title)), units.Sum(u => u.Bands.Count), headers, traitLines, markers, units.Sum(u => u.Feats.Count));
            var emptyBands = units.Sum(u => u.Bands.Count(b => u.Feats.All(f => f.Band != b.Level)));
            if (count.LoreBookmarks != count.LoreHeadings || count.HeritageBookmarks != count.Heritages || count.FeatsBookmarks > count.Heritages || count.LineageBookmarks != count.Lineages
                || count.BandBookmarks != count.Bands || emptyBands != 0 || count.FeatHeaders != count.TraitLines || count.FeatHeaders != count.Markers || count.FeatHeaders != count.Feats)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: a versatile heritage range's counts differ: {count.LoreBookmarks} lore bookmarks and {count.LoreHeadings} lore headings; {count.HeritageBookmarks} heritage bookmarks and {count.Heritages} heritages; " +
                    $"{count.FeatsBookmarks} feats bookmarks; {count.LineageBookmarks} lineage bookmarks and {count.Lineages} lineage headings; {count.BandBookmarks} level bookmarks, {count.Bands} bands ({emptyBands} with no feat); " +
                    $"{count.FeatHeaders} feat headers, {count.TraitLines} trait lines, {count.Markers} level markers and {count.Feats} entries.");
            }

            result.Add(count);
        }

        if (mine.Count == 0)
        {
            return result;
        }

        // The parsed entries of the rows' ranges are the sums of what the scans found.
        var parsedHeritages = parsed.Count(e => e.Type == "heritage" && e.Owner == null);
        var parsedFeats = parsed.Count(e => e.Type == "feat" && e.TraitOwner != null);
        if (parsedHeritages != result.Sum(c => c.Heritages) || parsedFeats != result.Sum(c => c.Feats))
        {
            throw new DegreesException($"The versatile heritages parsed are {parsedHeritages} heritages and {parsedFeats} feats, and the ranges' scans found {result.Sum(c => c.Heritages)} and {result.Sum(c => c.Feats)}.");
        }

        // The overview ("In This Book") lists the kinds of versatile heritage, one child for each range (a mixed-ancestry kind holds more than one heritage).
        var overview = spine.FirstOrDefault(n => n.Title == "In This Book" && mine.All(r => r.Node.Path.Length == n.Path.Length && r.Node.Path.Take(n.Path.Length - 1).SequenceEqual(n.Path.Take(n.Path.Length - 1))));
        if (overview == null)
        {
            throw new DegreesException("The overview of the versatile heritages (the section that lists their kinds) was not found beside the ranges taken, so the one independent count of the kinds is missing.");
        }
        else
        {
            var kinds = spine.Count(n => n.Path.Length == overview.Path.Length + 1 && n.Path.Take(overview.Path.Length).SequenceEqual(overview.Path) && !n.Sidebar);
            if (kinds != mine.Count)
            {
                throw new DegreesException($"The overview of the versatile heritages lists {kinds} kinds, and {mine.Count} ranges are taken.");
            }
        }

        return result;
    }

    /// <summary>
    /// For the taken <c>backgrounds</c> range, from the document: the headings that end in the background word, the sentences that open the rules of a background, the sentences that grant a feat, and the entries parsed.
    /// The chapter prints no list of its backgrounds, so there is no fifth count. All four must agree.
    /// </summary>
    public static BackgroundCounts? CheckBackgroundCounts(BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var range = ranges.FirstOrDefault(r => !r.Own && r.Row.Parser == "backgrounds");
        if (range == null)
        {
            return null;
        }

        var slice = range.BlocksIn(blocks);
        var header = new Regex(@"^.+\s+BACKGROUND$", RegexOptions.CultureInvariant);
        var first = slice.FindIndex(b => b.Role == "block-header");
        var headers = slice.Count(b => b.Role == "block-header" && header.IsMatch(SpanText.Normalize(string.Join(' ', b.Lines)).Trim()));
        var text = SpanText.Normalize(string.Join(' ', slice.Skip(first < 0 ? slice.Count : first).Where(b => b.Role != "block-header").SelectMany(b => b.Lines)));
        var count = new BackgroundCounts(headers, Regex.Matches(text, @"\b" + EntriesStage.BackgroundRulesStart, RegexOptions.CultureInvariant).Count,
            Regex.Matches(text, @"\bgain (?:the |a )[^.]*?\bfeat\b", RegexOptions.CultureInvariant).Count, parsed.Count(e => e.Type == "background"));
        if (count.Headers != count.BoostSentences || count.Headers != count.FeatSentences || count.Headers != count.Entries)
        {
            throw new DegreesException($"Chapter {range.Row.Chapter}: the backgrounds' counts differ: {count.Headers} headings, {count.BoostSentences} boost sentences, {count.FeatSentences} feat sentences and {count.Entries} entries.");
        }

        return count;
    }
}
