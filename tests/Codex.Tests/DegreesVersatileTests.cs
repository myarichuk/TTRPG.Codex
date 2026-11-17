using Codex.ContentImport.Degrees;

namespace Codex.Tests;

/// <summary>
/// Chapter 7b's parsers over synthetic blocks (invented words only): a versatile heritage (its lore read past, its rarity, its lineage feats under no band, its feats under a band, the heritage's key in their trait line, and
/// the key following a redaction row that renames the heritage), a section of several heritages with an introduction, the backgrounds (each shape of the boosts, the trained skills, the Lore subject and the skill feat, and a shape that
/// fails) and a rules row that reads the tail of its own text past (the tail is hashed as left-out text). CI never sees a book.
/// </summary>
public class DegreesVersatileTests
{
    private static Block Plain(string role, double top, string text) =>
        new(1, 0, role, 60, 0, 300, top, new List<string> { text }, new List<LineInfo> { new(60, 200, string.Empty) });

    private static BlocksResult Book(params Block[] blocks) =>
        new(blocks.ToList(), 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static readonly List<SpineNode> FolkSpine = new()
    {
        new(0, "Chapter 9: Folk", new[] { "Chapter 9: Folk" }, 1, 1, false),
        new(1, "In This Book", new[] { "Chapter 9: Folk", "In This Book" }, 1, 1, false),
        new(2, "Moss Kin", new[] { "Chapter 9: Folk", "In This Book", "Moss Kin" }, 1, 1, false),
        new(1, "Moss Kin", new[] { "Chapter 9: Folk", "Moss Kin" }, 1, 1, false),
        new(2, "You might...", new[] { "Chapter 9: Folk", "Moss Kin", "You might..." }, 1, 1, false),
        new(2, "Moss Kin (Uncommon Heritage)", new[] { "Chapter 9: Folk", "Moss Kin", "Moss Kin (Uncommon Heritage)" }, 1, 1, false),
        new(2, "Moss Kin Feats", new[] { "Chapter 9: Folk", "Moss Kin", "Moss Kin Feats" }, 1, 1, false),
        new(3, "Lineages", new[] { "Chapter 9: Folk", "Moss Kin", "Moss Kin Feats", "Lineages" }, 1, 1, false),
        new(2, "1st Level", new[] { "Chapter 9: Folk", "Moss Kin", "1st Level" }, 1, 1, false),
    };

    private static List<TakeRow> VersatileRows() => TakeList.Parse("""
        rows:
          - { chapter: folk, book: pc, parser: versatile, target: heritage, category: Ancestries, approved: true, columns: 1, countsDiffer: "No printed list.", paths: [["Chapter 9: Folk", "Moss Kin"]] }
        """).Rows;

    private static BlocksResult MossKin(string lineageHeader = "DAMP ROOT FEAT 1", bool strayText = false) => Book(
        Plain("section", 800, "MOSS KIN"),
        Plain("rules", 790, "Moss kin like damp."),
        Plain("entry-header", 780, "You might..."),
        Plain("rules", 770, "Be damp."),
        Plain("entry-header", 760, "Moss Kin (Uncommon Heritage)"),
        Plain("rules", 750, "You are mossy. You gain the moss-kin trait."),
        Plain("section", 740, "Moss Kin Feats"),
        Plain("rules", 730, "These feats are for moss kin."),
        Plain("entry-header", 720, "Lineages"),
        Plain("rules", 710, "Pick one lineage."),
        Plain("block-header", 700, lineageHeader),
        Plain("tag", 690, "LINEAGE MOSS-KIN"),
        Plain("body", 680, "You are damp."),
        Plain("block-header", 670, "1ST LEVEL"),
        strayText ? Plain("body", 665, "Stray words.") : Plain("body", 665, string.Empty),
        Plain("block-header", 660, "SOFT STEP FEAT 1"),
        Plain("tag", 650, "MOSS-KIN"),
        Plain("body", 640, "You step softly."));

    private static PipelineOutput RunMoss(BlocksResult blocks, string redactions = "normalization: 2\nrows: []\n") =>
        ImportPipeline.Run(Prune(blocks), FolkSpine, VersatileRows(), RedactionTable.Parse(redactions), "pc");

    /// <summary>The empty body block is only a placeholder for the optional stray text: it is dropped.</summary>
    private static BlocksResult Prune(BlocksResult blocks) =>
        Book(blocks.Blocks.Where(b => !(b.Role == "body" && b.Lines.All(string.IsNullOrEmpty))).ToArray());

    [Fact]
    public void AVersatileHeritageNamesNoAncestry_ItsLoreIsReadPast_AndItsFeatsCarryItsKey()
    {
        var run = RunMoss(MossKin());

        var heritage = run.Final.Single(e => e.Type == "heritage");
        Assert.Equal("moss-kin", heritage.Id);
        Assert.Equal(new[] { "uncommon" }, heritage.Traits);
        Assert.DoesNotContain(heritage.Raw, r => r.Label == "ancestry");
        Assert.Equal("[\"moss-kin\"]", heritage.Raw.Single(r => r.Label == "grantsTraits").Text);
        Assert.DoesNotContain("damp", heritage.Description); // the lore is read past

        var feats = run.Final.Where(e => e.Type == "feat").OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "damp-root", "soft-step" }, feats.Select(f => f.Id));
        Assert.All(feats, f => Assert.Equal(1, f.Level));
        Assert.Equal(new[] { "lineage", "moss-kin" }, feats[0].Traits); // a lineage feat sits under no band and is of 1st level
        Assert.Equal(new[] { "moss-kin" }, feats[1].Traits);

        // The flavor (the lore, the sentence under each title) is left-out text.
        var pruned = Prune(MossKin());
        var ranges = TakeStage.Resolve(FolkSpine, pruned, VersatileRows());
        var flavor = string.Join(' ', ImportPipeline.FlavorBlocks(ranges.Single(), pruned).SelectMany(b => b.Lines));
        Assert.Contains("like damp", flavor);
        Assert.Contains("Pick one lineage", flavor);
        Assert.DoesNotContain("You are mossy", flavor);
    }

    [Fact]
    public void ARenamedVersatileHeritage_TakesItsFeatsTraitWithIt()
    {
        var row = $"  - {{ book: pc, entry: moss-kin, field: name, span: {{ sha256: \"{SpanText.Sha256("Moss Kin")}\", length: 8 }}, action: replace, text: \"Fen Kin\", reason: \"A coined name.\" }}\n";
        var run = RunMoss(MossKin(), "normalization: 2\nrows:\n" + row);

        Assert.Equal("fen-kin", run.Final.Single(e => e.Type == "heritage").Id);
        Assert.All(run.Final.Where(e => e.Type == "feat"), f => Assert.Contains("fen-kin", f.Traits));
        Assert.All(run.Final.Where(e => e.Type == "feat"), f => Assert.DoesNotContain("moss-kin", f.Traits));
    }

    [Fact]
    public void AFeatWhoseLevelIsNotItsBands_AndTextUnderABand_FailTheRun()
    {
        var level = Assert.Throws<DegreesException>(() => RunMoss(MossKin(lineageHeader: "DAMP ROOT FEAT 2")));
        Assert.Contains("under the band for level 1", level.Message);

        var stray = Assert.Throws<DegreesException>(() => RunMoss(MossKin(strayText: true)));
        Assert.Contains("after a level band and before a feat's header", stray.Message);
    }

    [Fact]
    public void ASectionOfSeveralHeritages_HasItsIntroductionAsARule_AndEachUnitItsHeritage()
    {
        var slice = new List<Block>
        {
            Plain("rules", 800, "Mixed folk exist."),
            Plain("section", 790, "Alpha"),
            Plain("entry-header", 780, "Alpha (Heritage)"),
            Plain("rules", 770, "You gain the alpha trait."),
            Plain("entry-header", 760, "Alpha Feats"),
            Plain("block-header", 750, "1ST LEVEL"),
            Plain("block-header", 740, "ONE FEAT 1"),
            Plain("tag", 730, "ALPHA"),
            Plain("body", 720, "You do one."),
            Plain("section", 710, "Beta"),
            Plain("entry-header", 700, "Beta (Heritage)"),
            Plain("rules", 690, "You gain the beta trait."),
            Plain("entry-header", 680, "Beta Feats"),
            Plain("block-header", 670, "1ST LEVEL"),
            Plain("block-header", 660, "TWO FEAT 1"),
            Plain("tag", 650, "BETA"),
            Plain("body", 640, "You do two."),
            Plain("body", 630, "PICTURE"),
        };

        var scan = EntriesStage.ScanVersatile(slice);

        Assert.Equal(3, scan.Units.Count);
        Assert.Null(scan.Units[0].Heritage); // the introduction
        Assert.Single(scan.Units[0].Flavor);
        Assert.All(scan.Units.Skip(1), u => Assert.Single(u.Feats));
        Assert.Single(scan.Units[2].Captions); // a caption is kept apart from the unit's text
        Assert.Empty(scan.Units[2].Flavor);

        // A part with no heritage that is not the introduction of several fails.
        Assert.Throws<DegreesException>(() => EntriesStage.ScanVersatile(new List<Block> { Plain("rules", 800, "Only text.") }));
    }

    // ---- the backgrounds

    private static readonly List<SpineNode> TradeSpine = new()
    {
        new(0, "Chapter 9: Folk", new[] { "Chapter 9: Folk" }, 1, 1, false),
        new(1, "Trades", new[] { "Chapter 9: Folk", "Trades" }, 1, 1, false),
    };

    private static PipelineOutput RunTrades(string bakerBoosts = "One must be to Strength or Dexterity, and one is a free attribute boost.")
    {
        var blocks = Book(
            Plain("section", 800, "TRADES"),
            Plain("rules", 790, "Pick a trade. It is permanent."),
            Plain("block-header", 780, "BAKER BACKGROUND"),
            Plain("body", 770, $"You baked bread. Choose two attribute boosts. {bakerBoosts} You're trained in the Crafting skill and the Baking Lore skill. You gain the Oven Mastery skill feat."),
            Plain("block-header", 760, "WANDERER BACKGROUND"),
            Plain("body", 750, "You roamed. Choose two attribute boosts. One must be to Wisdom or Charisma, and one is a free attribute boost. You're trained in your choice of the Nature or Survival skill and a Lore skill related to one place you roamed. You gain a skill feat: Fast Walk if you chose Nature or Far Reach if you chose Survival."),
            Plain("block-header", 740, "PRIEST BACKGROUND"),
            Plain("body", 730, "You served. Choose two attribute boosts. One must be to an attribute set by your deity and the other is a free attribute boost. You're trained in the deity's skill and gain the Oven Mastery feat with that skill. You are also trained in a Lore skill related to your deity."));
        var rows = TakeList.Parse("""
            rows:
              - { chapter: trades, book: pc, parser: backgrounds, target: background, category: Backgrounds, approved: true, columns: 1, countsDiffer: "No printed list.", paths: [["Chapter 9: Folk", "Trades"]] }
            """).Rows;
        return ImportPipeline.Run(blocks, TradeSpine, rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");
    }

    private static string Raw(FinalEntry e, string label) => e.Raw.SingleOrDefault(r => r.Label == label)?.Text ?? string.Empty;

    private static string Extracted(FinalEntry e, string label) => e.Extracted.SingleOrDefault(r => r.Label == label)?.Text ?? string.Empty;

    [Fact]
    public void EachBackgroundShape_GivesItsBoostsSkillsLoreAndFeat_AndTheStoryIsReadPast()
    {
        var run = RunTrades();

        var baker = run.Final.Single(e => e.Id == "baker");
        Assert.Equal("[\"str|dex\", \"free\"]", Raw(baker, "boosts"));
        Assert.Equal("[\"crafting\"]", Raw(baker, "skills"));
        Assert.Equal("Baking", Extracted(baker, "lore"));
        Assert.Equal("oven-mastery", Extracted(baker, "skillFeat"));
        Assert.StartsWith("Choose two attribute", baker.Description);
        Assert.DoesNotContain("baked bread", baker.Description); // the story is read past

        // A choice between skills, a Lore phrase and two feats by the skill chosen.
        var wanderer = run.Final.Single(e => e.Id == "wanderer");
        Assert.Equal("[\"wis|cha\", \"free\"]", Raw(wanderer, "boosts"));
        Assert.Equal("[\"nature|survival\"]", Raw(wanderer, "skills"));
        Assert.StartsWith("related to", Extracted(wanderer, "lore"));
        Assert.Equal("fast-walk|far-reach", Extracted(wanderer, "skillFeat"));

        // An attribute that another entry names: two free boosts, no skills of its own.
        var priest = run.Final.Single(e => e.Id == "priest");
        Assert.Equal("[\"free\", \"free\"]", Raw(priest, "boosts"));
        Assert.Equal(string.Empty, Raw(priest, "skills"));
        Assert.Equal("oven-mastery", Extracted(priest, "skillFeat"));

        var intro = run.Final.Single(e => e.Type == "rule");
        Assert.Equal("trades", intro.Id);
        Assert.Equal("Backgrounds", intro.Category);
    }

    [Fact]
    public void ABackgroundWhoseBoostsHaveAnotherShape_FailsTheRun()
    {
        var ex = Assert.Throws<DegreesException>(() => RunTrades("One must be to Strength and one is free."));
        Assert.Contains("boosts", ex.Message);
    }

    // ---- the own-text tail

    [Fact]
    public void ARulesRowReadsTheTailOfItsOwnTextPast_AndTheTailIsLeftOutText()
    {
        var spine = new List<SpineNode>
        {
            new(0, "Words", new[] { "Words" }, 1, 1, false),
            new(1, "Sub", new[] { "Words", "Sub" }, 1, 1, false),
        };
        var blocks = Book(
            Plain("section", 800, "WORDS"),
            Plain("rules", 790, "Plain rules."),
            Plain("block-header", 780, "TABLE HEADING"),
            Plain("body", 770, "Zorbish Quillon speak."),
            Plain("entry-header", 760, "Sub"),
            Plain("rules", 750, "Sub text."));
        var rows = TakeList.Parse("""
            rows:
              - { chapter: words, book: pc, parser: rules, target: rule, category: Words, approved: true, columns: 1, tailFromHeader: true, countsDiffer: "The child is not taken.", own: [[Words]], ownName: Words }
            """).Rows;

        var run = ImportPipeline.Run(blocks, spine, rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");
        Assert.Equal("Plain rules.", run.Final.Single().Description);

        var ranges = TakeStage.Resolve(spine, blocks, rows);
        Assert.Equal(2, ranges.Single().TailEnd - ranges.Single().End);
        var (words, _) = ImportPipeline.DenylistWords(blocks, ranges, new HashSet<string>());
        Assert.Contains("zorbish", words);
        Assert.Contains("quillon", words);
        Assert.DoesNotContain("plain", words);
    }

    [Fact]
    public void ARetiredPair_MayBeDroppedByFresh_AndAnyOtherUnexplainedDropStillStopsTheRun()
    {
        var hash = new string('a', 64);
        var retired = Waivers.ParseRetired($"# note\nretired-pair {hash} an artifact\nsomeword\n");
        Assert.Equal("an artifact", retired[hash]);
        Assert.DoesNotContain(Waivers.Parse($"retired-pair {hash} an artifact\nsomeword\n"), w => w.StartsWith("retired", StringComparison.Ordinal)); // a retired line is no waived word
        Assert.Throws<DegreesException>(() => Waivers.ParseRetired("retired-pair nothex a reason\n"));

        var existing = new HashSet<string> { hash, "b" };
        var review = ImportPipeline.ReviewFresh("pairs", existing, new[] { "b" }, Array.Empty<string>(), new HashSet<string>(), new HashSet<string>(), retired);
        Assert.Single(review.Retired!);

        // Without the line the same drop fails, and so does a drop of a hash that is not the retired one.
        Assert.Throws<DegreesException>(() => ImportPipeline.ReviewFresh("pairs", existing, new[] { "b" }, Array.Empty<string>(), new HashSet<string>(), new HashSet<string>()));
        Assert.Throws<DegreesException>(() => ImportPipeline.ReviewFresh("pairs", new HashSet<string> { hash, "b", "c" }, new[] { "b" }, Array.Empty<string>(), new HashSet<string>(), new HashSet<string>(), retired));
    }
}
