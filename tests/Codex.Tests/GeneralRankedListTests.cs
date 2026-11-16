using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>General ranked lists (<c>sheetRules.ranked</c>, plan step P3): ranks that grants and choices reach as they reach the skills, scores
/// published as values, and the places that read a rank (armor class, an action). The system and its words are invented here.</summary>
public class GeneralRankedListTests
{
    private const string Header = """
        id: wardens
        name: Wardens
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 10, modifier: half-minus-five }
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: half-minus-five }
          - { key: wis, label: Wisdom, abbreviation: WIS, default: 10, modifier: half-minus-five }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: armorClass, label: Armor Class, type: Number, defaultValue: 10 }
        entityTypes:
          - { type: class, label: Class }
          - { type: feat, label: Feat }
          - { type: armor, label: Armor }
          - { type: weapon, label: Weapon }
        """;

    private const string Skills = """
            skills:
              list:
                - { key: climb, label: Climb, attribute: str }
              ranks:
                - { key: trained, label: Trained, sheetKey: skillTrained, value: "level + 2" }
                - { key: expert, label: Expert, sheetKey: skillExpert, value: "level + 4" }
                - { key: master, label: Master, sheetKey: skillMaster, value: "level + 6" }
              saves:
                ranks:
                  - { key: trained, label: Trained, sheetKey: saveTrained, value: "level + 2" }
          """;

    private const string Ranked = """
            ranked:
              - { key: perception, label: Perception, ranksOf: skills, attribute: wis, publish: true }
              - { key: classDc, label: Class DC, ranksOf: skills, base: 10, attributeFrom: { field: class, property: keyAttribute }, publish: true }
              - key: armor
                label: Armor
                ranksOf: skills
                list:
                  - { key: unarmored, label: Unarmored }
                  - { key: light, label: Light armor }
                  - { key: medium, label: Medium armor }
                  - { key: heavy, label: Heavy armor }
              - { key: weapons, label: Weapons, ranksOf: skills, list: [{ key: simple, label: Simple }, { key: martial, label: Martial }] }
              - { key: wardcraft, label: Wardcraft, ranks: [{ key: novice, value: "level + 1" }, { key: adept, value: "level + 3" }], list: [{ key: hearth }, { key: road }] }
          """;

    private const string Rest = """
            armorClass:
              key: armorClass
              unarmored: "10 + mod(dex)"
              dexAttribute: dex
              rank: { list: armor, property: armorCategory, unarmored: unarmored }
            inventory: { key: inventory, label: Gear, types: [armor, weapon] }
            actions:
              - { key: strikes, label: Strike, inventory: equipped, attackBonus: "rankValue + abilityMod", rank: { list: weapons, property: category } }
            lists:
              - { key: feats, label: Feats, targetEntityType: feat }
            features:
              - { field: class, property: features, byField: level }
              - { list: feats }
          """;

    private static string Yaml(string skills = Skills, string ranked = Ranked) => Header + "\nsheetRules:\n  enforcement: warn\n" + skills + "\n" + ranked + "\n" + Rest + "\n";

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse(Yaml());

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);
    private static List<object> Items(params object[] items) => items.ToList();

    private static Dictionary<string, object> Row(string name, int level, params Dictionary<string, object>[] adds)
        => Map(("name", name), ("level", (long)level), ("description", name + "."), ("adds", adds.Cast<object>().ToList()));

    private static Dictionary<string, object> Grant(string to, string? rank = null, int raise = 0, object[]? entries = null, params (string Key, object Value)[] more)
    {
        var add = Map(("to", to));
        if (rank != null) add["rank"] = rank;
        if (raise != 0) add["raise"] = (long)raise;
        if (entries != null) add["entries"] = entries.ToList();
        foreach (var (key, value) in more) add[key] = value;
        return add;
    }

    private static ContentEntry Entry(string type, string id, params (string Key, object Value)[] properties)
    {
        var entry = new ContentEntry { Type = type, Id = id, Name = id };
        foreach (var (key, value) in properties) entry.Properties[key] = value;
        return entry;
    }

    private static ContentEntry Warden(params Dictionary<string, object>[] rows) =>
        Entry("class", "warden", ("keyAttribute", "str"), ("features", rows.Cast<object>().ToList()));

    private sealed class Entries(params ContentEntry[] all) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => all.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => all.Where(e => e.Type == type);
    }

    private static Dictionary<string, object> Sheet(params (string Key, object Value)[] more)
    {
        var sheet = Map(("level", 5L), ("str", 18L), ("dex", 14L), ("wis", 12L), ("class", "warden"), ("acMode", "auto"));
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static SheetEvaluation Evaluate(Dictionary<string, object> sheet, ContentEntry warden, params ContentEntry[] others) =>
        new SheetRuleEngine(System, new Entries(others.Append(warden).ToArray())).Recompute(sheet);

    private static long Perception(SheetEvaluation e) => Convert.ToInt64(e.Derived["perception"]);

    private static string Errors(string yaml)
    {
        try
        {
            SystemDefinitionLoader.Parse(yaml);
            return string.Empty;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    // ---- 1: published scores ----

    [Fact]
    public void ATickInARankWorksOutTheScore_AndNoRankLeavesTheAttributeAlone()
    {
        var plain = Evaluate(Sheet(), Warden());
        Assert.Equal(1, Perception(plain));
        Assert.Equal(14, Convert.ToInt64(plain.Derived["classDc"])); // 10 + Str 4, untrained

        var expert = Evaluate(Sheet(("perceptionExpert", Items("perception"))), Warden());
        Assert.Equal(10, Perception(expert)); // 5 + 4, + Wis 1
        Assert.Equal(2, expert.Ranked["perception"].Single().Rank);
    }

    [Fact]
    public void AClassGrantAndTheKeyAttributeOfTheClassMakeTheClassDc()
    {
        var evaluation = Evaluate(Sheet(), Warden(Row("Watchful", 1, Grant("perception", "trained"), Grant("classDc", "trained"))));
        Assert.Equal(8, Perception(evaluation)); // 7 + 1
        Assert.Equal(21, Convert.ToInt64(evaluation.Derived["classDc"])); // 10 + 7 + 4
        Assert.Equal(7, evaluation.DerivedInputs["classDc"]["rank"]);
    }

    // ---- 2: the ranks a list takes ----

    [Fact]
    public void RanksOfSkills_GetTheirOwnSheetKeys_AndNeverTheSkillsOwn()
    {
        var rules = System.SheetRules!;
        var perception = rules.Ranked.Single(l => l.Key == "perception");
        Assert.Equal(new[] { "perceptionTrained", "perceptionExpert", "perceptionMaster" }, rules.RanksFor(perception).Select(r => r.SheetKey));
        Assert.Equal(new[] { "skillTrained", "skillExpert", "skillMaster" }, rules.Skills!.SkillRanks().Select(r => r.SheetKey)); // untouched
        Assert.Equal(new[] { "wardcraftNovice", "wardcraftAdept" }, rules.RanksFor(rules.Ranked.Single(l => l.Key == "wardcraft")).Select(r => r.SheetKey));
    }

    [Fact]
    public void AnExplicitSheetKeyWins_AndASharedYamlAliasGivesEachListItsOwnKeys()
    {
        var system = SystemDefinitionLoader.Parse(Yaml(ranked: """
                ranked:
                  - { key: first, label: First, ranks: &steps [{ key: low }, { key: high }], list: [{ key: a }] }
                  - { key: third, label: Third, ranks: [{ key: low, sheetKey: thirdOwnKey }], list: [{ key: c }] }
                  - { key: second, label: Second, ranks: *steps, list: [{ key: b }] }
                  - { key: armor, label: Armor, ranksOf: skills, list: [{ key: unarmored }] }
                  - { key: weapons, label: Weapons, ranksOf: skills, list: [{ key: simple }] }
              """));
        var rules = system.SheetRules!;
        Assert.Equal(new[] { "firstLow", "firstHigh" }, rules.RanksFor(rules.Ranked[0]).Select(r => r.SheetKey));
        Assert.Equal(new[] { "secondLow", "secondHigh" }, rules.RanksFor(rules.Ranked[2]).Select(r => r.SheetKey));
        Assert.Equal("thirdOwnKey", rules.RanksFor(rules.Ranked[1]).Single().SheetKey);
        Assert.Equal(string.Empty, rules.Ranked[0].Ranks[0].SheetKey); // the source was not changed by the copy
    }

    // ---- 3: grants ----

    [Fact]
    public void AFixedGrantByEntries_ByRankSheetKey_AndARaise()
    {
        var warden = Warden(
            Row("Training", 1, Grant("armor", "trained", entries: new object[] { "light" })),
            Row("Eye", 1, Grant("perceptionExpert")),
            Row("Lift", 1, Grant("wardcraft", raise: 1, entries: new object[] { "road" })),
            Row("Lift again", 1, Grant("wardcraft", raise: 5, entries: new object[] { "road" })));
        var evaluation = Evaluate(Sheet(), warden);
        Assert.Equal(1, evaluation.Ranked["armor"].Single(s => s.Key == "light").Rank);
        Assert.Equal(0, evaluation.Ranked["armor"].Single(s => s.Key == "heavy").Rank);
        Assert.Equal(2, evaluation.Ranked["perception"].Single().Rank); // a one-key list needs no entries
        Assert.Equal(2, evaluation.Ranked["wardcraft"].Single(s => s.Key == "road").Rank); // trained to the top and no further
        Assert.Equal(8 + 0, evaluation.Ranked["wardcraft"].Single(s => s.Key == "road").RankBonus); // level + 3
    }

    [Fact]
    public void ARankAndARaiseTogetherWarn()
    {
        var evaluation = Evaluate(Sheet(), Warden(Row("Muddled", 1, Grant("perception", "trained", raise: 1))));
        Assert.Single(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
        Assert.Equal(0, evaluation.Ranked["perception"].Single().Rank);
    }

    // ---- 4: choices ----

    [Fact]
    public void AChoiceWithAMinRank_IsPendingThenMade_AndAStalePickIsAViolation()
    {
        var warden = Warden(Row("Training", 1, Grant("armor", "trained", entries: new object[] { "light", "medium" })));
        var drill = Entry("feat", "drill", ("adds", Items(Grant("armor", "expert", more: new[] { ("choice", (object)"armor-pick"), ("choose", 1L), ("minRank", "trained") }))));
        var sheet = Sheet(("feats", Items("drill")));

        var pending = Evaluate(sheet, warden, drill);
        var choice = Assert.Single(pending.PendingChoices);
        Assert.Equal("armor", choice.Target);
        Assert.Equal(new[] { "light", "medium" }, choice.Options.Select(o => o.Key));
        Assert.Contains(choice.Key, pending.RankChoiceKeys);

        SheetRuleEngine.SetChoice(sheet, choice.Key, new[] { "medium" });
        var made = Evaluate(sheet, warden, drill);
        Assert.Empty(made.PendingChoices);
        Assert.Equal(2, made.Ranked["armor"].Single(s => s.Key == "medium").Rank);

        SheetRuleEngine.SetChoice(sheet, choice.Key, new[] { "heavy" });
        var stale = Evaluate(sheet, warden, drill);
        Assert.Contains(stale.Violations, v => v.FieldKey == choice.Key && v.Key == SheetRuleEngine.ChoiceStaleKey);
    }

    // ---- 5: modifiers on a published value ----

    [Fact]
    public void AModifierOnAPublishedValueCountsOnce_AndTheRollTargetsAreNotRead()
    {
        var warden = Warden(Row("Eye", 1, Grant("perception", "trained")));
        var bonus = Entry("feat", "keen", ("modifiers", Items(Map(("target", "perception"), ("amount", "1")))));
        var other = Entry("feat", "keener", ("modifiers", Items(Map(("target", "perception"), ("amount", "1")))));
        var sheet = Sheet(("feats", Items("keen", "keener")));
        Assert.Equal(10, Perception(Evaluate(sheet, warden, bonus, other))); // 8, and an untyped +1 twice is +2, not +4

        var check = Entry("feat", "dread", ("modifiers", Items(Map(("target", "check"), ("amount", "-2")))));
        Assert.Equal(8, Perception(Evaluate(Sheet(("feats", Items("dread"))), warden, check)));
    }

    // ---- 6: armor class ----

    [Fact]
    public void ArmorClassAddsTheRankOfWhatIsWorn_OrOfTheUnarmoredKey()
    {
        var warden = Warden(Row("Training", 1, Grant("armor", "trained", entries: new object[] { "unarmored", "medium" })));
        var coat = Entry("armor", "coat", ("armorBase", 13L), ("armorDexCap", 2L), ("armorCategory", "medium"));
        var plate = Entry("armor", "plate", ("armorBase", 18L), ("armorDexCap", 0L), ("armorCategory", "heavy"));

        var worn = Evaluate(Sheet(("inventory", Items("coat|equipped:1"))), warden, coat, plate);
        Assert.Equal(22, worn.ArmorClass!.Total); // 13 + Dex 2 + 7
        Assert.Contains(worn.ArmorClass.Parts, l => l.Kind == "rank" && l.Value == 7);

        Assert.Equal(19, Evaluate(Sheet(), warden, coat, plate).ArmorClass!.Total); // 10 + Dex 2 + 7

        var heavy = Evaluate(Sheet(("inventory", Items("plate|equipped:1"))), warden, coat, plate);
        Assert.Equal(18, heavy.ArmorClass!.Total); // untrained: no rank line
        Assert.DoesNotContain(heavy.ArmorClass.Parts, l => l.Kind == "rank");
    }

    // ---- 7: actions ----

    [Fact]
    public void AnActionReadsTheRankOfItsEntrysKey()
    {
        var warden = Warden(Row("Drill", 1, Grant("weapons", "trained", entries: new object[] { "martial" })));
        var blade = Entry("weapon", "blade", ("category", "martial"), ("use", Map(("attack", "melee"), ("abilities", Items("str")), ("damage", "1d8"))));
        var stick = Entry("weapon", "stick", ("category", "simple"), ("use", Map(("attack", "melee"), ("abilities", Items("str")), ("damage", "1d4"))));
        var evaluation = Evaluate(Sheet(("inventory", Items("blade|equipped:1", "stick|equipped:1"))), warden, blade, stick);
        Assert.Equal(11, evaluation.Actions.Single(a => a.Entry.Id == "blade").AttackBonus); // 7 + Str 4
        Assert.Equal(4, evaluation.Actions.Single(a => a.Entry.Id == "stick").AttackBonus); // untrained: Str alone
    }

    // ---- 9: a system with ranked lists and no skills ----

    [Fact]
    public void ASystemWithRankedListsAndNoSkillsWorks_AndAnUnsupportedAddWarnsOnce()
    {
        var system = SystemDefinitionLoader.Parse(Header + """

            sheetRules:
              ranked:
                - { key: perception, label: Perception, attribute: wis, publish: true, ranks: [{ key: trained, value: "level + 2" }] }
              features:
                - { field: class, property: features, byField: level }
            """);
        var warden = Warden(Row("Eye", 1, Grant("perception", "trained")),
            Row("Lost", 1, Grant("nowhere", null, entries: null, more: new[] { ("choice", (object)"pick"), ("choose", 1L), ("from", Items("a")) })));
        var evaluation = new SheetRuleEngine(system, new Entries(warden)).Recompute(Sheet());
        Assert.Equal(8, Convert.ToInt64(evaluation.Derived["perception"]));
        Assert.Single(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
    }

    // ---- 10: the loader ----

    [Theory]
    [InlineData("ranked: [{ label: No key, ranksOf: skills }]", "sheetRules.ranked needs a key on every list.")]
    [InlineData("ranked: [{ key: a, ranksOf: skills }, { key: a, ranksOf: skills }]", "sheetRules.ranked key 'a' is declared more than once.")]
    [InlineData("ranked: [{ key: skills, ranksOf: saves }]", "sheetRules.ranked key 'skills' is reserved (skills, saves).")]
    [InlineData("ranked: [{ key: level, ranksOf: skills }]", "sheetRules.ranked 'level' collides with an actor field, a derived value, a sheetRules.lists key, or the armor class or hit point key.")]
    [InlineData("ranked: [{ key: feats, ranksOf: skills }]", "sheetRules.ranked 'feats' collides")]
    [InlineData("ranked: [{ key: a }]", "sheetRules.ranked 'a' needs ranks or ranksOf, not both.")]
    [InlineData("ranked: [{ key: a, ranksOf: skills, ranks: [{ key: x }] }]", "sheetRules.ranked 'a' needs ranks or ranksOf, not both.")]
    [InlineData("ranked: [{ key: a, ranksOf: nothing }]", "sheetRules.ranked 'a' ranksOf 'nothing' names no list with ranks of its own.")]
    [InlineData("ranked: [{ key: a, ranks: [{ key: x }, { key: x }] }]", "sheetRules.ranked.a.ranks key 'x' is declared more than once.")]
    [InlineData("ranked: [{ key: a, ranks: [{ key: '3' }] }]", "sheetRules.ranked.a.ranks key '3' cannot be a number")]
    [InlineData("ranked: [{ key: a, ranks: [{ key: x, value: 'level +' }] }]", "sheetRules.ranked.a.ranks 'x' value 'level +' cannot be read")]
    [InlineData("ranked: [{ key: a, ranks: [{ key: x, sheetKey: skillTrained }] }]", "sheetRules.ranked.a.ranks sheetKey 'skillTrained' is used by more than one rank.")]
    [InlineData("ranked: [{ key: a, ranksOf: skills, list: [{ key: k }, { key: k }] }]", "sheetRules.ranked 'a' key 'k' is declared more than once.")]
    [InlineData("ranked: [{ key: a, ranksOf: skills, attribute: luck }]", "sheetRules.ranked 'a' uses attribute 'luck', which is not one of the system's attributes.")]
    [InlineData("ranked: [{ key: a, ranksOf: skills, attributeFrom: { field: level, property: p } }]", "sheetRules.ranked 'a' attributeFrom.field 'level' is not an EntityReference actor field.")]
    public void TheLoaderRefusesAMistakeInTheRankedLists(string ranked, string message)
    {
        var errors = Errors(Header + "\nsheetRules:\n  lists: [{ key: feats, label: Feats, targetEntityType: feat }]\n" + Skills + "\n  " + ranked + "\n");
        Assert.Contains(message, errors);
    }

    [Fact]
    public void TheLoaderRefusesAnArmorClassOrActionRankThatNamesNoRankedList()
    {
        var armor = Errors(Header + "\nsheetRules:\n" + Skills + "\n  ranked: [{ key: a, ranksOf: skills, list: [{ key: k }] }]\n  armorClass: { unarmored: '10', dexAttribute: dex, rank: { list: nope, property: p } }\n");
        Assert.Contains("sheetRules.armorClass.rank.list 'nope' is not a sheetRules.ranked list.", armor);
        var unarmored = Errors(Header + "\nsheetRules:\n" + Skills + "\n  ranked: [{ key: a, ranksOf: skills, list: [{ key: k }] }]\n  armorClass: { unarmored: '10', dexAttribute: dex, rank: { list: a, property: p, unarmored: z } }\n");
        Assert.Contains("sheetRules.armorClass.rank.unarmored 'z' is not a key of a.", unarmored);
        var action = Errors(Header + "\nsheetRules:\n" + Skills + "\n  ranked: [{ key: a, ranksOf: skills }]\n  actions: [{ key: hit, label: Hit, inventory: equipped, rank: { list: nope, property: p } }]\n");
        Assert.Contains("sheetRules.actions 'hit' rank.list 'nope' is not a sheetRules.ranked list.", action);
    }

    [Fact]
    public void TheGrantCheckListsTheRanksOfAGeneralList()
    {
        var vocabulary = FeatureGrantVocabulary.For(System)!;
        var armor = vocabulary.Lists.Single(l => l.Key == "armor");
        Assert.Equal(new[] { "trained", "expert", "master" }, armor.Ranks!.Select(r => r.Key));
        Assert.Contains(vocabulary.Targets, t => t.Key == "perception");
        Assert.Contains("rankValue", vocabulary.FormulaNames);

        var entry = Entry("feat", "grand", ("adds", Items(Grant("armor", "grandmaster", entries: new object[] { "light" }))));
        var problem = Assert.Single(FeatureGrantCheck.Problems(System, entry));
        Assert.Contains("trained", problem);
    }

    // ---- 11: P3c, hit points from a number of the class ----

    private const string FlatRules = """
          hitPoints: { key: maxHp, currentKey: hp, flatProperty: hp, attribute: wis, once: { field: ancestry, property: hp } }
          features: []
        """;

    private static readonly SystemDefinition FlatSystem = SystemDefinitionLoader.Parse(Header.Replace("  - { key: armorClass,", "  - { key: ancestry, label: Ancestry, type: EntityReference, targetEntityType: ancestry }\n  - { key: armorClass,")
        .Replace("  - { type: feat, label: Feat }", "  - { type: feat, label: Feat }\n  - { type: ancestry, label: Ancestry }") + "\nsheetRules:\n" + FlatRules);

    private static SheetEvaluation Flat(Dictionary<string, object> sheet, int perLevel = 6)
    {
        var warden = Entry("class", "warden", ("hp", (long)perLevel));
        var folk = Entry("ancestry", "hillfolk", ("hp", 4L));
        return new SheetRuleEngine(FlatSystem, new Entries(warden, folk)).Recompute(sheet);
    }

    [Fact]
    public void FlatHitPoints_AreTheClassNumberPlusTheAttributeEveryLevel_AndTheAncestrysOnce()
    {
        var sheet = Sheet(("ancestry", "hillfolk"));
        var five = Flat(sheet);
        Assert.True(five.HitPoints!.Active); // a sheet with no mode follows the rules
        Assert.Equal(4 + (6 + 1) * 5, five.HitPoints.Max); // once + (class + Wis 1) x 5
        Assert.Equal(4, five.HitPoints.Once);
        Assert.Equal(5, five.HitPoints.Levels.Count);
        Assert.All(five.HitPoints.Levels, l => Assert.Equal(0, l.Die));

        sheet["level"] = 1L;
        Assert.Equal(4 + 7, Flat(sheet).HitPoints!.Max);
    }

    [Fact]
    public void FlatHitPoints_CanStillBeManual_AndAClassWithoutTheNumberWarns()
    {
        var sheet = Sheet(("ancestry", "hillfolk"), ("hpMode", "manual"));
        Assert.False(Flat(sheet).HitPoints!.Active);

        var bare = new SheetRuleEngine(FlatSystem, new Entries(Entry("class", "warden"))).Recompute(Sheet());
        Assert.Contains(bare.Warnings, w => w.Message.Contains("has no hit points per level"));
    }

    [Fact]
    public void ApplyingFlatHitPoints_KeepsCurrentHitPointsInStepWithTheMaximum()
    {
        var sheet = Sheet(("ancestry", "hillfolk"));
        var engine = new SheetRuleEngine(FlatSystem, new Entries(Entry("class", "warden", ("hp", 6L)), Entry("ancestry", "hillfolk", ("hp", 4L))));
        engine.ApplyHitPoints(sheet, engine.Recompute(sheet));
        Assert.Equal(39L, sheet["maxHp"]);
        Assert.Equal(39L, sheet["hp"]); // a fresh sheet starts at full health

        sheet["hp"] = 30L;
        sheet["level"] = 6L;
        engine.ApplyHitPoints(sheet, engine.Recompute(sheet));
        Assert.Equal(46L, sheet["maxHp"]);
        Assert.Equal(37L, sheet["hp"]); // the seven the level gave
    }

    [Fact]
    public void TheLoaderRefusesFlatHitPointsMixedWithDice_AndAnOnceFieldThatIsNoReference()
    {
        var yaml = Header + "\nsheetRules:\n  hitPoints: { key: maxHp, currentKey: hp, flatProperty: hp, attribute: wis, firstLevel: average, once: { field: level, property: hp } }\n";
        var errors = Errors(yaml);
        Assert.Contains("sheetRules.hitPoints: flatProperty and firstLevel/average cannot both be given.", errors);
        Assert.Contains("sheetRules.hitPoints.once.field 'level' is not an EntityReference actor field.", errors);
        Assert.DoesNotContain("flatProperty and firstLevel/average", Errors(yaml.Replace("firstLevel: average, ", "")));
    }
}
