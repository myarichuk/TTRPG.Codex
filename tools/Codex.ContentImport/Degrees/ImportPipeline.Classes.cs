using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>What one class's range holds, counted two or three ways from the document (see <see cref="ImportPipeline.CheckClassCounts"/>).</summary>
public sealed record ClassCounts(string Name, int LoreBookmarks, int LoreHeadings, int FeatureBookmarks, int Features, int RacketBookmarks, int Rackets, int Subclasses,
    int BandBookmarks, int Bands, int FeatHeaders, int TraitLines, int Markers, int Feats, int Listed);

public static partial class ImportPipeline
{
    private static readonly Regex BookmarkLevel = new(@"^(?<name>.+?)(?:\s*\((?<n>\d+)(?:st|nd|rd|th)\))?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// For every taken <c>classes</c> range, from the document and not from the parser's text handling: the bookmark children of the class before its class features against the flavor headings read past;
    /// the bookmark children of the class features (less the feats section) against the feature rows, by name and level and in order; the children of the subclass feature against the subclasses read;
    /// the children of the feats section (the level bands) against the bands found; the feat headers against the trait lines against the lines that end in the word and a level, against the feat
    /// entries parsed; and the printed list of the class's feats (name and level) against the feats parsed, as a set. Every band must hold a feat. Any difference fails the run. (The advancement table
    /// was compared with the feature rows by the parser.)
    /// </summary>
    public static List<ClassCounts> CheckClassCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var result = new List<ClassCounts>();
        var marker = new Regex(@"\sFEAT\s+\d+$", RegexOptions.CultureInvariant);
        // The children of a bookmark by position, not by path: a class has two bookmarks of one title (the feature and the section of its feats), and a path would give both the same children.
        List<SpineNode> Children(SpineNode of)
        {
            var at = spine.ToList().FindIndex(n => ReferenceEquals(n, of));
            return spine.Skip(at + 1).TakeWhile(n => n.Depth > of.Depth).Where(n => n.Depth == of.Depth + 1 && !n.Sidebar).ToList();
        }

        foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "classes"))
        {
            var node = range.Node;
            var where = $"Chapter {range.Row.Chapter}, path {range.PathNumber}";
            var slice = range.BlocksIn(blocks);
            var scan = EntriesStage.ScanClass(slice, range.Row);
            var kids = Children(node);
            var featuresNode = kids.SingleOrDefault(k => k.Title.Equals("Class Features", StringComparison.OrdinalIgnoreCase))
                ?? throw new DegreesException($"{where}: the class has no Class Features bookmark among its children.");
            var lore = kids.TakeWhile(k => k != featuresNode).Count();
            var below = Children(featuresNode);
            var featsNode = below.Where(k => Children(k).Count > 0 && k.Title.EndsWith("Feats", StringComparison.OrdinalIgnoreCase)).ToList();
            if (featsNode.Count != 1)
            {
                throw new DegreesException($"{where}: the class has {featsNode.Count} feats sections (a bookmark that ends in Feats and has children) under its class features, not one.");
            }

            // The feature rows: every other child of the class features, as the headings give them.
            var featureKids = below.Where(k => k != featsNode[0]).Select(k =>
            {
                var m = BookmarkLevel.Match(k.Title);
                return (Name: EntriesStage.Slug(m.Groups["name"].Value), Level: m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 1);
            }).ToList();
            var featureRows = scan.Features.Select(f => (Name: EntriesStage.Slug(f.Name), f.Level)).ToList();
            if (!featureKids.SequenceEqual(featureRows))
            {
                throw new DegreesException($"{where}: the class features differ from their bookmarks ({featureKids.Count} bookmarks, {featureRows.Count} headings; as sequences of name and level they are not equal).");
            }

            var racketKids = range.Row.SubclassFeature == null ? new List<SpineNode>()
                : Children(below.Single(k => EntriesStage.Slug(BookmarkLevel.Match(k.Title).Groups["name"].Value) == EntriesStage.Slug(range.Row.SubclassFeature)));
            var racketNames = racketKids.Select(k => EntriesStage.Slug(k.Title)).ToList();
            var racketHeadings = scan.Subclasses.Select(s => EntriesStage.Slug(string.Join(' ', s.Header.Lines))).ToList();
            var bandKids = Children(featsNode[0]).Select(k => EntriesStage.Slug(k.Title)).ToList();
            var bands = scan.Bands.Select(b => EntriesStage.Slug(string.Join(' ', b.Block.Lines))).ToList();

            var markers = slice.SelectMany(b => b.Lines).Count(l => marker.IsMatch(SpanText.Normalize(l)));
            var traitLines = Enumerable.Range(0, slice.Count).Count(k => slice[k].Role == "tag" && (k == 0 || slice[k - 1].Role != "tag") && slice.Take(k).Any(b => b.Role == "section" && BlockTextOf(b).EndsWith("Feats", StringComparison.Ordinal)));
            var headers = slice.Count(b => b.Role == "block-header" && marker.IsMatch(" " + EntriesStage.HeaderWithoutIcons(string.Join(' ', b.Lines))));
            var classKey = EntriesStage.Slug(node.Title);
            var mine = parsed.Where(e => e.ClassKey == classKey).ToList();
            var subclasses = parsed.Count(e => e.Type == "subclass" && e.Owner != null && e.Owner.Name == node.Title);
            var listed = EntriesStage.ByNamePairs(scan);
            var count = new ClassCounts(node.Title, lore, scan.LoreHeaders, featureKids.Count, featureRows.Count, racketKids.Count, scan.Subclasses.Count, subclasses,
                bandKids.Count, scan.Bands.Count, headers, traitLines, markers, mine.Count, listed.Count);
            var emptyBands = scan.Bands.Count(b => scan.Feats.All(f => f.Band != b.Level));
            var parsedPairs = mine.Select(e => (EntriesStage.Slug(e.Name), e.Level ?? 0)).ToHashSet();
            // A class whose feats section has no bookmark for its 1st-level band (the muse feats of the bard) says so in its row's countsDiffer: every other band must still be a bookmark, in order.
            var bandsAgree = bandKids.SequenceEqual(bands)
                || (!string.IsNullOrWhiteSpace(range.Row.CountsDiffer) && scan.Bands.Count > 1 && scan.Bands[0].Level == 1 && bandKids.SequenceEqual(bands.Skip(1)));
            if (count.LoreBookmarks != count.LoreHeadings || !racketNames.SequenceEqual(racketHeadings) || count.Rackets != count.Subclasses || !bandsAgree || emptyBands != 0
                || count.FeatHeaders != count.TraitLines || count.FeatHeaders != count.Markers || count.FeatHeaders != count.Feats || scan.ByNameHeaders != 1
                || !listed.ToHashSet().SetEquals(parsedPairs) || listed.Count != parsedPairs.Count || count.Listed != count.Feats)
            {
                throw new DegreesException($"{where}: a class's counts differ: {count.LoreBookmarks} flavor bookmarks and {count.LoreHeadings} headings; {count.RacketBookmarks} subclass bookmarks, {count.Rackets} headings and {count.Subclasses} entries; " +
                    $"{count.BandBookmarks} level bookmarks and {count.Bands} bands ({emptyBands} with no feat); {count.FeatHeaders} feat headers, {count.TraitLines} trait lines, {count.Markers} level markers, {count.Feats} entries, " +
                    $"and {count.Listed} in the printed list ({scan.ByNameHeaders} lists; as sets of name and level they {(listed.ToHashSet().SetEquals(parsedPairs) ? "agree" : "differ")}).");
            }

            result.Add(count);
        }

        return result;
    }

    private static string BlockTextOf(Block b) => SpanText.Normalize(string.Join(' ', b.Lines)).Trim();

    /// <summary>
    /// Replaces the names a class's grants hold (<see cref="EntriesStage.GrantToken"/>) by the final ids of the feats and spells they name. A feat that several classes print is the one whose trait line
    /// carries the granting class's key; a name that matches no entry, or more than one, makes no grant (the text of the feature still says it): it is returned, by the kind and the slug of the name.
    /// </summary>
    public static (int Resolved, List<string> Left) ResolveGrantNames(List<FinalEntry> final)
    {
        var resolved = 0;
        var left = new List<string>();
        var classKeys = final.Where(e => e.Type == "class").ToDictionary(e => e.Id, e => EntriesStage.Slug(e.Name), StringComparer.Ordinal);
        var byName = final.Where(e => e.Type is "spell" or "feat").GroupBy(e => (e.Type, EntriesStage.Slug(e.Name))).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var entry in final.Where(e => e.Features.Count > 0))
        {
            var classKey = entry.Type == "class" ? EntriesStage.Slug(entry.Name)
                : entry.Raw.FirstOrDefault(r => r.Label == "class") is { } link && classKeys.TryGetValue(link.Text.Trim('"'), out var key) ? key : string.Empty;
            foreach (var feature in entry.Features.Where(f => f.Adds.Any(a => EntriesStage.GrantToken.IsMatch(a))))
            {
                var adds = new List<string>();
                foreach (var add in feature.Adds)
                {
                    var token = EntriesStage.GrantToken.Match(add);
                    if (!token.Success)
                    {
                        adds.Add(add);
                        continue;
                    }

                    var type = token.Groups["kind"].Value == "feat" ? "feat" : "spell";
                    var slug = EntriesStage.Slug(token.Groups["name"].Value);
                    var found = byName.TryGetValue((type, slug), out var candidates) ? candidates : new List<FinalEntry>();
                    if (found.Count > 1 && type == "feat")
                    {
                        found = found.Where(f => f.Traits.Contains(classKey)).ToList();
                    }

                    if (found.Count == 1)
                    {
                        adds.Add(add.Replace(token.Value, found[0].Id, StringComparison.Ordinal));
                        resolved++;
                    }
                    else
                    {
                        left.Add(type + ":" + slug);
                    }
                }

                feature.Adds.Clear();
                feature.Adds.AddRange(adds);
            }
        }

        return (resolved, left);
    }
}
