using Codex.ContentImport.Degrees;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Codex.Tests;

/// <summary>
/// Chapter 8a's classes over synthetic blocks (invented words only): a class's statistics box and initial proficiencies, a feature row with the level in its heading, the grants the feature sentences make, a subclass whose first
/// paragraph is read past, a level band of feats with the printed list of them, and every gate the parser has (the advancement table against the feature rows, a proficiency sentence of a shape it does not know, the counts
/// against the bookmarks and the list of feats). CI never sees a book.
/// </summary>
public class DegreesClassesTests
{
    // A line that opens with the word of a labelled field has it as its bold label, as the book's text does.
    private static Block Rows(string role, double top, int column, params string[] lines) =>
        new(1, column, role, 60, 0, 300, top, lines.ToList(), lines.Select(l => new LineInfo(60, 200, l.StartsWith("Prerequisites ", StringComparison.Ordinal) ? "Prerequisites" : string.Empty)).ToList());

    private static Block Plain(string role, double top, string text) => Rows(role, top, 0, text);

    private static Block Box(double top, string key, string hp) =>
        new(1, 0, "body", 60, 0, 300, top, new List<string> { key + " " + hp, "Explaining words, then an attribute boost to a made-up", "choice of two." },
            new List<LineInfo> { new(60, 200, string.Empty, new List<LineCell> { new(60, key), new(160, hp) }), new(60, 200, string.Empty), new(60, 200, string.Empty) });

    private static Block Table(double top, params (int Level, string Text)[] rows)
    {
        var lines = new List<string> { "Your Level Class Features" };
        var info = new List<LineInfo> { new(60, 200, string.Empty) };
        foreach (var (level, text) in rows)
        {
            lines.Add(level + " " + text);
            info.Add(new LineInfo(60, 200, string.Empty, new List<LineCell> { new(60, level.ToString()), new(90, text) }));
        }

        return new Block(1, 0, "body", 60, 0, 300, top, lines, info);
    }

    private static (int, string)[] TableRows(string first = "Ancestry and background, initial proficiencies, gadget style, tinker feat", string third = "Keen eyes, tinker feat")
    {
        var rows = new List<(int, string)> { (1, first) };
        for (var level = 2; level <= 20; level++)
        {
            rows.Add((level, level == 3 ? third : "Tinker feat"));
        }

        return rows.ToArray();
    }

    private static readonly string[] FeatNames = { "Alpha Gizmo", "Beta Gizmo", "Gamma Gizmo", "Delta Gizmo", "Epsilon Gizmo", "Zeta Gizmo", "Eta Gizmo", "Theta Gizmo", "Iota Gizmo", "Kappa Gizmo" };

    private static List<Block> Tinker(string keen = "Your proficiency rank for Perception increases to expert.", (int, string)[]? table = null, string[]? list = null, string skills = "Trained in Crafting",
        string perception = "Trained in Perception", bool secondBand = true, bool slanted = false, bool singular = false)
    {
        var blocks = new List<Block>
        {
            Plain("section", 800, "Tinker"),
            Plain("entry-header", 790, "KEY ATTRIBUTE HIT POINTS"),
            Box(780, "Intelligence or Wisdom", "6 plus your Constitution modifier"),
            Plain("entry-header", 770, "During play..."),
            Plain("rules", 760, "You tinker with things."),
            Plain("block-header", 750, "INITIAL PROFICIENCIES"),
            Plain("body", 740, "At 1st level, you gain these ranks."),
            Plain("block-header", 730, "PERCEPTION"),
            Plain("body", 720, perception),
            Plain("block-header", 710, "SAVING THROWS"),
            Rows("body", 700, 0, "Expert in Reflex", "Trained in Will", "Trained in Fortitude"),
            Plain("block-header", 690, "SKILLS"),
            Rows("body", 680, 0, skills, "Trained in extra skills equal to 4 plus your Intelligence"),
            Plain("block-header", 670, "ATTACKS"),
            Rows("body", 660, 0, "Trained in simple weapons", "Trained in unarmed attacks"),
            Plain("block-header", 650, "DEFENSES"),
            Plain("body", 640, "Trained in light armor"),
            Plain("block-header", 630, "CLASS DC"),
            Plain("body", 620, "Trained in tinker class DC"),
            Plain("block-header", 610, "TINKER ADVANCEMENT"),
            Table(600, table ?? TableRows()),
            Plain("section", 500, "Class Features"),
            Plain("rules", 490, "You gain these abilities."),
            Plain("entry-header", 480, "Ancestry and Background"),
            Plain("rules", 470, "You have the benefits of your ancestry and background."),
            Plain("entry-header", 460, "Initial Proficiencies"),
            Plain("rules", 450, "These are noted at the start."),
            Plain("entry-header", 440, "Gadget Style"),
            Plain("rules", 430, "Choose a style."),
            Plain("block-header", 420, "Springs"),
            Rows("rules", 410, 0, "Springs bounce about the workshop.", "You are trained in Athletics.", "You can choose Dexterity as your key attribute."),
            Plain("block-header", 400, "Cogs"),
            Rows("rules", 390, 0, "Cogs turn in the dark.", "You are trained in Stealth.", "When you gain keen eyes, you also gain expert proficiency in light armor."),
            Plain("entry-header", 380, "Keen Eyes 3rd"),
            Rows("rules", 370, 0, "You see well.", keen),
            Plain("entry-header", 360, "Tinker Feats"),
            Plain("rules", 350, "You gain a tinker feat."),
            Plain("section", 340, "Tinker Feats"),
            Plain("rules", 330, "Pick one of these."),
            Plain("block-header", 320, "TINKER FEATS BY NAME"),
            Rows("body", 310, 0, new[] { "Feat Level" }.Concat((list ?? FeatNames.Select((n, i) => n + " " + (i < 5 ? 1 : 2)).ToArray())).ToArray()),
            Plain("block-header", 300, "1ST LEVEL"),
        };
        for (var i = 0; i < 5; i++)
        {
            blocks.Add(Plain("block-header", 290 - i * 20, FeatNames[i].ToUpperInvariant() + (i == 0 ? " [one-action]" : string.Empty) + " FEAT 1"));
            blocks.Add(Plain("tag", 289 - i * 20, "TINKER"));
            blocks.Add(i == 0 && singular
                ? new Block(1, 0, "body", 60, 0, 300, 288, new List<string> { "Prerequisite trained in Crafting", "You tinker with a thing." },
                    new List<LineInfo> { new(60, 200, "Prerequisite"), new(60, 200, string.Empty) })
                : i == 0 && slanted
                // A slanted block: each line starts a little further in than the one before (five points, then four, then five), so no line is "set in" by the book's hanging indent, and the first line is short.
                ? new Block(1, 0, "body", 60, 0, 300, 288, new List<string> { "Requirements You are wielding a thing.", "You tinker with a thing and a thing and a", "thing and a thing and a thing and a", "thing." },
                    new List<LineInfo> { new(60, 150, "Requirements"), new(65, 300, string.Empty), new(69, 300, string.Empty), new(73, 150, string.Empty) })
                : Rows("body", 288 - i * 20, 0, "Prerequisites trained in Crafting", "You tinker with " + FeatNames[i].ToLowerInvariant() + "."));
        }

        if (secondBand)
        {
            blocks.Add(Plain("block-header", 180, "2ND LEVEL"));
            for (var i = 5; i < 10; i++)
            {
                blocks.Add(Plain("block-header", 170 - i * 5, FeatNames[i].ToUpperInvariant() + " FEAT 2"));
                blocks.Add(Plain("tag", 169 - i * 5, "TINKER STANCE"));
                blocks.Add(Plain("body", 168 - i * 5, "You tinker with " + FeatNames[i].ToLowerInvariant() + "."));
            }
        }

        return blocks;
    }

    private static List<SpineNode> Spine(bool secondBand = true)
    {
        var path = new[] { "Chapter 9: Makers", "Tinker" };
        string[] P(params string[] more) => path.Concat(more).ToArray();
        var nodes = new List<SpineNode>
        {
            new(0, "Chapter 9: Makers", new[] { "Chapter 9: Makers" }, 1, 1, false),
            new(1, "Tinker", path, 1, 1, false),
            new(2, "During play...", P("During play..."), 1, 1, false),
            new(2, "Class Features", P("Class Features"), 1, 1, false),
            new(3, "Ancestry and Background", P("Class Features", "Ancestry and Background"), 1, 1, false),
            new(3, "Initial Proficiencies", P("Class Features", "Initial Proficiencies"), 1, 1, false),
            new(3, "Gadget Style", P("Class Features", "Gadget Style"), 1, 1, false),
            new(4, "Springs", P("Class Features", "Gadget Style", "Springs"), 1, 1, false),
            new(4, "Cogs", P("Class Features", "Gadget Style", "Cogs"), 1, 1, false),
            new(3, "Keen Eyes (3rd)", P("Class Features", "Keen Eyes (3rd)"), 1, 1, false),
            new(3, "Tinker Feats", P("Class Features", "Tinker Feats"), 1, 1, false),
            new(3, "Tinker Feats", P("Class Features", "Tinker Feats"), 1, 1, false),
            new(4, "1st Level", P("Class Features", "Tinker Feats", "1st Level"), 1, 1, false),
        };
        if (secondBand)
        {
            nodes.Add(new(4, "2nd Level", P("Class Features", "Tinker Feats", "2nd Level"), 1, 1, false));
        }

        return nodes;
    }

    private const string RowYaml = """
        rows:
          - { chapter: makers, book: pc, parser: classes, target: class, folder: classes, approved: true, columns: 1, subclassFeature: Gadget Style, saveAttributes: { Fortitude: con, Reflex: dex, Will: wis }, paths: [["Chapter 9: Makers", "Tinker"]] }
        """;

    private static List<TakeRow> Take(string yaml = RowYaml) => TakeList.Parse(yaml).Rows;

    private static BlocksResult Book(List<Block> blocks) =>
        new(blocks, 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static PipelineOutput Run(List<Block> blocks, bool secondBand = true, string yaml = RowYaml) =>
        ImportPipeline.Run(Book(blocks), Spine(secondBand), Take(yaml), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

    [Fact]
    public void TheStatisticsBoxAndTheInitialProficiencies_BecomeTheClassesFieldsAndItsFirstFeatureRow()
    {
        var tinker = Run(Tinker()).Final.Single(e => e.Type == "class");

        Assert.Equal(("tinker", "classes", "pc > Chapter 9: Makers > Tinker"), (tinker.Id, tinker.Folder, tinker.Source));
        // A choice of two key attributes is written a|b, and so is the class's boost; the saves it starts trained in are by attribute; the extra skills are a number.
        Assert.Equal(new[] { ("hp", "6"), ("keyAttribute", "\"int|wis\""), ("boosts", "[\"int|wis\"]"), ("savingThrows", "[\"wis\", \"con\"]"), ("extraSkills", "4") }, tinker.Raw.Select(r => (r.Label, r.Text)));
        Assert.Equal(string.Empty, tinker.Description);

        var initial = tinker.Features.Single(f => f.Name == "Initial Proficiencies");
        Assert.Equal(1, initial.Level);
        Assert.Equal(new[]
        {
            "{ to: skills, entries: [crafting], rank: trained }",
            "{ to: weapons, entries: [simple, unarmed], rank: trained }",
            "{ to: armor, entries: [light], rank: trained }",
            "{ to: classDc, rank: trained }",
            "{ to: perception, rank: trained }",
            "{ to: saves, entries: [dex], rank: expert }",
        }.Order(), initial.Adds.Order());
        // The text says the book's statements by category, and not the book's explanation above them.
        Assert.Contains("**Saving throws.** Expert in Reflex. Trained in Will. Trained in Fortitude.", initial.Description);
        Assert.DoesNotContain("At 1st level", initial.Description);
        Assert.DoesNotContain("noted at the start", initial.Description);
    }

    [Fact]
    public void AFeaturesLevelIsItsHeadings_AndItsSentencesMakeGrants_AndTheFeatureRowsFollowTheBook()
    {
        var tinker = Run(Tinker()).Final.Single(e => e.Type == "class");

        Assert.Equal(new[] { ("Ancestry and Background", 1), ("Initial Proficiencies", 1), ("Gadget Style", 1), ("Keen Eyes", 3), ("Tinker Feats", 1) }, tinker.Features.Select(f => (f.Name, f.Level)));
        var keen = tinker.Features.Single(f => f.Name == "Keen Eyes");
        Assert.Equal(new[] { "{ to: perception, rank: expert }" }, keen.Adds);
        Assert.Equal("You see well.\nYour proficiency rank for Perception increases to expert.", keen.Description);
        Assert.All(tinker.Features.Where(f => f.Name != "Initial Proficiencies" && f.Name != "Keen Eyes"), f => Assert.Empty(f.Adds));
    }

    [Fact]
    public void ASubclass_ReadsItsFirstParagraphPast_NamesItsClass_AndGrantsWhatItsSentencesGrant()
    {
        var result = Run(Tinker());
        var springs = result.Final.Single(e => e.Id == "springs");
        var cogs = result.Final.Single(e => e.Id == "cogs");

        Assert.Equal("subclass", springs.Type);
        Assert.Equal("pc > Chapter 9: Makers > Tinker > Gadget Style", springs.Source);
        Assert.Equal("\"tinker\"", springs.Raw.Single(r => r.Label == "class").Text);
        // The first paragraph (the flavor) is not in the feature row; the rest is, with a skill the sentence trains and the other key attribute the racket allows.
        var row = Assert.Single(springs.Features);
        Assert.Equal("You are trained in Athletics.\nYou can choose Dexterity as your key attribute.", row.Description);
        Assert.Equal(new[] { "{ to: skills, entries: [athletics], rank: trained }" }, row.Adds);
        Assert.Equal("\"dex\"", springs.Raw.Single(r => r.Label == "keyAttribute").Text);
        Assert.DoesNotContain("workshop", string.Join(' ', result.Final.Select(e => e.Description + string.Join(' ', e.Features.Select(f => f.Description)))));

        // A later grant is a row of its own, at the level of the class's feature it is tied to (the class's Keen Eyes is a 3rd level feature).
        Assert.Equal(new[] { ("Cogs", 1), ("Cogs Keen Eyes", 3) }, cogs.Features.Select(f => (f.Name, f.Level)));
        Assert.Equal(new[] { "{ to: armor, entries: [light], rank: expert }" }, cogs.Features[1].Adds);
        Assert.Equal(new[] { "{ to: skills, entries: [stealth], rank: trained }" }, cogs.Features[0].Adds);
        Assert.Equal(2, result.Parsed.Count(e => e.Type == "subclass"));
    }

    [Fact]
    public void TheClassFeats_AreReadUnderTheirBands_WithTheClassesKeyInTheirTraits_AndThePrintedListAgrees()
    {
        var spine = Spine();
        var result = ImportPipeline.Run(Book(Tinker()), spine, Take(), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");
        var feats = result.Final.Where(e => e.Type == "feat").OrderBy(e => e.Level).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

        Assert.Equal(10, feats.Count);
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 2, 2, 2, 2, 2 }, feats.Select(f => f.Level!.Value));
        Assert.All(feats, f => Assert.Equal("pc > Chapter 9: Makers > Tinker > Tinker Feats", f.Source));
        Assert.Equal(("alpha-gizmo", "1"), (feats[0].Id, feats[0].Actions));
        Assert.Equal(new[] { "tinker" }, feats[0].Traits);
        Assert.Equal(new[] { "tinker", "stance" }, feats[5].Traits);
        Assert.Equal(("prerequisites", "trained in Crafting"), (feats[0].Extracted.Single().Label, feats[0].Extracted.Single().Text));
        var counts = ImportPipeline.CheckClassCounts(spine, Book(Tinker()), result.Ranges, result.Parsed).Single();
        Assert.Equal((1, 5, 2, 2, 10, 10, 10, 10, 10), (counts.LoreHeadings, counts.Features, counts.Rackets, counts.Bands, counts.FeatHeaders, counts.TraitLines, counts.Markers, counts.Feats, counts.Listed));
    }

    [Fact]
    public void ALabelledLineInASlantedBlock_EndsItsParagraph_WhenItIsShort_AndTheTextAfterItIsTheDescription()
    {
        // The lines of this block start at four places, five points further in each time: the layout's hanging indent says nothing, so only the short first line ends the labelled paragraph.
        var feat = Run(Tinker(slanted: true)).Final.Single(e => e.Id == "alpha-gizmo");

        Assert.Equal(("requirements", "You are wielding a thing."), (feat.Extracted.Single().Label, feat.Extracted.Single().Text));
        Assert.Equal("You tinker with a thing and a thing and a thing and a thing and a thing and a thing.", feat.Description);
    }

    [Fact]
    public void ThePrerequisiteLabelInTheSingular_IsTheSameField()
    {
        var feat = Run(Tinker(singular: true)).Final.Single(e => e.Id == "alpha-gizmo");

        Assert.Equal(("prerequisites", "trained in Crafting"), (feat.Extracted.Single().Label, feat.Extracted.Single().Text));
        Assert.Equal("You tinker with a thing.", feat.Description);
    }

    [Fact]
    public void TheFlavorIsReadPast_AndHashedAsLeftOutText()
    {
        var blocks = Tinker();
        var range = TakeStage.Resolve(Spine(), Book(blocks), Take()).Single();
        var flavor = ImportPipeline.FlavorBlocks(range, Book(blocks)).SelectMany(b => b.Lines).ToList();

        Assert.Contains("You tinker with things.", flavor); // the flavor child's text
        Assert.Contains("At 1st level, you gain these ranks.", flavor); // the proficiencies' explanation
        Assert.Contains("Springs bounce about the workshop.", flavor); // a subclass's first paragraph
        Assert.Contains("These are noted at the start.", flavor); // the proficiencies feature's own text, which the generated one replaces
    }

    [Fact]
    public void AnAdvancementTableThatDisagreesWithTheFeatureRows_FailsTheRun()
    {
        // The table lacks a feature the headings have (the gadget style), or names one at another level, or lists a new feature that no earlier level gave.
        Assert.Contains("advancement table", Assert.Throws<DegreesException>(() => Run(Tinker(table: TableRows(first: "Ancestry and background, initial proficiencies, tinker feat")))).Message);
        Assert.Contains("advancement table", Assert.Throws<DegreesException>(() => Run(Tinker(table: TableRows(third: "Tinker feat")))).Message);
        Assert.Contains("advancement table", Assert.Throws<DegreesException>(() => Run(Tinker(table: TableRows(third: "Keen eyes, odd gear, tinker feat")))).Message);
        var short20 = TableRows().Take(19).ToArray();
        Assert.Contains("20 levels", Assert.Throws<DegreesException>(() => Run(Tinker(table: short20))).Message);
    }

    [Fact]
    public void ANameTheTableSaysOtherwise_IsMappedByTheRow_AndOnlyWhenTheRowSaysSo()
    {
        var other = TableRows(third: "Sharp sight, tinker feat");
        Assert.Contains("advancement table", Assert.Throws<DegreesException>(() => Run(Tinker(table: other))).Message);

        var mapped = RowYaml.Replace("paths:", "advancementNames: { sharp sight: Keen Eyes }, paths:", StringComparison.Ordinal);
        Assert.Equal(1, Run(Tinker(table: other), yaml: mapped).Final.Count(e => e.Type == "class"));
    }

    [Fact]
    public void AProficiencySentenceOfAShapeTheReaderDoesNotKnow_FailsTheRun_UnlessItIsAboutAWeaponGroup()
    {
        Assert.Contains("shape", Assert.Throws<DegreesException>(() => Run(Tinker(keen: "Your proficiency is better now."))).Message);

        // A weapon group is a choice the sheet does not model: the sentence stays text, with no grant.
        var tinker = Run(Tinker(keen: "Choose one weapon group. Your proficiency rank increases to master with the simple weapons in that group.")).Final.Single(e => e.Type == "class");
        Assert.Empty(tinker.Features.Single(f => f.Name == "Keen Eyes").Adds);
    }

    [Fact]
    public void AnInitialStatementOfAShapeTheReaderDoesNotKnow_FailsTheRun()
    {
        Assert.Throws<DegreesException>(() => Run(Tinker(skills: "Skilled in Crafting")));
        Assert.Throws<DegreesException>(() => Run(Tinker(perception: "Trained in Hearing")));
    }

    [Fact]
    public void TheCountsAgainstTheBookmarksAndThePrintedList_FailTheRunWhenTheyDiffer()
    {
        // A band with no bookmark, and a printed list that names another feat, a missing one or another level.
        Assert.Contains("counts differ", Assert.Throws<DegreesException>(() => Run(Tinker(), secondBand: false)).Message);
        var names = FeatNames.Select((n, i) => n + " " + (i < 5 ? 1 : 2)).ToArray();
        Assert.Contains("counts differ", Assert.Throws<DegreesException>(() => Run(Tinker(list: names.Take(9).Concat(new[] { "Lambda Gizmo 2" }).ToArray()))).Message);
        Assert.Contains("counts differ", Assert.Throws<DegreesException>(() => Run(Tinker(list: names.Take(5).Concat(new[] { "Zeta Gizmo 3" }).Concat(names.Skip(6)).ToArray()))).Message);
    }

    [Fact]
    public void ARowThatNamesASubclassFeatureTheClassLacks_FailsTheRun_AndWithoutOneTheHeadingsAreTheFeaturesActions()
    {
        var wrong = RowYaml.Replace("subclassFeature: Gadget Style", "subclassFeature: Nothing Here", StringComparison.Ordinal);
        Assert.Contains("holds subclasses", Assert.Throws<DegreesException>(() => Run(Tinker(), yaml: wrong)).Message);

        // With no subclass feature named, a heading under a feature is an action it gives: its title and its text are part of the feature, and no subclass is made.
        var without = RowYaml.Replace("subclassFeature: Gadget Style, ", string.Empty, StringComparison.Ordinal);
        var ids = Run(Tinker(), yaml: without);
        Assert.DoesNotContain(ids.Final, e => e.Type == "subclass");
        Assert.Contains("**Springs.**", ids.Final.Single(e => e.Type == "class").Features.Single(f => f.Name == "Gadget Style").Description);
    }

    // ---- Blocks: a sidebar whose title is set in the entry-header font ----

    private const string Roles = """
        rules:
          - { role: entry-header, family: [Helvetica-Bold], size: [13, 13] }
          - { role: block-header, family: [Helvetica-Bold], size: [11, 11] }
          - { role: tag, family: [Courier-Bold], size: [1, 9] }
          - { role: rules, family: [Times], size: [1, 11] }
          - { role: body, family: [Helvetica], size: [1, 11] }
        """;

    [Fact]
    public void ASidebarWhoseTitleIsInTheEntryHeaderFont_StartsAtItsWholeTitle_AndItsCapitalsSubtitleIsPartOfIt()
    {
        var b = new PdfDocumentBuilder();
        var hel = b.AddStandard14Font(Standard14Font.Helvetica);
        var helBold = b.AddStandard14Font(Standard14Font.HelveticaBold);
        var courBold = b.AddStandard14Font(Standard14Font.CourierBold);
        var p = b.AddPage(603, 783);
        p.AddText("Sample Tinker", 13, new PdfPoint(60, 720), helBold);
        p.AddText("GADGETEER", 11, new PdfPoint(60, 700), helBold);
        p.AddText("Box words here.", 9, new PdfPoint(60, 686), hel);
        p.AddText("Skills", 11, new PdfPoint(60, 670), helBold);
        p.AddText("More box words.", 9, new PdfPoint(60, 656), hel);
        p.AddText("An Entry", 13, new PdfPoint(60, 600), helBold); // a heading that is not a sidebar's title: the page's own
        p.AddText("DO IT [one-action]", 11, new PdfPoint(60, 580), helBold);
        p.AddText("MOVE", 8, new PdfPoint(65, 566), courBold);
        p.AddText("Do text.", 9, new PdfPoint(60, 552), hel);
        using var doc = PdfDocument.Open(b.Build());
        var layout = LayoutStage.Run(doc, FontRoles.Parse(Roles));

        var result = BlocksStage.Run(layout.Pages, new BlocksOptions
        {
            SidebarPages = new HashSet<int> { 1 },
            SidebarStarts = new Dictionary<int, int> { [1] = 1 },
            SidebarTitles = new Dictionary<int, IReadOnlyList<string>> { [1] = new[] { "Sample Tinker" } },
        });

        Assert.Equal(new[] { "Sample Tinker", "GADGETEER", "Box words here.", "Skills", "More box words." }, result.Dropped!.Select(d => d.Lines[0]));
        Assert.Equal(new[] { "An Entry", "DO IT [one-action]", "MOVE", "Do text." }, result.Blocks.Select(k => k.Lines[0]));
        Assert.Empty(result.SidebarProblems!);

        // A part of the title is not enough in this font (a heading of the page's own that ends like the title): it is the page's own heading, and the sidebar is then missing.
        var partial = new PdfDocumentBuilder();
        var pHel = partial.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = partial.AddPage(603, 783);
        page.AddText("Tinker", 13, new PdfPoint(60, 720), pHel);
        using var partialDoc = PdfDocument.Open(partial.Build());
        var partialResult = BlocksStage.Run(LayoutStage.Run(partialDoc, FontRoles.Parse(Roles)).Pages, new BlocksOptions
        {
            SidebarPages = new HashSet<int> { 1 },
            SidebarStarts = new Dictionary<int, int> { [1] = 1 },
            SidebarTitles = new Dictionary<int, IReadOnlyList<string>> { [1] = new[] { "Sample Tinker" } },
        });
        Assert.Equal(new[] { "Tinker" }, partialResult.Blocks.Select(k => k.Lines[0]));
        Assert.NotEmpty(partialResult.SidebarProblems!);
    }

    [Fact]
    public void ASidebarThatFillsAColumn_DoesNotMakeTheNextColumnsOpeningTextItsContinuation_OnlyWhenTheRowSaysSo()
    {
        var b = new PdfDocumentBuilder();
        var hel = b.AddStandard14Font(Standard14Font.Helvetica);
        var helBold = b.AddStandard14Font(Standard14Font.HelveticaBold);
        var p = b.AddPage(603, 783);
        p.AddText("Sample Tinker", 13, new PdfPoint(60, 720), helBold);
        p.AddText("Box words here.", 9, new PdfPoint(60, 700), hel);
        p.AddText("More box words in the sidebar.", 9, new PdfPoint(60, 688), hel);
        p.AddText("Text that goes on from the page before.", 9, new PdfPoint(340, 720), hel);
        p.AddText("And more of that text in the column.", 9, new PdfPoint(340, 708), hel);
        using var doc = PdfDocument.Open(b.Build());
        var pages = LayoutStage.Run(doc, FontRoles.Parse(Roles)).Pages;
        BlocksOptions Options(params int[] fills) => new()
        {
            SidebarPages = new HashSet<int> { 1 },
            SidebarStarts = new Dictionary<int, int> { [1] = 1 },
            SidebarTitles = new Dictionary<int, IReadOnlyList<string>> { [1] = new[] { "Sample Tinker" } },
            SidebarFillsColumn = fills.ToHashSet(),
        };

        // Without the row's word the page is flagged: the column after a sidebar opens with body text, which may be the sidebar's own.
        Assert.Equal(new[] { 1 }, BlocksStage.Run(pages, Options()).SidebarProblems!);
        var said = BlocksStage.Run(pages, Options(1));
        Assert.Empty(said.SidebarProblems!);
        Assert.Contains(said.Blocks, k => k.Lines[0].StartsWith("Text that goes on", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRowsOptions_AreReadFromTheTakeList()
    {
        var row = Take(RowYaml.Replace("paths:", "sidebarFillsColumn: [147], endsAtPage: [[Chapter, Section]], paths:", StringComparison.Ordinal)).Single();

        Assert.Equal(new[] { 147 }, row.SidebarFillsColumn);
        Assert.True(row.EndsAtPageFor(new[] { "chapter", "section" }));
        Assert.False(row.EndsAtPageFor(new[] { "chapter", "other" }));
        Assert.Equal("Gadget Style", row.SubclassFeature);
        Assert.Equal("wis", row.SaveAttributes.Single(kv => kv.Key.Equals("will", StringComparison.OrdinalIgnoreCase)).Value);
    }
}
