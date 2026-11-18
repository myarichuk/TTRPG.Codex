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

    // ---- Chapter 8b: a spellcaster (invented words only): the SPELLS proficiency line, the spells-per-day table in its two halves, the advancement table's "Nth-rank spells" rows, the text's own numbers, the grants by name and the
    // runs of a subclass gained at a later level. ----

    private const string CasterRowYaml = """
        rows:
          - { chapter: makers, book: pc, parser: classes, target: class, folder: classes, approved: true, columns: 1, subclassFeature: Gadget Style, grantsByName: true, saveAttributes: { Fortitude: con, Reflex: dex, Will: wis }, paths: [["Chapter 9: Makers", "Tinker"]] }
        """;

    private static int[] CasterSlots(int level) => Enumerable.Range(1, 10).Select(rank => rank == 10 ? 0 : level < 2 * rank - 1 ? 0 : level == 2 * rank - 1 ? 2 : 3).ToArray();

    private static int HighestRank(int level) => CasterSlots(level).Select((n, i) => n > 0 ? i + 1 : 0).Max();

    private static string Ordinal(int n) => n + (n == 1 ? "st" : n == 2 ? "nd" : n == 3 ? "rd" : "th");

    private static (int, string)[] CasterTable(bool wrongRank = false)
    {
        var rows = new List<(int, string)> { (1, "Ancestry and background, initial proficiencies, tinker spellcasting, gadget style, tinker feat") };
        for (var level = 2; level <= 20; level++)
        {
            var parts = new List<string>();
            if (HighestRank(level) > HighestRank(level - 1)) parts.Add(Ordinal(HighestRank(level) + (wrongRank && level == 7 ? 1 : 0)) + "-rank spells");
            if (level == 3) parts.AddRange(new[] { "keen eyes", "second gear" });
            if (level == 5) parts.Add("third gear");
            parts.Add("tinker feat");
            rows.Add((level, string.Join(", ", parts)));
        }

        return rows.ToArray();
    }

    private static string SlotCell(int n, bool star) => n == 0 ? "—" : n + (star ? "*" : string.Empty);

    private static List<Block> Caster(string tradition = "arcane", string attribute = "Intelligence", string textCantrips = "four", int tableCantrips = 4, int rowsInSecondHalf = 20, bool wrongRank = false, bool spellsLabel = true,
        bool withTable = true, string keyWord = "Intelligence", bool listAmongFeatures = false)
    {
        var blocks = Tinker(table: CasterTable(wrongRank));
        blocks[blocks.FindIndex(b => b.Role == "body" && b.Lines[0].StartsWith("Intelligence or Wisdom", StringComparison.Ordinal))] =
            Box(780, keyWord, "6 plus your Constitution modifier");
        if (spellsLabel)
        {
            var at = blocks.FindIndex(b => b.Lines.Count == 1 && b.Lines[0] == "Trained in light armor") + 1;
            blocks.Insert(at, Plain("block-header", 635, "SPELLS"));
            blocks.Insert(at + 1, Rows("body", 634, 0, "Trained in spell attack modifier", "Trained in spell DC"));
        }

        var gadget = blocks.FindIndex(b => b.Role == "entry-header" && b.Lines[0] == "Gadget Style");
        var added = new List<Block>
        {
            Plain("entry-header", 445, "Tinker Spellcasting"),
            Plain("rules", 444, $"Tinkers can cast spells of the {tradition} tradition, each with a spring or a cog. On the first day you can prepare two 1st-rank spells and {textCantrips} cantrips. The tinker's key attribute is {attribute}, your spell attack and your spell DC both use your {attribute} modifier. When the lamps are lit, you start with a focus pool of 1 Focus Point, and the work begins."),
        };
        if (withTable)
        {
            added.Add(Plain("block-header", 443, "TINKER SPELLS PER DAY"));
            var first = new List<string> { "Your Spell", "Level Cantrips 1st 2nd 3rd 4th" };
            for (var level = 1; level <= 20; level++)
            {
                var s = CasterSlots(level);
                first.Add($"{level} {tableCantrips} {SlotCell(s[0], level == 1)} {SlotCell(s[1], false)} {SlotCell(s[2], false)} {SlotCell(s[3], false)}");
            }

            first.Add("* A made-up note about the starred slot, which goes on");
            added.Add(Rows("body", 442, 0, first.ToArray()));
            added.Add(Plain("rules", 441, "Some words between the two halves of the table."));
            added.Add(Rows("body", 440, 1, "Rank", "5th 6th 7th 8th 9th 10th"));
            var second = Enumerable.Range(1, rowsInSecondHalf).Select(level => string.Join(' ', Enumerable.Range(5, 6).Select(r => SlotCell(r == 10 ? 0 : CasterSlots(level)[r - 1], false)))).ToList();
            for (var i = 0; i < 3 && i < second.Count; i++) added.Add(Rows("body", 439 - i, 1, second[i]));
            second = second.Skip(3).ToList();
            second.Add("slot that works differently from the others.");
            added.Add(Rows("body", 430, 1, second.ToArray()));
            added.Add(Plain("body", 429, "in the next column, which is a note too."));
        }

        blocks.InsertRange(gadget, added);
        if (listAmongFeatures)
        {
            var header = blocks.FindIndex(b => b.Role == "block-header" && b.Lines[0] == "TINKER FEATS BY NAME");
            var moved = blocks.GetRange(header, 2);
            blocks.RemoveRange(header, 2);
            blocks.InsertRange(blocks.FindIndex(b => b.Role == "entry-header" && b.Lines[0] == "Keen Eyes 3rd") + 2, moved);
        }

        return WithRuns(blocks);
    }

    private static List<SpineNode> CasterSpine()
    {
        var spine = Spine();
        var at = spine.FindIndex(n => n.Title == "Initial Proficiencies") + 1;
        spine.Insert(at, new SpineNode(3, "Tinker Spellcasting", new[] { "Chapter 9: Makers", "Tinker", "Class Features", "Tinker Spellcasting" }, 1, 1, false));
        return spine;
    }

    private static PipelineOutput RunCaster(List<Block> blocks, string yaml = CasterRowYaml) =>
        ImportPipeline.Run(Book(blocks), CasterSpine(), Take(yaml), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

    /// <summary>The blocks of the subclasses' text with a muse's feat and spell, an order's skill, and runs gained at later levels, in place of the plain ones.</summary>
    private static List<Block> WithRuns(List<Block> blocks)
    {
        var springs = blocks.FindIndex(b => b.Lines[0] == "Springs bounce about the workshop.");
        blocks[springs] = Rows("rules", 410, 0, "Springs bounce about the workshop.", "You are trained in Athletics.", "First Gear (1st): You gain the Alpha Gizmo tinker feat.",
            "Second Gear (3rd): Your proficiency ranks for spell DCs and spell attack modifiers increase to expert.", "Third Gear (5th): You gain master proficiency in martial weapons.");
        blocks.Insert(springs + 1, new Block(1, 0, "body", 60, 0, 300, 409, new List<string> { "Order Skill Crafting", "Muse Feat Alpha Gizmo", "Muse Spell Chime Bolt (page 12)" },
            new List<LineInfo> { new(60, 200, "Order Skill"), new(60, 200, "Muse Feat"), new(60, 200, "Muse Spell") }));
        var cogs = blocks.FindIndex(b => b.Lines[0] == "Cogs turn in the dark.");
        blocks[cogs] = Rows("rules", 390, 0, "Cogs turn in the dark.", "First Gear (1st): You gain the Beta Gizmo tinker feat.", "Second Gear (3rd): Your proficiency ranks for spell DCs and spell attack modifiers increase to expert.",
            "Third Gear (5th): You gain master proficiency with the favored weapon of your guild, simple weapons, and unarmed attacks.");
        return blocks;
    }

    [Fact]
    public void ASpellcastersSpellsLabel_TableAndText_BecomeItsTraditionCastingFocusPoolAndRows_AndTheTableIsNotItsText()
    {
        var result = RunCaster(Caster());
        var tinker = result.Final.Single(e => e.Type == "class");

        Assert.Equal(new[] { "hp", "keyAttribute", "boosts", "savingThrows", "extraSkills", "tradition", "casting", "focusPool" }, tinker.Raw.Select(r => r.Label).Take(8));
        Assert.Equal(("\"int\"", "\"arcane\"", "\"prepared\"", "1"), (tinker.Raw.Single(r => r.Label == "keyAttribute").Text, tinker.Raw.Single(r => r.Label == "tradition").Text, tinker.Raw.Single(r => r.Label == "casting").Text, tinker.Raw.Single(r => r.Label == "focusPool").Text));
        // One row for each of the 20 levels, with the cantrips, the slots of each rank from the 1st, the highest rank and a spell for each slot; a mark on a number (the star) is no part of it.
        var rows = tinker.Raw.Single(r => r.Label == "spellcasting").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(20, rows.Length);
        Assert.Equal("      - { level: 1, cantrips: 4, slots: \"2\", maxRank: 1, spells: 2 }", rows[0]);
        Assert.Equal("      - { level: 4, cantrips: 4, slots: \"3/3\", maxRank: 2, spells: 6 }", rows[3]);
        Assert.Equal("      - { level: 17, cantrips: 4, slots: \"3/3/3/3/3/3/3/3/2\", maxRank: 9, spells: 26 }", rows[16]);
        Assert.Equal("      - { level: 20, cantrips: 4, slots: \"3/3/3/3/3/3/3/3/3\", maxRank: 9, spells: 27 }", rows[19]);
        // The table and its notes are no feature text; the spell attack modifier and the spell DC are granted with the class's other initial ranks.
        var spellcasting = tinker.Features.Single(f => f.Name == "Tinker Spellcasting");
        Assert.DoesNotContain("made-up note", spellcasting.Description);
        Assert.DoesNotContain("Level Cantrips", spellcasting.Description);
        Assert.DoesNotContain("in the next column", string.Join(' ', tinker.Features.Select(f => f.Description)));
        Assert.Contains("{ to: spellAttack, rank: trained }", tinker.Features.Single(f => f.Name == "Initial Proficiencies").Adds);
        Assert.Contains("{ to: spellDc, rank: trained }", tinker.Features.Single(f => f.Name == "Initial Proficiencies").Adds);
        Assert.Contains("**Spells.** Trained in spell attack modifier. Trained in spell DC.", tinker.Features.Single(f => f.Name == "Initial Proficiencies").Description);
    }

    [Fact]
    public void ASubclassesLabelledLinesAndLaterRuns_GrantFeatsSpellsAndRanks_AndTheFavoredWeaponOfAFaithIsLeftToTheText()
    {
        var result = RunCaster(Caster());
        var springs = result.Final.Single(e => e.Id == "springs");
        var cogs = result.Final.Single(e => e.Id == "cogs");

        // A skill the order trains, the feat the muse gives (by id: the pack's feat of that name), and the spell it names, which the pack of this synthetic book does not hold: it stays text.
        Assert.Equal(new[] { "{ to: skills, entries: [athletics], rank: trained }", "{ to: skills, entries: [crafting], rank: trained }", "{ to: feats, entries: [alpha-gizmo] }" }.Order(), springs.Features[0].Adds.Distinct().Order());
        Assert.Contains("**Muse Spell.** Chime Bolt", springs.Features[0].Description);
        Assert.DoesNotContain("page 12", springs.Features[0].Description);
        Assert.Contains("spell:chime-bolt", result.GrantsByName.Left);
        Assert.True(result.GrantsByName.Resolved >= 2);
        // A run gained at a later level is a row of its own, at that level; the one at the 1st level stays in the subclass's own row, and its feat is a grant.
        Assert.Equal(new[] { ("Cogs", 1), ("Second Gear", 3), ("Third Gear", 5) }, cogs.Features.Select(f => (f.Name, f.Level)));
        Assert.Equal(new[] { ("Springs", 1), ("Second Gear", 3), ("Third Gear", 5) }, springs.Features.Select(f => (f.Name, f.Level)));
        Assert.Equal(new[] { "{ to: feats, entries: [beta-gizmo] }" }, cogs.Features[0].Adds);
        Assert.Equal(new[] { "{ to: spellAttack, rank: expert }", "{ to: spellDc, rank: expert }" }, cogs.Features[1].Adds.Order().ToArray());
        // A sentence that names the faith's favored weapon among other things grants the others (the sheet has no weapon of its own in a ranked list).
        Assert.Equal(new[] { "{ to: weapons, entries: [simple, unarmed], rank: master }" }, cogs.Features[2].Adds);
    }

    [Fact]
    public void ThePrintedListAmongTheFeatures_IsStillTheIndependentCount()
    {
        var spine = CasterSpine();
        var blocks = Caster(listAmongFeatures: true);
        var result = ImportPipeline.Run(Book(blocks), spine, Take(CasterRowYaml), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

        Assert.Equal(10, result.Final.Count(e => e.Type == "feat"));
        var counts = ImportPipeline.CheckClassCounts(spine, Book(blocks), result.Ranges, result.Parsed).Single();
        Assert.Equal((10, 10), (counts.Feats, counts.Listed));
    }

    [Fact]
    public void ASpellcastersGates_FailTheRun_WhenTheTableTheTextOrTheTableOfAdvancementDisagree()
    {
        // The advancement table names a new spell rank at a level the spells-per-day table does not agree with.
        Assert.Contains("new spell ranks", Assert.Throws<DegreesException>(() => RunCaster(Caster(wrongRank: true))).Message);
        // A half of the table with 19 rows.
        Assert.Contains("20 rows", Assert.Throws<DegreesException>(() => RunCaster(Caster(rowsInSecondHalf: 19))).Message);
        // The text says four cantrips and the table has five; the text names no tradition; the key attribute is not the one the text says; a spells line with no table, and a table with no spells line.
        Assert.Contains("1st-level spells and cantrips", Assert.Throws<DegreesException>(() => RunCaster(Caster(tableCantrips: 5))).Message);
        Assert.Contains("tradition", Assert.Throws<DegreesException>(() => RunCaster(Caster(tradition: "made-up"))).Message);
        Assert.Contains("key attribute", Assert.Throws<DegreesException>(() => RunCaster(Caster(keyWord: "Wisdom"))).Message);
        Assert.Contains("one of each", Assert.Throws<DegreesException>(() => RunCaster(Caster(withTable: false))).Message);
        Assert.Contains("one of each", Assert.Throws<DegreesException>(() => RunCaster(Caster(spellsLabel: false))).Message);
    }

    [Fact]
    public void TheSpellTablesBlocks_AreTheNumbersAndDashesOfRows_WithNotesOnlyAtTheEnd()
    {
        Assert.True(EntriesStage.IsSpellTableBlock(Rows("body", 0, 0, "Your Spell", "Level Cantrips 1st 2nd 3rd 4th", "1 5 2* — — —", "* a note"), false));
        Assert.True(EntriesStage.IsSpellTableBlock(Rows("body", 0, 0, "Rank", "5th 6th 7th 8th 9th 10th"), false));
        Assert.True(EntriesStage.IsSpellTableBlock(Rows("body", 0, 0, "— — — — — —"), false));
        // A note cut at the foot of a column goes on in a block of its own: only right after a table block, and only in lowercase.
        Assert.True(EntriesStage.IsSpellTableBlock(Rows("body", 0, 0, "slot that works differently."), true));
        Assert.False(EntriesStage.IsSpellTableBlock(Rows("body", 0, 0, "slot that works differently."), false));
        Assert.False(EntriesStage.IsSpellTableBlock(Rows("body", 0, 0, "Slot that works differently."), true));
        // A row after a note is no table, and a labelled line is not one.
        Assert.False(EntriesStage.IsSpellTableBlock(Rows("body", 0, 0, "* a note", "1 5 2 — — —"), false));
        Assert.False(EntriesStage.IsSpellTableBlock(Rows("body", 0, 0, "Order Skill Athletics", "Druid Feat Animal"), false));
    }

    [Fact]
    public void TheSpellRankRowsOfTheAdvancementTable_AreNoFeaturesAndAreCheckedAgainstTheTable()
    {
        var table = EntriesStage.ParseAdvancement(Table(0, CasterTable()), Take(CasterRowYaml).Single());
        Assert.Equal(new[] { 3, 5, 7, 9, 11, 13, 15, 17 }.Concat(new[] { 19 }.Take(0)), table.Where(t => t.Name.EndsWith("-rank spell", StringComparison.Ordinal)).Select(t => t.Level));
        Assert.All(table.Where(t => t.Name.EndsWith("-rank spell", StringComparison.Ordinal)), t => Assert.True(EntriesStage.IsSpellRankRow(t.Name)));
        Assert.False(EntriesStage.IsSpellRankRow("tinker feat"));
    }

    [Fact]
    public void AnInitialProficiencyThatIsUntrained_OrAFaithsFavoredWeapon_GivesNothing_AndTheSpellWordsAreTheSheetsLists()
    {
        var row = Take(CasterRowYaml).Single();
        var initial = EntriesStage.ParseInitial(new List<(string, List<Block>)>
        {
            ("DEFENSES", new List<Block> { Rows("body", 0, 0, "Untrained in heavy armor, but a vow may change that", "Trained in unarmored defense") }),
            ("ATTACKS", new List<Block> { Rows("body", 0, 0, "Trained in simple weapons", "Trained in the favored weapon of your deity, a list of three.", "A rare one comes with a permit.") }),
            ("SPELLS", new List<Block> { Rows("body", 0, 0, "Trained in spell attack modifier", "Trained in spell DC") }),
        }, row);

        Assert.Equal(new[] { "{ to: armor, entries: [unarmored], rank: trained }", "{ to: weapons, entries: [simple], rank: trained }", "{ to: spellAttack, rank: trained }", "{ to: spellDc, rank: trained }" }.Order(), initial.Adds.Order());
        Assert.Contains("Untrained in heavy armor, but a vow may change that.", initial.Statements.Single(s => s.Label == "DEFENSES").Text);
    }

    [Fact]
    public void TheGrantShapes_OfASubclassWithAFaith_AreTheOnesTheSentencesSay_AndOtherSentencesMakeNone()
    {
        var row = Take(CasterRowYaml).Single();
        // "You have R proficiency in X" after "you're trained in", "Your proficiency rank in X increases", "if you gain F" later grants; the faith's favored weapon is left out of a list.
        var adds = EntriesStage.SentenceGrants("You're trained in light armor, and you have expert proficiency in Will saves. Your proficiency rank in heavy armor increases to master. You gain expert proficiency with the favored weapon of your cult, martial weapons, and unarmed attacks.", row, out var notModelled);
        Assert.Equal(new[]
        {
            "{ to: armor, entries: [light], rank: trained }",
            "{ to: saves, entries: [wis], rank: expert }",
            "{ to: armor, entries: [heavy], rank: master }",
            "{ to: weapons, entries: [martial, unarmed], rank: expert }",
        }, adds);
        Assert.Equal(1, notModelled);
        Assert.Equal(new[] { ("tough hide", "expert", "heavy armor") },
            EntriesStage.LaterGrants("At 4th level, if you gain the tough hide class feature, you also gain expert proficiency in heavy armor."));

        // A grant by name is a marker until the ids are final; a sentence that is a choice or a condition makes none.
        Assert.Equal(new[] { "{ to: feats, entries: [@feat(Alpha Gizmo)] }", "{ to: feats, entries: [@feat(Cog Turner)] }", "{ to: focusSpells, entries: [@spell(chime bolt)] }", "{ to: grantedSpells, entries: [@spell(low note)] }" },
            EntriesStage.NamedGrants("You gain the Alpha Gizmo tinker feat. If your faith allows it, you gain the Beta Gizmo feat. You gain the Cog Turner tinker feat, a fast one. You gain your choice of the Gamma Gizmo or Delta Gizmo feat. You learn the chime bolt composition spell. You learn the low note composition cantrip."));
        Assert.Equal(new[] { "{ to: skills, entries: [crafting], rank: trained }", "{ to: feats, entries: [@feat(Alpha Gizmo)] }", "{ to: focusSpells, entries: [@spell(chime bolt)] }", "{ to: grantedSpells, entries: [@spell(low note)] }" },
            EntriesStage.LabelledGrants(new[] { new LabelledText("Order Skill", "Crafting"), new LabelledText("Druid Feat", "Alpha Gizmo"), new LabelledText("Order Spell", "chime bolt"), new LabelledText("Muse Spell", "low note"), new LabelledText("Anathema", "Be rude.") }));
    }

    [Fact]
    public void ARunGainedAtALevel_IsARowOfItsOwn_AndTheOneAtTheFirstLevelStaysInTheSubclass()
    {
        var (kept, rows) = EntriesStage.SplitLevelledParagraphs(new[]
        {
            new LabelledText(string.Empty, "Some words. First Gear (1st): The first run. Second Gear (3rd): The second run. Final Gear (19th): The last run."),
            new LabelledText("Other Label", "Not touched. Gear Gear (3rd): But a label's text is left alone."),
        });

        Assert.Equal(new[] { ("Second Gear", 3, "The second run."), ("Final Gear", 19, "The last run.") }, rows);
        Assert.Equal(new[] { (string.Empty, "Some words."), ("First Gear (1st)", "The first run."), ("Other Label", "Not touched. Gear Gear (3rd): But a label's text is left alone.") }, kept.Select(k => (k.Label, k.Text)));
    }

    [Fact]
    public void AClassFeatureTheRowRenames_KeepsTheBooksNameForTheBookmarks_AndTheTableMapsItsOwn()
    {
        var yaml = CasterRowYaml.Replace("grantsByName: true,", "grantsByName: true, featureNames: { Keen Eyes: Sharp Sight }, advancementNames: { keen eyes: Sharp Sight },", StringComparison.Ordinal);
        var result = RunCaster(Caster(), yaml);

        Assert.Equal(new[] { "Sharp Sight" }, result.Final.Single(e => e.Type == "class").Features.Where(f => f.Level == 3).Select(f => f.Name));
    }

    [Fact]
    public void AGrantNamedByAClass_BecomesTheIdOfTheEntryOfThatName_OrNothingWhenThereAreNoneOrManyAndNoClassKeyTellsThem()
    {
        FinalEntry Spell(string id, string name) => new() { Id = id, Name = name, Type = "spell" };
        FinalEntry Feat(string id, string name, params string[] traits) => new() { Id = id, Name = name, Type = "feat", Traits = traits.ToList() };
        var cls = new FinalEntry { Id = "tinker-class", Name = "Tinker", Type = "class", Features = new List<FinalFeature> { new("Row", 1, "text", new List<string> { "{ to: feats, entries: [@feat(Gizmo)] }", "{ to: feats, entries: [@feat(Doodad)] }", "{ to: focusSpells, entries: [@spell(Chime Bolt)] }", "{ to: grantedSpells, entries: [@spell(Missing)] }", "{ to: skills, entries: [athletics], rank: trained }" }) } };
        var final = new List<FinalEntry> { cls, Feat("gizmo-tinker", "Gizmo", "tinker"), Feat("gizmo-other", "Gizmo", "other"), Feat("doodad", "Doodad"), Feat("doodad-too", "Doodad"), Spell("chime-bolt-spell", "Chime Bolt") };

        var (resolved, left) = ImportPipeline.ResolveGrantNames(final);

        Assert.Equal(2, resolved); // the feat of the class's key, and the spell
        Assert.Equal(new[] { "feat:doodad", "spell:missing" }, left.Order());
        Assert.Equal(new[] { "{ to: feats, entries: [gizmo-tinker] }", "{ to: focusSpells, entries: [chime-bolt-spell] }", "{ to: skills, entries: [athletics], rank: trained }" }, cls.Features.Single().Adds);
    }

    [Fact]
    public void TheNameOfADroppedEntry_IsHashedByItsWords_AndTheLetterAfterAnApostropheIsNoWord()
    {
        var blocks = Tinker();
        blocks[blocks.FindIndex(b => b.Role == "block-header" && b.Lines[0].StartsWith("ALPHA GIZMO", StringComparison.Ordinal))] = Plain("block-header", 290, "ALPHA'S GIZMO [one-action] FEAT 1");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Alpha's Gizmo"))).ToLowerInvariant();
        var redactions = RedactionTable.Parse($"normalization: 2\nrows:\n  - {{ book: pc, entry: alpha-s-gizmo, field: name, span: {{ sha256: \"{hash}\", length: 13 }}, action: drop-entry, reason: \"A test.\" }}\n");
        var spine = Spine();

        var redacted = ImportPipeline.RedactedWords(Book(blocks), TakeStage.Resolve(spine, Book(blocks), Take()), redactions, "pc", spine);

        // Without the possessive's letter, the denylist would hold the hash of "s" and stop every possessive of the packs.
        Assert.Equal(new[] { "alpha", "gizmo" }, redacted.Names);
    }

    [Fact]
    public void AClassFeatureTheRowSaysInOtherWords_PutsTheBooksHeadingAmongTheReservedNames()
    {
        var yaml = CasterRowYaml.Replace("grantsByName: true,", "grantsByName: true, featureNames: { Keen Eyes: Sharp Sight }, advancementNames: { keen eyes: Sharp Sight },", StringComparison.Ordinal);
        var spine = CasterSpine();
        var blocks = Caster();

        var redacted = ImportPipeline.RedactedWords(Book(blocks), TakeStage.Resolve(spine, Book(blocks), Take(yaml)), RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc", spine);

        Assert.Equal(new[] { "eyes", "keen" }, redacted.Names);
    }

    [Fact]
    public void AFeatsInlineAction_IsSetOutWithItsLabelledLines_AndABulletedTriggerWithItsEffectIsTwoLines()
    {
        var entry = new ParsedEntry();
        entry.Parts.Add(new LabelledText("Spin Cog.", "[2 actions] (primal, sonic) Frequency once per hour; Requirements You hold a cog; Effect The cog spins; it whirs."));
        entry.Parts.Add(new LabelledText("• Trigger", "You fall; Effect Choose a spring."));
        EntriesStage.SplitInlineAction(entry);

        Assert.Equal(new[] { ("Spin Cog (2 actions)", string.Empty), ("Traits", "primal sonic"), ("Frequency", "once per hour"), ("Requirements", "You hold a cog"), ("Effect", "The cog spins; it whirs."), ("• Trigger", "You fall"), ("Effect", "Choose a spring.") },
            entry.Parts.Select(p => (p.Label, p.Text)));
    }

    [Fact]
    public void ALineSetInButStartingInLowercase_GoesOnTheSentence_AndAHyphenBetweenTwoCapitalsKeepsNoSpace()
    {
        var blocks = Caster();
        var body = blocks.FindIndex(b => b.Lines.Count > 0 && b.Lines[0] == "Prerequisites trained in Crafting");
        blocks[body] = new Block(1, 0, "body", 60, 0, 300, 288, new List<string> { "Prerequisites trained in Crafting", "You use Well-", "Versed tools and a", "thing that goes on." },
            new List<LineInfo> { new(60, 200, "Prerequisites"), new(60, 300, string.Empty), new(60, 200, string.Empty), new(70, 300, string.Empty) });

        var feat = RunCaster(blocks).Final.Single(e => e.Id == "alpha-gizmo");

        // The last line is set in (the layout's art pushes it) and starts in lowercase after a sentence that has not ended: it is no paragraph. The hyphen at the end of a line, between two capitalized parts, is the name's own.
        Assert.Equal("You use Well-Versed tools and a thing that goes on.", feat.Description);
    }

    [Fact]
    public void TheColumnLabelsOfThePrintedList_AloneInABlockAfterItsTitle_BelongToTheList()
    {
        // The printed list sits among the class features here, where a block that is not its body would become the text of a feature.
        var blocks = Caster(listAmongFeatures: true);
        var list = blocks.FindIndex(b => b.Role == "body" && b.Lines[0] == "Feat Level");
        var rest = blocks[list].Lines.Skip(1).ToList();
        blocks[list] = Rows("body", 310, 0, rest.ToArray());
        blocks.Insert(list, Plain("body", 311, "Feat Level"));
        var result = RunCaster(blocks);

        Assert.All(result.Final.Single(e => e.Type == "class").Features, f => Assert.DoesNotContain("Feat Level", f.Description));
        Assert.Equal(10, result.Final.Count(e => e.Type == "feat"));
    }
}
