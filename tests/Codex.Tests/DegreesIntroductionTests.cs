using Codex.ContentImport.Degrees;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Codex.Tests;

/// <summary>
/// The second half of chapter 9's options over synthetic pages and blocks (invented words only): a page of one column with strips of two, the text of a picture inside a rectangle, a page whose text is in the sidebars'
/// font, a pinned gutter that lets a sidebar's heading be found beside the other column's text, and the count of the bookmarks below the taken sections. Every gate has a test. CI never sees a book.
/// </summary>
public class DegreesIntroductionTests
{
    private const string Roles = """
        rules:
          - { role: section, family: [Helvetica-Bold], size: [16, 16] }
          - { role: block-header, family: [Helvetica-Bold], size: [11, 11] }
          - { role: rules, family: [Times], size: [1, 11] }
          - { role: body, family: [Helvetica], size: [1, 11] }
        """;

    private static List<LayoutPage> Pages(Action<PdfPageBuilder, PdfDocumentBuilder.AddedFont, PdfDocumentBuilder.AddedFont, PdfDocumentBuilder.AddedFont> draw)
    {
        var b = new PdfDocumentBuilder();
        var times = b.AddStandard14Font(Standard14Font.TimesRoman);
        var hel = b.AddStandard14Font(Standard14Font.Helvetica);
        var helBold = b.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = b.AddPage(603, 783);
        draw(page, times, hel, helBold);
        using var doc = PdfDocument.Open(b.Build());
        return LayoutStage.Run(doc, FontRoles.Parse(Roles)).Pages;
    }

    private const string Across = "A line of invented words that runs across the whole width of the page, past the middle, with enough of them to cross it.";

    // --- a page of one column with strips of two ---

    private static List<LayoutPage> StripPage() => Pages((p, times, _, _) =>
    {
        p.AddText(Across, 9, new PdfPoint(60, 740), times);
        p.AddText("Left one alpha beta gamma", 9, new PdfPoint(60, 700), times);
        p.AddText("Left two delta epsilon zeta", 9, new PdfPoint(60, 688), times);
        p.AddText("Right one eta theta iota", 9, new PdfPoint(340, 700), times);
        p.AddText("Right two kappa lambda mu", 9, new PdfPoint(340, 688), times);
        p.AddText(Across, 9, new PdfPoint(60, 660), times);
    });

    [Fact]
    public void AStripOfTwoColumnsOnAOneColumnPage_IsReadLeftThenRight_BetweenTheFullWidthLinesAbove_AndBelow()
    {
        var pages = StripPage();

        // Without the strip the two columns' lines run together, so the page is one column and its lines are the two columns' words.
        var plain = BlocksStage.Run(pages);
        Assert.Contains(plain.Blocks.SelectMany(b => b.Lines), l => l.Contains("Left one", StringComparison.Ordinal) && l.Contains("Right one", StringComparison.Ordinal));
        Assert.Equal(new[] { 1 }, plain.OneColumnPageNumbers);

        // With it the blocks follow the page from the top: the line above, the strip's left column, its right column, the line below. The page is no one-column page any more.
        var banded = BlocksStage.Run(pages, new BlocksOptions { Bands = new Dictionary<int, IReadOnlyList<IReadOnlyList<double>>> { [1] = new[] { (IReadOnlyList<double>)new[] { 710.0, 680.0, 300.0 } } } });
        Assert.Equal(new[] { Across, "Left one alpha beta gamma Left two delta epsilon zeta", "Right one eta theta iota Right two kappa lambda mu", Across },
            banded.Blocks.Select(b => string.Join(' ', b.Lines)));
        Assert.Equal(new[] { 0, 0, 1, 0 }, banded.Blocks.Select(b => b.Column));
        Assert.Empty(banded.OneColumnPageNumbers!);
    }

    private static readonly List<SpineNode> OneSpine = new()
    {
        new SpineNode(0, "Book", new[] { "Book" }, 1, 1, false),
        new SpineNode(1, "Part", new[] { "Book", "Part" }, 1, 1, false),
    };

    [Fact]
    public void BandsAndArtRegions_NameOnlyAPageOfARulesRowThatTheRowCovers_WithWellFormedNumbers_AndNotASidebarPage()
    {
        string Row(string extra, string parser = "rules") => $"rows:\n  - {{ chapter: a, book: pc, parser: {parser}, target: rule, approved: true, {extra}, paths: [[\"Book\", \"Part\"]] }}\n";
        var good = ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bands: { 1: [[700, 600, 300]] }, artRegions: { 1: [[50, 500, 250, 400]] }")).Rows);
        Assert.Equal(new[] { 700.0, 600.0, 300.0 }, good.Bands[1].Single());
        Assert.Equal(new[] { 50.0, 500.0, 250.0, 400.0 }, good.ArtRegions[1].Single());

        // A page the row does not cover, a strip whose top is not above its bottom, a rectangle the wrong way round, and a row of another parser fail the run.
        Assert.Contains("bands", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bands: { 2: [[700, 600, 300]] }")).Rows)).Message);
        Assert.Contains("bands", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bands: { 1: [[600, 700, 300]] }")).Rows)).Message);
        Assert.Contains("artRegions", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("artRegions: { 3: [[50, 500, 250, 400]] }")).Rows)).Message);
        Assert.Contains("artRegions", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("artRegions: { 1: [[250, 500, 50, 400]] }")).Rows)).Message);
        Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bands: { 1: [[700, 600, 300]] }", "entries")).Rows));

        // Strips that overlap fail (a word in the overlap would go to the upper strip); strips that touch or are apart do not.
        Assert.Contains("overlap", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bands: { 1: [[400, 300, 300], [500, 350, 300]] }")).Rows)).Message);
        Assert.Equal(2, ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bands: { 1: [[700, 500, 300], [500, 300, 300]] }")).Rows).Bands[1].Count);

        // bodyAsRules is held to the same rule: a page the row covers, in a rules row.
        Assert.Contains("bodyAsRules", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bodyAsRules: [2]")).Rows)).Message);
        Assert.Contains("bodyAsRules", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bodyAsRules: [1]", "entries")).Rows)).Message);
        ImportPipeline.OptionsFor(OneSpine, TakeList.Parse(Row("bodyAsRules: [1]")).Rows);

        // A page a sidebar bookmark covers keeps its sidebar split by titles, which strips would break up.
        var withSidebar = OneSpine.Append(new SpineNode(2, "Sidebar: Box", new[] { "Book", "Part", "Sidebar: Box" }, 1, 1, true)).ToList();
        Assert.Contains("bands", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(withSidebar, TakeList.Parse(Row("bands: { 1: [[700, 600, 300]] }")).Rows)).Message);
    }

    // --- the text of a picture inside a rectangle ---

    [Fact]
    public void ABlockWhollyInsideAnArtRegion_IsThePicturesText_NotCountedAsAHeading_SkippedByTheEntry_AndHashedWithTheTextLeftOut()
    {
        var pages = Pages((p, times, hel, helBold) =>
        {
            p.AddText("Part", 16, new PdfPoint(60, 740), helBold);
            p.AddText("Words of the part.", 9, new PdfPoint(60, 720), times);
            p.AddText("Odd Label Words", 9, new PdfPoint(300, 600), hel); // inside the rectangle: a form's label
            p.AddText("7", 16, new PdfPoint(310, 580), helBold); // a numeral set in the section font, inside it too
            p.AddText("More words of the part.", 9, new PdfPoint(60, 500), times); // outside it
        });
        var regions = new Dictionary<int, IReadOnlyList<IReadOnlyList<double>>> { [1] = new[] { (IReadOnlyList<double>)new[] { 280.0, 640.0, 450.0, 560.0 } } };

        var plain = BlocksStage.Run(pages);
        var blocks = BlocksStage.Run(pages, new BlocksOptions { ArtRegions = regions });
        Assert.Equal(0, plain.Blocks.Count(b => b.Role == BlocksStage.ArtRole));
        Assert.Equal(new[] { "Odd Label Words", "7" }, blocks.Blocks.Where(b => b.Role == BlocksStage.ArtRole).Select(b => b.Lines[0]));
        Assert.Equal(2, plain.Blocks.Count(b => b.Role == "section")); // the numeral would be a second heading: it would fail the count of the section's headings and the claim of its blocks

        var spine = new List<SpineNode> { new(0, "Book", new[] { "Book" }, 1, 1, false), new(1, "Part", new[] { "Book", "Part" }, 1, 1, false), new(1, "Next", new[] { "Book", "Next" }, 2, 2, false) };
        var yaml = "rows:\n  - { chapter: a, book: pc, parser: rules, target: rule, category: Test, approved: true, artRegions: { 1: [[280, 640, 450, 560]] }, paths: [[\"Book\", \"Part\"]] }\n";
        var all = blocks with { Blocks = blocks.Blocks.Append(new Block(2, 0, "section", 60, 0, 200, 740, new List<string> { "Next" })).ToList() };
        var ranges = TakeStage.Resolve(spine, all, TakeList.Parse(yaml).Rows);
        var entry = Assert.Single(EntriesStage.Run(all, ranges, "pc", null, spine));
        Assert.Equal("Words of the part.\nMore words of the part.", entry.Render());
        var left = TakeStage.LeftOut(all, ranges).Select(b => string.Join(' ', b.Lines)).ToList();
        Assert.Contains("Odd Label Words", left);
        Assert.Contains("7", left);
        Assert.DoesNotContain("Words of the part.", left);

        // Without the rectangle the numeral is a block the rules parser does not claim, so the run fails instead of reading it.
        var unclaimed = Assert.Throws<DegreesException>(() => TakeStage.Resolve(spine, plain with { Blocks = plain.Blocks.Append(new Block(2, 0, "section", 60, 0, 200, 740, new List<string> { "Next" })).ToList() },
            TakeList.Parse(yaml.Replace("artRegions: { 1: [[280, 640, 450, 560]] }, ", string.Empty, StringComparison.Ordinal)).Rows));
        Assert.Contains("do not claim", unclaimed.Message.Replace("does not claim", "do not claim", StringComparison.Ordinal));
    }

    // --- a page whose text is in the font of the sidebars ---

    private static Block Indented(string role, params (double Left, double Right, string Label, string Text)[] lines) =>
        new(1, 0, role, 60, 0, 300, 700, lines.Select(l => l.Text).ToList(), lines.Select(l => new LineInfo(l.Left, l.Right, l.Label)).ToList());

    [Fact]
    public void BodyFontTextOfAPageTheRowNames_IsReadAsRulesText_WithItsParagraphsFoundByTheirFirstLineIndents()
    {
        var spine = new List<SpineNode> { new(0, "Book", new[] { "Book" }, 1, 1, false), new(1, "Part", new[] { "Book", "Part" }, 1, 1, false) };
        var blocks = new List<Block>
        {
            new(1, 0, "section", 60, 0, 200, 740, new List<string> { "Part" }),
            Indented("body", (70, 299, "Alpha:", "Alpha: words that open the first paragraph and run on to the edge"), (60, 150, string.Empty, "and end here."),
                (70, 299, "Beta:", "Beta: words that open the second paragraph and run on to the edge"), (60, 170, string.Empty, "and end there.")),
        };
        const string Row = "rows:\n  - {{ chapter: a, book: pc, parser: rules, target: rule, category: Test, approved: true, subheadings: true, {0}paths: [[\"Book\", \"Part\"]] }}\n";
        Block[] Book(string yaml, out List<ParsedEntry> parsed)
        {
            var book = new BlocksResult(blocks, 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());
            var ranges = TakeStage.Resolve(spine, book, TakeList.Parse(yaml).Rows);
            parsed = EntriesStage.Run(book, ranges, "pc", null, spine);
            return blocks.ToArray();
        }

        Book(string.Format(Row, "bodyAsRules: [1], "), out var asRules);
        Assert.Equal(2, Assert.Single(asRules).Paragraphs.Count);
        Assert.StartsWith("Beta:", Assert.Single(asRules).Paragraphs[1], StringComparison.Ordinal);

        // A row that does not name the page reads its body-font blocks as ragged text with hanging indents, as before.
        Book(string.Format(Row, string.Empty), out var asBody);
        Assert.NotEqual(asRules[0].Render(), asBody[0].Render());
    }

    // --- a pinned gutter finds a sidebar's heading beside the other column's text ---

    [Fact]
    public void APinnedGutter_SeparatesASidebarsHeadingFromTheOtherColumnsLine_SoTheBookmarksBackTheSplit()
    {
        // A line across the whole page leaves no empty band, so no gutter is found and a heading in the left column shares its line with the right column's words.
        var pages = Pages((p, times, hel, helBold) =>
        {
            p.AddText(Across, 9, new PdfPoint(60, 740), times);
            p.AddText("BOX TITLE", 11, new PdfPoint(60, 700), helBold);
            p.AddText("Right column words of the page's own text.", 9, new PdfPoint(340, 700), times);
            p.AddText("Words of the box.", 9, new PdfPoint(60, 686), hel);
            p.AddText("More right column words of the page.", 9, new PdfPoint(340, 686), times);
            for (var y = 660; y > 560; y -= 12)
            {
                p.AddText(Across, 9, new PdfPoint(60, y), times); // a picture's lines set across the page
            }
        });
        BlocksOptions Options(bool pin) => new()
        {
            SidebarPages = new HashSet<int> { 1 },
            SidebarStarts = new Dictionary<int, int> { [1] = 1 },
            SidebarTitles = new Dictionary<int, IReadOnlyList<string>> { [1] = new[] { "Box Title" } },
            PinnedGutters = pin ? new Dictionary<int, double> { [1] = 300 } : new Dictionary<int, double>(),
        };

        var unpinned = BlocksStage.Run(pages, Options(false));
        Assert.Equal(new[] { 1 }, unpinned.SidebarProblems!); // the check that the bookmarks back the split fires: no heading matched the title
        Assert.Empty(unpinned.Dropped!);

        var pinned = BlocksStage.Run(pages, Options(true));
        Assert.Empty(pinned.SidebarProblems!);
        Assert.Equal(new[] { "BOX TITLE", "Words of the box." }, pinned.Dropped!.Select(d => d.Lines[0]));
        Assert.DoesNotContain(pinned.Blocks, b => b.Lines.Any(l => l.Contains("Words of the box", StringComparison.Ordinal)));
        Assert.Contains(pinned.Blocks, b => b.Lines.Any(l => l.StartsWith("Right column words", StringComparison.Ordinal)));
    }

    // --- the bookmarks below the taken sections, counted ---

    private static SpineNode Node(int depth, string title, params string[] path) => new(depth, title, path, 1, 1, false);

    private static readonly List<SpineNode> CoverSpine = new()
    {
        Node(0, "Book", "Book"), Node(1, "Part", "Book", "Part"), Node(2, "Alpha", "Book", "Part", "Alpha"), Node(3, "Alpha One", "Book", "Part", "Alpha", "Alpha One"),
        Node(2, "Beta", "Book", "Part", "Beta"), Node(3, "Beta One", "Book", "Part", "Beta", "Beta One"),
        new(2, "Sidebar: Side Box", new[] { "Book", "Part", "Sidebar: Side Box" }, 1, 1, true), new(2, "Sidebars: Gamma Box & Delta Box", new[] { "Book", "Part", "Sidebars: Gamma Box & Delta Box" }, 1, 1, true),
        Node(1, "Next", "Book", "Next"),
    };

    private static List<Block> CoverBlocks() => new()
    {
        Plain("section", 700, "Part"), Plain("rules", 690, "Own words of the part."),
        Plain("entry-header", 660, "Alpha"), Plain("rules", 650, "Words of alpha."), Plain("block-header", 640, "Alpha One"), Plain("rules", 630, "Words of the part of alpha."),
        Plain("entry-header", 600, "Beta"), Plain("rules", 590, "Words of beta."), Plain("entry-header", 580, "Beta One"), Plain("rules", 570, "Words of the part of beta."),
        Plain("section", 500, "Next"), Plain("rules", 480, "Words after."),
    };

    private static Block Plain(string role, double top, string text) => new(1, 0, role, 60, 0, 300, top, new List<string> { text }, new List<LineInfo> { new(60, 299, string.Empty) });

    [Fact]
    public void EveryBookmarkBelowATakenSection_IsAnEntrysOwn_InsideATakenPath_OrNamedAsLeftOut_AndTheCountsAreTheDocumentsOwn()
    {
        const string Row = "rows:\n  - {{ chapter: a, book: pc, parser: rules, target: rule, category: Test, approved: true, subheadings: true, own: [[\"Book\", \"Part\"]], paths: [[\"Book\", \"Part\", \"Alpha\"]], {0}sidebars: [[\"Book\", \"Part\", \"Sidebars: Gamma Box & Delta Box\", \"Delta Box\"]] }}\n";
        var book = new BlocksResult(CoverBlocks(), 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());
        List<TakenRange> Ranges(string extra) => TakeStage.Resolve(CoverSpine, book, TakeList.Parse(string.Format(Row, extra)).Rows);

        // The part's own text and alpha are entries, alpha's heading is inside alpha's path, beta and its heading are named as left out; of the three sidebars one is taken.
        var covered = Assert.Single(ImportPipeline.CheckBookmarkCoverage(CoverSpine, Ranges("leftOutSections: [[\"Book\", \"Part\", \"Beta\"]], ")));
        Assert.Equal(new BookmarkCoverage(5, 2, 1, 2, 1, 2, "a"), covered);

        // A row that gives no list is not counted this way; a row whose list leaves a section out fails the run, and so does a name that is no section below the taken ones.
        Assert.Empty(ImportPipeline.CheckBookmarkCoverage(CoverSpine, Ranges(string.Empty)));
        Assert.Contains("forgot", Assert.Throws<DegreesException>(() => ImportPipeline.CheckBookmarkCoverage(CoverSpine, Ranges("leftOutSections: [], "))).Message);
        Assert.Contains("below no section", Assert.Throws<DegreesException>(() => ImportPipeline.CheckBookmarkCoverage(CoverSpine, Ranges("leftOutSections: [[\"Book\", \"Next\"]], "))).Message);
        Assert.Contains("not in the book", Assert.Throws<DegreesException>(() => ImportPipeline.CheckBookmarkCoverage(CoverSpine, Ranges("leftOutSections: [[\"Book\", \"Part\", \"Nothing\"]], "))).Message);
    }
}
