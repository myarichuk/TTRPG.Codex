using Codex.ContentImport.Degrees;

namespace Codex.Tests;

/// <summary>
/// Chapter 9's options of the take-list over synthetic blocks (invented words only): a path named by the hash of a title, one sidebar taken from a bookmark that holds two, a section whose children in the section font are
/// paragraphs of its entry, the labels of a picture and the pieces of a sidebar left out, the groups of actions, and an action whose requirement ran into its text. Every gate has a test. CI never sees a book.
/// </summary>
public class DegreesPlayingTests
{
    private static Block Lines(string role, double top, params (double Left, string Label, string Text)[] lines) =>
        new(1, 0, role, lines.Min(l => l.Left), 0, 300, top, lines.Select(l => l.Text).ToList(), lines.Select(l => new LineInfo(l.Left, 299, l.Label)).ToList());

    private static Block Plain(string role, double top, string text) => Lines(role, top, (60, string.Empty, text));

    private static SpineNode Node(int depth, string title, params string[] path) => new(depth, title, path, 1, 1, false);

    private static BlocksResult Book(List<Block> blocks) =>
        new(blocks, 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static (BlocksResult Blocks, List<TakenRange> Ranges, List<ParsedEntry> Parsed) Parse(string yaml, List<SpineNode> spine, List<Block> blocks)
    {
        var book = Book(blocks);
        var ranges = TakeStage.Resolve(spine, book, TakeList.Parse(yaml).Rows);
        return (book, ranges, EntriesStage.Run(book, ranges, "pc", null, spine));
    }

    [Fact]
    public void APathNamedByTheHashOfATitle_FindsItsBookmark_NamesTheEntryByTheTitle_AndLeavesTheHashOutOfTheSource()
    {
        var spine = new List<SpineNode> { Node(0, "Book", "Book"), Node(1, "The Quiet Gate", "Book", "The Quiet Gate"), Node(1, "Next Part", "Book", "Next Part") };
        var hash = SpineStage.HashSegment("The Quiet Gate");
        var blocks = new List<Block>
        {
            Plain("section", 700, "The Quiet Gate"), Plain("rules", 680, "Words of the first part."),
            Plain("section", 600, "Next Part"), Plain("rules", 580, "Words of the second part."),
        };

        var (_, _, parsed) = Parse($"rows:\n  - {{ chapter: a, book: pc, parser: rules, target: rule, category: Test, approved: true, paths: [[\"Book\", \"{hash}\"]] }}\n", spine, blocks);

        var entry = Assert.Single(parsed);
        Assert.Equal("The Quiet Gate", entry.Name); // the title, until a redaction row renames the entry (by the same hash, as its id's)
        Assert.Equal("the-quiet-gate", entry.Id);
        Assert.Equal("pc > Book", entry.Source); // the hash is not in the source
        Assert.Equal('#' + SpanText.Sha256(entry.Id), hash);
        Assert.True(SpineStage.IsHashSegment(hash));
        Assert.False(SpineStage.IsHashSegment("The Quiet Gate"));
        Assert.Null(SpineStage.FindUnique(spine, new[] { "Book", "#" + new string('0', 64) }));
    }

    [Fact]
    public void OneSidebarOfABookmarkThatHoldsTwoIsNamedByTheTitleAfterItsPath_AndAnythingElseIsNoSidebar()
    {
        var spine = new List<SpineNode>
        {
            Node(0, "Book", "Book"),
            new(1, "Sidebars: Alpha Box & Beta Box", new[] { "Book", "Sidebars: Alpha Box & Beta Box" }, 1, 1, true),
            new(1, "Sidebar: Single Box", new[] { "Book", "Sidebar: Single Box" }, 1, 1, true),
            Node(1, "Plain Part", "Book", "Plain Part"),
        };

        var beta = ImportPipeline.ResolveSidebar(spine, new[] { "Book", "Sidebars: Alpha Box & Beta Box", "Beta Box" });
        var single = ImportPipeline.ResolveSidebar(spine, new[] { "Book", "Sidebar: Single Box" });

        Assert.Equal("Beta Box", beta!.Value.Title);
        Assert.Equal("Sidebars: Alpha Box & Beta Box", beta.Value.Node.Title);
        Assert.Equal("Single Box", single!.Value.Title);
        Assert.Null(ImportPipeline.ResolveSidebar(spine, new[] { "Book", "Sidebars: Alpha Box & Beta Box" })); // two sidebars: the path must say which
        Assert.Null(ImportPipeline.ResolveSidebar(spine, new[] { "Book", "Sidebars: Alpha Box & Beta Box", "Gamma Box" }));
        Assert.Null(ImportPipeline.ResolveSidebar(spine, new[] { "Book", "Plain Part" }));
        Assert.Null(ImportPipeline.ResolveSidebar(spine, new[] { "Book", "Plain Part", "Alpha Box" }));
        Assert.Throws<DegreesException>(() => TakeList.Parse("rows:\n  - { chapter: a, book: pc, parser: rules, target: rule, approved: true, entryNames: { Other: X }, sidebars: [[\"Book\", \"Sidebar: Single Box\"]], paths: [[\"Book\", \"Plain Part\"]] }\n"));
        Assert.Single(TakeList.Parse("rows:\n  - { chapter: a, book: pc, parser: rules, target: rule, approved: true, entryNames: { \"Sidebar: Single Box\": Renamed }, sidebars: [[\"Book\", \"Sidebar: Single Box\"]], paths: [[\"Book\", \"Plain Part\"]] }\n").Rows);
    }

    [Fact]
    public void ASectionWhoseChildrenAreInTheSectionFontFoldsThemAsParagraphs_AndTheirCountIsGuarded()
    {
        List<SpineNode> Spine(bool withThird) => new List<SpineNode>
        {
            Node(0, "Book", "Book"), Node(1, "Overview", "Book", "Overview"), Node(2, "Alpha", "Book", "Overview", "Alpha"), Node(2, "Beta", "Book", "Overview", "Beta"),
            Node(2, "Gamma", "Book", "Overview", "Gamma"), Node(1, "After", "Book", "After"),
        }.Where(n => withThird || n.Title != "Gamma").ToList();
        var blocks = new List<Block>
        {
            Plain("section", 700, "Overview"), Plain("rules", 680, "Words of the overview."),
            Plain("section", 640, "Alpha"), Plain("rules", 620, "Words of alpha."),
            Plain("section", 580, "Beta"), Plain("rules", 560, "Words of beta."),
            Plain("section", 500, "After"), Plain("rules", 480, "Words after."),
        };
        const string Row = "rows:\n  - {{ chapter: a, book: pc, parser: rules, target: rule, category: Test, approved: true, foldSections: true, {0}paths: [[\"Book\", \"Overview\"]] }}\n";

        var (book, ranges, parsed) = Parse(string.Format(Row, string.Empty), Spine(false), blocks);

        Assert.Equal("Words of the overview.\nAlpha\nWords of alpha.\nBeta\nWords of beta.", Assert.Single(parsed).Render());
        Assert.Equal((2, 2), Assert.Single(ImportPipeline.CheckFoldCounts(Spine(false), book, ranges)));
        // A third bookmark with no heading fails the count, unless the row says why it differs.
        var (book3, ranges3, _) = Parse(string.Format(Row, string.Empty), Spine(true), blocks);
        Assert.Throws<DegreesException>(() => ImportPipeline.CheckFoldCounts(Spine(true), book3, ranges3));
        var (bookWhy, rangesWhy, _) = Parse(string.Format(Row, "countsDiffer: the third has no heading, "), Spine(true), blocks);
        Assert.Empty(ImportPipeline.CheckFoldCounts(Spine(true), bookWhy, rangesWhy));
        // A row folds one font or the other, and only the rules parser folds.
        Assert.Throws<DegreesException>(() => TakeList.Parse(string.Format(Row, "foldChildren: true, ")));
        Assert.Throws<DegreesException>(() => TakeList.Parse("rows:\n  - { chapter: a, book: pc, parser: actions, target: action, approved: true, foldSections: true, paths: [[\"Book\"]] }\n"));
    }

    [Fact]
    public void TheLabelsOfAPictureAndThePiecesOfASidebarAreSkipped_AndHashedWithTheTextLeftOut()
    {
        var spine = new List<SpineNode> { Node(0, "Book", "Book"), Node(1, "Part", "Book", "Part"), Node(1, "Next", "Book", "Next") };
        var blocks = new List<Block>
        {
            Plain("section", 700, "Part"), Plain("rules", 680, "Words of the part."),
            Plain("display", 660, "7"), Plain("body", 640, "Odd Label"), Plain("block-header", 620, "5 feet"), Plain("block-header", 600, "Real Heading"),
            Plain("body", 580, "A body line that is a whole sentence of the part."),
            Plain("rules", 560, "More words of the part."), Plain("section", 500, "Next"), Plain("rules", 480, "Words after."),
        };
        const string Row = "rows:\n  - {{ chapter: a, book: pc, parser: rules, target: rule, category: Test, approved: true, subheadings: true, {0}: [1], paths: [[\"Book\", \"Part\"]] }}\n";

        var (book, ranges, parsed) = Parse(string.Format(Row, "artPages"), spine, blocks);

        var text = Assert.Single(parsed).Render();
        Assert.DoesNotContain("Odd Label", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5 feet", text, StringComparison.Ordinal);
        Assert.Contains("Real Heading", text, StringComparison.Ordinal); // a heading with words, a sentence in the body font and the rules text stay
        Assert.Contains("A body line that is a whole sentence of the part.", text, StringComparison.Ordinal);
        var left = TakeStage.LeftOut(book, ranges).Select(b => string.Join(' ', b.Lines)).ToList();
        Assert.Contains("Odd Label", left);
        Assert.Contains("5 feet", left);
        Assert.Contains("7", left);
        Assert.DoesNotContain("Real Heading", left);

        // On a page the row names as a sidebar's, every block in the body font is the sidebar's.
        var (spillBook, spillRanges, spilled) = Parse(string.Format(Row, "sidebarSpill: [1], artPages"), spine, blocks);
        var spillText = Assert.Single(spilled).Render();
        Assert.DoesNotContain("A body line that is a whole sentence of the part.", spillText, StringComparison.Ordinal);
        Assert.Contains("A body line that is a whole sentence of the part.", TakeStage.LeftOut(spillBook, spillRanges).Select(b => string.Join(' ', b.Lines)));
        Assert.Throws<DegreesException>(() => TakeList.Parse("rows:\n  - { chapter: a, book: pc, parser: entries, target: spell, approved: true, artPages: [1], paths: [[\"Book\"]] }\n"));
    }

    [Fact]
    public void TheGroupsOfActionsAreCountedAgainstTheirHeadings_AGroupWithNoTextMakesNoRule_AndARequirementThatRanIntoTheTextIsSplit()
    {
        var spine = new List<SpineNode>
        {
            Node(0, "Book", "Book"), Node(1, "Basics", "Book", "Basics"), Node(2, "Group One", "Book", "Basics", "Group One"), Node(2, "Group Two", "Book", "Basics", "Group Two"), Node(1, "Later", "Book", "Later"),
        };
        var blocks = new List<Block>
        {
            Plain("section", 700, "Basics"), Plain("rules", 680, "Actions are common tasks."),
            Plain("entry-header", 650, "Group One"),
            Plain("block-header", 630, "STEP ABOUT [one-action]"), Plain("tag", 610, "MOVE"),
            Lines("body", 590, (60, "Requirements", "Requirements You are ready. You step aside and the others do not.")),
            Plain("entry-header", 550, "Group Two"), Plain("rules", 530, "These are the odd ones."),
            Plain("section", 500, "Later"), Plain("rules", 480, "Words after."),
        };
        const string Row = "rows:\n  - {{ chapter: a, book: pc, parser: actions, target: action, category: Test, approved: true, leadName: Using Basics, {0}paths: [[\"Book\", \"Basics\"]] }}\n";

        var (book, ranges, parsed) = Parse(string.Format(Row, "actionGroups: true, "), spine, blocks);

        var action = Assert.Single(parsed, e => e.Type == "action");
        Assert.Equal("Step About", action.Name);
        Assert.Equal("You are ready.", Assert.Single(action.Extracted).Text); // the requirement is its one sentence
        Assert.Equal("You step aside and the others do not.", action.Render());
        Assert.Equal(new[] { "group-two", "using-basics" }, parsed.Where(e => e.Type == "rule").Select(e => e.Id).Order()); // Group One holds no text of its own: no entry
        var counts = ImportPipeline.CheckActionCounts(spine, book, ranges, parsed);
        Assert.Equal((2, 2, 1, 1, 1), (counts.Children, counts.Units, counts.Headers, counts.TraitLines, counts.Icons));
        // Without the option the two group headings are not the bookmarks' count of actions, and the run fails.
        var (plainBook, plainRanges, plainParsed) = Parse(string.Format(Row, string.Empty), spine, blocks);
        Assert.Throws<DegreesException>(() => ImportPipeline.CheckActionCounts(spine, plainBook, plainRanges, plainParsed));
        Assert.Throws<DegreesException>(() => TakeList.Parse("rows:\n  - { chapter: a, book: pc, parser: rules, target: rule, approved: true, actionGroups: true, paths: [[\"Book\"]] }\n"));
    }
}
