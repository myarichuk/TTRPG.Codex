using Codex.ContentImport.Degrees;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Codex.Tests;

/// <summary>
/// Chapter 5a's spells over synthetic blocks (invented words only): the header with its kind word, the stat lines with a second label in the middle of a line, the range of action costs, the
/// boxes among the spells, the id suffix, the four lists as the independent count, the section counts, and the changes to the shared stages that the chapter needed. CI never sees a book.
/// </summary>
public class DegreesSpellsTests
{
    private sealed record L(string Text, double Right = 300, string Label = "", string[]? Bold = null);

    private static Block B(string role, double top, params L[] lines) =>
        new(1, 0, role, 60, 0, lines.Max(l => l.Right), top, lines.Select(l => l.Text).ToList(), lines.Select(l => new LineInfo(60, l.Right, l.Label, null, l.Bold?.ToList())).ToList());

    private static Block Plain(string role, double top, string text) => B(role, top, new L(text, 200));

    private static BlocksResult Book(params Block[] blocks) =>
        new(blocks.ToList(), 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static readonly List<SpineNode> Spine = new()
    {
        new(0, "Chapter 9: Charms", new[] { "Chapter 9: Charms" }, 1, 1, false),
        new(1, "Spell Lists", new[] { "Chapter 9: Charms", "Spell Lists" }, 1, 1, false),
        new(2, "Arcane Spells", new[] { "Chapter 9: Charms", "Spell Lists", "Arcane Spells" }, 1, 1, false),
        new(2, "Spells", new[] { "Chapter 9: Charms", "Spell Lists", "Spells" }, 1, 1, false),
    };

    private const string RowsYaml = """
        rows:
          - { chapter: charms, book: pc, parser: spelllists, target: spell, approved: true, columns: 1, paths: [["Chapter 9: Charms", "Spell Lists", "Arcane Spells"]] }
          - { chapter: charms, book: pc, parser: entries, target: spell, category: Spells, approved: true, columns: 1, headerRole: block-header, levelWord: "spell|cantrip", boxes: ["The Spark Trait", "Zip"], paths: [["Chapter 9: Charms", "Spell Lists", "Spells"]] }
        """;

    private static List<TakeRow> Rows() => TakeList.Parse(RowsYaml).Rows;

    private static BlocksResult Charms(bool bindListed = true) => Book(
        Plain("section", 720, "CHAPTER 9:"),
        Plain("section", 700, "CHARMS"),
        Plain("section", 680, "Spell Lists"),
        Plain("section", 660, "Arcane Spells"),
        Plain("entry-header", 640, "Arcane Cantrips"),
        B("body", 625, new L("Glow A small light.", 200, "Glow")),
        Plain("entry-header", 600, "Arcane 1st-Rank Spells"),
        bindListed
            ? B("body", 585, new L("Zap A sharp bolt.", 200, "Zap"), new L("Bind Hold a creature in place.", 200, "Bind"), new L("Zip Dash ahead.", 200, "Zip"))
            : B("body", 585, new L("Zap A sharp bolt.", 200, "Zap"), new L("Zip Dash ahead.", 200, "Zip")),
        Plain("section", 560, "Spells"),
        Plain("block-header", 540, "GLOW [one-action] CANTRIP 1"),
        Plain("tag", 525, "LIGHT"),
        B("body", 510, new L("Traditions arcane", 150, "Traditions"), new L("Range 35 feet; Targets 2 widgets", 220, "Range", new[] { "Targets" }), new L("You make a light.", 150), new L("Heightened (+1) The light grows.", 250, "Heightened (+1)")),
        Plain("block-header", 470, "ZAP [two-actions] SPELL 1"),
        Plain("tag", 455, "FIRE FORCE"),
        B("body", 440, new L("Traditions arcane", 150, "Traditions"), new L("Cast 4 hours; Requirements a stone", 220, "Cast", new[] { "Requirements" }), new L("You fire a bolt.", 150), new L("Critical Success It misses.", 200, "Critical Success")),
        Plain("block-header", 410, "THE SPARK TRAIT"),
        B("body", 395, new L("An invented box of rules.", 150)),
        Plain("block-header", 370, "BIND [one-action] TO [three-actions] SPELL 1"),
        Plain("tag", 355, "FORCE"),
        B("body", 340, new L("Traditions arcane", 150, "Traditions"), new L("You hold it.", 150)),
        Plain("block-header", 300, "ZIP"),
        B("body", 285, new L("Another invented box.", 150)),
        Plain("block-header", 260, "ZIP SPELL 1"),
        Plain("tag", 245, "AIR"),
        B("body", 230, new L("Traditions arcane", 150, "Traditions"), new L("You dash.", 150)));

    private static PipelineOutput Run(BlocksResult blocks) => ImportPipeline.Run(blocks, Spine, Rows(), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

    [Fact]
    public void TheHeaderWordIsTheKind_TheNumberTheRank_AndTheStatLinesFillTheTypesFields()
    {
        var result = Run(Charms());

        var glow = result.Final.Single(e => e.Id == "glow");
        Assert.Equal(("spell", "cantrip", 1, "1"), (glow.Type, glow.Kind, glow.Level, glow.Actions));
        Assert.Equal(new[] { "light" }, glow.Traits);
        Assert.Equal(new[] { "arcane" }, glow.Traditions);
        // A line of the shape "Label value; Label value" is two fields: the second label is bold in the middle of the line, and the semicolon between them is not text.
        Assert.Equal(new[] { ("range", "35 feet"), ("targets", "2 widgets") }, glow.Extracted.Select(x => (x.Label, x.Text)));
        Assert.Equal("You make a light.\n**Heightened (+1).** The light grows.", glow.Description);

        var zap = result.Final.Single(e => e.Id == "zap");
        Assert.Equal(("spell", 1, "2"), (zap.Kind, zap.Level, zap.Actions));
        Assert.Equal(new[] { ("castTime", "4 hours"), ("requirements", "a stone") }, zap.Extracted.Select(x => (x.Label, x.Text)));
        Assert.Equal(new[] { "fire", "force" }, zap.Traits);
        Assert.Equal("You fire a bolt.\n**Critical Success.** It misses.", zap.Description);
    }

    [Fact]
    public void ARangeOfActionCostsIsTheCastTime_NotAnActionCost()
    {
        var bind = Run(Charms()).Final.Single(e => e.Id == "bind");

        Assert.Null(bind.Actions);
        Assert.Equal(("castTime", "1 to 3 actions"), (bind.Extracted.Single().Label, bind.Extracted.Single().Text));
        Assert.Equal("Bind", bind.Name); // the word between the two icons is not a part of the name
    }

    [Fact]
    public void ABoxAmongTheSpellsIsARuleOfItsOwn_AndASpellNamedLikeAnotherEntryTakesTheSuffix()
    {
        var result = Run(Charms());

        var box = result.Final.Single(e => e.Id == "the-spark-trait");
        Assert.Equal(("rule", "Spells", "An invented box of rules."), (box.Type, box.Category, box.Description));
        Assert.Equal(new[] { "zip", "zip-spell" }, result.Final.Where(e => e.Id.StartsWith("zip", StringComparison.Ordinal)).Select(e => e.Id).Order());
        Assert.Equal("spell", result.Final.Single(e => e.Id == "zip-spell").Type);
        Assert.Equal("rule", result.Final.Single(e => e.Id == "zip").Type);
        Assert.Null(result.Final.Single(e => e.Id == "glow").Category); // the row's category is for the rules it writes, not for the spells
    }

    [Fact]
    public void AnIdThatHasShippedIsNeverRenamed_ALaterEntryWithTheSameNameTakesTheSuffixInstead()
    {
        // The committed pack already has a spell "zip": a later chapter's rule of the same name must not take it, so the rule is the one that is suffixed.
        var shipped = new Dictionary<string, string> { ["zip"] = "spells" };
        var result = ImportPipeline.Run(Charms(), Spine, Rows(), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc", shipped);

        Assert.Equal("spell", result.Final.Single(e => e.Id == "zip").Type);
        Assert.Equal("rule", result.Final.Single(e => e.Id == "zip-rule").Type);
        Assert.DoesNotContain(result.Final, e => e.Id == "zip-spell");

        // With a rule that shipped under the id, or with nothing shipped, the spell takes the suffix, as in the first run.
        Assert.Contains(ImportPipeline.Run(Charms(), Spine, Rows(), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc", new Dictionary<string, string> { ["zip"] = "rules" }).Final, e => e.Id == "zip-spell");
        Assert.Contains(Run(Charms()).Final, e => e.Id == "zip-spell");
    }

    [Fact]
    public void ABoxTheRowDoesNotNameIsReadPastOnlyWhenTheRowCountsIt()
    {
        var unnamed = TakeList.Parse(RowsYaml.Replace("boxes: [\"The Spark Trait\", \"Zip\"]", "boxes: [\"The Spark Trait\"]")).Rows;
        var ex = Assert.Throws<DegreesException>(() => ImportPipeline.Run(Charms(), Spine, unnamed, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc"));
        Assert.Contains("holds 1 box(es) the row does not take, but the row says it reads 0 past", ex.Message);

        var counted = TakeList.Parse(RowsYaml.Replace("boxes: [\"The Spark Trait\", \"Zip\"]", "boxes: [\"The Spark Trait\"], readPastBoxes: 1")).Rows;
        var result = ImportPipeline.Run(Charms(), Spine, counted, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");
        Assert.DoesNotContain(result.Final, e => e.Id == "zip" && e.Type == "rule");
        Assert.Contains(result.Final, e => e.Id == "zip" && e.Type == "spell"); // no clash now: the box is not an entry
        Assert.Equal(1, EntriesStage.ReadPastBoxBlocks(counted[1], result.Ranges.Single(r => r.Row.Parser == "entries").BlocksIn(Charms())).Count(b => b.Role == "block-header"));
    }

    [Fact]
    public void TheFourListsAreTheIndependentCount_AndASpellMissingFromAListFailsTheRun()
    {
        var counts = ImportPipeline.CheckSpellCounts(Charms(), Run(Charms()).Ranges, Run(Charms()).Parsed)!;
        Assert.Equal((4, 4, 4, 4, 4), (counts.Headers, counts.TraitLines, counts.TraditionLines, counts.Listed, counts.Parsed));
        Assert.Equal(new[] { 4, 0, 0, 0 }, new[] { "arcane", "divine", "occult", "primal" }.Select(t => counts.PerTradition[t]));

        var ex = Assert.Throws<DegreesException>(() => Run(Charms(bindListed: false)));
        Assert.Contains("The arcane list names 3 spells and the parsed spells that say arcane are 4", ex.Message);
        Assert.Contains("1 parsed spell(s) are not listed", ex.Message);
    }

    [Fact]
    public void TheDegreeAndHeightenedGuard_FailsWhenALabelAtALineStartIsNotInAnEntry()
    {
        var result = Run(Charms());
        Assert.Equal((1, 1), ImportPipeline.CheckSpellDegrees(Charms(), result.Ranges, result.Parsed));

        // A degree label that opens a line but is not a spell's (it sits in a box that is read past): the importer's count and the parser's differ.
        var odd = Charms();
        odd.Blocks.Insert(odd.Blocks.Count - 6, B("body", 295, new L("Failure It breaks.", 150, "Failure")));
        var ex = Assert.Throws<DegreesException>(() => ImportPipeline.CheckSpellDegrees(odd, Run(Charms()).Ranges, result.Parsed));
        Assert.Contains("degree labels", ex.Message);
    }

    [Fact]
    public void ThePackFileHoldsKindRankActionsTraitsTraditionsAndTheFieldsInTheSystemsOrder()
    {
        var result = Run(Charms());
        var yaml = PackEmitter.Files(result.Final, result.Report)["spells/spells.yaml"];

        Assert.Contains("    kind: \"cantrip\"\n    rank: 1\n    actions: \"one\"\n    traits: [\"light\"]\n    traditions: [\"arcane\"]\n    range: \"35 feet\"\n    targets: \"2 widgets\"\n", yaml);
        Assert.Contains("    kind: \"spell\"\n    rank: 1\n    actions: \"two\"\n    traits: [\"fire\", \"force\"]\n    traditions: [\"arcane\"]\n    castTime: \"4 hours\"\n    requirements: \"a stone\"\n", yaml);
        Assert.Contains("- id: \"zip-spell\"", yaml);
        Assert.Contains("- id: \"the-spark-trait\"", PackEmitter.Files(result.Final, result.Report)["rules/rules.yaml"]);
    }

    [Fact]
    public void AnUnknownBoldRunAtALineStart_IsNotALabelOfASpell()
    {
        // "Damage" opens a wrapped line of a battle form's attack, set in bold: it is not a field of the spell.
        var book = Charms();
        book.Blocks[11] = B("body", 510, new L("Traditions arcane", 150, "Traditions"), new L("You make a light and strike, Damage 2d6 fire.", 250), new L("Damage 2d6 fire.", 150, "Damage"));
        var glow = Run(book).Final.Single(e => e.Id == "glow");

        Assert.Contains("Damage 2d6 fire.", glow.Description);
        Assert.DoesNotContain("**Damage", glow.Description);
    }

    [Fact]
    public void ASidebarBookmarkThatNamesTwoSidebarsWithAnd_IsTwoTitles_ButAnAmpersandWins()
    {
        // A part of a split on "and" is marked: it must match its heading exactly.
        Assert.Equal(new[] { "=Alpha", "=Beta" }, ImportPipeline.SidebarTitles("Sidebars: Alpha and Beta"));
        Assert.Equal(new[] { "Alpha", "Beta" }, ImportPipeline.SidebarTitles("Sidebars: Alpha & Beta"));
        Assert.Equal(new[] { "Alpha and Beta", "Gamma" }, ImportPipeline.SidebarTitles("Sidebars: Alpha and Beta & Gamma"));
        Assert.Equal(new[] { "Alpha and Beta" }, ImportPipeline.SidebarTitles("Sidebar: Alpha and Beta"));
    }

    // ---- the rules sections: own text with a sidebar bookmark first, the text that runs over a page break, the counts of children and headings

    private static readonly List<SpineNode> Sections = new()
    {
        new(0, "Root", new[] { "Root" }, 1, 2, false),
        new(1, "Sidebar: Box", new[] { "Root", "Sidebar: Box" }, 1, 1, true),
        new(1, "Sec A", new[] { "Root", "Sec A" }, 1, 1, false),
        new(2, "Part One", new[] { "Root", "Sec A", "Part One" }, 1, 1, false),
        new(1, "Sec B", new[] { "Root", "Sec B" }, 2, 2, false),
    };

    private static Block Page(int page, string role, double top, string text) => new(page, 0, role, 60, 0, 200, top, new List<string> { text }, new List<LineInfo> { new(60, 200, string.Empty) });

    private static BlocksResult SectionBook(bool extraHeading = false) => Book(
        Page(1, "section", 700, "Root"), Page(1, "rules", 680, "Own text of the root."), Page(1, "section", 660, "Sec A"), Page(1, "rules", 640, "Own text of A."),
        Page(1, "entry-header", 620, "Part One"), Page(1, "rules", 600, "Text of part one."),
        Page(2, "rules", 700, "Text of part one that runs over the page."),
        Page(2, "section", 680, "Sec B"), Page(2, "rules", 660, "Text of B."));

    private const string RunsHere = ", runsToTitle: [[Root, Sec A, Part One]]";

    private static List<TakeRow> SectionRows(string extra = "") => TakeList.Parse(
        "rows:\n  - { chapter: c, book: pc, parser: rules, target: rule, approved: true, columns: 1, own: [[Root]], ownName: Root text }\n" +
        "  - { chapter: c, book: pc, parser: rules, target: rule, approved: true, columns: 1" + extra + ", own: [[Root, Sec A]], paths: [[Root, Sec A, Part One], [Root, Sec B]] }\n").Rows;

    [Fact]
    public void AnOwnTextPathSkipsASidebarBookmarkBeforeItsFirstChild()
    {
        var blocks = SectionBook();
        var ranges = TakeStage.Resolve(Sections, blocks, SectionRows(RunsHere));

        var root = ranges.Single(r => r.Row.OwnName == "Root text");
        Assert.Equal(new[] { "Own text of the root." }, root.BlocksIn(blocks).Select(b => b.Lines[0]));
    }

    [Fact]
    public void TextThatRunsOverAPageBreakAboveTheNextTitleNeedsTheRowsSayAndThenEndsAtThatTitle()
    {
        var blocks = SectionBook();
        Assert.Contains("text sits above the next section's title", Assert.Throws<DegreesException>(() => TakeStage.Resolve(Sections, blocks, SectionRows())).Message);

        // The flag is per path: naming another section does not let this one run over, so a section that does not need it still fails when text sits above the next title.
        Assert.Contains("text sits above the next section's title", Assert.Throws<DegreesException>(() => TakeStage.Resolve(Sections, blocks, SectionRows(", runsToTitle: [[Root, Sec B]]"))).Message);

        var part = TakeStage.Resolve(Sections, blocks, SectionRows(RunsHere)).Single(r => r.Row.PathOf(r).SequenceEqual(new[] { "Root", "Sec A", "Part One" }));
        Assert.Equal(new[] { "Text of part one.", "Text of part one that runs over the page." }, part.BlocksIn(blocks).Select(b => b.Lines[0]));
    }

    [Fact]
    public void TheChildrenOfASectionAreCountedAgainstItsHeadings_AndAnUnnamedHeadingOrAMissingOneFailsTheRun()
    {
        var blocks = SectionBook();
        var ranges = TakeStage.Resolve(Sections, blocks, SectionRows(RunsHere));
        Assert.Equal(new[] { (2, 2), (1, 1) }, ImportPipeline.CheckRuleSectionCounts(Sections, blocks, ranges));

        // A heading in the section font with no bookmark (a section the take-list would have swallowed whole) makes the headings outnumber the children.
        var extra = SectionBook();
        extra.Blocks.Insert(7, Page(2, "section", 690, "Sec Unnamed"));
        Assert.Contains("2 bookmark children (sidebars left out) but 3 section headings", Assert.Throws<DegreesException>(() => ImportPipeline.CheckRuleSectionCounts(Sections, extra, ranges)).Message);
    }

    // ---- the table detector: a header line with two cells under which another such header comes is not a table

    private const string Roles = """
        rules:
          - { role: block-header, family: [Helvetica-Bold], size: [11, 11] }
          - { role: body, family: [Helvetica], size: [1, 11] }
        """;

    [Fact]
    public void TwoStatBlockHeadersInARowAreNotATable_ButARowOfABodyLineStillIs()
    {
        PdfDocumentBuilder Build(bool tableRow, bool entryHeader = false)
        {
            var b = new PdfDocumentBuilder();
            var hel = b.AddStandard14Font(Standard14Font.Helvetica);
            var bold = b.AddStandard14Font(Standard14Font.HelveticaBold);
            var page = b.AddPage(603, 783);
            page.AddText("Wolf", 11, new PdfPoint(60, 700), bold);
            page.AddText(entryHeader ? "FOCUS 1" : "BADGE 1", 11, new PdfPoint(200, 700), bold);
            page.AddText("A short line.", 9, new PdfPoint(60, 688), hel);
            if (tableRow)
            {
                page.AddText("Left", 9, new PdfPoint(60, 676), hel);
                page.AddText("Right", 9, new PdfPoint(200, 676), hel);
            }
            else
            {
                page.AddText("Fox", 11, new PdfPoint(60, 676), bold);
                page.AddText("BADGE 2", 11, new PdfPoint(200, 676), bold);
            }

            return b;
        }

        using (var doc = PdfDocument.Open(Build(false).Build()))
        {
            var blocks = BlocksStage.Run(LayoutStage.Run(doc, FontRoles.Parse(Roles)).Pages);
            Assert.Equal(0, blocks.Tables);
            Assert.Equal(2, blocks.RejectedTableHeaders);
        }

        using (var doc = PdfDocument.Open(Build(true).Build()))
        {
            Assert.Equal(1, BlocksStage.Run(LayoutStage.Run(doc, FontRoles.Parse(Roles)).Pages).Tables);
        }

        // An entry's header (the name, then the kind word and rank in a cell of its own) is never a table's header row, whatever sits under it: a focus spell's header over a stat line of two cells.
        using (var doc = PdfDocument.Open(Build(true, entryHeader: true).Build()))
        {
            var blocks = BlocksStage.Run(LayoutStage.Run(doc, FontRoles.Parse(Roles)).Pages);
            Assert.Equal(0, blocks.Tables);
            Assert.Contains(blocks.Blocks, b => b.Role == "block-header" && b.Lines[0].EndsWith("FOCUS 1", StringComparison.Ordinal));
        }
    }
}
