using Codex.ContentImport.Degrees;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Codex.Tests;

/// <summary>
/// Chapter 6b's machinery over synthetic words, blocks and PDFs (invented names only): the gear table reader (two panels read as one table, an indented row as a version, a heading as a band, the note under a
/// table, a table beside a column of text, and the rows that fail the run), the join of a gear row to its description (a heading's description for the rows under it, the undescribed rows by name, the lead of a
/// section as a rule, the alchemical note that adds two traits), and the sidebar the take-list takes as a rule. CI never sees a book.
/// </summary>
public class DegreesGearTests
{
    private static LayoutWord W(string text, double left, double bottom, string role = "body", double size = 9) =>
        new(1, text, "F", size, left, left + (text.Length * 5), bottom, bottom + size, role);

    private static readonly IReadOnlyList<IReadOnlyList<string>> Sets = new List<IReadOnlyList<string>> { new[] { "Price", "Bulk", "Hands" } };

    private const double Width = 603;

    private static List<LayoutWord> Row(double left, double y, string name, string price, string bulk, string hands, double nameShift = 0) =>
        new() { W(name, left + nameShift, y), W(price, left + 80, y), W(bulk, left + 142, y), W(hands, left + 192, y) };

    // --- the table reader ---

    [Fact]
    public void ATableInTwoPanelsIsOneTable_AnIndentedRowIsAVersion_AHeadingIsABand_AndTheNoteIsTheLastRow()
    {
        // The left panel's header is on the same line as the first row of the right panel, which has no header of its own; its columns are the left panel's, 240 points to the right.
        var words = new List<LayoutWord> { W("GEAR", 60, 710, "block-header", 11) };
        words.AddRange(new[] { W("Item", 60, 698), W("Price", 140, 698), W("Bulk", 200, 698), W("Hands", 250, 698) });
        words.AddRange(new[] { W("Lamp", 300, 698), W("2gp", 380, 698), W("L", 442, 698), W("1", 492, 698) });
        words.AddRange(Row(60, 686, "Chain", "4gp", "1", "2"));
        words.Add(W("Lantern", 60, 674)); // a heading: a name and no cell
        words.AddRange(Row(60, 662, "Hooded", "7sp", "L", "1", 6)); // set in from the name column's edge
        words.AddRange(new[] { W("Pole", 300, 686), W("1cp", 380, 686), W("1", 442, 686), W("2*", 492, 686) });
        words.AddRange(new[] { W("*", 300, 674), W("Use", 308, 674), W("two", 330, 674), W("hands.", 350, 674) });

        var (tables, rest) = GearTables.Carve(1, Width, words, Sets, twoUp: true);

        var table = Assert.Single(tables);
        Assert.Empty(rest);
        Assert.Equal(new[] { "title", "head", "row", "band", "sub", "row", "row", "note" }, table.Rows!.Select(r => r[0]));
        Assert.Equal(new[] { "head", "Item", "Price", "Bulk", "Hands" }, table.Rows[1]);
        Assert.Equal(new[] { "row", "Chain", "4gp", "1", "2" }, table.Rows[2]);
        Assert.Equal(new[] { "band", "Lantern" }, table.Rows[3]);
        Assert.Equal(new[] { "sub", "Hooded", "7sp", "L", "1" }, table.Rows[4]);
        Assert.Equal(new[] { "row", "Lamp", "2gp", "L", "1" }, table.Rows[5]); // the right panel's first row
        Assert.Equal("2*", table.Rows[6][4]);
        Assert.Equal(new[] { "note", "*", "Use two hands." }, table.Rows[7]);
        // The block holds the words' own places (the right panel is not left moved).
        Assert.True(table.Right > 500);
    }

    [Fact]
    public void ATableBesideAColumnOfText_TakesOnlyTheWordsSetInTheTablesFonts()
    {
        var words = new List<LayoutWord> { W("PRICES", 300, 710, "block-header", 11) };
        words.AddRange(new[] { W("Item", 300, 698), W("Price", 380, 698), W("Bulk", 442, 698), W("Hands", 492, 698) });
        words.AddRange(Row(300, 686, "Rope", "5sp", "L", "2"));
        // Running text in the left column on the same baselines is no part of the table, and stays.
        words.AddRange(new[] { W("Some", 60, 698, "rules"), W("text", 90, 686, "rules") });

        var (tables, rest) = GearTables.Carve(1, Width, words, Sets, twoUp: false);

        Assert.Equal(new[] { "title", "head", "row" }, Assert.Single(tables).Rows!.Select(r => r[0]));
        Assert.Equal(new[] { "Some", "text" }, rest.Select(w => w.Text));
        // A page with no header row is returned untouched.
        var plain = new List<LayoutWord> { W("Just", 60, 600), W("words", 100, 600) };
        Assert.Same(plain, GearTables.Carve(1, Width, plain, Sets, false).Words);
    }

    [Fact]
    public void ARowWithAMissingCell_ALineWithNoName_AndAHeadingWithDigits_FailTheRun()
    {
        var head = new List<LayoutWord> { W("Item", 60, 698), W("Price", 140, 698), W("Bulk", 200, 698), W("Hands", 250, 698) };

        var missing = head.Concat(new[] { W("Rope", 60, 686), W("5sp", 140, 686), W("2", 252, 686) }).ToList();
        Assert.Contains("3 cells where its header has 4", Assert.Throws<DegreesException>(() => GearTables.Carve(1, Width, missing, Sets, false)).Message, StringComparison.Ordinal);

        var nameless = head.Concat(new[] { W("5sp", 140, 686), W("L", 202, 686), W("2", 252, 686) }).ToList();
        Assert.Contains("no name cell", Assert.Throws<DegreesException>(() => GearTables.Carve(1, Width, nameless, Sets, false)).Message, StringComparison.Ordinal);

        var digits = head.Concat(new[] { W("Rope 5", 60, 686) }).ToList();
        Assert.Contains("not a heading", Assert.Throws<DegreesException>(() => GearTables.Carve(1, Width, digits, Sets, false)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyJoinsARowToItsDescription_WithItsItemLevelAndBracketsLeftOff()
    {
        Assert.Equal("candle", GearRows.Key("Candle (10)"));
        Assert.Equal("hoodedlantern", GearRows.Key("Hooded lantern (level 1)"));
        Assert.Equal(GearRows.Key("Ten-Foot Pole"), GearRows.Key("Ten-foot pole"));
        Assert.Equal("Clothing, Fine", EntriesStage.GearTitle("Clothing, fine"));
        Assert.Equal("Flint and Steel", EntriesStage.GearTitle("Flint and steel"));
        Assert.Equal("Ten-Foot Pole", EntriesStage.GearTitle("Ten-foot pole"));
        Assert.Equal("Candle (10)", EntriesStage.GearTitle("Candle (10)"));
        Assert.Equal("Spellbook (blank)", EntriesStage.GearTitle("Spellbook (blank)"));
    }

    // --- the items parser, over a synthetic book ---

    private const string GearYaml = """
        rows:
          - chapter: kit
            book: pc
            parser: items
            target: gear
            folder: gear
            itemKind: gear
            approved: true
            artLabels: true
            ruleCategory: Equipment
            countsDiffer: Plain gear has no paragraph.
            undescribed: [Rope (50 feet), Pup]
            leadRules: [["Chapter 9: Kit", "More"]]
            tables: [["Chapter 9: Kit"]]
            tableColumns: [["Price", "Bulk", "Hands"], ["Price", "Bulk", "Item Traits*"]]
            paths: [["Chapter 9: Kit", "Parts"], ["Chapter 9: Kit", "More"]]
        """;

    private static readonly List<SpineNode> Spine = new()
    {
        new(0, "Chapter 9: Kit", new[] { "Chapter 9: Kit" }, 1, 1, false),
        new(1, "Parts", new[] { "Chapter 9: Kit", "Parts" }, 1, 1, false),
        new(1, "More", new[] { "Chapter 9: Kit", "More" }, 1, 1, false),
    };

    private static Block Text(string role, double top, params (string Text, string Label)[] lines) =>
        new(1, 0, role, 60, 0, 300, top, lines.Select(l => l.Text).ToList(), lines.Select(l => new LineInfo(60, 300, l.Label)).ToList());

    private static Block Table(params string[][] rows) =>
        new(1, 0, StatTables.Role, 60, 0, 400, 700, rows.Select(r => string.Join(" | ", r.Skip(1))).ToList(), null, rows.Select(r => r.ToList()).ToList());

    private static BlocksResult Kit(string note = "All these items have the alchemical and consumable traits.", bool extraDescription = false, string parts = "Parts", string more = "More", bool emptyBand = false) => new(
        new List<Block>
        {
            Table(new[] { "title", "ADVENTURING GEAR" }, new[] { "head", "Item", "Price", "Bulk", "Hands" },
                new[] { "row", "Hammer", "1 sp", "L", "1" },
                new[] { "band", "Lantern" }, new[] { "sub", "Bull's-eye", "1 gp", "1", "1" }, new[] { "sub", "Hooded (level 1)", "7 sp", "L", "1" },
                new[] { "row", "Rope (50 feet)", "5 sp", "L", "2*" },
                new[] { "band", "Tent" }, new[] { "sub", "Pup", "8 sp", "L", "2" },
                new[] { "row", "Candle (10)", "1 cp", "—", "1" },
                new[] { "note", "*", "Use two hands." }),
            Table(new[] { "title", "ALCHEMICAL GEAR" }, new[] { "head", "Alchemical Bombs", "Price", "Bulk", "Item Traits*" },
                new[] { "row", "Lesser spark flask", "3 gp", "L", "Bomb, fire" }, new[] { "note", "*", note }),
            Text("section", 740, (parts, string.Empty)),
            Text("rules", 730, ("This line says what follows.", string.Empty)),
            Text("rules", 720, ("Hammer: A tool for hitting.", "Hammer:"), ("Lantern: A lantern sheds light.", "Lantern:")),
            Text("rules", 700, ("Candle: A candle burns.", "Candle:")),
            Text("section", 680, (more, string.Empty)),
            Text("rules", 670, ("The items of this section are made by alchemists.", string.Empty)),
            Text("rules", 660, ("Lesser Spark Flask: Throw it.", "Lesser Spark Flask:")),
            extraDescription ? Text("rules", 650, ("Spoon: Not in the table.", "Spoon:")) : Text("body", 650, ("SPARK FLASK", string.Empty)),
        }.Concat(emptyBand ? new[] { Table(new[] { "title", "ADVENTURING GEAR" }, new[] { "head", "Item", "Price", "Bulk", "Hands" }, new[] { "band", "Lonely" }, new[] { "row", "Spare", "1 cp", "L", "1" }) } : Array.Empty<Block>()).ToList(),
        0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static PipelineOutput Run(BlocksResult blocks, string yaml = GearYaml) =>
        ImportPipeline.Run(blocks, Spine, TakeList.Parse(yaml).Rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

    [Fact]
    public void AGearEntryIsItsRowAndItsDescription_ARowUnderAHeadingReadsTheHeadingsDescription()
    {
        var result = Run(Kit());

        Assert.Equal(new[] { "candle-10", "hammer", "lantern-bull-s-eye", "lantern-hooded", "lesser-spark-flask", "more", "rope-50-feet", "tent-pup" }, result.Final.Select(e => e.Id).Order());
        var hooded = result.Final.Single(e => e.Id == "lantern-hooded");
        Assert.Equal("Lantern, Hooded", hooded.Name);
        Assert.Equal(1, hooded.Level);
        Assert.StartsWith("A lantern sheds light.\n**Price.** 7 sp", hooded.Description, StringComparison.Ordinal);
        Assert.Equal(new[] { "kind", "price", "bulk", "hands", "variantOf" }, hooded.Raw.Select(r => r.Label));
        Assert.Equal("\"Lantern\"", hooded.Raw.Single(r => r.Label == "variantOf").Text);
        Assert.Equal("Lantern, Bull's-Eye", result.Final.Single(e => e.Id == "lantern-bull-s-eye").Name);
        // A row of the book's quantity keeps its brackets in its name, and a dash cell is no property.
        var candle = result.Final.Single(e => e.Id == "candle-10");
        Assert.Equal("Candle (10)", candle.Name);
        Assert.DoesNotContain(candle.Raw, r => r.Label == "bulk");
        // The rows the take-list names have no paragraph: their text is their statistics, with the note under the table when the row carries its star.
        var rope = result.Final.Single(e => e.Id == "rope-50-feet");
        Assert.Equal("**Price.** 5 sp\n**Bulk.** L\n**Hands.** 2\n**Note.** Use two hands.", rope.Description);
        Assert.Equal("**Price.** 8 sp\n**Bulk.** L\n**Hands.** 2", result.Final.Single(e => e.Id == "tent-pup").Description);
        // The alchemical table's note says all its items are alchemical and consumable, and the traits come from it.
        var flask = result.Final.Single(e => e.Id == "lesser-spark-flask");
        Assert.Equal(new[] { "bomb", "fire", "alchemical", "consumable" }, flask.Traits);
        Assert.Equal("Throw it.\n**Traits.** bomb, fire\n**Price.** 3 gp\n**Bulk.** L\n**Note.** All these items have the alchemical and consumable traits.", flask.Description);
        Assert.Equal("\"bomb\"", flask.Raw.First().Text);
        // The text before a section's first description is its own rules (leadRules names the path); a section that is not named has its lead read past.
        var rule = result.Final.Single(e => e.Id == "more");
        Assert.Equal(("rule", "Equipment", "The items of this section are made by alchemists."), (rule.Type, rule.Category, rule.Description));
        Assert.DoesNotContain(result.Final, e => e.Name == "Parts");
    }

    [Fact]
    public void ARowWithNoDescription_AndADescriptionWithNoRow_FailTheRun_UnlessTheRowSaysSo()
    {
        var ex = Assert.Throws<DegreesException>(() => Run(Kit(), GearYaml.Replace("undescribed: [Rope (50 feet), Pup]", "undescribed: [Rope (50 feet)]")));
        Assert.Contains("1 row(s) have no description (Pup)", ex.Message, StringComparison.Ordinal);

        ex = Assert.Throws<DegreesException>(() => Run(Kit(extraDescription: true)));
        Assert.Contains("1 description(s) have no row (Spoon)", ex.Message, StringComparison.Ordinal);

        // A name that is said to have no description, and has one (its own, or its heading's), fails the same way, and so does one that is no row.
        ex = Assert.Throws<DegreesException>(() => Run(Kit(), GearYaml.Replace("undescribed: [Rope (50 feet), Pup]", "undescribed: [Rope (50 feet), Pup, Hammer]")));
        Assert.Contains("an undescribed name has a description", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<DegreesException>(() => Run(Kit(), GearYaml.Replace("undescribed: [Rope (50 feet), Pup]", "undescribed: [Rope (50 feet), Pup, Spoon]")));
        Assert.Contains("an undescribed name has a description", ex.Message, StringComparison.Ordinal);
        // The alchemical items are alchemical and consumable because their note says so: a note that stops saying it fails the run.
        ex = Assert.Throws<DegreesException>(() => Run(Kit("These items are light.")));
        Assert.Contains("no longer says", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCountsAgreeTwoWays_ByRowsAndByDescriptions()
    {
        var kit = Kit();
        var counts = ImportPipeline.CheckItemCounts(Spine, kit, TakeStage.Resolve(Spine, kit, TakeList.Parse(GearYaml).Rows), Run(kit).Parsed).Single();

        // 7 rows (a heading is no row), 4 descriptions (3 of rows... Hammer, Candle, the flask; one of a heading, Lantern); 2 rows read the heading's, 2 are undescribed.
        Assert.Equal(("gear", 7, 4, 7), (counts.Kind, counts.TableRows, counts.Descriptions, counts.Entries));
        Assert.Equal("bomb:1 gear:6", string.Join(' ', counts.PerCategory.Select(p => $"{p.Key}:{p.Value}")));
    }

    [Fact]
    public void TheTwoUpPagesAndTheLeadRulesAndTheSidebarsBelongToTheirParsers()
    {
        Assert.Throws<DegreesException>(() => TakeList.Parse(GearYaml.Replace("itemKind: gear", "itemKind: weapon")));
        Assert.Throws<DegreesException>(() => TakeList.Parse(GearYaml.Replace("    ruleCategory: Equipment\n", string.Empty)));
        Assert.Throws<DegreesException>(() => TakeList.Parse(GearYaml.Replace("leadRules:", "twoUp: [[\"Chapter 9: Kit\"]]\n    leadRules:").Replace("itemKind: gear", "itemKind: armor").Replace("target: gear", "target: armor")));
        Assert.Throws<DegreesException>(() => TakeList.Parse("rows:\n  - { chapter: a, book: pc, parser: glossary, target: trait, approved: true, sidebars: [[\"A\", \"Sidebar: B\"]], paths: [[\"A\"]] }\n"));
        // The take-list of the gear is read: the two-up page and the lead rules are named by bookmark paths.
        var rows = TakeList.Parse(GearYaml.Replace("leadRules:", "twoUp: [[\"Chapter 9: Kit\"]]\n    leadRules:")).Rows;
        Assert.Equal(new[] { new[] { "Chapter 9: Kit" } }, rows[0].TwoUp.Select(p => p.ToArray()));
        Assert.Equal(new[] { new[] { "Chapter 9: Kit", "More" } }, rows[0].LeadRules.Select(p => p.ToArray()));
        // A gear row's pages go to the gear reader (with the two-up flag), not to the stat table reader.
        var options = ImportPipeline.OptionsFor(Spine, rows);
        Assert.True(options.GearTablePages[1].TwoUp);
        Assert.Empty(options.StatTablePages);
    }

    [Fact]
    public void AGearEntrysSourceIsTheSectionItComesFrom_ByItsTable()
    {
        var spine = new List<SpineNode>
        {
            new(0, "Chapter 9: Kit", new[] { "Chapter 9: Kit" }, 1, 1, false),
            new(1, "Adventuring Gear", new[] { "Chapter 9: Kit", "Adventuring Gear" }, 1, 1, false),
            new(1, "Alchemical Bombs", new[] { "Chapter 9: Kit", "Alchemical Bombs" }, 1, 1, false),
        };
        var yaml = GearYaml.Replace("\"Parts\"", "\"Adventuring Gear\"").Replace("\"More\"", "\"Alchemical Bombs\"");
        var result = ImportPipeline.Run(Kit(parts: "Adventuring Gear", more: "Alchemical Bombs"), spine, TakeList.Parse(yaml).Rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

        Assert.Equal("pc > Chapter 9: Kit > Adventuring Gear", result.Final.Single(e => e.Id == "hammer").Source);
        Assert.Equal("pc > Chapter 9: Kit > Adventuring Gear", result.Final.Single(e => e.Id == "tent-pup").Source);
        Assert.Equal("pc > Chapter 9: Kit > Alchemical Bombs", result.Final.Single(e => e.Id == "lesser-spark-flask").Source);
        Assert.Equal("pc > Chapter 9: Kit > Alchemical Bombs", result.Final.Single(e => e.Id == "alchemical-bombs").Source); // its lead rule, the same section
    }

    [Fact]
    public void AnOptionThatNamesWhatTheRowDoesNotHaveFails_AndSoDoesATwoUpPageWithOnePanel_AndAHeadingWithNothingUnderIt()
    {
        Assert.Contains("leadRules", Assert.Throws<DegreesException>(() => TakeList.Parse(GearYaml.Replace("leadRules: [[\"Chapter 9: Kit\", \"More\"]]", "leadRules: [[\"Chapter 9: Kit\", \"Elsewhere\"]]"))).Message, StringComparison.Ordinal);
        Assert.Contains("twoUp", Assert.Throws<DegreesException>(() => TakeList.Parse(GearYaml.Replace("leadRules:", "twoUp: [[\"Chapter 9: Kit\", \"Parts\"]]\n    leadRules:"))).Message, StringComparison.Ordinal);
        var rules = "rows:\n  - { chapter: a, book: pc, parser: rules, target: rule, approved: true, paths: [[\"A\", \"Spellcasting\"]], entryNames: { KEY: VAL } }\n";
        Assert.Contains("entryNames", Assert.Throws<DegreesException>(() => TakeList.Parse(rules.Replace("KEY", "Nothing").Replace("VAL", "Name"))).Message, StringComparison.Ordinal);
        Assert.Contains("entryNames", Assert.Throws<DegreesException>(() => TakeList.Parse(rules.Replace("VAL", "\"\"").Replace("KEY", "Spellcasting"))).Message, StringComparison.Ordinal);
        Assert.Contains("entryNames", Assert.Throws<DegreesException>(() => TakeList.Parse(GearYaml.Replace("leadRules:", "entryNames: { Parts: X }\n    leadRules:"))).Message, StringComparison.Ordinal);
        Assert.Equal("Name", TakeList.Parse(rules.Replace("KEY", "Spellcasting").Replace("VAL", "Name")).Rows[0].EntryNames["Spellcasting"]);

        // A page the row names two-up whose table has no right panel is not read as one panel.
        var single = new List<LayoutWord> { W("Item", 60, 698), W("Price", 140, 698), W("Bulk", 200, 698), W("Hands", 250, 698), W("Rope", 60, 686), W("5sp", 140, 686), W("L", 202, 686), W("2", 252, 686) };
        Assert.Contains("two panels", Assert.Throws<DegreesException>(() => GearTables.Carve(1, Width, single, Sets, twoUp: true)).Message, StringComparison.Ordinal);
        // A two-up page with no table at all is no failure.
        Assert.Empty(GearTables.Carve(1, Width, new List<LayoutWord> { W("Plain", 60, 600) }, Sets, twoUp: true).Tables);

        // A heading with no indented row under it (a name that wrapped) fails, wherever it is.
        Assert.Contains("no indented row", Assert.Throws<DegreesException>(() => Run(Kit(emptyBand: true))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadingWordInsideALongerWordIsNotTheHeadingSaid()
    {
        // `Tent` heading over `Contents`: the substring is no word of the name, so the entry is named by both.
        var kit = Kit();
        var rows = kit.Blocks.Select(b => b.Role == StatTables.Role ? b with { Rows = b.Rows!.Select(r => r.Count > 1 && r[1] == "Pup" ? new List<string> { "sub", "Contents", "8 sp", "L", "2" } : r).ToList() } : b).ToList();
        var yaml = GearYaml.Replace("Pup]", "Contents]");
        var result = ImportPipeline.Run(kit with { Blocks = rows }, Spine, TakeList.Parse(yaml).Rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

        Assert.Equal("Tent, Contents", result.Final.Single(e => e.Id == "tent-contents").Name);
    }

    // --- a sidebar the take-list takes as a rule, over a generated PDF ---

    private const string Roles = """
        rules:
          - { role: section, family: [Helvetica-Bold], size: [16, 16] }
          - { role: block-header, family: [Helvetica-Bold], size: [11, 11] }
          - { role: rules, family: [Times], size: [1, 11] }
          - { role: body, family: [Helvetica], size: [1, 11] }
        """;

    private const string SidebarYaml = """
        rows:
          - chapter: kit
            book: pc
            parser: rules
            target: rule
            category: Equipment
            approved: true
            columns: 1
            entryNames: { Parts: Spare Parts }
            sidebars: [["Chapter", "Sidebar: Wheel Box"]]
            paths: [["Chapter", "Parts"]]
        """;

    private static readonly List<SpineNode> SidebarSpine = new()
    {
        new(0, "Chapter", new[] { "Chapter" }, 1, 1, false),
        new(1, "Parts", new[] { "Chapter", "Parts" }, 1, 1, false),
        new(1, "Sidebar: Wheel Box", new[] { "Chapter", "Sidebar: Wheel Box" }, 1, 1, true),
    };

    private static BlocksResult SidebarBook(IReadOnlyList<TakeRow> rows)
    {
        var b = new PdfDocumentBuilder();
        var times = b.AddStandard14Font(Standard14Font.TimesRoman);
        var hel = b.AddStandard14Font(Standard14Font.Helvetica);
        var helBold = b.AddStandard14Font(Standard14Font.HelveticaBold);
        var p = b.AddPage(603, 783);
        p.AddText("Parts", 16, new PdfPoint(60, 740), helBold);
        p.AddText("The text of the section.", 10, new PdfPoint(60, 722), times);
        p.AddText("WHEEL BOX", 11, new PdfPoint(60, 690), helBold);
        p.AddText("Seat Belts: Strap in.", 9, new PdfPoint(69, 676), hel);
        p.AddText("More box words.", 9, new PdfPoint(60, 664), hel);
        using var doc = PdfDocument.Open(b.Build());
        var layout = LayoutStage.Run(doc, FontRoles.Parse(Roles));
        return BlocksStage.Run(layout.Pages, ImportPipeline.OptionsFor(SidebarSpine, rows));
    }

    [Fact]
    public void ASidebarIsTakenByItsBookmarkPath_NotByItsTitle_AnUntakenNeighbourAndARepeatedTitleStayDropped()
    {
        var spine = new List<SpineNode>
        {
            new(0, "Chapter", new[] { "Chapter" }, 1, 2, false),
            new(1, "Parts", new[] { "Chapter", "Parts" }, 1, 1, false),
            new(1, "Sidebar: Wheel Box", new[] { "Chapter", "Sidebar: Wheel Box" }, 1, 1, true),
            new(1, "Sidebar: Other Box", new[] { "Chapter", "Sidebar: Other Box" }, 1, 1, true),
            new(1, "Later", new[] { "Chapter", "Later" }, 2, 2, false),
            new(2, "Sidebar: Wheel Box", new[] { "Chapter", "Later", "Sidebar: Wheel Box" }, 2, 2, true),
        };
        var b = new PdfDocumentBuilder();
        var times = b.AddStandard14Font(Standard14Font.TimesRoman);
        var hel = b.AddStandard14Font(Standard14Font.Helvetica);
        var helBold = b.AddStandard14Font(Standard14Font.HelveticaBold);
        var one = b.AddPage(603, 783);
        one.AddText("Parts", 16, new PdfPoint(60, 740), helBold);
        one.AddText("The text of the section.", 10, new PdfPoint(60, 722), times);
        one.AddText("WHEEL BOX", 11, new PdfPoint(60, 690), helBold);
        one.AddText("Seat Belts: Strap in.", 9, new PdfPoint(69, 676), hel);
        one.AddText("OTHER BOX", 11, new PdfPoint(60, 650), helBold);
        one.AddText("Other words.", 9, new PdfPoint(69, 636), hel);
        var two = b.AddPage(603, 783);
        two.AddText("Later", 16, new PdfPoint(60, 740), helBold);
        two.AddText("WHEEL BOX", 11, new PdfPoint(60, 710), helBold);
        two.AddText("Second words.", 9, new PdfPoint(69, 696), hel);
        using var doc = PdfDocument.Open(b.Build());
        var layout = LayoutStage.Run(doc, FontRoles.Parse(Roles));
        var rows = TakeList.Parse(SidebarYaml).Rows;

        var blocks = BlocksStage.Run(layout.Pages, ImportPipeline.OptionsFor(spine, rows));

        var taken = Assert.Single(blocks.TakenSidebars!);
        Assert.Equal(new[] { "WHEEL BOX", "Seat Belts: Strap in." }, taken.Value.SelectMany(k => k.Lines)); // the first page's only
        Assert.Equal(new[] { "OTHER BOX", "Other words.", "WHEEL BOX", "Second words." }, blocks.Dropped!.SelectMany(k => k.Lines)); // the neighbour and the repeated title are dropped as ever
    }

    [Fact]
    public void ASidebarTheTakeListNamesIsAnEntry_AndIsNotLeftOutOrDropped_ThoughEverySidebarElseIs()
    {
        var rows = TakeList.Parse(SidebarYaml).Rows;
        var blocks = SidebarBook(rows);

        // The sidebar is kept apart under its title's key: it is not among the dropped blocks (so the denylist does not hash it as left-out text), nor among the section's own.
        Assert.Empty(blocks.Dropped!);
        Assert.Equal(new[] { "Parts", "The text of the section." }, blocks.Blocks.Select(k => k.Lines[0]));
        var taken = Assert.Single(blocks.TakenSidebars!);
        Assert.Equal("wheelbox", taken.Key); // the title's letters and digits, in lowercase
        Assert.Equal(new[] { "WHEEL BOX", "Seat Belts: Strap in.", "More box words." }, taken.Value.SelectMany(k => k.Lines));

        var result = ImportPipeline.Run(blocks, SidebarSpine, rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");
        Assert.Equal(new[] { "spare-parts", "wheel-box" }, result.Final.Select(e => e.Id).Order()); // `entryNames` renames the section whose title would clash
        var box = result.Final.Single(e => e.Id == "wheel-box");
        Assert.Equal(("rule", "Equipment", "pc > Chapter > Sidebar: Wheel Box"), (box.Type, box.Category, box.Source)); // the bookmark path, prefix and all
        Assert.Contains("Seat Belts: Strap in.", box.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("WHEEL BOX", box.Description, StringComparison.Ordinal); // its heading is its name

        // Without the row's `sidebars` the same page drops the sidebar, as every other one is.
        var plain = TakeList.Parse(SidebarYaml.Replace("    sidebars: [[\"Chapter\", \"Sidebar: Wheel Box\"]]\n", string.Empty)).Rows;
        var dropped = SidebarBook(plain);
        Assert.Equal(2, dropped.Dropped!.Count); // the heading and the box's lines
        Assert.Empty(dropped.TakenSidebars!);
        // A path that is no sidebar bookmark fails the run.
        Assert.Throws<DegreesException>(() => ImportPipeline.OptionsFor(SidebarSpine, TakeList.Parse(SidebarYaml.Replace("Sidebar: Wheel Box", "Parts")).Rows));
    }

    [Fact]
    public void ALineThatOpensWithADegreeLabel_EndsASidebarThatIsDropped_ButBelongsToOneThatIsTaken()
    {
        var b = new PdfDocumentBuilder();
        var times = b.AddStandard14Font(Standard14Font.TimesRoman);
        var hel = b.AddStandard14Font(Standard14Font.Helvetica);
        var helBold = b.AddStandard14Font(Standard14Font.HelveticaBold);
        var p = b.AddPage(603, 783);
        p.AddText("Parts", 16, new PdfPoint(60, 740), helBold);
        p.AddText("The text of the section.", 10, new PdfPoint(60, 722), times);
        p.AddText("WHEEL BOX", 11, new PdfPoint(60, 690), helBold);
        p.AddText("Seat Belts: Strap in.", 9, new PdfPoint(60, 676), hel);
        p.AddText("Success", 9, new PdfPoint(60, 664), helBold);
        p.AddText("The strap holds.", 9, new PdfPoint(100, 664), hel);
        using var doc = PdfDocument.Open(b.Build());
        var layout = LayoutStage.Run(doc, FontRoles.Parse(Roles));

        // A sidebar that is dropped ends where a line opens with the label of a degree of success (the page's own action text goes on): the line stays with the page.
        var plain = BlocksStage.Run(layout.Pages, ImportPipeline.OptionsFor(SidebarSpine, TakeList.Parse(SidebarYaml.Replace("    sidebars: [[\"Chapter\", \"Sidebar: Wheel Box\"]]\n", string.Empty)).Rows));
        Assert.Contains(plain.Blocks, k => k.Lines.Any(l => l.StartsWith("Success", StringComparison.Ordinal)));

        // A sidebar the take-list takes keeps its own degrees of success: they are its text, and no block of it is the page's.
        var taken = BlocksStage.Run(layout.Pages, ImportPipeline.OptionsFor(SidebarSpine, TakeList.Parse(SidebarYaml).Rows));
        Assert.DoesNotContain(taken.Blocks, k => k.Lines.Any(l => l.StartsWith("Success", StringComparison.Ordinal)));
        Assert.Contains(Assert.Single(taken.TakenSidebars!).Value, k => k.Lines.Any(l => l.StartsWith("Success", StringComparison.Ordinal)));
    }
}
