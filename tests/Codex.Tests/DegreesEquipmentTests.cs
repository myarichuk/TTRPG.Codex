using Codex.ContentImport.Degrees;

namespace Codex.Tests;

/// <summary>
/// Chapter 6a's stat tables and items over synthetic words and blocks (invented names only): the table reader (columns by the header's labels, a band, a wrapped row, a name that wraps, an ammunition line,
/// a footnote and its note, and the row with a missing cell that fails the run), and the join of a table row to the paragraph that describes it (a row with none, a description with none, the names an
/// items row excludes, the fist's row with no paragraph, the descriptions that make a rule). CI never sees a book.
/// </summary>
public class DegreesEquipmentTests
{
    private static LayoutWord W(string text, double left, double bottom, double size = 9, string role = "body") =>
        new(1, text, "F", size, left, left + (text.Length * 5), bottom, bottom + size, role);

    private static readonly IReadOnlyList<IReadOnlyList<string>> Labels = new List<IReadOnlyList<string>> { new[] { "Price", "Bulk", "Group" } };

    private static List<LayoutWord> Header(double y, string name) =>
        new() { W(name, 60, y), W("Price", 140, y), W("Bulk", 200, y), W("Group", 250, y) };

    private static List<LayoutWord> Row(double y, string name, string price, string bulk, string group) =>
        new() { W(name, 60, y), W(price, 140, y), W(bulk, 200, y), W(group, 250, y) };

    [Fact]
    public void ATableIsCarvedFromItsHeader_ItsCaptionAndItsRows_AndTheTextAroundItStays()
    {
        var words = new List<LayoutWord> { W("TOOLS", 60, 710, 12, "block-header") };
        words.AddRange(Header(698, "Tools"));
        words.AddRange(Row(686, "Hammer", "1sp", "1", "Maul"));
        words.AddRange(Row(674, "Saw", "2gp", "L", "Cutter"));
        words.Add(W("Some", 60, 600));
        words.Add(W("text", 100, 600));

        var (tables, rest) = StatTables.Carve(1, words, Labels);

        var table = Assert.Single(tables);
        Assert.Equal(StatTables.Role, table.Role);
        Assert.Equal(new[] { "title", "head", "row", "row" }, table.Rows!.Select(r => r[0]));
        Assert.Equal(new[] { "row", "Hammer", "1sp", "1", "Maul" }, table.Rows![2]);
        Assert.Equal("Hammer | 1sp | 1 | Maul", table.Lines[2]); // the lines are the cells, with no word for the row's kind (the counts of the book's words would read it)
        Assert.Equal(new[] { "head", "Tools", "Price", "Bulk", "Group" }, table.Rows[1]);
        Assert.Equal(new[] { "Some", "text" }, rest.Select(w => w.Text)); // the page's own text stays
        // A page with no header row is returned as it was.
        var plain = new List<LayoutWord> { W("Just", 60, 600), W("words", 100, 600) };
        Assert.Empty(StatTables.Carve(1, plain, Labels).Tables);
        Assert.Same(plain, StatTables.Carve(1, plain, Labels).Words);
    }

    [Fact]
    public void ABandAWrappedCellAWrappedNameAnAmmunitionLineAndAFootnoteAreRead()
    {
        var words = new List<LayoutWord> { W("TOOLS", 60, 722, 12, "block-header"), W("Uncommon", 60, 710) };
        words.AddRange(Header(698, "Tools"));
        words.AddRange(Row(686, "Bow", "5gp", "1", "Bow"));
        // The price of the ammunition and its Bulk only, set in from the name column's edge.
        words.AddRange(new[] { W("10", 69, 674), W("arrows", 80, 674), W("1sp", 140, 674), W("L", 200, 674) });
        words.AddRange(Row(662, "Longbow", "9gp", "2", "Bow"));
        words.Add(W("(level", 60, 650));
        words.Add(W("1)", 90, 650));
        words.AddRange(Row(638, "Sling", "1cp", "L", "Sling"));
        words.Add(W("(wraps)", 250, 626)); // a cell that runs on, with no name
        // A footnote mark (a raised, small digit) at the end of the Bulk cell of the sling, and the note under the table.
        words.Add(W("2", 215, 641, 5.2));
        words.Add(W("2", 56, 617, 5.2));
        words.Add(W("Note", 62, 614));
        words.Add(W("text.", 90, 614));

        var (tables, rest) = StatTables.Carve(1, words, Labels);

        var rows = Assert.Single(tables).Rows!;
        Assert.Empty(rest);
        Assert.Equal(new[] { "title", "band", "head", "row", "ammo", "row", "row", "note" }, rows.Select(r => r[0]));
        Assert.Equal(new[] { "band", "Uncommon" }, rows[1]);
        Assert.Equal(new[] { "ammo", "10 arrows", "1sp", "L" }, rows[4]);
        Assert.Equal("Longbow (level 1)", rows[5][1]);
        Assert.Equal("Sling", rows[6][1]);
        Assert.Equal("Sling (wraps)", rows[6][4]);
        Assert.Equal("L ^2", rows[6][3]);
        Assert.Equal(new[] { "note", "2", "Note text." }, rows[7]);
    }

    [Fact]
    public void ARowWithAMissingCellFails_AndSoDoesACellUnderNoLabel()
    {
        var short1 = Header(698, "Tools").Concat(new[] { W("Hammer", 60, 686), W("1sp", 140, 686), W("Maul", 250, 686) }).ToList();
        Assert.Contains("3 cells where its header has 4", Assert.Throws<DegreesException>(() => StatTables.Carve(1, short1, Labels)).Message, StringComparison.Ordinal);

        var stray = Header(698, "Tools").Concat(Row(686, "Hammer", "1sp", "1", "Maul")).Concat(new[] { W("Far", 480, 686) }).ToList();
        Assert.Contains("under exactly one column label", Assert.Throws<DegreesException>(() => StatTables.Carve(1, stray, Labels)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportedIdThatEqualsAHomebrewIdFailsTheRun_InAnyOfTheThreeFormsTheFilesWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "degrees-homebrew-" + Guid.NewGuid().ToString("N"));
        var pack = Path.Combine(root, "rules-core");
        var folder = Path.Combine(root, "homebrew-core", "weapons");
        Directory.CreateDirectory(pack);
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "weapons.yaml"), "- id: plain-one\n  name: A\n- id: \"quoted-one\"\n  name: B\n- { id: flow-one, name: C }\n");
            Assert.Equal(new[] { "flow-one", "plain-one", "quoted-one" }, PackEmitter.HomebrewIds(pack).Order());
            var entries = new[] { "dirk", "plain-one", "flow-one" }.Select(i => new FinalEntry { Id = i }).ToList();
            Assert.Equal(new[] { "flow-one", "plain-one" }, PackEmitter.HomebrewClashes(entries, pack));
        }
        finally
        {
            Directory.Delete(root, true);
        }

        // The real homebrew pack's files read in the same way (its armor and weapons are written in the flow form and the block form).
        var real = PackEmitter.HomebrewIds(Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees", "packs", "rules-core"));
        Assert.Subset(real, new HashSet<string> { "quilted-vest", "lacquered-scale", "reed-spear", "ledge-axe" });
    }

    [Theory]
    [InlineData("Thrown 10 ft.", "thrown")]
    [InlineData("two-hand d8", "two-hand")]
    [InlineData("Versatile S", "versatile")]
    [InlineData("Attached to shield", "attached")]
    [InlineData("ranged trip", "ranged-trip")]
    [InlineData("free-hand", "free-hand")]
    public void ATraitsKeyIsItsWordsUpToTheFirstParameter(string printed, string key) => Assert.Equal(key, EntriesStage.TraitKey(printed));

    // --- the items parser, over a synthetic book ---

    private const string ItemsYaml = """
        rows:
          - chapter: kit
            book: pc
            parser: items
            target: weapon
            itemKind: weapon
            approved: true
            artLabels: true
            countsDiffer: The hat is not a weapon.
            exclude: [Hat]
            undescribed: [Fist]
            ruleDescriptions: [Pebble]
            ruleName: Kinds of Pebbles
            ruleCategory: Equipment
            tables: [["Chapter 9: Kit"]]
            tableColumns: [["Price", "Damage", "Bulk", "Hands", "Group", "Weapon Traits"]]
            paths: [["Chapter 9: Kit", "Descriptions"]]
        """;

    private static readonly List<SpineNode> Spine = new()
    {
        new(0, "Chapter 9: Kit", new[] { "Chapter 9: Kit" }, 1, 1, false),
        new(1, "Damage", new[] { "Chapter 9: Kit", "Damage" }, 1, 1, false),
        new(1, "Bulk", new[] { "Chapter 9: Kit", "Bulk" }, 1, 1, false),
        new(1, "Hands", new[] { "Chapter 9: Kit", "Hands" }, 1, 1, false),
        new(1, "Group", new[] { "Chapter 9: Kit", "Group" }, 1, 1, false),
        new(1, "Weapon Traits", new[] { "Chapter 9: Kit", "Weapon Traits" }, 1, 1, false),
        new(1, "Descriptions", new[] { "Chapter 9: Kit", "Descriptions" }, 1, 1, false),
    };

    private static Block Text(string role, double top, params (string Text, string Label)[] lines) =>
        new(1, 0, role, 60, 0, 300, top, lines.Select(l => l.Text).ToList(), lines.Select(l => new LineInfo(60, 300, l.Label)).ToList());

    private static Block Table(params string[][] rows) =>
        new(1, 0, StatTables.Role, 60, 0, 400, 700, rows.Select(r => string.Join(" | ", r.Skip(1))).ToList(), null, rows.Select(r => r.ToList()).ToList());

    private static BlocksResult Kit(bool extraDescription = false) => new(
        new List<Block>
        {
            Table(new[] { "title", "MELEE WEAPONS" }, new[] { "head", "Simple Weapons", "Price", "Damage", "Bulk", "Hands", "Group", "Weapon Traits" },
                new[] { "row", "Dirk", "2 sp", "1d4 P", "L", "1", "Knife", "Agile, finesse, thrown 10 ft." }, new[] { "row", "Hat", "—", "1 B", "—", "—", "Cloth", "—" },
                new[] { "row", "Fist", "—", "1d3 B", "—", "—", "Brawl", "Agile" }, new[] { "band", "Uncommon" },
                new[] { "head", "Martial Weapons", "Price", "Damage", "Bulk", "Hands", "Group", "Weapon Traits" },
                new[] { "row", "Pike (level 1)", "1 gp", "1d10 S", "2", "2", "Spear", "Deadly d12, versatile P" }),
            Text("section", 720, ("Descriptions", string.Empty)),
            Text("body", 700, ("DIRK", string.Empty)),
            Text("rules", 690, ("Every kind of thing is described below.", string.Empty), ("Dirk: A short blade for close work.", "Dirk:")),
            Text("rules", 670, ("Pike: A long pole with a point.", "Pike:"), ("Pebble: A small stone.", "Pebble:")),
            extraDescription ? Text("rules", 650, ("Spoon: Not in the table.", "Spoon:")) : Text("body", 650, ("PEBBLE", string.Empty)),
        },
        0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static PipelineOutput Run(BlocksResult blocks, string yaml = ItemsYaml) =>
        ImportPipeline.Run(blocks, Spine, TakeList.Parse(yaml).Rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc");

    [Fact]
    public void AnItemIsItsTableRowAndItsDescription_JoinedByName()
    {
        var result = Run(Kit());

        Assert.Equal(new[] { "dirk", "fist", "pike" }, result.Final.Where(e => e.Type == "weapon").Select(e => e.Id).Order());
        var dirk = result.Final.Single(e => e.Id == "dirk");
        Assert.Equal("Dirk", dirk.Name);
        Assert.Equal(new[] { "weapons" }, dirk.Tags);
        Assert.Equal(new[] { "agile", "finesse", "thrown" }, dirk.Traits);
        Assert.Equal("A short blade for close work.\n**Traits.** agile, finesse, thrown 10 ft.", dirk.Description);
        Assert.Equal(new[] { "weaponCategory", "damage", "damageType", "hands", "bulk", "group", "price", "use" }, dirk.Raw.Select(r => r.Label));
        Assert.Equal("{ attack: melee, abilities: [str, dex], damage: 1d4 }", dirk.Raw.Single(r => r.Label == "use").Text);
        Assert.Equal("\"simple\"", dirk.Raw.Single(r => r.Label == "weaponCategory").Text);
        // A row under a rarity heading carries the rarity as a trait, a name with an item level has the level, and a trait's parameter stays in the printed line.
        var pike = result.Final.Single(e => e.Id == "pike");
        Assert.Equal(1, pike.Level);
        Assert.Equal(new[] { "deadly", "versatile", "uncommon" }, pike.Traits);
        Assert.Equal("{ attack: melee, abilities: [str], damage: 1d10 }", pike.Raw.Single(r => r.Label == "use").Text);
        Assert.Contains("**Traits.** deadly d12, versatile piercing", pike.Description, StringComparison.Ordinal);
        // The row with no paragraph is an entry of its row alone; a hat is no weapon (excluded); and the pebble's paragraph is a rule, since it has no row.
        Assert.Equal("**Traits.** agile", result.Final.Single(e => e.Id == "fist").Description);
        Assert.DoesNotContain(result.Final, e => e.Id == "hat");
        var pebbles = result.Final.Single(e => e.Type == "rule");
        Assert.Equal(("kinds-of-pebbles", "Equipment", "**Pebble.** A small stone."), (pebbles.Id, pebbles.Category, pebbles.Description));
        // The caption and the Range column agree on melee: a melee row has no range.
        Assert.DoesNotContain(dirk.Raw, r => r.Label == "range");
    }

    [Fact]
    public void ARowWithNoDescription_AndADescriptionWithNoRow_FailTheRun_UnlessTheRowSaysSo()
    {
        var ex = Assert.Throws<DegreesException>(() => Run(Kit(), ItemsYaml.Replace("undescribed: [Fist]", "undescribed: []")));
        Assert.Contains("1 row(s) have no description", ex.Message, StringComparison.Ordinal);

        ex = Assert.Throws<DegreesException>(() => Run(Kit(extraDescription: true)));
        Assert.Contains("1 description(s) have no row", ex.Message, StringComparison.Ordinal);

        // An excluded name must match a row or a description, and an exclusion needs its reason.
        ex = Assert.Throws<DegreesException>(() => Run(Kit(), ItemsYaml.Replace("exclude: [Hat]", "exclude: [Hat, Cape]")));
        Assert.Contains("excluded name", ex.Message, StringComparison.Ordinal);
        Assert.Throws<DegreesException>(() => TakeList.Parse(ItemsYaml.Replace("countsDiffer: The hat is not a weapon.\n", string.Empty)));
        // The art label ("DIRK" in capitals, set alone) is no text: without the row's say so it is a paragraph of the lead and read past too, so only the counts show it.
        Assert.Equal(3, ImportPipeline.CheckItemCounts(Spine, Kit(), TakeStage.Resolve(Spine, Kit(), TakeList.Parse(ItemsYaml).Rows), Run(Kit()).Parsed).Single().Entries);
    }

    [Fact]
    public void AnItemsRowNeedsItsTablesItsColumnsAndAKind_AndTheColumnsMustBeTitlesOfTheChapter()
    {
        Assert.Throws<DegreesException>(() => TakeList.Parse(ItemsYaml.Replace("itemKind: weapon\n", string.Empty)));
        Assert.Throws<DegreesException>(() => TakeList.Parse(ItemsYaml.Replace("itemKind: weapon", "itemKind: wand")));
        Assert.Throws<DegreesException>(() => TakeList.Parse(ItemsYaml.Replace("[\"Price\", \"Damage\"", "[\"Cost\", \"Damage\"")));
        Assert.Throws<DegreesException>(() => TakeList.Parse(ItemsYaml.Replace("parser: items", "parser: rules").Replace("itemKind: weapon\n", string.Empty)));

        // A label that is no bookmark of the chapter fails the count of the columns against the document.
        var ex = Assert.Throws<DegreesException>(() => Run(Kit(), ItemsYaml.Replace("\"Weapon Traits\"]]", "\"Weapon Marks\"]]")));
        Assert.Contains("not the title of any bookmark", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamelessLineMayOnlyWrapTheLastColumn_AndAFootnoteMarkNeedsACell()
    {
        // A nameless line with a cell under the Bulk column is a shifted value: it fails, and is never joined to the row above.
        var shifted = Header(698, "Tools").Concat(Row(686, "Hammer", "1sp", "1", "Maul")).Concat(new[] { W("L", 200, 674) }).ToList();
        Assert.Contains("only one that wraps", Assert.Throws<DegreesException>(() => StatTables.Carve(1, shifted, Labels)).Message, StringComparison.Ordinal);

        // A mark with no cell to its left on its line is not dropped into the page's text.
        var lonely = Header(698, "Tools").Concat(Row(686, "Hammer", "1sp", "1", "Maul")).Concat(new[] { W("3", 61, 689, 5.2) }).ToList();
        Assert.Contains("no cell to its left", Assert.Throws<DegreesException>(() => StatTables.Carve(1, lonely, Labels)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARarityHeadingFollowedByARowOrByTheEndOfTheTableFails()
    {
        string[] head = { "head", "Simple Weapons", "Price", "Damage", "Bulk", "Hands", "Group", "Weapon Traits" };
        string[] row = { "row", "Dirk", "2 sp", "1d4 P", "L", "1", "Knife", "Agile" };
        BlocksResult Book(params string[][] rows) => new(new List<Block> { Table(new[] { new[] { "title", "MELEE WEAPONS" }, head }.Concat(rows).ToArray()), Text("section", 720, ("Descriptions", string.Empty)), Text("rules", 700, ("Dirk: A blade.", "Dirk:")) },
            0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());
        var yaml = ItemsYaml.Replace("exclude: [Hat]", "exclude: []").Replace("    countsDiffer: The hat is not a weapon.\n", string.Empty).Replace("undescribed: [Fist]", "undescribed: []").Replace("ruleDescriptions: [Pebble]", "ruleDescriptions: []")
            .Replace("    ruleName: Kinds of Pebbles\n", string.Empty).Replace("    ruleCategory: Equipment\n", string.Empty);

        Assert.Equal(new[] { "dirk" }, Run(Book(row), yaml).Final.Select(e => e.Id));
        Assert.Contains("lost", Assert.Throws<DegreesException>(() => Run(Book(new[] { "band", "Uncommon" }, row), yaml)).Message, StringComparison.Ordinal);
        Assert.Contains("lost", Assert.Throws<DegreesException>(() => Run(Book(row, new[] { "band", "Uncommon" }), yaml)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("+2/+4", 2)]
    [InlineData("–5 ft.", -5)]
    [InlineData("+0", 0)]
    [InlineData("120 ft.", 120)]
    [InlineData("—", null)]
    public void ANumericCellIsASignedNumberWithFeetOrASecondBonus(string cell, int? expected) => Assert.Equal(expected, EntriesStage.Signed(cell, "test"));

    [Theory]
    [InlineData("1 L")]
    [InlineData("3 gp")]
    [InlineData("+2 extra")]
    public void ACellWithOtherTrailingTextFails(string cell) => Assert.Throws<DegreesException>(() => EntriesStage.Signed(cell, "test"));

    // --- stable ids, with and without a pack folder ---

    private static readonly List<SpineNode> ClashSpine = new()
    {
        new(0, "Chapter 9: Kit", new[] { "Chapter 9: Kit" }, 1, 1, false),
        new(1, "Damage", new[] { "Chapter 9: Kit", "Damage" }, 1, 1, false),
        new(1, "Bulk", new[] { "Chapter 9: Kit", "Bulk" }, 1, 1, false),
        new(1, "Hands", new[] { "Chapter 9: Kit", "Hands" }, 1, 1, false),
        new(1, "Group", new[] { "Chapter 9: Kit", "Group" }, 1, 1, false),
        new(1, "Weapon Traits", new[] { "Chapter 9: Kit", "Weapon Traits" }, 1, 1, false),
        new(1, "Dirk", new[] { "Chapter 9: Kit", "Dirk" }, 1, 1, false),
        new(1, "Descriptions", new[] { "Chapter 9: Kit", "Descriptions" }, 1, 1, false),
    };

    private const string RuleRow = "  - { chapter: lore, book: pc, parser: rules, target: rule, category: Equipment, approved: true, paths: [[\"Chapter 9: Kit\", \"Dirk\"]] }\n";

    private static BlocksResult ClashBook()
    {
        var kit = Kit().Blocks;
        var blocks = new List<Block> { kit[0], Text("section", 740, ("Dirk", string.Empty)), Text("rules", 735, ("A rule about dirks.", string.Empty)) };
        blocks.AddRange(kit.Skip(1));
        return new BlocksResult(blocks, 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());
    }

    private static IEnumerable<(string Id, string Type)> Ids(PipelineOutput result) => result.Final.Where(e => e.Id.StartsWith("dirk", StringComparison.Ordinal)).Select(e => (e.Id, e.Type)).OrderBy(e => e.Type, StringComparer.Ordinal);

    private static PipelineOutput RunClash(string yaml, IReadOnlyDictionary<string, string>? shipped) =>
        ImportPipeline.Run(ClashBook(), ClashSpine, TakeList.Parse(yaml).Rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc", shipped);

    [Fact]
    public void AnIdThatTwoTypesShare_IsKeptByTheEarlierRow_WithOrWithoutAShippedMap_AndByTheShippedOneWhenThereIsOne()
    {
        var itemsFirst = ItemsYaml + "\n" + RuleRow;
        var rulesFirst = "rows:\n" + RuleRow + string.Join('\n', ItemsYaml.Split('\n').Skip(1));

        // No pack to read: the entry of the earlier row keeps `dirk`, the other takes the suffix of its type; the run does not fail on the clash either way.
        Assert.Equal(new[] { ("dirk-rule", "rule"), ("dirk", "weapon") }, Ids(RunClash(itemsFirst, null)));
        Assert.Equal(new[] { ("dirk", "rule"), ("dirk-weapon", "weapon") }, Ids(RunClash(rulesFirst, null)));
        // A shipped id keeps its entry, whichever row is first, so a pack folder and no pack folder agree where the pack agrees with shipping order and the pack wins where it does not.
        var shippedAsRule = new Dictionary<string, string> { ["dirk"] = "rules" };
        Assert.Equal(new[] { ("dirk", "rule"), ("dirk-weapon", "weapon") }, Ids(RunClash(itemsFirst, shippedAsRule)));
        var shippedAsWeapon = new Dictionary<string, string> { ["dirk"] = "weapons" };
        Assert.Equal(new[] { ("dirk-rule", "rule"), ("dirk", "weapon") }, Ids(RunClash(rulesFirst, shippedAsWeapon)));
    }

    [Fact]
    public void TheCommittedPackIsTheSourceOfShippedIds_WhetherOrNotARunNamesAPackFolder()
    {
        var committed = PackEmitter.CommittedPackDir();
        Assert.NotNull(committed);
        Assert.Equal(Path.GetFullPath(committed!), Path.GetFullPath(Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees", "packs", "rules-core")));
        // `hide` shipped as an action and `staff` as a trait: the armor and the weapon of those names are the ones that take the suffix, with no pack folder named.
        var shipped = DegreesCommand.StableIds(null)!;
        Assert.Equal(("actions", "traits"), (shipped["hide"], shipped["staff"]));
        Assert.Equal(shipped.OrderBy(k => k.Key, StringComparer.Ordinal), DegreesCommand.StableIds(committed)!.OrderBy(k => k.Key, StringComparer.Ordinal));
    }

    // --- the review of a regeneration from scratch (denylist --fresh) ---

    private static HashSet<string> Set(params string[] hashes) => hashes.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void AFreshRegeneration_NeverDropsTheHashOfARedactedWordOrOfADroppedEntrysName()
    {
        var ex = Assert.Throws<DegreesException>(() => ImportPipeline.ReviewFresh("denylist", Set("a", "b", "c"), new[] { "a" }, Array.Empty<string>(), Set("c"), null));
        Assert.Contains("redaction row", ex.Message, StringComparison.Ordinal);
        // A hash that is gone and is not one of those is fine, and counted.
        var review = ImportPipeline.ReviewFresh("denylist", Set("a", "b", "c"), new[] { "a" }, Array.Empty<string>(), Set("z"), null);
        Assert.Equal((1, 1, 0, 0, 2, 0), (review.Written, review.Kept, review.New, review.Waived, review.Taken, review.Titles));
    }

    [Fact]
    public void AFreshRegeneration_CountsWhatIsWaivedWhatIsNewAndWhatWentWithItsSection()
    {
        // The file holds a, b, c and d; the fresh list is a and e; b is lifted by a waiver; c and d are left-out text that is taken now.
        var review = ImportPipeline.ReviewFresh("denylist", Set("a", "b", "c", "d"), new[] { "a", "e" }, new[] { "b" }, Set(), null);

        Assert.Equal("denylist", review.File);
        Assert.Equal((2, 1, 1, 1, 2, 0), (review.Written, review.Kept, review.New, review.Waived, review.Taken, review.Titles));
        // A lifted hash is not written even when the fresh list has it.
        Assert.Equal(1, ImportPipeline.ReviewFresh("denylist", Set("a"), new[] { "a", "b" }, new[] { "b" }, Set(), null).Written);
    }

    [Fact]
    public void AFreshRegeneration_KeepsThePairRule_ApairIsOnlyDroppedForAWaiverATitleOrADeniedWord()
    {
        // b is a pair of a take-list title, c is lifted by a waiver; d is neither: it would vanish only because a range has been taken, so the run fails.
        var ok = ImportPipeline.ReviewFresh("pairs", Set("a", "b", "c"), new[] { "a" }, new[] { "c" }, Set(), Set("b"));
        Assert.Equal((1, 1, 0, 1, 0, 1), (ok.Written, ok.Kept, ok.New, ok.Waived, ok.Taken, ok.Titles));

        var ex = Assert.Throws<DegreesException>(() => ImportPipeline.ReviewFresh("pairs", Set("a", "b", "d"), new[] { "a" }, Array.Empty<string>(), Set(), Set("b")));
        Assert.Contains("1 pair(s)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshRegeneration_RefusesATakeListWithRowsOfAnotherBook_AndAMissingRedactionRowFailsInsteadOfDropping()
    {
        var rows = TakeList.Parse(ItemsYaml.Replace("rows:\n", "rows:\n  - { chapter: other, book: mc, parser: rules, target: rule, approved: true, paths: [[\"X\"]] }\n")).Rows;
        Assert.Throws<DegreesException>(() => ImportPipeline.RefuseFreshWithOtherBooks(rows, "pc"));
        ImportPipeline.RefuseFreshWithOtherBooks(rows.Where(r => r.Book == "pc"), "pc");

        var table = RedactionTable.Parse("normalization: 2\nrows:\n  - { book: pc, entry: here, field: description, span: { sha256: \"" + new string('a', 64) + "\", length: 3 }, action: cut, reason: \"r\" }\n  - { book: pc, entry: gone, field: description, span: { sha256: \"" + new string('b', 64) + "\", length: 3 }, action: cut, reason: \"r\" }\n");
        var entries = new[] { new RedactionEntry("here", new Dictionary<string, string> { ["description"] = "abc" }) };
        Assert.Equal("gone", Assert.Single(RedactionStage.RowsWithoutEntry(entries, table)).Entry);
    }
}
