using Codex.ContentImport.Degrees;

namespace Codex.Tests;

/// <summary>
/// Chapter 7a's ancestries over synthetic blocks (invented words only): the statistics box that the book floats into the second column of a page between two parts of a feat, the lore that is read past
/// (and hashed for the denylist), a heritage's rarity as a trait, the heritage's ancestry id, the feat's level against its level band, the counts against the bookmarks and the table of contents, and a
/// redaction row that renames an entry (its id follows its new name). CI never sees a book.
/// </summary>
public class DegreesAncestriesTests
{
    private static Block Plain(string role, double top, string text, int column = 0) =>
        new(1, column, role, 60, 0, 300, top, new List<string> { text }, new List<LineInfo> { new(60, 200, string.Empty) });

    private static Block Lines(string role, double top, int column, params string[] lines) =>
        new(1, column, role, 60, 0, 300, top, lines.ToList(), lines.Select(_ => new LineInfo(60, 200, string.Empty)).ToList());

    private static BlocksResult Book(params Block[] blocks) =>
        new(blocks.ToList(), 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static readonly List<SpineNode> Spine = new()
    {
        new(0, "Chapter 9: Folk", new[] { "Chapter 9: Folk" }, 1, 1, false),
        new(1, "Mossfolk", new[] { "Chapter 9: Folk", "Mossfolk" }, 1, 1, false),
        new(2, "Lore", new[] { "Chapter 9: Folk", "Mossfolk", "Lore" }, 1, 1, false),
        new(2, "Mossfolk Heritages", new[] { "Chapter 9: Folk", "Mossfolk", "Mossfolk Heritages" }, 1, 1, false),
        new(3, "Soft Mossfolk (Uncommon)", new[] { "Chapter 9: Folk", "Mossfolk", "Mossfolk Heritages", "Soft Mossfolk (Uncommon)" }, 1, 1, false),
        new(2, "Ancestry Feats", new[] { "Chapter 9: Folk", "Mossfolk", "Ancestry Feats" }, 1, 1, false),
        new(3, "1st Level", new[] { "Chapter 9: Folk", "Mossfolk", "Ancestry Feats", "1st Level" }, 1, 1, false),
        new(3, "5th Level", new[] { "Chapter 9: Folk", "Mossfolk", "Ancestry Feats", "5th Level" }, 1, 1, false),
    };

    private const string RowsYaml = """
        rows:
          - { chapter: folk, book: pc, parser: ancestries, target: ancestry, approved: true, columns: 1, paths: [["Chapter 9: Folk", "Mossfolk"]] }
        """;

    private static List<TakeRow> Rows() => TakeList.Parse(RowsYaml).Rows;

    private static BlocksResult Folk(string leapHeader = "LOFTY LEAP [one-action] FEAT 5", bool extraBand = false) => Book(
        Plain("body", 800, "Mossfolk ........ 3 Versatile Heritages ........ 9"), // the table of contents
        Plain("section", 720, "Mossfolk"),
        Plain("entry-header", 700, "Lore"),
        Plain("rules", 690, "Mossfolk like moss."),
        Plain("section", 670, "Mossfolk Heritages"),
        Plain("rules", 660, "Pick one."),
        Plain("entry-header", 650, "Soft Mossfolk (Uncommon)"),
        Plain("rules", 640, "You are soft. You gain a cushion."),
        Plain("section", 620, "Ancestry Feats"),
        Plain("rules", 610, "Pick feats."),
        Plain("block-header", 600, "1ST LEVEL"),
        Plain("block-header", 590, "PLINTH STEP FEAT 1"),
        Plain("tag", 580, "MOSSFOLK"),
        Plain("body", 570, "You step on a plinth, and the"),
        // The statistics box, floated into the second column: the first part of the feat above goes on after it.
        Lines("block-header", 720, 1, "HIT POINTS"),
        Lines("body", 710, 1, "7"),
        Lines("block-header", 700, 1, "SIZE"),
        Lines("body", 690, 1, "Small"),
        Lines("block-header", 680, 1, "SPEED"),
        Lines("body", 670, 1, "25 feet"),
        Lines("block-header", 660, 1, "ATTRIBUTE BOOSTS"),
        Lines("body", 650, 1, "Wisdom", "Free"),
        Lines("block-header", 640, 1, "ATTRIBUTE FLAW"),
        Lines("body", 630, 1, "Strength"),
        Lines("block-header", 620, 1, "LANGUAGES"),
        Lines("body", 610, 1, "Common", "Mossy", "Additional languages equal to your Intelligence modifier (if it’s positive). Choose from Plinthic, Lofty."),
        Lines("block-header", 600, 1, "TRAITS"),
        Lines("body", 590, 1, "Mossfolk Plant"),
        Lines("block-header", 580, 1, "SQUISH"),
        Lines("body", 570, 1, "You squish."),
        Plain("body", 560, "ground shakes."),
        Plain("block-header", 550, "5TH LEVEL"),
        Plain("block-header", 540, leapHeader),
        Plain("tag", 530, "MOSSFOLK"),
        Plain("body", 520, "You leap."),
        extraBand ? Plain("block-header", 510, "9TH LEVEL") : Plain("body", 510, "The end."));

    private static PipelineOutput Run(BlocksResult blocks, string redactions = "normalization: 2\nrows: []\n") =>
        ImportPipeline.Run(blocks, Spine, Rows(), RedactionTable.Parse(redactions), "pc");

    [Fact]
    public void TheBoxBecomesTheAncestrysFields_AndTheHeritageAndTheFeatsAreRead_AndTheLoreIsNot()
    {
        var ancestries = Run(Folk()).Final;

        var ancestry = ancestries.Single(e => e.Type == "ancestry");
        Assert.Equal("mossfolk", ancestry.Id);
        Assert.Equal(new[] { "mossfolk", "plant" }, ancestry.Traits);
        Assert.Equal(new[] { ("hp", "7"), ("size", "\"Small\""), ("speed", "25"), ("boosts", "[\"wis\", \"free\", \"-str\"]"), ("languages", "[\"common\", \"mossy\"]"), ("extraLanguages", "0"), ("specialAbilities", "[\"squish\"]") },
            ancestry.Raw.Select(r => (r.Label, r.Text)));
        Assert.Equal("**Squish.** You squish.", ancestry.Description); // the ability's text is the description, and nothing of the lore
        Assert.DoesNotContain("moss", ancestries.Where(e => e.Type != "ancestry").Select(e => e.Description).Concat(new[] { ancestry.Description }).Single(d => d.Contains("Squish")), StringComparison.OrdinalIgnoreCase);

        // A heritage's rarity in its heading is a trait, not part of its name or id; it names its ancestry by id.
        var heritage = ancestries.Single(e => e.Type == "heritage");
        Assert.Equal(("soft-mossfolk", "Soft Mossfolk", "heritages"), (heritage.Id, heritage.Name, heritage.Folder));
        Assert.Equal(new[] { "uncommon" }, heritage.Traits);
        Assert.Equal(("ancestry", "\"mossfolk\""), (heritage.Raw.Single().Label, heritage.Raw.Single().Text));
        Assert.Equal("You are soft. You gain a cushion.", heritage.Description);
        Assert.Equal("pc > Chapter 9: Folk > Mossfolk > Mossfolk Heritages", heritage.Source);

        // The feats: the level is the band's, the trait line is lowercase keys, the cost is the icon's; the second half of the first feat's text comes after the box.
        var feats = ancestries.Where(e => e.Type == "feat").OrderBy(e => e.Level).ToList();
        Assert.Equal(new[] { ("plinth-step", 1, (string?)null), ("lofty-leap", 5, "1") }, feats.Select(f => (f.Id, f.Level!.Value, f.Actions)));
        Assert.All(feats, f => Assert.Equal(new[] { "mossfolk" }, f.Traits));
        Assert.Equal("You step on a plinth, and the ground shakes.", feats[0].Description);
        Assert.Equal("pc > Chapter 9: Folk > Mossfolk > Ancestry Feats", feats[0].Source);
    }

    [Fact]
    public void TheLoreAndTheLanguagesTheBoxOffersAreLeftOutText_ForTheDenylist()
    {
        var blocks = Folk();
        var ranges = TakeStage.Resolve(Spine, blocks, Rows());
        var left = EntriesStage.AncestryFlavor(ranges.Single().BlocksIn(blocks)).SelectMany(b => b.Lines).ToList();

        Assert.Contains("Mossfolk like moss.", left);
        Assert.Contains("Pick one.", left);
        Assert.Contains("Pick feats.", left);
        Assert.Contains(left, l => l.StartsWith("Choose from Plinthic", StringComparison.Ordinal));
        Assert.DoesNotContain("You are soft. You gain a cushion.", left);
        Assert.DoesNotContain("You squish.", left);
    }

    [Fact]
    public void AFeatWhosePrintedLevelIsNotItsBands_FailsTheRun()
    {
        var error = Assert.Throws<DegreesException>(() => Run(Folk("LOFTY LEAP [one-action] FEAT 9")));
        Assert.Contains("under the band for level 5", error.Message);
    }

    [Fact]
    public void ALevelBandTheBookmarksDoNotHave_FailsTheCountsWithBothNumbers()
    {
        var error = Assert.Throws<DegreesException>(() => Run(Folk(extraBand: true)));
        Assert.Contains("2 level bookmarks, 3 bands", error.Message);
    }

    [Fact]
    public void AnAncestryTheTableOfContentsDoesNotList_FailsTheRun()
    {
        var blocks = Folk();
        blocks.Blocks[0] = Plain("body", 800, "Somefolk ........ 3 Versatile Heritages ........ 9");

        Assert.Contains("table of contents", Assert.Throws<DegreesException>(() => Run(blocks)).Message);
    }

    [Fact]
    public void ARedactionRowThatReplacesAName_GivesTheEntryTheIdOfItsNewName()
    {
        var row = "normalization: 2\nrows:\n  - book: pc\n    entry: soft-mossfolk\n    field: name\n"
            + "    span: { sha256: \"" + SpanText.Sha256("Soft Mossfolk") + "\", length: 13 }\n"
            + "    action: replace\n    text: \"Gentle Mossfolk\"\n    reason: \"A name said in plain words.\"\n";

        var result = Run(Folk(), row);

        var renamed = result.Final.Single(e => e.Type == "heritage");
        Assert.Equal(("gentle-mossfolk", "Gentle Mossfolk"), (renamed.Id, renamed.Name));
        Assert.Equal("mossfolk", result.Final.Single(e => e.Type == "ancestry").Id);
    }

    [Fact]
    public void TheSystemMustDeclareTheHeritageAndFeatTypes_ForTheAncestriesParser()
    {
        const string system = """
            entityTypes:
              - { type: ancestry, folder: ancestries }
              - { type: feat, folder: feats }
            """;

        Assert.Contains("heritage", Assert.Throws<DegreesException>(() => TakeList.Parse(RowsYaml).CheckAgainstSystem(system)).Message);
        TakeList.Parse(RowsYaml).CheckAgainstSystem(system + "\n  - { type: heritage, folder: heritages }\n");
    }

    [Fact]
    public void AGutterPinnedForAPageTheRowDoesNotCover_FailsTheRun()
    {
        var rows = TakeList.Parse("""
            rows:
              - { chapter: folk, book: pc, parser: ancestries, target: ancestry, approved: true, gutterAt: { 7: 288 }, paths: [["Chapter 9: Folk", "Mossfolk"]] }
            """).Rows;

        Assert.Contains("gutterAt", Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(Spine, rows)).Message);
        var good = TakeList.Parse(RowsYaml.Replace("columns: 1,", "gutterAt: { 1: 288 },", StringComparison.Ordinal)).Rows;
        Assert.Equal(288, ImportPipeline.OptionsFor(Spine, good).PinnedGutters[1]);
        // A page pinned by two rows, a row that says one column, and an x that is no place on the page fail.
        Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(Spine, good.Concat(good).ToList()));
        Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(Spine, TakeList.Parse(RowsYaml.Replace("columns: 1,", "columns: 1, gutterAt: { 1: 288 },", StringComparison.Ordinal)).Rows));
        var wide = new BlocksOptions { PinnedGutters = new Dictionary<int, double> { [1] = 700 } };
        Assert.Contains("pinned", Assert.Throws<DegreesException>(() => BlocksStage.Run(new List<LayoutPage> { new(1, 603, 783, new List<LayoutWord> { new(1, "w", "Body", 9, 60, 80, 700, 709, "body") }) }, wide)).Message);
    }

    [Fact]
    public void ABookmarkStartsOnTheEarliestPageOfItselfAndItsChildren_AndThePreviousOneEndsBeforeIt()
    {
        var flat = new List<(int Depth, string Title, string[] Path, int Page)>
        {
            (0, "Intro", new[] { "Intro" }, 1),
            (0, "Parent", new[] { "Parent" }, 6), // points one page after its first child
            (1, "Child", new[] { "Parent", "Child" }, 5),
            (1, "Second", new[] { "Parent", "Second" }, 7),
            (0, "After", new[] { "After" }, 9),
        };

        var nodes = SpineStage.Build(flat, 12).ToDictionary(n => n.Title);

        Assert.Equal((5, 8), (nodes["Parent"].PageStart, nodes["Parent"].PageEnd));
        Assert.Equal((1, 4), (nodes["Intro"].PageStart, nodes["Intro"].PageEnd)); // the page the parent's first child starts on is the parent's
        Assert.Equal((5, 6), (nodes["Child"].PageStart, nodes["Child"].PageEnd));
        Assert.Equal(6, flat[1].Page); // the input is not changed
    }

    [Fact]
    public void APinnedGutterCutsThePageThere_NotAtAWiderEmptyBandInsideAColumn()
    {
        // Two real columns (60 to 240 and 252 to 540); the right one has a gap of 80 points in every line, wider than the 12-point gutter between the columns.
        var words = new List<LayoutWord>();
        for (var i = 0; i < 25; i++)
        {
            var bottom = 700 - i * 11;
            words.Add(new LayoutWord(1, "alpha", "Body", 9, 60, 240, bottom, bottom + 9, "body"));
            words.Add(new LayoutWord(1, "beta", "Body", 9, 252, 380, bottom, bottom + 9, "body"));
            words.Add(new LayoutWord(1, "gamma", "Body", 9, 460, 540, bottom, bottom + 9, "body"));
        }

        var pages = new List<LayoutPage> { new(1, 603, 783, words) };
        var searched = BlocksStage.Run(pages);
        var pinned = BlocksStage.Run(pages, new BlocksOptions { PinnedGutters = new Dictionary<int, double> { [1] = 246 } });

        // Left to itself the search cuts at the wide gap, so the first column holds both "alpha" and "beta".
        Assert.Contains(searched.Blocks, b => b.Column == 0 && b.Lines[0] == "alpha beta");
        // Pinned, the columns are the real ones, and the gap stays inside the second column's lines.
        Assert.All(pinned.Blocks.Where(b => b.Column == 0), b => Assert.All(b.Lines, l => Assert.Equal("alpha", l)));
        Assert.All(pinned.Blocks.Where(b => b.Column == 1), b => Assert.All(b.Lines, l => Assert.Equal("beta gamma", l)));
        Assert.Equal(new[] { 0, 1 }, pinned.Blocks.Select(b => b.Column).Distinct().Order());
    }

    private static List<Block> Box(params (string Label, string[] Lines)[] parts)
    {
        var blocks = new List<Block>();
        foreach (var (label, lines) in parts)
        {
            blocks.Add(Lines("block-header", 700 - blocks.Count * 10, 1, label));
            if (lines.Length > 0) blocks.Add(Lines("body", 700 - blocks.Count * 10, 1, lines));
        }

        return blocks;
    }

    private static readonly (string, string[])[] Plain4 =
    {
        ("HIT POINTS", new[] { "8" }), ("SIZE", new[] { "Medium" }), ("SPEED", new[] { "25 feet" }),
    };

    [Fact]
    public void ABoxWithNoFlaw_TwoFreeBoosts_AndALanguageCountWithANumber_IsRead()
    {
        var box = EntriesStage.ParseAncestryBox(Box(Plain4.Concat(new (string, string[])[]
        {
            ("ATTRIBUTE BOOSTS", new[] { "Two free attribute boosts" }),
            ("LANGUAGES", new[] { "Common", "Additional languages equal to 1 + your Intelligence modifier (if it’s positive). Choose from the list of common languages." }),
            ("TRAITS", new[] { "Mossfolk Humanoid" }),
        }).ToArray()));

        Assert.Equal(new[] { "free", "free" }, box.Boosts);
        Assert.Equal(new[] { "common" }, box.Languages);
        Assert.Equal(1, box.ExtraLanguages);
        Assert.Empty(box.Specials);
        Assert.StartsWith("Choose from", box.ChooseFrom);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("noText")]
    [InlineData("notAnAttribute")]
    [InlineData("featHeader")]
    [InlineData("band")]
    [InlineData("hp")]
    public void ABoxThatIsNotInTheBooksShape_FailsTheRunWithAMessage(string fault)
    {
        var tail = new (string, string[])[]
        {
            ("ATTRIBUTE BOOSTS", new[] { fault == "notAnAttribute" ? "Wisp" : "Wisdom" }),
            ("LANGUAGES", new[] { "Common", "Additional languages equal to your Intelligence modifier (if it’s positive)." }),
            ("TRAITS", new[] { "Mossfolk" }),
        };
        var parts = fault == "order" ? new (string, string[])[] { ("SIZE", new[] { "Small" }), ("HIT POINTS", new[] { "8" }), ("SPEED", new[] { "25 feet" }) }.Concat(tail)
            : fault == "hp" ? new (string, string[])[] { ("HIT POINTS", new[] { "lots" }), ("SIZE", new[] { "Small" }), ("SPEED", new[] { "25 feet" }) }.Concat(tail)
            : Plain4.Concat(tail);
        if (fault == "noText") parts = parts.Append(("SQUISH", Array.Empty<string>()));
        if (fault == "featHeader") parts = parts.Append(("PLINTH STEP [one-action] FEAT 1", new[] { "You step." }));
        if (fault == "band") parts = parts.Append(("5TH LEVEL", new[] { "Text." }));

        Assert.Throws<DegreesException>(() => EntriesStage.ParseAncestryBox(Box(parts.ToArray())));
    }

    [Fact]
    public void ATextBlockUnderALevelBandBeforeTheNextFeat_FailsTheRun()
    {
        var blocks = Folk();
        // A body block between the 5th-level band and the next feat's header: a feat whose text flowed past the band heading.
        blocks.Blocks.Insert(blocks.Blocks.FindIndex(b => b.Lines[0] == "5TH LEVEL") + 1, Plain("body", 545, "Stray words."));

        Assert.Contains("before a feat's header", Assert.Throws<DegreesException>(() => Run(blocks)).Message);
    }
}
