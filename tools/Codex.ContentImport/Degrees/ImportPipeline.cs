using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// The denylist waivers (<c>Data/degrees-denylist-waivers.txt</c>): game terms a person has reviewed and decided the packs may say although a left-out section
/// capitalizes them too. One lowercase word per line, `#` lines are comments. Each line is a reviewed decision, not a convenience.
/// </summary>
public static class Waivers
{
    private const string RetiredPrefix = "retired-pair ";

    private const string CompoundPrefix = "compound ";

    public static HashSet<string> Parse(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith(RetiredPrefix, StringComparison.Ordinal) && !l.StartsWith(CompoundPrefix, StringComparison.Ordinal))
            .Select(l => l.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public static HashSet<string> Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-denylist-waivers.txt"));

    /// <summary>
    /// The compounds a person has allowed (lines <c>compound &lt;word&gt; &lt;next word&gt;</c>): a word that stays on the denylist, because the book capitalizes it in a name the pack must never say, is allowed in the pack
    /// only where the word after it is the second one (one feat's name). The word gate honours it for those two adjacent words and for no other place.
    /// </summary>
    public static HashSet<(string Word, string Next)> ParseCompounds(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(CompoundPrefix, StringComparison.Ordinal))
            .Select(l => l[CompoundPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2).Select(p => (p[0].ToLowerInvariant(), p[1].ToLowerInvariant())).ToHashSet();

    public static HashSet<(string Word, string Next)> EmbeddedCompounds() => ParseCompounds(BooksConfig.EmbeddedYaml("degrees-denylist-waivers.txt"));

    /// <summary>
    /// The pair hashes a person has retired (lines <c>retired-pair &lt;64 hex digits&gt; &lt;one-line reason&gt;</c>, no words): a hash that nothing produces any more (an artifact of an intermediate build), which
    /// <c>denylist --fresh</c> may drop, saying so. Any other unexplained drop still stops the run.
    /// </summary>
    public static Dictionary<string, string> ParseRetired(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(RetiredPrefix, StringComparison.Ordinal)))
        {
            var parts = line[RetiredPrefix.Length..].Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(parts[0], "^[0-9a-f]{64}$") || parts[1].Length == 0)
            {
                throw new DegreesException("A retired-pair line of the waivers file is not a 64-digit hash and a reason.");
            }

            result[parts[0]] = parts[1];
        }

        return result;
    }

    public static Dictionary<string, string> EmbeddedRetired() => ParseRetired(BooksConfig.EmbeddedYaml("degrees-denylist-waivers.txt"));
}

/// <summary>What stages 3 to 5 made from one book's taken rows.</summary>
public sealed record PipelineOutput(List<TakenRange> Ranges, List<ParsedEntry> Parsed, List<FinalEntry> Final, RedactionReport Redaction, List<ReportRow> Report)
{
    /// <summary>The grants of the classes that name a feat or a spell: how many became an id, and which names (kind and slug) the pack has no entry for, so they stay text.</summary>
    public (int Resolved, List<string> Left) GrantsByName { get; init; } = (0, new List<string>());

    /// <summary>The curriculum lines of the subclasses (a wizard's schools): how many subclasses have one and how many spell names they list, every one a spell of the pack (see <see cref="ImportPipeline.CheckCurriculum"/>).</summary>
    public (int Subclasses, int Names) Curriculum { get; init; } = (0, 0);
}

/// <summary>The part of the run that needs no PDF: blocks in, entries and report rows out. The tests drive it with synthetic books.</summary>
public static partial class ImportPipeline
{
    /// <summary>The options the blocks stage needs from the take-list and the spine: how many columns a taken page may have, and where sidebars may be.</summary>
    public static BlocksOptions OptionsFor(IReadOnlyList<SpineNode> spine, IEnumerable<TakeRow> rows)
    {
        var columns = new Dictionary<int, int>();
        var pinned = new Dictionary<int, double>();
        var bands = new Dictionary<int, IReadOnlyList<IReadOnlyList<double>>>();
        var art = new Dictionary<int, IReadOnlyList<IReadOnlyList<double>>>();
        foreach (var row in rows)
        {
            var pages = TakeStage.PagesOf(spine, row);
            foreach (var page in pages)
            {
                columns[page] = Math.Max(columns.GetValueOrDefault(page), row.Columns);
            }

            foreach (var (page, x) in row.GutterAt)
            {
                if (!pages.Contains(page) || x <= 0 || pinned.ContainsKey(page) || row.Columns != 2)
                {
                    throw new DegreesException($"Take-list row for chapter {row.Chapter}: gutterAt names a page the row does not cover or another row pins already, a row that does not have two columns, or an x that is not on a page (the blocks stage also holds x to the page's width).");
                }

                pinned[page] = x;
            }

            foreach (var (page, strips) in row.Bands)
            {
                var ok = row.Parser == "rules" && pages.Contains(page) && !pinned.ContainsKey(page) && !bands.ContainsKey(page) && strips.Count > 0
                    && strips.All(s => s.Count == 3 && s[0] > s[1] && s[2] > 0) && !spine.Any(n => n.Sidebar && page >= n.PageStart && page <= n.PageEnd);
                if (!ok)
                {
                    throw new DegreesException($"Take-list row for chapter {row.Chapter}: bands name a page the row does not cover (a rules row), a page a sidebar bookmark covers, a page that is pinned or banded already, or a strip that is not [top, bottom, x] with the top above the bottom.");
                }

                // A word in the overlap of two strips would go to the upper one and cut the lower one short: the strips must not overlap.
                var byTop = strips.OrderByDescending(s => s[0]).ToList();
                for (var i = 0; i + 1 < byTop.Count; i++)
                {
                    if (byTop[i][1] < byTop[i + 1][0])
                    {
                        throw new DegreesException($"Take-list row for chapter {row.Chapter}: the strips of bands on page {page} overlap (the bottom of a strip must not be below the top of the next one).");
                    }
                }

                bands[page] = strips.Select(s => (IReadOnlyList<double>)s).ToList();
            }

            if (row.BodyAsRules.Any(page => row.Parser != "rules" || !pages.Contains(page)))
            {
                throw new DegreesException($"Take-list row for chapter {row.Chapter}: bodyAsRules names a page the row does not cover, or a row that is not a rules row.");
            }

            foreach (var (page, regions) in row.ArtRegions)
            {
                var ok = row.Parser == "rules" && pages.Contains(page) && !art.ContainsKey(page) && regions.Count > 0 && regions.All(r => r.Count == 4 && r[0] < r[2] && r[1] > r[3]);
                if (!ok)
                {
                    throw new DegreesException($"Take-list row for chapter {row.Chapter}: artRegions name a page the row does not cover (a rules row), a page another row names already, or a rectangle that is not [left, top, right, bottom] with the left before the right and the top above the bottom.");
                }

                art[page] = regions.Select(r => (IReadOnlyList<double>)r).ToList();
            }
        }

        var sidebars = spine.Where(n => n.Sidebar).SelectMany(n => Enumerable.Range(n.PageStart, n.PageEnd - n.PageStart + 1)).ToHashSet();
        var starts = spine.Where(n => n.Sidebar).GroupBy(n => n.PageStart).ToDictionary(g => g.Key, g => g.Count());
        var titles = spine.Where(n => n.Sidebar).GroupBy(n => n.PageStart)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.SelectMany(n => SidebarTitles(n.Title)).ToList());
        // The pages an items row names for its stat tables, with the column label lists a header row there may carry.
        var tables = new Dictionary<int, List<IReadOnlyList<string>>>();
        var gearTables = new Dictionary<int, List<IReadOnlyList<string>>>();
        var twoUp = new HashSet<int>();
        foreach (var row in rows.Where(r => r.Parser == "items"))
        {
            // A row of kind gear is carved by the gear reader, which also knows the pages whose table is set in two panels.
            var target = row.ItemKind == "gear" ? gearTables : tables;
            foreach (var path in row.Tables)
            {
                var node = SpineStage.FindUnique(spine, path) ?? throw new DegreesException("An items row names a bookmark path for its tables that is not in the book.");
                for (var page = node.PageStart; page <= node.PageEnd; page++)
                {
                    if (!target.TryGetValue(page, out var sets))
                    {
                        target[page] = sets = new List<IReadOnlyList<string>>();
                    }

                    sets.AddRange(row.TableColumns.Where(c => !sets.Any(s => s.SequenceEqual(c))));
                }
            }

            foreach (var path in row.TwoUp)
            {
                var node = SpineStage.FindUnique(spine, path) ?? throw new DegreesException("An items row names a bookmark path for its two-panel table that is not in the book.");
                twoUp.UnionWith(Enumerable.Range(node.PageStart, node.PageEnd - node.PageStart + 1));
            }
        }

        // The sidebars the take-list takes as rules: their titles' keys, so the blocks stage keeps them apart instead of dropping them.
        var takenSidebars = new Dictionary<int, IReadOnlySet<string>>();
        foreach (var path in rows.SelectMany(r => r.Sidebars))
        {
            var (node, title) = ResolveSidebar(spine, path) ?? throw new DegreesException("A rules row names a sidebar path that is not one sidebar of the book.");

            // Taken on the bookmark's own pages only, so a sidebar of the same title elsewhere stays a dropped one.
            for (var page = node.PageStart; page <= node.PageEnd; page++)
            {
                var keys = takenSidebars.TryGetValue(page, out var held) ? (HashSet<string>)held : new HashSet<string>(StringComparer.Ordinal);
                keys.Add(BlocksStage.SidebarTitleSet.Key(title));
                takenSidebars[page] = keys;
            }
        }

        return new BlocksOptions
        {
            TakenSidebarKeys = takenSidebars,
            MaxColumns = page => columns.GetValueOrDefault(page, 2),
            PinnedGutters = pinned,
            Bands = bands,
            ArtRegions = art,
            SidebarFillsColumn = rows.SelectMany(r => r.SidebarFillsColumn).ToHashSet(),
            SidebarPages = sidebars,
            SidebarStarts = starts,
            SidebarTitles = titles,
            StatTablePages = tables.ToDictionary(t => t.Key, t => (IReadOnlyList<IReadOnlyList<string>>)t.Value),
            GearTablePages = gearTables.ToDictionary(t => t.Key, t => new GearPage(t.Value, twoUp.Contains(t.Key))),
        };
    }

    /// <summary>
    /// The one sidebar a take-list path names: the path of a sidebar bookmark that holds one sidebar, or the path of a bookmark of several ("Sidebars: X &amp; Y") with the title of the one taken as its last
    /// segment. The partner stays a dropped sidebar. Null when the path is neither.
    /// </summary>
    public static (SpineNode Node, string Title)? ResolveSidebar(IReadOnlyList<SpineNode> spine, IReadOnlyList<string> path)
    {
        var node = SpineStage.FindUnique(spine, path);
        if (node is { Sidebar: true })
        {
            var named = SidebarTitles(node.Title).ToList();
            return named.Count == 1 ? (node, named[0]) : null;
        }

        if (node == null && path.Count > 1 && SpineStage.FindUnique(spine, path.Take(path.Count - 1).ToList()) is { Sidebar: true } several)
        {
            var want = BlocksStage.SidebarTitleSet.Key(path[^1]);
            var hits = SidebarTitles(several.Title).Where(t => BlocksStage.SidebarTitleSet.Key(t) == want).ToList();
            return hits.Count == 1 ? (several, hits[0]) : null;
        }

        return null;
    }

    /// <summary>
    /// The sidebars one sidebar bookmark names: "Sidebar: X" is one, "Sidebars: X &amp; Y" is two. A single sidebar whose own title holds an ampersand would be read as two, and
    /// its page would be flagged (loud, so left as it is).
    /// </summary>
    public static IEnumerable<string> SidebarTitles(string bookmarkTitle)
    {
        var colon = bookmarkTitle.IndexOf(':', StringComparison.Ordinal);
        var rest = colon >= 0 ? bookmarkTitle[(colon + 1)..] : bookmarkTitle;
        // "Sidebars: X and Y" names two as well, when no ampersand does (a title that says "and" inside one sidebar's name, as in "A and B & C", is split at the ampersand only).
        var plural = colon >= 0 && bookmarkTitle[..colon].TrimEnd().EndsWith('s') && !rest.Contains(" & ", StringComparison.Ordinal);
        var parts = rest.Split(plural ? new[] { " and " } : new[] { " & " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A split on "and" is a guess (a single title may hold the word): its parts must match their headings exactly, or the page is flagged (see BlocksStage.SidebarTitleSet).
        return plural ? parts.Select(p => "=" + p) : parts;
    }

    public static PipelineOutput Run(BlocksResult blocks, IReadOnlyList<SpineNode> spine, IReadOnlyList<TakeRow> rows, RedactionTable redactions, string book, IReadOnlyDictionary<string, string>? shippedIds = null)
    {
        var ranges = TakeStage.Resolve(spine, blocks, rows);
        CheckEntryCounts(spine, blocks, ranges);
        CheckGlossaryCounts(blocks, ranges);
        CheckLevelCounts(blocks, ranges);
        CheckRuleSectionCounts(spine, blocks, ranges);
        CheckFoldCounts(spine, blocks, ranges);
        CheckBookmarkCoverage(spine, ranges);
        CheckTableLabels(spine, blocks, rows);
        var parsed = EntriesStage.Run(blocks, ranges, book, shippedIds, spine);
        CheckItemCounts(spine, blocks, ranges, parsed);
        CheckAncestryCounts(spine, blocks, ranges, parsed);
        CheckAncestryList(blocks, parsed);
        CheckVersatileCounts(spine, blocks, ranges, parsed);
        CheckClassCounts(spine, blocks, ranges, parsed);
        CheckBackgroundCounts(blocks, ranges, parsed);
        CheckCompanionCounts(spine, blocks, ranges, parsed);
        CheckFamiliarCounts(spine, blocks, ranges, parsed);
        CheckArchetypeCounts(spine, blocks, ranges, parsed);
        CheckActionCounts(spine, blocks, ranges, parsed);
        CheckSidebarDegrees(blocks, ranges, parsed);
        CheckSpellCounts(blocks, ranges, parsed);
        CheckSpellDegrees(blocks, ranges, parsed);
        CheckRankLists(blocks, ranges, parsed);

        // Only this book's rows apply, so one book's rows are never "unmatched" in the other's run.
        var scoped = new RedactionTable { Normalization = redactions.Normalization, Rows = redactions.Rows.Where(r => r.Book == null || r.Book == book).ToList() };
        var redactable = parsed.Select(e => new RedactionEntry(e.Id, e.RedactionFields())).ToList();
        var report = RedactionStage.Run(redactable, scoped);
        var kept = redactable.ToDictionary(e => e.Id, StringComparer.Ordinal);
        // A redaction row that replaces an entry's whole name (a name the setting coined, said in plain words) gives the entry the id of its new name: an id is a slug of the name, and the old one would
        // carry the word the row takes out. Rows name an entry by its parsed id, so this only happens here, after them.
        string FinalId(ParsedEntry e) => kept[e.Id].Fields["name"] == e.Name ? e.Id : EntriesStage.Slug(kept[e.Id].Fields["name"]);
        var final = parsed.Where(e => kept.ContainsKey(e.Id)).Select(e => new FinalEntry
        {
            Id = FinalId(e),
            Type = e.Type,
            Folder = e.Folder,
            Chapter = e.Chapter,
            Source = e.Source,
            Category = e.Category,
            Approved = e.Approved,
            RowNumber = e.RowNumber,
            Name = kept[e.Id].Fields["name"],
            Description = kept[e.Id].Fields["description"],
            Level = e.Level,
            Kind = e.Kind,
            Traditions = e.Traditions.ToList(),
            Actions = e.Actions,
            // A feat of a versatile heritage carries the heritage's key in its trait line: the key of the heritage's final name (a heritage a redaction row renamed takes its feats' trait with it).
            Traits = e.TraitOwner == null ? e.Traits.ToList()
                : e.Traits.Select(t => t == EntriesStage.Slug(e.TraitOwner.Name) ? EntriesStage.Slug(kept[e.TraitOwner.Id].Fields["name"]) : t).ToList(),
            Skill = e.Skill,
            Proficiency = e.Proficiency,
            Extracted = e.Extracted.Select(x => new LabelledText(x.Label, kept[e.Id].Fields[x.Label])).ToList(),
            Tags = e.Tags.ToList(),
            Raw = e.Raw.ToList(),
            // A subclass's own feature row is named like the subclass (a choice with one row): when a row has renamed the entry, the row takes the new name.
            Features = e.Features.Select(f => new FinalFeature(e.Type == "subclass" && f.Name == e.Name ? kept[e.Id].Fields["name"] : f.Name, f.Level, kept[e.Id].Fields[ParsedEntry.FeatureField(f)], f.Adds.ToList(), f.Uses)).ToList(),
        }).ToList();

        // The traits a versatile heritage gives (a heritage that names no ancestry): the words of its "You gain the X trait" sentence, read from the description after redaction, so a renamed heritage says its new key.
        foreach (var heritage in final.Where(e => e.Type == "heritage" && e.Raw.All(r => r.Label != "ancestry")))
        {
            var gain = Regex.Match(heritage.Description, @"You gain the (?<l>[a-z-]+ trait(?:,? (?:and )?(?:the )?[a-z-]+ trait)*)", RegexOptions.CultureInvariant);
            var granted = gain.Success ? Regex.Matches(gain.Groups["l"].Value, @"(?<t>[a-z-]+) trait").Select(m => m.Groups["t"].Value).ToList() : new List<string>();
            if (granted.Count == 0)
            {
                continue; // a printing whose heritage says it otherwise has no field; the pack's tests pin that every versatile heritage has one
            }

            heritage.Raw.Add(new LabelledText("grantsTraits", "[" + string.Join(", ", granted.Select(t => "\"" + t + "\"")) + "]"));
        }

        if (final.GroupBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new DegreesException($"{repeated.Count()} entries share one id ({repeated.Key}) after a redaction row renamed one of them.");
        }

        // A subclass names its class the same way, and the same rows could rename the class since.
        var classIds = final.Where(e => e.Type == "class").Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (final.Where(e => e.Type == "subclass" && e.Raw.Any(r => r.Label == "class")).Any(s => !classIds.Contains(s.Raw.Single(r => r.Label == "class").Text.Trim('"'))))
        {
            throw new DegreesException("A subclass names a class whose final id is another (a redaction row renamed the class), so the link would match nothing.");
        }

        // A heritage names its ancestry by the id the ancestry had when the stage ran: if a redaction row has renamed the ancestry since, the link would match nothing, with no error.
        var ancestryIds = final.Where(e => e.Type == "ancestry").Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (final.Where(e => e.Type == "heritage" && e.Raw.Any(r => r.Label == "ancestry")).Any(h => !ancestryIds.Contains(h.Raw.Single(r => r.Label == "ancestry").Text.Trim('"'))))
        {
            throw new DegreesException("A heritage names an ancestry whose final id is another (a redaction row renamed the ancestry), so the link would match nothing.");
        }

        // An archetype names its class by id: the class of its own name. A feat names its archetype by the id the archetype had when the stage ran, which a redaction row could have changed since.
        foreach (var archetype in final.Where(e => e.Type == "archetype"))
        {
            var classes = final.Where(e => e.Type == "class" && EntriesStage.Slug(e.Name) == EntriesStage.Slug(archetype.Name)).ToList();
            if (classes.Count != 1)
            {
                throw new DegreesException($"An archetype (entry {archetype.RowNumber}) has {classes.Count} classes of its name, not one, so its class could not be linked.");
            }

            archetype.Raw.Insert(0, new LabelledText("class", "\"" + classes[0].Id + "\""));
        }

        var archetypeIds = final.Where(e => e.Type == "archetype").Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (final.Where(e => e.Type == "feat" && e.Raw.Any(r => r.Label == "archetype")).Any(f => !archetypeIds.Contains(f.Raw.Single(r => r.Label == "archetype").Text.Trim('"'))))
        {
            throw new DegreesException("A feat names an archetype whose final id is another (a redaction row renamed the archetype), so the link would match nothing.");
        }

        // A class's grants by name become grants by id, now that every id is final.
        var named = ResolveGrantNames(final);
        var curriculum = CheckCurriculum(final);
        return new PipelineOutput(ranges, parsed, final, report, ReportRows(blocks, spine, rows, redactions, book, parsed, final, ranges)) { GrantsByName = named, Curriculum = curriculum };
    }

    /// <summary>
    /// Counts the entries of every taken <c>entries</c> range two ways, from the document: the bookmark children of the taken section (minus the sidebar bookmarks) and the
    /// blocks in the entry-header font in the range. They must agree, or the row says why in <see cref="TakeRow.CountsDiffer"/>; otherwise the run fails with both counts.
    /// A section with no child bookmarks has nothing to compare (header font only). Returns the (bookmarks, headers) pairs for the ranges it compared.
    /// </summary>
    public static List<(int Bookmarks, int Headers)> CheckEntryCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges)
    {
        var compared = new List<(int, int)>();
        foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "entries"))
        {
            var node = range.Node;
            // A row with groups holds a tree of headings (a class, then a domain or an order under it): every bookmark below the section is one heading, in the section font or the entry-header font.
            var groups = range.Row.Groups;
            var children = spine.Where(n => n.Path.Length > node.Path.Length && (groups || n.Depth == node.Depth + 1 && n.Path.Length == node.Path.Length + 1)
                && n.Path.Take(node.Path.Length).SequenceEqual(node.Path) && !n.Sidebar).Count();
            if (children == 0)
            {
                continue;
            }

            var headers = blocks.Blocks.Skip(range.Start).Take(range.End - range.Start).Count(b => b.Role == "entry-header" || (groups && b.Role == "section"));
            compared.Add((children, headers));
            if (children != headers && string.IsNullOrWhiteSpace(range.Row.CountsDiffer))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the taken section has {children} bookmark children (sidebars left out) but {headers} entry-header blocks in its range, and the row gives no countsDiffer reason.");
            }
        }

        return compared;
    }

    /// <summary>
    /// Counts the trait entries of every taken <c>glossary</c> range two ways, from the document: the definitions and the pointer-only trait lines the parser finds at entry
    /// starts, and every occurrence of the word "(trait)" in the range. They must agree, or the row says why in <see cref="TakeRow.CountsDiffer"/>; otherwise the run fails
    /// with both counts. Returns the (definitions, pointers, occurrences) of each range.
    /// </summary>
    public static List<(int Definitions, int Pointers, int Tokens)> CheckGlossaryCounts(BlocksResult blocks, IEnumerable<TakenRange> ranges)
    {
        var compared = new List<(int, int, int)>();
        foreach (var range in ranges.Where(r => r.Row.Parser == "glossary"))
        {
            var scan = EntriesStage.ScanGlossary(range.BlocksIn(blocks), new HashSet<string>(StringComparer.Ordinal));
            compared.Add((scan.Definitions.Count, scan.Pointers, scan.TraitTokens));
            if (scan.Definitions.Count + scan.Pointers != scan.TraitTokens && string.IsNullOrWhiteSpace(range.Row.CountsDiffer))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the glossary range has {scan.Definitions.Count} trait definitions and {scan.Pointers} pointer-only trait lines at entry starts, but {scan.TraitTokens} occurrences of the trait mark, and the row gives no countsDiffer reason.");
            }
        }

        return compared;
    }

    /// <summary>
    /// Counts the entries of every taken <c>entries</c> range whose headers end in a word and a level (<see cref="TakeRow.LevelWord"/>) three ways, from the document, because such a section has
    /// no child bookmarks: the header-font blocks of the header shape, the trait-line blocks (one follows each header), and the lines in the range that end in the capitalized word and a number
    /// (the level markers, read line by line, whatever block they sit in). They must agree, or the row says why in <see cref="TakeRow.CountsDiffer"/>; otherwise the run fails with the three counts.
    /// Returns the (headers, trait lines, level markers) of each range.
    /// </summary>
    public static List<(int Headers, int TraitLines, int Markers, int TableRows)> CheckLevelCounts(BlocksResult blocks, IEnumerable<TakenRange> ranges)
    {
        var compared = new List<(int, int, int, int)>();
        foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "entries" && r.Row.LevelWord != null))
        {
            var slice = range.BlocksIn(blocks);
            var marker = new Regex(@"\s(?:" + string.Join('|', range.Row.LevelWords.Select(w => Regex.Escape(w.ToUpperInvariant()))) + @")\s+\d+$", RegexOptions.CultureInvariant);
            var headers = slice.Count(range.Row.IsHeader);
            // A box's own stat block has a trait line under a heading that is no entry's: it is no entry's trait line.
            var inBox = EntriesStage.BoxIndexes(range.Row, slice);
            // A trait line that wraps is two tag blocks in a row (the second is the rest of the first), so a run of them is one.
            var traitLines = Enumerable.Range(0, slice.Count).Count(k => slice[k].Role == "tag" && !inBox.Contains(k) && (k == 0 || slice[k - 1].Role != "tag"));
            var markers = slice.SelectMany(b => b.Lines).Count(l => marker.IsMatch(SpanText.Normalize(l)));

            // The overview tables before the first header list every entry with its level: a count that no header, trait line or classification step touches. Not every chapter has them.
            var table = LeadTableRows(blocks, range);
            var pairs = slice.Where(range.Row.IsHeader).Select(b => HeaderPair(b, range.Row)).ToHashSet();
            var tableRows = table.Count;
            var tableAgrees = tableRows == 0 || table.SetEquals(pairs);
            compared.Add((headers, traitLines, markers, tableRows));
            if ((headers != traitLines || headers != markers || !tableAgrees) && string.IsNullOrWhiteSpace(range.Row.CountsDiffer))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the range has {headers} header blocks, {traitLines} trait-line blocks and {markers} level markers, " +
                    $"and its overview tables list {tableRows} entries ({table.Except(pairs).Count()} not among the headers, {pairs.Except(table).Count()} headers not in the tables), and the row gives no countsDiffer reason.");
            }
        }

        return compared;
    }

    /// <summary>
    /// For every taken own-text path of a <c>rules</c> row (a section whose own text comes before its first child section): the section's non-sidebar bookmark children against the headings
    /// of the right font in its range, up to the last child's own heading. A child is a heading in the section font under a chapter and in the entry-header font under a section. They
    /// must be equal: a bookmark with no heading, or a heading with no bookmark (a section the take-list would have swallowed whole), fails the run. Returns the (children, headings) pairs.
    /// </summary>
    public static List<(int Children, int Headings)> CheckRuleSectionCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IEnumerable<TakenRange> ranges)
    {
        var compared = new List<(int, int)>();
        // A row that gives a countsDiffer reason is not compared (the chapter's own text of the equipment chapter, whose section headings share a font with their children's titles: see the row).
        foreach (var range in ranges.Where(r => r.Own && r.Row.Parser == "rules" && string.IsNullOrWhiteSpace(r.Row.CountsDiffer)))
        {
            var node = range.Node;
            var children = spine.Where(n => n.Depth == node.Depth + 1 && n.Path.Length == node.Path.Length + 1 && n.Path.Take(node.Path.Length).SequenceEqual(node.Path) && !n.Sidebar).ToList();
            if (children.Count == 0)
            {
                continue;
            }

            var role = node.Depth == 0 ? "section" : "entry-header";
            // The children's titles are in the font of their own first heading (the equipment chapter sets a section's children in the section font under a chapter section, and in the entry-header font under a section).
            var first = BlocksStage.SidebarTitleSet.Key(children[0].Title);
            var firstHeading = blocks.Blocks.Skip(range.End).TakeWhile(b => b.Page <= children[0].PageStart).FirstOrDefault(b => b.Page == children[0].PageStart && b.Role is "section" or "entry-header" && BlocksStage.SidebarTitleSet.Key(string.Join(' ', b.Lines)) == first);
            if (firstHeading != null)
            {
                role = firstHeading.Role;
            }

            var last = BlocksStage.SidebarTitleSet.Key(children[^1].Title);
            var headings = 0;
            var found = false;
            for (var i = range.End; i < blocks.Blocks.Count && !found; i++)
            {
                var b = blocks.Blocks[i];
                if (b.Role == role)
                {
                    headings++;
                    found = BlocksStage.SidebarTitleSet.Key(string.Join(' ', b.Lines)) == last;
                }
            }

            compared.Add((children.Count, headings));
            if (!found || children.Count != headings)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, own-text path {range.PathNumber - range.Row.Paths.Count}: the section has {children.Count} bookmark children (sidebars left out) but {headings} {role} headings up to the last child's, so a heading has no bookmark or a bookmark no heading.");
            }
        }

        return compared;
    }

    /// <summary>
    /// For every taken <c>entries</c> range whose row names a <see cref="TakeRow.ListHeading"/>: the (name, rank) lines of the table under that heading (the book's own list of the row's entries by rank)
    /// against the (name, rank) of the parsed entries. The two sets must be equal, or the run fails. Returns the (entries, listed) counts of each range.
    /// </summary>
    public static List<(int Entries, int Listed)> CheckRankLists(BlocksResult blocks, IEnumerable<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var compared = new List<(int, int)>();
        foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "entries" && r.Row.ListHeading != null))
        {
            var want = BlocksStage.SidebarTitleSet.Key(range.Row.ListHeading!);
            var headings = Enumerable.Range(0, blocks.Blocks.Count).Where(i => blocks.Blocks[i].Role == "block-header" && BlocksStage.SidebarTitleSet.Key(string.Join(' ', blocks.Blocks[i].Lines)) == want).ToList();
            if (headings.Count != 1)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the list heading of the row is found {headings.Count} times in the book, so its table is not known.");
            }

            var listed = new HashSet<(string Name, int Rank)>();
            var lines = 0;
            for (var i = headings[0] + 1; i < blocks.Blocks.Count && blocks.Blocks[i].Role == "body"; i++)
            {
                foreach (var line in blocks.Blocks[i].Lines.Select(l => SpanText.Normalize(l).Trim()))
                {
                    var m = Regex.Match(line, @"^(?<rank>\d{1,2}) (?<name>\p{L}[\p{L} '’-]*)$", RegexOptions.CultureInvariant);
                    if (m.Success)
                    {
                        lines++;
                        listed.Add((EntriesStage.Slug(m.Groups["name"].Value), int.Parse(m.Groups["rank"].Value, System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
            }

            var mine = parsed.Where(e => e.RowNumber == range.RowNumber && e.Type == range.Row.Target).Select(e => (EntriesStage.Slug(e.Name), e.Level ?? 0)).ToHashSet();
            compared.Add((mine.Count, listed.Count));
            if (lines != listed.Count || !mine.SetEquals(listed))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the book's list by rank has {listed.Count} entries ({lines} lines) and the parsed entries are {mine.Count}: {listed.Except(mine).Count()} listed are not among them and {mine.Except(listed).Count()} parsed are not listed.");
            }
        }

        return compared;
    }

    /// <summary>What <see cref="CheckSpellCounts"/> counted.</summary>
    /// <param name="Headers">Spell headers (block-header font, the shape of a name, an optional icon, the kind word and a rank).</param>
    /// <param name="TraitLines">Trait lines directly under a header.</param>
    /// <param name="TraditionLines">Lines that open with the traditions label.</param>
    /// <param name="Listed">Distinct (tradition, name, rank) of the four tradition lists.</param>
    /// <param name="Parsed">Distinct (tradition, name, rank) of the parsed spells.</param>
    /// <param name="PerTradition">The number of spells each tradition's list holds, by tradition key.</param>
    public sealed record SpellCounts(int Headers, int TraitLines, int TraditionLines, int Listed, int Parsed, SortedDictionary<string, int> PerTradition, int OnNoList, int ArtLabels = 0);

    private static readonly Regex ListHeading = new(@"^(?<tradition>Arcane|Divine|Occult|Primal) (?:(?<cantrips>Cantrips)|(?<rank>\d+)(?:st|nd|rd|th)-Rank Spells)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Counts the spells of a <c>spells</c> row three ways from the document and checks them against four lists. The headers of the range, the trait lines below them and the lines that open
    /// with the traditions label must agree (or the row says why in <see cref="TakeRow.CountsDiffer"/>). The four tradition lists, which the row of <c>spelllists</c> reads past, give
    /// (tradition, name, rank) for every spell on each list; the parsed spells give the same triples from their own traditions line and header. The two sets must be equal for each
    /// tradition, and a spell on no list fails the run unless the row says why. Nothing here is decided by the spell parser's text handling beyond the header and the traditions line.
    /// </summary>
    public static SpellCounts? CheckSpellCounts(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        // Only the row of the spells the four lists name is counted against them: the focus spells and the rituals are on no tradition's list.
        var spellRanges = ranges.Where(r => !r.Own && r.Row.Parser == "entries" && r.Row.Target == "spell" && r.Row.OnTraditionLists).ToList();
        var listRanges = ranges.Where(r => r.Row.Parser == "spelllists").ToList();
        if (spellRanges.Count == 0 && listRanges.Count == 0)
        {
            return null;
        }

        if (spellRanges.Count == 0 || listRanges.Count == 0)
        {
            throw new DegreesException("A spells row needs the tradition lists to count against (a spelllists row), and the lists need spells.");
        }

        int headers = 0, traitLines = 0, traditionLines = 0, art = 0;
        foreach (var range in spellRanges)
        {
            var slice = range.BlocksIn(blocks);
            // The labels of the page's art (a lone line in capitals set in from the column's edge, in the bold face of a header): skipped, and counted here to be pinned.
            art += slice.Count(b => EntriesStage.IsStrayLabel(b, slice, range.Row));
            var h = slice.Count(range.Row.IsHeader);
            var inBox = EntriesStage.BoxIndexes(range.Row, slice);
            var t = Enumerable.Range(0, slice.Count).Count(k => slice[k].Role == "tag" && !inBox.Contains(k) && (k == 0 || slice[k - 1].Role != "tag"));
            var l = slice.Where(b => b.Info != null).SelectMany(b => b.Info!).Count(i => SpanText.Normalize(i.Label).Trim().ToLowerInvariant() == "traditions");
            headers += h;
            traitLines += t;
            traditionLines += l;
            if ((h != t || h != l) && string.IsNullOrWhiteSpace(range.Row.CountsDiffer))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the range has {h} spell headers, {t} trait lines below them and {l} traditions lines, and the row gives no countsDiffer reason.");
            }
        }

        // The lists: one range per tradition, a heading per rank, and a bold name at the start of each entry's line.
        var listed = new Dictionary<string, HashSet<(string Name, string Rank)>>(StringComparer.Ordinal);
        foreach (var range in listRanges)
        {
            var tradition = range.Node.Title.Split(' ')[0].ToLowerInvariant();
            if (!EntriesStage.TraditionKeys.Contains(tradition))
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: a tradition list's title does not start with one of the four traditions.");
            }

            var set = listed.TryGetValue(tradition, out var existing) ? existing : listed[tradition] = new HashSet<(string, string)>();
            string? rank = null;
            foreach (var b in range.BlocksIn(blocks))
            {
                if (b.Role == "entry-header")
                {
                    var m = ListHeading.Match(SpanText.Normalize(string.Join(' ', b.Lines)));
                    if (!m.Success || !string.Equals(m.Groups["tradition"].Value, tradition, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: a heading of a tradition list is not a rank of that tradition.");
                    }

                    rank = m.Groups["cantrips"].Success ? "c" : m.Groups["rank"].Value;
                    continue;
                }

                if (rank == null)
                {
                    throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: text of a tradition list comes before its first rank heading.");
                }

                for (var i = 0; b.Info != null && i < b.Lines.Count; i++)
                {
                    var name = SpanText.Normalize(b.Info[i].Label).Trim();
                    if (name.Length > 0 && !set.Add((EntriesStage.Slug(name), rank)))
                    {
                        throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: a spell is listed twice at one rank of one tradition.");
                    }
                }
            }
        }

        var listRows = spellRanges.Select(r => r.RowNumber).ToHashSet();
        var spells = parsed.Where(e => e.Type == "spell" && listRows.Contains(e.RowNumber)).ToList();
        var onNoList = spells.Count(e => e.Traditions.Count == 0);
        if (onNoList > 0 && spellRanges.All(r => string.IsNullOrWhiteSpace(r.Row.CountsDiffer)))
        {
            throw new DegreesException($"{onNoList} parsed spell(s) have no tradition, so they are on no list, and the row gives no countsDiffer reason.");
        }

        var perTradition = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var parsedTotal = 0;
        foreach (var tradition in EntriesStage.TraditionKeys)
        {
            var mine = spells.Where(e => e.Traditions.Contains(tradition)).Select(e => (EntriesStage.Slug(e.Name), e.Kind == "cantrip" ? "c" : (e.Level ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToHashSet();
            var theirs = listed.GetValueOrDefault(tradition) ?? new HashSet<(string, string)>();
            perTradition[tradition] = theirs.Count;
            parsedTotal += mine.Count;
            if (!mine.SetEquals(theirs))
            {
                throw new DegreesException($"The {tradition} list names {theirs.Count} spells and the parsed spells that say {tradition} are {mine.Count}: {theirs.Except(mine).Count()} listed spell(s) are not among them and {mine.Except(theirs).Count()} parsed spell(s) are not listed.");
            }
        }

        return new SpellCounts(headers, traitLines, traditionLines, listed.Values.Sum(s => s.Count), parsedTotal, perTradition, onNoList, art);
    }

    /// <summary>
    /// The degrees of success and heightened entries of a spells row, counted from the blocks and against what the parser made: the bold degree labels (and heightened labels) that open a
    /// line of the range, against the degree sections and heightened parts of the entries the row wrote. A sidebar that took some of a spell's text with it lowers the parsed count, and a
    /// dropped block on a taken page that opens a line with one of the labels says so directly. Returns the (degrees, heightened) counts.
    /// </summary>
    public static (int Degrees, int Heightened) CheckSpellDegrees(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
    {
        var spellRanges = ranges.Where(r => !r.Own && r.Row.Parser == "entries" && r.Row.Target == "spell").ToList();
        if (spellRanges.Count == 0)
        {
            return (0, 0);
        }

        string Label(LineInfo i) => SpanText.Normalize(i.Label).Trim().ToLowerInvariant();
        var infos = spellRanges.SelectMany(r => r.BlocksIn(blocks)).Where(b => b.Info != null && b.Role == "body").SelectMany(b => b.Info!).ToList();
        var degreeLabels = infos.Count(i => DegreeLabels.Contains(Label(i)));
        var heightenedLabels = infos.Count(i => Label(i).StartsWith("heightened", StringComparison.Ordinal));
        var rows = spellRanges.Select(r => r.RowNumber).ToHashSet();
        var mine = parsed.Where(e => rows.Contains(e.RowNumber)).ToList();
        var degrees = mine.Sum(e => e.Degrees.Count);
        var heightened = mine.Sum(e => e.Parts.Count(p => p.Label.StartsWith("Heightened", StringComparison.Ordinal)));
        if (degreeLabels != degrees || heightenedLabels != heightened)
        {
            throw new DegreesException($"The ranges of the spells rows have {degreeLabels} degree labels and {heightenedLabels} heightened labels at line starts, but the parser made {degrees} degree sections and {heightened} heightened entries.");
        }

        var pages = spellRanges.SelectMany(r => r.Indexes).Select(i => blocks.Blocks[i].Page).ToHashSet();
        var lost = (blocks.Dropped ?? new List<Block>()).Where(b => pages.Contains(b.Page) && b.Info != null
            && b.Info.Any(i => DegreeLabels.Contains(Label(i)) || Label(i).StartsWith("heightened", StringComparison.Ordinal))).ToList();
        if (lost.Count > 0)
        {
            throw new DegreesException($"{lost.Count} dropped sidebar block(s) on the pages of a spells row hold a degree or heightened label at a line start, so a spell's text may have gone with a sidebar (pages {string.Join(",", lost.Select(b => b.Page).Distinct())}).");
        }

        return (degrees, heightened);
    }

/// <summary>What <see cref="CheckActionCounts"/> counted for the <c>actions</c> rows together.</summary>
/// <param name="Children">Non-sidebar bookmark children of the taken sections (the general actions; the skills).</param>
/// <param name="Units">What those children stand for in the blocks: the action headings of a section with none of its own, the skill headings of one that has them.</param>
/// <param name="Headers">Block-header headings of an action's shape (an icon in the line, or a trait line right below).</param>
/// <param name="TraitLines">Headings that have a trait line right below.</param>
/// <param name="Icons">Headings that have an action icon in the line.</param>
/// <param name="TableNames">Distinct (name, proficiency) pairs of the overview table of actions, or -1 when the book has none in a tail the take-list reads past.</param>
/// <param name="EmptyGroups">The group headings of the <c>actionGroups</c> rows that hold no text of their own and so make no rule (the rest are rules; a rule the row's lead names is not a group).</param>
public sealed record ActionCounts(int Children, int Units, int Headers, int TraitLines, int Icons, int TableNames, int EmptyGroups = 0);

private static readonly HashSet<string> DegreeLabels = new(StringComparer.Ordinal) { "critical success", "success", "failure", "critical failure" };

private static readonly Regex ActionIconToken = new(@"\[(?:(?:one|two|three|free)-actions?|reaction)\]", RegexOptions.CultureInvariant);

/// <summary>
/// Counts the actions of every <c>actions</c> row three ways from the document and one more way from the overview table, none of which the action parser decides.
/// <list type="bullet">
/// <item>The bookmark children of a taken section (the general actions; the skills, with their sidebar bookmarks left out) against what the blocks hold for each child: action
/// headings, or skill headings. They must agree, or the row says why in <see cref="TakeRow.CountsDiffer"/>.</item>
/// <item>The action headings against the trait lines below them against the action icons in them. Some actions have no trait line and some have no icon, so the three differ
/// and the row says why; every heading has at least one of the two.</item>
/// <item>The overview table of actions by skill, which a rules row reads past in its tail: it has two columns (untrained actions, trained actions) and names every action with no
/// skill column, so its distinct (name, proficiency) pairs must be exactly the parsed actions' pairs. This one fails the run whatever the row says.</item>
/// </list>
/// </summary>
public static ActionCounts CheckActionCounts(IReadOnlyList<SpineNode> spine, BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
{
    int children = 0, units = 0, headers = 0, tags = 0, icons = 0, emptyGroups = 0;
    foreach (var range in ranges.Where(r => !r.Own && r.Row.Parser == "actions"))
    {
        var slice = range.BlocksIn(blocks);
        var heads = Enumerable.Range(0, slice.Count).Where(i => BlocksStage.IsActionHeader(slice, i)).ToList();
        var tagged = heads.Count(i => i + 1 < slice.Count && slice[i + 1].Role == "tag");
        var iconed = heads.Count(i => slice[i].Lines.Any(l => ActionIconToken.IsMatch(l)));
        var kids = spine.Count(n => n.Depth == range.Node.Depth + 1 && n.Path.Length == range.Node.Path.Length + 1 && n.Path.Take(range.Node.Path.Length).SequenceEqual(range.Node.Path) && !n.Sidebar);
        var sections = slice.Count(b => b.Role == "section");
        // A section of groups of actions (the basic and the specialty basic actions): each child bookmark is a group heading in the entry-header font that holds several actions.
        var unit = range.Row.ActionGroups ? slice.Count(b => b.Role == "entry-header") : sections > 0 ? sections : heads.Count;
        if (range.Row.ActionGroups)
        {
            // The group headings that made no rule: the row's rules are its lead's and the groups that have text.
            emptyGroups += unit - (parsed.Count(e => e.Type == "rule" && e.RowNumber == range.RowNumber) - (range.Row.LeadName != null ? 1 : 0));
        }

        headers += heads.Count;
        tags += tagged;
        icons += iconed;
        if (kids > 0)
        {
            children += kids;
            units += unit;
            if (kids != unit)
            {
                throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the taken section has {kids} bookmark children (sidebars left out) but {unit} {(sections > 0 ? "skill headings" : "action headings")} in its range, and the book has no section whose children differ from its headings, so no reason is accepted.");
            }
        }

        if (range.Row.NoTraitLine is { } noTrait && range.Row.NoIcon is { } noIcon && (heads.Count - tagged != noTrait || heads.Count - iconed != noIcon))
        {
            // The row's reason is for these numbers: the headings with no trait line and the headings with no icon.
            throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: {heads.Count - tagged} of {heads.Count} action headings have no trait line and {heads.Count - iconed} have no action icon, but the row's countsDiffer reason is for {noTrait} and {noIcon}.");
        }

        if ((heads.Count != tagged || heads.Count != iconed) && string.IsNullOrWhiteSpace(range.Row.CountsDiffer))
        {
            throw new DegreesException($"Chapter {range.Row.Chapter}, path {range.PathNumber}: the range has {heads.Count} action headings, {tagged} trait lines below them and {iconed} action icons in them, and the row gives no countsDiffer reason.");
        }
    }

    // Degrees of success, counted from the blocks: the bold degree labels that open a line of the range's text, against the degree sections the parser made. A sidebar that took
    // some of an action's text with it (and its degrees) lowers the parsed count; a dropped sidebar block on a taken page that holds such a line says so directly.
    var actionRanges = ranges.Where(r => !r.Own && r.Row.Parser == "actions").ToList();
    var labels = actionRanges.SelectMany(r => r.BlocksIn(blocks)).Where(b => b.Info != null).SelectMany(b => b.Info!)
        .Count(i => DegreeLabels.Contains(SpanText.Normalize(i.Label).Trim().ToLowerInvariant()));
    var degrees = parsed.Where(e => actionRanges.Any(r => r.RowNumber == e.RowNumber)).Sum(e => e.Degrees.Count);
    if (labels != degrees)
    {
        throw new DegreesException($"The ranges of the actions rows have {labels} degree labels at line starts but the parser made {degrees} degree sections.");
    }

    var pages = actionRanges.SelectMany(r => r.Indexes).Select(i => blocks.Blocks[i].Page).ToHashSet();
    var lost = (blocks.Dropped ?? new List<Block>()).Where(b => pages.Contains(b.Page) && b.Info != null && b.Info.Any(i => DegreeLabels.Contains(SpanText.Normalize(i.Label).Trim().ToLowerInvariant()))).ToList();
    if (lost.Count > 0)
    {
        throw new DegreesException($"{lost.Count} dropped sidebar block(s) on the pages of an actions row hold a degree label at a line start, so an action's text may have gone with a sidebar (pages {string.Join(",", lost.Select(b => b.Page).Distinct())}).");
    }

    var table = OverviewTableActions(blocks, ranges);
    if (ranges.Any(r => r.TailEnd >= 0 && r.Row.Parser == "rules") && table == null && actionRanges.Count > 0)
    {
        throw new DegreesException("An actions row is taken and a rules row reads its tail past, but no tail holds the overview table of actions, so the actions cannot be counted against it.");
    }

    // The overview table lists the skills chapter's actions: the actions of the chapters whose tail it is in (another chapter's actions, the basic ones, are in no such table).
    var tableChapters = ranges.Where(r => r.TailEnd >= 0 && r.Row.Parser == "rules").Select(r => r.Row.Chapter).ToHashSet(StringComparer.Ordinal);
    var names = parsed.Where(e => e.Type == "action" && tableChapters.Contains(e.Chapter)).Select(e => (e.Name.ToLowerInvariant(), e.Proficiency ?? string.Empty)).ToHashSet();
    if (table != null && !table.SetEquals(names))
    {
        throw new DegreesException($"The overview table of actions lists {table.Count} (name, proficiency) pairs, of which {table.Except(names).Count()} are not among the {names.Count} parsed actions' ({names.Except(table).Count()} parsed pairs are not in the table).");
    }

    return new ActionCounts(children, units, headers, tags, icons, table?.Count ?? -1, emptyGroups);
}

/// <summary>
/// Degrees of success of the sidebars the take-list takes, counted from the document: the bold degree labels that open a line of their blocks, against the degree sections the parser made of the sidebars' entries.
/// A sidebar that lost its degrees to the text beside it (or took another's) makes them differ, so a later change of the split cannot pass silently.
/// </summary>
public static (int Labels, int Degrees) CheckSidebarDegrees(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlyList<ParsedEntry> parsed)
{
    var labels = (blocks.TakenSidebars?.Values.SelectMany(b => b) ?? Enumerable.Empty<Block>()).Where(b => b.Info != null).SelectMany(b => b.Info!)
        .Count(i => DegreeLabels.Contains(SpanText.Normalize(i.Label).Trim().ToLowerInvariant()));
    var rows = ranges.Where(r => r.Row.Sidebars.Count > 0).Select(r => r.RowNumber).ToHashSet();
    var degrees = parsed.Where(e => e.Type == "rule" && rows.Contains(e.RowNumber) && e.Source != null && e.Source.Contains("Sidebar", StringComparison.Ordinal)).Sum(e => e.Degrees.Count);
    if (labels != degrees)
    {
        throw new DegreesException($"The taken sidebars have {labels} degree labels at line starts but the parser made {degrees} degree sections of their entries, so a sidebar's degrees may have gone with the text beside it.");
    }

    return (labels, degrees);
}

/// <summary>
/// The (lowercase name, proficiency) pairs of the overview table in the tail of a range (see <see cref="TakeRow.TailFromHeader"/>), or null when no tail holds one. The table is a body
/// block whose first line has two cells; a cell in the left column is an untrained action and one in the right column a trained one. A cell that only holds a page pointer joins the cell
/// before it, one that only holds the markers of its trait (a letter, or letters set apart by commas) is dropped, and an icon or a page pointer is cut from a name.
/// </summary>
private static HashSet<(string Name, string Proficiency)>? OverviewTableActions(BlocksResult blocks, IReadOnlyList<TakenRange> ranges)
{
    foreach (var range in ranges.Where(r => r.TailEnd >= 0))
    {
        for (var i = range.End; i < range.TailEnd; i++)
        {
            var block = blocks.Blocks[i];
            if (block.Role != "body" || block.Info is not { Count: > 2 } info || info[0].Cells is not { Count: 2 } head)
            {
                continue;
            }

            var split = (head[0].Left + head[1].Left) / 2;
            var pairs = new HashSet<(string, string)>();
            for (var k = 1; k < info.Count; k++)
            {
                var cells = info[k].Cells ?? new List<LineCell> { new(info[k].Left, block.Lines[k]) };
                var merged = new List<LineCell>();
                foreach (var cell in cells)
                {
                    if (merged.Count > 0 && cell.Text.StartsWith('('))
                    {
                        merged[^1] = merged[^1] with { Text = merged[^1].Text + " " + cell.Text };
                    }
                    else
                    {
                        merged.Add(cell);
                    }
                }

                foreach (var cell in merged.Where(c => !Regex.IsMatch(c.Text, @"^[A-Z](?:, [A-Z])*$")))
                {
                    var name = Regex.Replace(Regex.Replace(SpanText.Normalize(cell.Text), @"\s*\[[^\]]+\]", string.Empty), @"\s*\((?:see )?pages? [^)]*\)", string.Empty).Trim();
                    pairs.Add((name.ToLowerInvariant(), cell.Left < split ? "untrained" : "trained"));
                }
            }

            return pairs;
        }
    }

    return null;
}

    /// <summary>(name in title case, level) of a header block.</summary>
    private static (string Name, int Level) HeaderPair(Block header, TakeRow row)
    {
        var m = Regex.Match(Regex.Replace(EntriesStage.Clean(string.Join(' ', header.Lines)), @"\s*\[[^\]]+\]", string.Empty), @"^(?<name>.+?)\s+" + row.LevelPattern + @"\s+(?<level>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return (EntriesStage.TitleCase(m.Groups["name"].Value), int.Parse(m.Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The (name, level) of every row of the overview tables in a range's lead: a line at its block's left edge that is a name (every word capitalized, or a small joining word) then a level.
    /// A row's continuation lines are set in from the edge, and the text columns are not names, so neither is read.
    /// </summary>
    private static HashSet<(string Name, int Level)> LeadTableRows(BlocksResult blocks, TakenRange range)
    {
        var rows = new HashSet<(string, int)>();
        if (range.LeadStart < 0)
        {
            return rows;
        }

        var name = new Regex(@"^(?<name>[A-Z][^0-9]*?) (?<level>\d{1,2})(?: |$)", RegexOptions.CultureInvariant);
        for (var i = range.LeadStart; i < range.Start; i++)
        {
            var block = blocks.Blocks[i];
            if (block.Role != "body" || block.Info == null)
            {
                continue;
            }

            for (var k = 0; k < block.Lines.Count; k++)
            {
                var line = SpanText.Normalize(block.Lines[k]);
                var m = name.Match(line);
                if (block.Info[k].Left - block.Left <= 2 && m.Success && !line.Contains(" Level ", StringComparison.Ordinal)
                    && m.Groups["name"].Value.Split(' ').All(w => EntriesStage.IsNameWord(w)))
                {
                    rows.Add((m.Groups["name"].Value, int.Parse(m.Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        return rows;
    }

    private static List<ReportRow> ReportRows(BlocksResult blocks, IReadOnlyList<SpineNode> spine, IReadOnlyList<TakeRow> rows, RedactionTable redactions, string book, List<ParsedEntry> parsed, List<FinalEntry> final, IReadOnlyList<TakenRange> ranges)
    {
        var result = new List<ReportRow>();
        var counted = new HashSet<int>();
        var coverages = CheckBookmarkCoverage(spine, ranges);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.Approved)
            {
                continue;
            }

            // A page two rows share is counted for the first of them only.
            var pages = TakeStage.PagesOf(spine, row).Where(counted.Add).ToHashSet();

            var mine = final.Where(e => e.RowNumber == i + 1).ToList();
            // Redaction rows are listed by the entries the row parsed, so a row that dropped its entry still shows.
            var ids = parsed.Where(e => e.RowNumber == i + 1).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var dropped = (blocks.Dropped ?? new List<Block>()).Where(b => pages.Contains(b.Page)).ToList();

            // Text a taken range reads past: its lead and tail (tables), and the boxes of examples. Counted, never listed.
            var mineRanges = ranges.Where(r => r.RowNumber == i + 1).ToList();
            var readPast = mineRanges.Sum(r => (r.LeadStart >= 0 ? r.Start - r.LeadStart : 0) + (r.TailEnd >= 0 ? r.TailEnd - r.End : 0))
                + mineRanges.Where(r => r.Row.Parser == "actions").Sum(r => EntriesStage.ExampleBoxes(r.BlocksIn(blocks)).Count)
                + mineRanges.Where(r => r.Row.Parser == "entries").Sum(r => EntriesStage.ReadPastBoxBlocks(r.Row, r.BlocksIn(blocks)).Count)
                + mineRanges.Where(r => r.Row.Parser == "spelllists").Sum(r => r.Indexes.Count())
                + mineRanges.Sum(r => FlavorBlocks(r, blocks).Count);

            // One report row for each type the row wrote (an actions row also writes rules), the row's own target first. The row's counts of dropped blocks go on the first.
            var types = mine.Select(e => e.Type).Distinct().OrderBy(t => t == row.Target ? 0 : 1).ThenBy(t => t, StringComparer.Ordinal).ToList();
            if (types.Count == 0)
            {
                types.Add(row.Target);
            }

            for (var t = 0; t < types.Count; t++)
            {
                var ofType = mine.Where(e => e.Type == types[t]).ToList();
                result.Add(new ReportRow(
                    row.Chapter,
                    row.Parser,
                    types[t],
                    ofType.Count,
                    ofType.Select(e => e.Id).ToList(),
                    t == 0 ? dropped.Count(b => b.Role != "opener") : 0,
                    t == 0 ? dropped.Count(b => b.Role == "opener") : 0,
                    t == 0 ? redactions.Rows.Select((r, n) => (r, n)).Where(x => (x.r.Book == null || x.r.Book == book) && ids.Any(x.r.Names)).Select(x => x.n + 1).ToList() : new List<int>(),
                    ofType.Count(e => PageReference().IsMatch(e.Description) || e.Extracted.Any(x => PageReference().IsMatch(x.Text)) || e.Features.Any(f => PageReference().IsMatch(f.Description))),
                    t == 0 ? readPast : 0,
                    t == 0 && row.LeftOutSections != null ? coverages.FirstOrDefault(c => c.Chapter == row.Chapter) : null));
            }
        }

        return result;
    }

    [GeneratedRegex(@"\bpages? \d+", RegexOptions.IgnoreCase)]
    private static partial Regex PageReference();

    /// <summary>The lowercase words the book writes in lowercase at least <paramref name="commonAfter"/> times anywhere in its text (taken, left out, planned, dropped).</summary>
    public static List<string> CommonWords(BlocksResult blocks, int commonAfter = 3)
    {
        var lower = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in blocks.Blocks.Concat(blocks.Dropped ?? new List<Block>()).Concat(blocks.TakenSidebars?.Values.SelectMany(b => b) ?? Enumerable.Empty<Block>()))
        {
            foreach (var word in block.Lines.SelectMany(PackWordCheck.Words).Where(w => !char.IsUpper(w[0])).Select(w => w.ToLowerInvariant()))
            {
                lower[word] = lower.GetValueOrDefault(word) + 1;
            }
        }

        return lower.Where(kv => kv.Value >= commonAfter).Select(kv => kv.Key).OrderBy(w => w, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The lowercase words the book capitalizes at the start of a sentence and never inside one, and also writes in lowercase somewhere: such a
    /// word is not a name, so the allowlist gate lets a sentence open with it, unless the denylist has it. A glossary range is read as its definitions and its dropped lines,
    /// each its own paragraph, so that an entry's term in front of the definition does not make the definition's first word look like the middle of a sentence.
    /// The words go into the common-word file with <see cref="CommonWords"/>; only the common words are left off the denylist, so a name that is only ever capitalized at a
    /// sentence start in a left-out section is still hashed.
    /// </summary>
    public static List<string> OpenerWords(BlocksResult blocks, IReadOnlyList<TakenRange> ranges)
    {
        var glossary = ranges.Where(r => r.Row.Parser == "glossary").ToList();
        var inGlossary = glossary.SelectMany(r => r.Indexes).ToHashSet();
        var paragraphs = new List<string>();
        for (var i = 0; i < blocks.Blocks.Count; i++)
        {
            if (!inGlossary.Contains(i))
            {
                paragraphs.Add(string.Join(' ', blocks.Blocks[i].Lines));
            }
        }

        foreach (var range in glossary)
        {
            var scan = EntriesStage.ScanGlossary(range.BlocksIn(blocks), new HashSet<string>(StringComparer.Ordinal));
            paragraphs.AddRange(scan.Definitions.Select(d => d.Text));
            paragraphs.AddRange(scan.Dropped);
        }

        paragraphs.AddRange((blocks.Dropped ?? new List<Block>()).Select(b => string.Join(' ', b.Lines)));
        paragraphs.AddRange((blocks.TakenSidebars?.Values.SelectMany(b => b) ?? Enumerable.Empty<Block>()).Select(b => string.Join(' ', b.Lines)));

        var starts = new HashSet<string>(StringComparer.Ordinal);
        var inside = new HashSet<string>(StringComparer.Ordinal);
        var lower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paragraph in paragraphs)
        {
            var text = SpanText.Normalize(paragraph);
            foreach (var sentence in SpanText.Sentences(text))
            {
                var first = true;
                foreach (var word in PackWordCheck.Words(text.Substring(sentence.Start, sentence.Length)))
                {
                    if (PackWordCheck.IsCapital(word))
                    {
                        (first ? starts : inside).Add(word.ToLowerInvariant());
                    }
                    else
                    {
                        lower.Add(word.ToLowerInvariant());
                    }

                    first = false;
                }
            }
        }

        // A word the book never writes in lowercase may be a name that only ever opens a sentence (the first name of a list of sample names, say), so it needs a lowercase use.
        return starts.Where(w => w.Length > 1 && !inside.Contains(w) && lower.Contains(w)).OrderBy(w => w, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// What the redaction rows say is not for the packs, as words for the denylist: the capitalized words of every source span a row matches (not the first word of the span, which is
    /// only capitalized as a sentence start), and the lowercase words of the name of every entry a drop-entry row removes. A redacted sentence is left-out text by definition, even though
    /// it sits in a taken range.
    /// </summary>
    public static (List<string> SpanWords, List<string> Names) RedactedWords(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, RedactionTable redactions, string book, IReadOnlyList<SpineNode>? spine = null, IReadOnlyDictionary<string, string>? shippedIds = null, string? packDir = null)
    {
        var parsed = EntriesStage.Run(blocks, ranges, book, shippedIds, spine);
        var entries = parsed.Select(e => new RedactionEntry(e.Id, e.RedactionFields())).ToList();
        var scoped = new RedactionTable { Normalization = redactions.Normalization, Rows = redactions.Rows.Where(r => r.Book == null || r.Book == book).ToList() };
        if (RedactionStage.RowsWithoutEntry(entries, scoped) is { Count: > 0 } missing)
        {
            throw new DegreesException($"{missing.Count} redaction row(s) of book {book} name no entry, so the words they remove cannot be hashed.");
        }

        var spans = RedactionStage.SourceSpans(entries, scoped);
        var spanWords = spans
            // A capitalized word the row's replacement says again (a name of a game term inside a sentence that was reworded) is not taken out.
            .SelectMany(s => PackWordCheck.Words(s.Text).Skip(1).Where(w => w.Length > 1 && PackWordCheck.IsCapital(w)).Select(w => w.ToLowerInvariant())
                .Where(w => !(s.Row.Action == RedactionRow.Replace && s.Row.Text != null && PackWordCheck.Words(s.Row.Text).Any(k => k.Equals(w, StringComparison.OrdinalIgnoreCase)))))
            // Every word a row takes out, in any case: a coined word written in lowercase is as reserved as a capitalized one. A word that stays in the row's replacement is not removed.
            // The ordinary words among them (the book writes them in lowercase at least three times) are left off the denylist with the other common words.
            .Concat(spans.SelectMany(s => RemovedWords(s.Row, s.Text)))
            .Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToList();
        var names = spans.Where(s => s.Row.Action == RedactionRow.DropEntry)
            .SelectMany(s => PackWordCheck.Words(parsed.First(e => e.Id == s.Id).Name))
            // The name a replace row takes out of an entry's name field is as reserved as a dropped entry's: the words of the old name that the new one does not say, with their plurals.
            // A word the committed pack already says (an ordinary word of an earlier chapter's renamed name) is no coined word: the pack's own word gate would stop it.
            .Concat(spans.Where(s => s.Row.Action == RedactionRow.Replace && s.Row.Field == "name").SelectMany(s => RemovedWords(s.Row, s.Text)).Where(w => !PackSays(w, packDir)))
            // The letter after an apostrophe ("Smith's Hammer", the name of a dropped entry) is no word of the name: its hash would sit on the denylist and stop every possessive in the packs.
            // The book's own heading of a class feature that a row says in other words (a coined word) is a reserved name too, with its plurals.
            .Concat(parsed.SelectMany(e => e.Features).Where(f => f.BookName != null).SelectMany(f => PackWordCheck.Words(f.BookName!)))
            .Select(w => w.ToLowerInvariant()).Where(w => w != "s").Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToList();
        return (spanWords, names);
    }

    /// <summary>
    /// The pairs of adjacent capitalized words (see <see cref="PackWordCheck.CapitalPairs"/>) of every source span a redaction row matches, except a pair the row's replacement says again. A name of two ordinary
    /// words that a row took out (an emblem of the setting) passes the word gates one by one, so the pair is what a later chapter's entry that says it would be caught by.
    /// </summary>
    public static List<(string First, string Second)> RedactedPairs(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, RedactionTable redactions, string book, IReadOnlyList<SpineNode>? spine = null, IReadOnlyDictionary<string, string>? shippedIds = null)
    {
        var parsed = EntriesStage.Run(blocks, ranges, book, shippedIds, spine);
        var entries = parsed.Select(e => new RedactionEntry(e.Id, e.RedactionFields())).ToList();
        var scoped = new RedactionTable { Normalization = redactions.Normalization, Rows = redactions.Rows.Where(r => r.Book == null || r.Book == book).ToList() };
        return RedactionStage.SourceSpans(entries, scoped)
            .Where(s => s.Row.Action != RedactionRow.DropEntry)
            .SelectMany(s =>
            {
                var kept = s.Row.Action == RedactionRow.Replace && s.Row.Text != null ? PackWordCheck.CapitalPairs(s.Row.Text).ToHashSet() : new HashSet<(string, string)>();
                return PackWordCheck.CapitalPairs(s.Text).Where(p => !kept.Contains(p));
            })
            .Distinct().ToList();
    }

    /// <summary>
    /// The names of more than one word that a redaction row removes, as the lists of their words (see <see cref="Denylist.EmbeddedPhrases"/>): the old name of an entry a replace row renames, the name of an entry a
    /// drop-entry row removes, and the book's heading of a class feature the take-list says in other words. A name the row's replacement says again is not removed.
    /// </summary>
    public static List<List<string>> RedactedPhrases(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, RedactionTable redactions, string book, IReadOnlyList<SpineNode>? spine = null, IReadOnlyDictionary<string, string>? shippedIds = null)
    {
        var parsed = EntriesStage.Run(blocks, ranges, book, shippedIds, spine);
        var entries = parsed.Select(e => new RedactionEntry(e.Id, e.RedactionFields())).ToList();
        var scoped = new RedactionTable { Normalization = redactions.Normalization, Rows = redactions.Rows.Where(r => r.Book == null || r.Book == book).ToList() };
        var spans = RedactionStage.SourceSpans(entries, scoped);
        var phrases = spans.Where(s => s.Row.Action == RedactionRow.DropEntry).Select(s => PackWordCheck.Words(parsed.First(e => e.Id == s.Id).Name).ToList())
            .Concat(spans.Where(s => s.Row.Action == RedactionRow.Replace && s.Row.Field == "name" && !(s.Row.Text != null && SpanText.Normalize(s.Row.Text).Equals(SpanText.Normalize(s.Text), StringComparison.OrdinalIgnoreCase)))
                .Select(s => PackWordCheck.Words(s.Text).ToList()))
            .Concat(parsed.SelectMany(e => e.Features).Where(f => f.BookName != null).Select(f => PackWordCheck.Words(f.BookName!).ToList()))
            .Where(words => words.Count > 1).ToList();
        return phrases.GroupBy(p => string.Join(' ', p.Select(w => w.ToLowerInvariant()))).Select(g => g.First()).ToList();
    }

    private static readonly Dictionary<string, HashSet<string>> PackWordSets = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the rules pack says the lowercase word anywhere, in any case: the pack folder the run names (<c>--pack</c>, the one the shipped ids come from), else the committed one (found beside the program, as
    /// <see cref="PackEmitter.CommittedPackDir"/> does). False when there is no pack.
    /// </summary>
    private static bool PackSays(string word, string? packDir = null)
    {
        var dir = packDir ?? PackEmitter.CommittedPackDir();
        var key = dir == null ? string.Empty : Path.GetFullPath(dir);
        if (!PackWordSets.TryGetValue(key, out var words))
        {
            words = dir == null || !Directory.Exists(dir) ? new HashSet<string>(StringComparer.Ordinal)
                : Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories).SelectMany(f => PackWordCheck.Words(File.ReadAllText(f))).Select(w => w.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            PackWordSets[key] = words;
        }

        // The plural a name is reserved with (see the denylist step) counts too: a pack that says "giants" may not have the name "giant" reserved.
        return words.Contains(word) || words.Contains(word + "s") || words.Contains(word + "es");
    }

    /// <summary>Whether the pack says the lowercase word itself (not a plural or a singular of it).</summary>
    internal static bool PackSaysExactly(string word, string? packDir)
    {
        PackSays(word, packDir); // loads the pack's words
        var dir = packDir ?? PackEmitter.CommittedPackDir();
        return PackWordSets.TryGetValue(dir == null ? string.Empty : Path.GetFullPath(dir), out var words) && words.Contains(word);
    }

    /// <summary>The forms a removed word is denied in: itself, the plural when it is a singular, the singular when it is a plural.</summary>
    public static IEnumerable<string> WordForms(string word)
    {
        yield return word;
        if (word.EndsWith("es", StringComparison.Ordinal) && word.Length > 3)
        {
            yield return word[..^2];
            yield return word[..^1];
        }
        else if (word.EndsWith('s') && word.Length > 2)
        {
            yield return word[..^1];
        }
        else
        {
            yield return word + "s";
            yield return word + "es";
        }
    }

    /// <summary>The lowercase words of a redacted span that the row takes out: all of them for a cut or a drop, those not in the replacement text for a replace.</summary>
    public static IEnumerable<string> RemovedWords(RedactionRow row, string span)
    {
        if (row.Action == RedactionRow.DropEntry)
        {
            return Enumerable.Empty<string>(); // the entry's name is hashed by the caller; the rest of that sentence stays what it was
        }

        var kept = row.Action == RedactionRow.Replace && row.Text != null
            ? PackWordCheck.Words(row.Text).Select(w => w.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return PackWordCheck.Words(span).Select(w => w.ToLowerInvariant()).Where(w => w.Length > 1 && !kept.Contains(w));
    }

    /// <summary>
    /// The lowercase words whose hashes go on the denylist: the capitalized words of everything the take-list leaves out (blocks no range takes, dropped sidebars
    /// and openers), minus the common ones. A word is common, and left off, when it also appears in lowercase at least <paramref name="commonAfter"/> times anywhere in
    /// the book's text. One-letter words are left off. Nothing else is, except a word in <paramref name="waivers"/> (<c>Data/degrees-denylist-waivers.txt</c>): a game term
    /// a person has reviewed and decided the packs may say.
    /// </summary>
    /// <summary>
    /// The hashed pairs of capitalized words of the left-out text (see <see cref="PackWordCheck.CapitalPairs"/>), for the pair gate: a name of two words whose words pass the word gates one by one
    /// (an ordinary word and a game term) is still caught. A pair is left off when either of its words is on the word denylist already (<paramref name="deniedWords"/>), when it is in a title
    /// of the take-list (every `source:` line says it), or when the waiver file waives the pair. Being game text of a taken range is no reason to leave a pair off. The pairs of the spans the redaction
    /// rows take out (<see cref="RedactedPairs"/>) are candidates too: a redacted sentence is left-out text although it sits in a taken range.
    /// </summary>
    public static List<string> DenylistPairs(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlyList<(int Start, int End)>? planned, IReadOnlyCollection<string> deniedWords, IReadOnlySet<string>? waivers = null, IEnumerable<(string First, string Second)>? redactedPairs = null)
    {
        var leftOut = LeftOutLines(blocks, ranges, planned);
        // A pair is NOT left off because the taken text says it too: the day a setting's name reaches a taken range, a regeneration must not silently remove its pair. A game pair that the
        // left-out text also capitalizes is waived by name in the waiver file, with its reason.
        var game = ranges.SelectMany(r => r.Row.Paths.Concat(r.Row.Own)).SelectMany(p => p).SelectMany(PackWordCheck.CapitalPairs).ToHashSet();
        var denied = deniedWords.ToHashSet(StringComparer.Ordinal);
        return leftOut.SelectMany(PackWordCheck.CapitalPairs).Concat(redactedPairs ?? Enumerable.Empty<(string, string)>()).Distinct()
            .Where(p => !game.Contains(p) && !denied.Contains(p.First) && !denied.Contains(p.Second) && !(waivers?.Contains(p.First + " " + p.Second) ?? false)) // a waiver line of two words waives that pair
            .Select(p => Denylist.PairHash(p.First, p.Second)).Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The lines the old sidebar rule would have dropped on a page the split by titles keeps as the page's own, on pages no row takes. They are left-out text for the words and for the pairs alike (a feat list a
    /// sample character's sidebar leaves beside a section that is planned stays hashed until that section is taken).
    /// </summary>
    private static IEnumerable<string> LegacyLines(BlocksResult blocks, IReadOnlyList<TakenRange> ranges)
    {
        var takenPages = ranges.SelectMany(r => r.Indexes.Concat(r.LeadStart >= 0 ? Enumerable.Range(r.LeadStart, r.Start - r.LeadStart) : Enumerable.Empty<int>())
            .Concat(r.TailEnd >= 0 ? Enumerable.Range(r.End, r.TailEnd - r.End) : Enumerable.Empty<int>())).Select(i => blocks.Blocks[i].Page).ToHashSet();
        return (blocks.LegacyDropped ?? new List<Block>()).Where(b => !takenPages.Contains(b.Page)).SelectMany(b => b.Lines);
    }

    private static IEnumerable<string> LeftOutLines(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlyList<(int Start, int End)>? planned) =>
        TakeStage.LeftOut(blocks, ranges, planned).SelectMany(b => b.Lines).Concat(LegacyLines(blocks, ranges))
            .Concat(ranges.Where(r => r.Row.Parser == "glossary").SelectMany(r => EntriesStage.ScanGlossary(r.BlocksIn(blocks), new HashSet<string>(StringComparer.Ordinal)).Dropped))
            .Concat(ranges.Where(r => r.Row.Parser == "actions").SelectMany(r => EntriesStage.ExampleBoxes(r.BlocksIn(blocks))).SelectMany(b => b.Lines))
            .Concat(ranges.Where(r => r.Row.Parser == "entries").SelectMany(r => EntriesStage.ReadPastBoxBlocks(r.Row, r.BlocksIn(blocks))).SelectMany(b => b.Lines))
            .Concat(ranges.SelectMany(r => FlavorBlocks(r, blocks).Concat(TailBlocks(r, blocks))).SelectMany(b => b.Lines));

    /// <summary>
    /// The blocks a <c>rules</c> range reads past at its end (<see cref="TakeRow.TailFromHeader"/>: a section's tables, from its first block-header heading on). They make no entry and no taken range holds them, so they are left-out
    /// text: a name in them (a language the tables list) stays on the denylist, as a name in the lore does.
    /// </summary>
    public static List<Block> TailBlocks(TakenRange range, BlocksResult blocks) =>
        range.Row.Parser == "rules" && range.TailEnd > range.End ? blocks.Blocks.Skip(range.End).Take(range.TailEnd - range.End).ToList() : new List<Block>();

    /// <summary>
    /// The blocks a range of the ancestry, versatile heritage or backgrounds parsers reads past inside the range (the lore, the sentences under a title, a background's story): they make no entry, so
    /// they are left-out text, and are counted as read past.
    /// </summary>
    public static List<Block> FlavorBlocks(TakenRange range, BlocksResult blocks) => range.Row.Parser switch
    {
        "ancestries" => EntriesStage.AncestryFlavor(range.BlocksIn(blocks)),
        "versatile" => EntriesStage.VersatileFlavor(range.BlocksIn(blocks)),
        "backgrounds" => EntriesStage.BackgroundFlavor(range.BlocksIn(blocks)),
        "classes" => EntriesStage.ClassFlavor(range.BlocksIn(blocks), range.Row),
        "archetypes" => EntriesStage.ArchetypeFlavor(range.BlocksIn(blocks)),
        _ => new List<Block>(),
    };

    /// <summary>
    /// The pair hashes a regeneration may drop without a waiver: the pairs in a take-list title (every `source:` line says them) and the pairs that have a word on the word denylist (the pair
    /// list leaves those off, since the word gate fires first).
    /// </summary>
    public static HashSet<string> ExplainedPairs(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlyList<(int Start, int End)>? planned, IReadOnlyCollection<string> deniedWords)
    {
        var denied = deniedWords.ToHashSet(StringComparer.Ordinal);
        // A sidebar the take-list takes as a rule (TakeRow.Sidebars) is taken text, which its entry reads, so its pairs are no longer left out: the reviewer reads the taken sidebar, as the titles are read.
        var taken = (blocks.TakenSidebars ?? new Dictionary<string, List<Block>>()).Values.SelectMany(b => b).SelectMany(b => b.Lines).SelectMany(PackWordCheck.CapitalPairs);
        var titles = ranges.SelectMany(r => r.Row.Paths.Concat(r.Row.Own)).SelectMany(p => p).SelectMany(PackWordCheck.CapitalPairs).Concat(taken);
        var withDenied = LeftOutLines(blocks, ranges, planned).SelectMany(PackWordCheck.CapitalPairs).Where(p => denied.Contains(p.First) || denied.Contains(p.Second));
        return titles.Concat(withDenied).Select(p => Denylist.PairHash(p.First, p.Second)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The review of a regeneration from scratch (<c>denylist --fresh</c>): the hashes the file holds that the new list does not. <paramref name="neverDropped"/> (the words of the redaction rows'
    /// spans and the names of the entries they drop) may not be among them, or the run fails. A hash a waiver lifts is counted as waived. For the pair list, <paramref name="explained"/> says which
    /// of the others may go (see <see cref="ExplainedPairs"/>); any other pair that would vanish fails the run, so a regeneration never silently removes a pair because a range has since been taken.
    /// The rest are counted as left-out text that was taken (or became common) since.
    /// </summary>
    public static FreshReview ReviewFresh(string file, IReadOnlySet<string> existing, IReadOnlyCollection<string> fresh, IReadOnlyCollection<string> lifted, IReadOnlySet<string> neverDropped, IReadOnlySet<string>? explained, IReadOnlyDictionary<string, string>? retired = null)
    {
        var next = fresh.ToHashSet(StringComparer.Ordinal);
        next.ExceptWith(lifted);
        var gone = existing.Where(h => !next.Contains(h)).ToList();
        if (gone.Any(neverDropped.Contains))
        {
            throw new DegreesException($"{file}: --fresh would drop {gone.Count(neverDropped.Contains)} hash(es) of the words a redaction row removes or of a dropped entry's name ({string.Join(", ", gone.Where(neverDropped.Contains))}).");
        }

        var waived = gone.Where(lifted.Contains).ToList();
        var retiredHere = retired == null ? new List<string>() : gone.Except(waived).Where(retired.ContainsKey).ToList();
        var rest = gone.Except(waived).Except(retiredHere).ToList();
        var unexplained = explained == null ? new List<string>() : rest.Where(h => !explained.Contains(h)).ToList();
        if (unexplained.Count > 0)
        {
            throw new DegreesException($"{file}: --fresh would drop {unexplained.Count} pair(s) that no waiver, take-list title or denied word explains; a pair is never removed because a range has since been taken ({string.Join(", ", unexplained)}).");
        }

        return new FreshReview(file, next.Count, next.Count(existing.Contains), next.Count(h => !existing.Contains(h)), waived.Count, explained == null ? rest.Count : 0, explained == null ? 0 : rest.Count, retiredHere.Select(h => $"{h}: {retired![h]}").ToList());
    }

    /// <summary>--fresh writes the files from one book's run alone, so it refuses when the take-list has rows of another book too (that book's hashes would be dropped).</summary>
    public static void RefuseFreshWithOtherBooks(IEnumerable<TakeRow> rows, string book)
    {
        if (rows.Any(r => r.Book != book))
        {
            throw new DegreesException("--fresh would drop the hashes another book's run merged in: the take-list has rows of another book.");
        }
    }

    /// <summary>
    /// The hashes a waiver lifts from the committed hash file: the waived single words that this book's own left-out text would hash without the waivers (<paramref name="bookWords"/>), so a
    /// waiver reviewed against one book never removes a hash another book's run put there. A waiver that equals the name of a drop-entry row (or its plural) fails instead: such a name is
    /// reserved, and no waiver can pass it.
    /// </summary>
    public static List<string> LiftedWaivers(IEnumerable<string> waivers, IEnumerable<string> reservedNames, IEnumerable<string> bookWords)
    {
        var reserved = reservedNames.SelectMany(n => new[] { n, n + "s", n + "es" }).ToHashSet(StringComparer.Ordinal);
        var words = waivers.Where(w => !w.Contains(' ')).ToList();
        if (words.Any(reserved.Contains))
        {
            throw new DegreesException("A waiver names an entry a redaction row drops, so a reserved name: no waiver may pass it.");
        }

        var book = bookWords.ToHashSet(StringComparer.Ordinal);
        return words.Where(book.Contains).Select(Denylist.Hash).ToList();
    }

    /// <summary>The pair hashes a pair waiver lifts: the waived pairs that this book's left-out text would hash without the waivers (<paramref name="bookPairs"/>).</summary>
    public static List<string> LiftedPairWaivers(IEnumerable<string> waivers, IEnumerable<string> bookPairs)
    {
        var book = bookPairs.ToHashSet(StringComparer.Ordinal);
        return waivers.Where(w => w.Contains(' ')).Select(Denylist.Hash).Where(book.Contains).ToList();
    }

    public static (List<string> Words, int Excluded) DenylistWords(BlocksResult blocks, IReadOnlyList<TakenRange> ranges, IReadOnlySet<string> waivers, int commonAfter = 3, IReadOnlyList<(int Start, int End)>? planned = null,
        (IReadOnlyList<string> SpanWords, IReadOnlyList<string> Names)? redacted = null, string? packDir = null)
    {
        var leftOut = TakeStage.LeftOut(blocks, ranges, planned).ToList();

        // Only common words, the book keys and waived game terms stay off the list. A word that is in a taken range is NOT left off: if the packs say it and the book
        // keeps it in a left-out section too, the gate must fire, and a person decides in the waiver file whether it is a game term.
        var exclude = new HashSet<string>(waivers, StringComparer.Ordinal);
        exclude.UnionWith(CommonWords(blocks, commonAfter));
        exclude.UnionWith(DegreesOptions.Books); // the book keys are in every `source:` line, and are not names
        // The words of the take-list's own titles are in every `source:` line, and the take-list test already holds them to the allowlist gate: they are not setting names.
        exclude.UnionWith(ranges.SelectMany(r => r.Row.Paths.Concat(r.Row.Own)).SelectMany(p => p).SelectMany(PackWordCheck.Words).Select(w => w.ToLowerInvariant()));

        // A glossary range is taken only for its trait definitions: the rest of the list (the index, the other entries, the pointer-only trait lines) is left out.
        var glossaryLeftOut = ranges.Where(r => r.Row.Parser == "glossary").SelectMany(r => EntriesStage.ScanGlossary(r.BlocksIn(blocks), new HashSet<string>(StringComparer.Ordinal)).Dropped);
        // The boxes of examples an actions range reads past are text no entry holds, and names turn up in them: their words are left out too.
        // What the old sidebar rule dropped from pages that no row takes is left out text too, though the split by titles keeps it as the page's own.
        var legacy = LegacyLines(blocks, ranges);
        var boxes = legacy.Concat(ranges.Where(r => r.Row.Parser == "actions").SelectMany(r => EntriesStage.ExampleBoxes(r.BlocksIn(blocks))).SelectMany(b => b.Lines))
            .Concat(ranges.Where(r => r.Row.Parser == "entries").SelectMany(r => EntriesStage.ReadPastBoxBlocks(r.Row, r.BlocksIn(blocks))).SelectMany(b => b.Lines))
            // An ancestry's lore is read past inside a taken range, and so is the list of languages its box offers: both are left-out text.
            .Concat(ranges.SelectMany(r => FlavorBlocks(r, blocks).Concat(TailBlocks(r, blocks))).SelectMany(b => b.Lines));
        var candidates = leftOut.SelectMany(b => b.Lines).Concat(glossaryLeftOut).Concat(boxes).SelectMany(PackWordCheck.Words).Where(w => w.Length > 1 && char.IsUpper(w[0]))
            .Select(w => w.ToLowerInvariant()).Concat(redacted?.SpanWords ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
        var words = candidates.Where(w => !exclude.Contains(w)).ToList();

        // The names of the entries a drop-entry row removes are reserved: they go on the list whatever their case or how often the book writes them in lowercase, with the plural
        // a trait is written in, so neither gate can pass one that a person once decided to drop.
        var reserved = (redacted?.Names ?? Array.Empty<string>()).SelectMany(n => new[] { n, n + "s", n + "es" }).ToList();
        // The words a row takes out of a sentence are guarded in their other number too (a coined plural may turn up where its singular was cut), unless the book writes the form in lowercase often.
        // A form that an entry of the pack already says (an earlier chapter's) is not added: the word gate would stop that entry, and no entry may change for a form.
        var forms = (redacted?.SpanWords ?? Array.Empty<string>()).Where(w => !exclude.Contains(w)).SelectMany(WordForms).Where(w => !exclude.Contains(w) && !PackSaysExactly(w, packDir));
        words = words.Concat(reserved).Concat(forms).Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToList();
        return (words, candidates.Count - candidates.Count(w => words.Contains(w)));
    }
}
