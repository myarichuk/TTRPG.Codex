using Codex.ContentImport.Degrees;

namespace Codex.Tests;

/// <summary>
/// Chapter 5b's focus spells and rituals over synthetic blocks (invented words only): the group a heading sets each entry under, the group headings counted against the bookmarks, the
/// ritual's labelled lines (cut at a semicolon whatever the bold runs say), the book's list by rank as the rituals' independent count, and the rows' own rules. CI never sees a book.
/// </summary>
public class DegreesFocusSpellsTests
{
    private sealed record L(string Text, double Right = 300, string Label = "", string[]? Bold = null, double Left = 60);

    private static Block B(string role, double top, params L[] lines) =>
        new(1, 0, role, 60, 0, lines.Max(l => l.Right), top, lines.Select(l => l.Text).ToList(), lines.Select(l => new LineInfo(l.Left, l.Right, l.Label, null, l.Bold?.ToList())).ToList());

    private static Block Plain(string role, double top, string text) => B(role, top, new L(text, 200));

    private static BlocksResult Book(params Block[] blocks) =>
        new(blocks.ToList(), 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static readonly List<SpineNode> Spine = new()
    {
        new(0, "Chapter 9: Charms", new[] { "Chapter 9: Charms" }, 1, 1, false),
        new(1, "Spell Lists", new[] { "Chapter 9: Charms", "Spell Lists" }, 1, 1, false),
        new(2, "Focus Spells", new[] { "Chapter 9: Charms", "Spell Lists", "Focus Spells" }, 1, 1, false),
        new(3, "Wild Charms", new[] { "Chapter 9: Charms", "Spell Lists", "Focus Spells", "Wild Charms" }, 1, 1, false),
        new(3, "Tamed Charms", new[] { "Chapter 9: Charms", "Spell Lists", "Focus Spells", "Tamed Charms" }, 1, 1, false),
        new(4, "Sub Group", new[] { "Chapter 9: Charms", "Spell Lists", "Focus Spells", "Tamed Charms", "Sub Group" }, 1, 1, false),
        new(1, "Rites", new[] { "Chapter 9: Charms", "Rites" }, 1, 1, false),
        new(2, "Rite List", new[] { "Chapter 9: Charms", "Rites", "Rite List" }, 1, 1, false),
    };

    private const string RowsYaml = """
        rows:
          - { chapter: charms, book: pc, parser: entries, target: spell, approved: true, columns: 1, headerRole: block-header, levelWord: "focus|cantrip", groups: true, paths: [["Chapter 9: Charms", "Spell Lists", "Focus Spells"]] }
          - { chapter: charms, book: pc, parser: entries, target: spell, approved: true, columns: 1, headerRole: block-header, levelWord: ritual, listHeading: "Rites by Rank", paths: [["Chapter 9: Charms", "Rites", "Rite List"]] }
        """;

    private static List<TakeRow> Rows() => TakeList.Parse(RowsYaml).Rows;

    private static BlocksResult Charms(bool swapListed = true, bool extraHeading = false) => Book(
        Plain("section", 720, "Focus Spells"),
        Plain("section", 700, "Wild Charms"),
        Plain("block-header", 680, "LEAP [one-action] FOCUS 1"),
        Plain("tag", 665, "UNCOMMON FOCUS LEAP"),
        B("body", 650, new L("Range 10 feet", 200, "Range"), new L("You leap.", 150)),
        Plain("block-header", 620, "BOUNCE [one-action] OR [two-actions] FOCUS 2"),
        Plain("tag", 605, "UNCOMMON FOCUS"),
        B("body", 590, new L("Saving Throw Will; Duration 1 round", 300, "Saving Throw", new[] { "Duration" }), new L("You bounce.", 150)),
        Plain("section", 560, "Tamed Charms"),
        Plain("block-header", 540, "TAME [reaction] FOCUS 4"),
        Plain("tag", 525, "UNCOMMON FOCUS"),
        B("body", 510, new L("Trigger Something happens.", 220, "Trigger"), new L("You tame it.", 150)),
        Plain("entry-header", 480, "Sub Group"),
        Plain("block-header", 460, "NIP [one-action] CANTRIP 3"),
        Plain("tag", 445, "UNCOMMON CANTRIP"),
        B("body", 430, new L("You nip.", 150)),
        extraHeading ? Plain("entry-header", 410, "Heading With No Bookmark") : Plain("body", 410, "Closing words."),
        Plain("section", 380, "Rites"),
        Plain("block-header", 360, "RITES BY RANK"),
        swapListed
            ? B("body", 345, new L("Rank Rite", 200), new L("2 swap", 200))
            : B("body", 345, new L("Rank Rite", 200), new L("2 other", 200)),
        Plain("section", 320, "Rite List"),
        Plain("block-header", 300, "SWAP RITUAL 2"),
        Plain("tag", 285, "UNCOMMON"),
        B("body", 270,
            new L("Cast 1 day; Cost a gem worth 5 gp; Secondary", 300, "Cast", new[] { "Cost", "Secondary" }),
            new L("Casters 2", 100, "Casters", null, 70), // a wrapped line is set in from the edge
            new L("Primary Check Arcana (expert); Secondary Checks Crafting", 300, "Primary Check", new[] { "Secondary" }),
            new L("Range touch", 100, "Range"),
            new L("You swap two things.", 150),
            new L("Critical Success It works.", 200, "Critical Success")));

    private static PipelineOutput Run(BlocksResult blocks, List<TakeRow>? rows = null) =>
        ImportPipeline.Run(blocks, Spine, rows ?? Rows(), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

    [Fact]
    public void AGroupHeadingIsTheGroupOfTheEntriesAfterIt_AndTheClassHeadingIsTheGroupOfWhatHasNoSubgroup()
    {
        var result = Run(Charms());

        Assert.Equal(new[] { ("leap", "Wild Charms"), ("bounce", "Wild Charms"), ("tame", "Tamed Charms"), ("nip", "Sub Group") },
            result.Final.Where(e => e.Id != "swap").OrderBy(e => e.Id == "leap" ? 0 : e.Id == "bounce" ? 1 : e.Id == "tame" ? 2 : 3).Select(e => (e.Id, e.Extracted.Single(x => x.Label == "focusGroup").Text)));
        // The header word is the kind and the number the rank; a cantrip of a group's list is a cantrip, whatever its group.
        Assert.Equal(new[] { ("focus", 1), ("focus", 2), ("focus", 4), ("cantrip", 3) },
            new[] { "leap", "bounce", "tame", "nip" }.Select(id => result.Final.Single(e => e.Id == id)).Select(e => (e.Kind!, e.Level!.Value)));
        Assert.Equal(new[] { "uncommon", "focus", "leap" }, result.Final.Single(e => e.Id == "leap").Traits);
        // The group is the first field, then the stat lines, in the system's order.
        Assert.Equal(new[] { "focusGroup", "range" }, result.Final.Single(e => e.Id == "leap").Extracted.Select(x => x.Label));
        Assert.Equal(new[] { "focusGroup", "trigger" }, result.Final.Single(e => e.Id == "tame").Extracted.Select(x => x.Label));
    }

    [Fact]
    public void ARangeOfCostsWithOrIsTheCastTimeToo()
    {
        var bounce = Run(Charms()).Final.Single(e => e.Id == "bounce");

        Assert.Null(bounce.Actions);
        Assert.Equal("1 or 2 actions", bounce.Extracted.Single(x => x.Label == "castTime").Text);
        Assert.Equal("Bounce", bounce.Name);
        // The older label of a saving throw is the defense field, and the second label of its line is cut as ever.
        Assert.Equal(new[] { ("castTime", "1 or 2 actions"), ("defense", "Will"), ("duration", "1 round") }, bounce.Extracted.Where(x => x.Label != "focusGroup").Select(x => (x.Label, x.Text)));
        Assert.Equal("You bounce.", bounce.Description);
    }

    [Fact]
    public void ARitualsLabelsAreCutAtTheSemicolon_WhateverTheBoldRunsSay_AndTheBooksListByRankIsTheCount()
    {
        var swap = Run(Charms()).Final.Single(e => e.Id == "swap");

        Assert.Equal(("spell", "ritual", 2, null), (swap.Type, swap.Kind, swap.Level, swap.Actions));
        // The second label of the first line is bold in two pieces (a word at the end of a line and the next word at the start of the next), and the third is a bold run of one word: neither is a cut by itself.
        Assert.Equal(new[] { ("castTime", "1 day"), ("cost", "a gem worth 5 gp"), ("secondaryCasters", "2"), ("primaryCheck", "Arcana (expert)"), ("secondaryChecks", "Crafting"), ("range", "touch") },
            swap.Extracted.Select(x => (x.Label, x.Text)));
        Assert.Equal("You swap two things.\n**Critical Success.** It works.", swap.Description);

        // A ritual that the book's list by rank does not name (or a listed one with no entry) fails the run.
        var ex = Assert.Throws<DegreesException>(() => Run(Charms(swapListed: false)));
        Assert.Contains("1 listed are not among them", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGroupHeadingsAreCountedAgainstTheSectionsBookmarks_AndAHeadingWithNoBookmarkFailsTheRun()
    {
        var counted = ImportPipeline.CheckEntryCounts(Spine, Charms(), TakeStage.Resolve(Spine, Charms(), Rows()));
        Assert.Equal((3, 3), counted.Single());

        var ex = Assert.Throws<DegreesException>(() => Run(Charms(extraHeading: true)));
        Assert.Contains("3 bookmark children", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupsAndAListHeadingBelongToAnEntriesRowOfSpellsWithALevelWord()
    {
        Assert.Throws<DegreesException>(() => TakeList.Parse("rows:\n  - { chapter: charms, book: pc, parser: rules, target: rule, approved: true, groups: true, paths: [[A]] }\n"));
        Assert.Throws<DegreesException>(() => TakeList.Parse("rows:\n  - { chapter: charms, book: pc, parser: entries, target: feat, approved: true, headerRole: block-header, levelWord: feat, listHeading: X, paths: [[A]] }\n"));
        Assert.True(Rows()[0].Groups);
        Assert.False(Rows()[0].OnTraditionLists); // a focus spell is on no tradition's list, so the lists do not count it
    }
}
