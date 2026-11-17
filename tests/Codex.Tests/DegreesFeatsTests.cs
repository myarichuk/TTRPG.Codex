using Codex.ContentImport.Degrees;

namespace Codex.Tests;

/// <summary>
/// Chapter 3's feats row over synthetic blocks (invented words only): a header in the block-header font, set in capitals and ending in a word and a level; the lead before the first
/// header read past; the trait line, the labelled fields and the stray label of the page's art; and the three counts, which must agree. CI never sees a book.
/// </summary>
public class DegreesFeatsTests
{
    private static Block Make(int page, int column, string role, double top, params (string Text, double Right, string Label)[] lines) =>
        new(page, column, role, 60, 0, 300, top, lines.Select(l => l.Text).ToList(), lines.Select(l => new LineInfo(60, l.Right, l.Label)).ToList());

    private static Block Header(double top, string text) => Make(1, 0, "block-header", top, (text, 200, string.Empty));

    private static Block Tags(double top, string text) => Make(1, 0, "tag", top, (text, 120, string.Empty));

    private static BlocksResult Book(params Block[] blocks) =>
        new(blocks.ToList(), 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static readonly List<SpineNode> Spine = new() { new(0, "Chapter 9: Widgets", new[] { "Chapter 9: Widgets" }, 1, 1, false) };

    private static List<TakeRow> Rows(string extra = "") =>
        TakeList.Parse("rows:\n  - { chapter: widgets, book: pc, parser: entries, target: feat, approved: true, columns: 1, headerRole: block-header, levelWord: feat, paths: [[\"Chapter 9: Widgets\"]]" + extra + " }\n").Rows;

    private static BlocksResult TwoFeats() => Book(
        Make(1, 0, "section", 720, ("CHAPTER 9:", 150, string.Empty)),
        Make(1, 0, "section", 700, ("WIDGETS", 150, string.Empty)),
        Make(1, 0, "block-header", 600, ("OVERVIEW OF THINGS", 200, string.Empty)),
        Make(1, 0, "rules", 580, ("An invented introduction that the row reads past.", 250, string.Empty)),
        Make(1, 0, "body", 560, ("Things Level Notes", 250, string.Empty), ("Small Leap 3 Hop a bit", 250, string.Empty), ("Boon of the Wind 1 Get a boon", 250, string.Empty), ("Make a Wish with 2", 250, string.Empty)),
        Header(500, "SMALL LEAP [one-action] FEAT 3"),
        Tags(485, "GENERAL SKILL"),
        Make(1, 0, "body", 470, ("Prerequisites trained in Hopping", 150, "Prerequisites"), ("You hop a little way. Roll a check.", 300, string.Empty), ("Success You land well.", 200, "Success")),
        new(1, 0, "body", 80, 0, 190, 400, new List<string> { "ZZ-WIDGET" }, new List<LineInfo> { new(80, 190, string.Empty) }),
        Header(380, "BOON OF THE WIND FEAT 1"),
        Tags(365, "GENERAL"),
        Make(1, 0, "body", 350, ("Trigger A thing happens.", 150, "Trigger"), ("You get a boon.", 150, string.Empty)));

    [Fact]
    public void TheHeaderGivesTheNameInTitleCase_TheLevelAndTheActionCost_AndTheLeadIsReadPast()
    {
        var result = ImportPipeline.Run(TwoFeats(), Spine, Rows(), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

        var leap = result.Final.Single(e => e.Id == "small-leap");
        Assert.Equal(("Small Leap", 3, "1"), (leap.Name, leap.Level, leap.Actions));
        Assert.Equal(new[] { "general", "skill" }, leap.Traits);
        Assert.Equal(("prerequisites", "trained in Hopping"), (leap.Extracted.Single().Label, leap.Extracted.Single().Text));
        Assert.Contains("You hop a little way.", leap.Description);
        Assert.Contains("**Success.** You land well.", leap.Description);
        Assert.DoesNotContain("Prerequisites", leap.Description);
        Assert.DoesNotContain("ZZ-WIDGET", leap.Description); // a lone line in capitals set in from the column edge is not entry text

        var boon = result.Final.Single(e => e.Id == "boon-of-the-wind");
        Assert.Equal(("Boon of the Wind", 1, null), (boon.Name, boon.Level, boon.Actions));
        Assert.Equal("trigger", boon.Extracted.Single().Label);
        Assert.Equal("You get a boon.", boon.Description);
        Assert.DoesNotContain(result.Final, e => e.Description.Contains("introduction"));
        Assert.Equal(2, result.Ranges.Single().BlocksIn(TwoFeats()).Count(b => b.Role == "tag"));
    }

    [Fact]
    public void ThePackFileHoldsTheLevelActionsTraitsAndFieldsAsProperties()
    {
        var result = ImportPipeline.Run(TwoFeats(), Spine, Rows(), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");
        var yaml = PackEmitter.Files(result.Final, result.Report)["feats/feats.yaml"];

        Assert.Contains("    level: 3\n    actions: \"one\"\n    traits: [\"general\", \"skill\"]\n    prerequisites: \"trained in Hopping\"\n", yaml);
        Assert.Contains("    level: 1\n    traits: [\"general\"]\n    trigger: \"A thing happens.\"\n", yaml);
    }

    [Fact]
    public void ARedactionRowCanNameAnExtractedField()
    {
        var sentence = "trained in Hopping";
        var table = RedactionTable.Parse($"normalization: 2\nrows:\n  - book: pc\n    entry: small-leap\n    field: prerequisites\n    span: {{ sha256: \"{SpanText.Sha256(sentence)}\", length: {sentence.Length} }}\n    action: replace\n    text: \"trained in jumping\"\n    reason: \"A test.\"\n");
        var result = ImportPipeline.Run(TwoFeats(), Spine, Rows(), table, "pc");

        Assert.Equal("trained in jumping", result.Final.Single(e => e.Id == "small-leap").Extracted.Single().Text);
    }

    [Fact]
    public void TheThreeCountsMustAgree_AndAHeaderWithoutALevelIsNotAHeader()
    {
        var blocks = TwoFeats();
        Assert.Equal(new[] { (2, 2, 2, 2) }, ImportPipeline.CheckLevelCounts(blocks, TakeStage.Resolve(Spine, blocks, Rows())));

        // A trait line with no header (or a header with no trait line) makes the counts differ, and the run fails with the three numbers.
        var odd = Book(blocks.Blocks.Concat(new[] { Tags(300, "GENERAL") }).ToArray());
        var ex = Assert.Throws<DegreesException>(() => ImportPipeline.CheckLevelCounts(odd, TakeStage.Resolve(Spine, odd, Rows())));
        Assert.Contains("2 header blocks, 3 trait-line blocks and 2 level markers", ex.Message);

        // The row's reason lets them differ.
        var reasoned = Rows(", countsDiffer: \"a stray trait line\"");
        Assert.Single(ImportPipeline.CheckLevelCounts(odd, TakeStage.Resolve(Spine, odd, reasoned)));

        Assert.False(Rows().Single().IsHeader(Make(1, 0, "block-header", 600, ("OVERVIEW OF THINGS", 200, string.Empty))));
        Assert.True(Rows().Single().IsHeader(Header(500, "SMALL LEAP FEAT 12")));
    }

    [Fact]
    public void TheOverviewTablesOfTheLeadAreAnIndependentCount_AndAnEntryMissingFromTheHeadersFailsTheRun()
    {
        var blocks = TwoFeats();
        // Drop the second feat's header, trait line and body: the headers, trait lines and markers all fall to 1 together, but the tables still list two.
        var fewer = Book(blocks.Blocks.Take(blocks.Blocks.Count - 3).ToArray());
        var ex = Assert.Throws<DegreesException>(() => ImportPipeline.CheckLevelCounts(fewer, TakeStage.Resolve(Spine, fewer, Rows())));
        Assert.Contains("1 header blocks, 1 trait-line blocks and 1 level markers", ex.Message);
        Assert.Contains("overview tables list 2 entries", ex.Message);
    }

    [Fact]
    public void ATitleInTwoSectionBlocksIsFound_AndAChapterWithNoHeaderFails()
    {
        var none = Book(
            Make(1, 0, "section", 720, ("CHAPTER 9:", 150, string.Empty)),
            Make(1, 0, "section", 700, ("WIDGETS", 150, string.Empty)),
            Make(1, 0, "rules", 600, ("Only an introduction.", 150, string.Empty)));
        Assert.Contains("first entry is unknown", Assert.Throws<DegreesException>(() => TakeStage.Resolve(Spine, none, Rows())).Message);

        var found = TakeStage.Resolve(Spine, TwoFeats(), Rows()).Single();
        Assert.Equal(5, found.Start);
        Assert.Equal(2, found.LeadStart);
        Assert.True(found.InLead(3));
    }

    [Theory]
    [InlineData("ZORB FLIMP", "Zorb Flimp")]
    [InlineData("WAVE AT THE HILL", "Wave at the Hill")]
    [InlineData("QUIB-SNAP", "Quib-Snap")]
    [InlineData("AN OWL OF TIN", "An Owl of Tin")]
    public void ANameSetInCapitalsIsPutInTitleCase(string upper, string expected) => Assert.Equal(expected, EntriesStage.TitleCase(upper));
}
