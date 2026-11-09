using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The primitives' evaluator (<see cref="SheetModel"/>, overhaul step 2) on small systems of its own. The reference
/// fixtures (tests/Codex.Tests/Fixtures/reference) are the spec; these pin what the spec leaves implicit.</summary>
public class SheetModelTests
{
    private sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static SystemDefinition System(string kind) => SystemDefinitionLoader.Parse($$"""
        id: model-test
        name: Model test
        attributes:
          - { key: str, label: Strength, default: 10, modifier: half-minus-five }
          - { key: dex, label: Dexterity, default: 10, modifier: half-minus-five }
        entityTypes:
          - { type: item, label: Item }
        modifierTypes:
          default: highest
          penalties: sum
          types: { dodge: sum, luck: lowest }
        progressions:
          fast: "level * 2"
        functions:
          twice: { formula: "(x) => x * 2" }
        conditionEffects:
          Hasted:
            modifiers: [{ target: Speed, amount: 10, type: enhancement }]
        sheetKinds:
          - kind: hero
            label: Hero
        {{kind}}
        """);

    private static SheetModelResult Evaluate(string kind, Dictionary<string, object> sheet, params ContentEntry[] entries) =>
        new SheetModel(System(kind), new Entries(entries), sheet).Evaluate();

    private static object? ValueOf(SheetModelResult result, string key) => result.Values.GetValueOrDefault(key);

    [Fact]
    public void ValuesMayBeListedInAnyOrder_EachIsWorkedOutWhenFirstRead()
    {
        var result = Evaluate("""
                values:
                  - { key: c, formula: "b + 1" }
                  - { key: b, formula: "a * 2" }
                  - { key: a, formula: "str" }
            """, new Dictionary<string, object> { ["str"] = 7L });

        Assert.Equal(15d, ValueOf(result, "c"));
        Assert.Empty(result.Warnings);
    }

    /// <summary>A value that reads itself, directly or round a loop, is the GM's formula problem: a warning, no value, and the
    /// values that do not depend on the loop still come out.</summary>
    [Fact]
    public void AValueThatDependsOnItself_IsAWarning_NotAStackOverflow()
    {
        var result = Evaluate("""
                values:
                  - { key: a, formula: "b + 1" }
                  - { key: b, formula: "a + 1" }
                  - { key: fine, formula: "3" }
            """, new Dictionary<string, object>());

        Assert.Null(ValueOf(result, "a"));
        Assert.Null(ValueOf(result, "b"));
        Assert.Equal(3d, ValueOf(result, "fine"));
        Assert.Contains(result.Warnings, w => w.Key == "Actor.Rule.FormulaError");
    }

    /// <summary>Single quotes are strings (not C# characters), and whole numbers are doubles: <c>7 / 2</c> is 3.5, rounding is
    /// explicit (<c>floor</c>, <c>div</c>), and <c>cond ? x : 0</c> does not fail on a mix of a double and an integer.</summary>
    [Fact]
    public void QuotesAreStrings_AndNumbersAreDoubles_SoDivisionNeverTruncatesQuietly()
    {
        var result = Evaluate("""
                values:
                  - { key: half, formula: "7 / 2" }
                  - { key: whole, formula: "div(7, 2)" }
                  - { key: down, formula: "floor(-7 / 2)" }
                  - { key: pick, formula: "mode == 'fast' ? str : 0" }
                  - { key: mode, input: true, options: [fast, slow], default: slow }
            """, new Dictionary<string, object> { ["mode"] = "fast", ["str"] = 12L });

        Assert.Equal(3.5, ValueOf(result, "half"));
        Assert.Equal(3d, ValueOf(result, "whole"));
        Assert.Equal(-4d, ValueOf(result, "down"));
        Assert.Equal(12d, ValueOf(result, "pick"));
        Assert.Equal("fast", ValueOf(result, "mode"));
    }

    /// <summary>A pick's fields, its entry's properties and the field defaults all read as members; one an item does not have
    /// is skipped by the aggregates, so a ring without <c>maxDex</c> sets no cap.</summary>
    [Fact]
    public void AnItemWithoutAProperty_IsSkippedByTheAggregates()
    {
        var result = Evaluate("""
                lists:
                  - key: gear
                    label: Gear
                    type: item
                    itemFields: [{ key: worn, type: Boolean, default: false }]
                values:
                  - { key: cap,    formula: "min(where(gear, g => g.worn), g => g.maxDex, 99)" }
                  - { key: weight, formula: "sum(gear, g => g.weight)" }
                  - { key: worn,   formula: "count(gear, g => g.worn)" }
                  - { key: names,  formula: "any(gear, g => g.name == 'Ring')" }
            """,
            new Dictionary<string, object>
            {
                ["gear"] = new List<object>
                {
                    new Dictionary<string, object> { ["id"] = "mail", ["worn"] = true },
                    new Dictionary<string, object> { ["id"] = "ring", ["worn"] = true },
                    new Dictionary<string, object> { ["id"] = "pack" }
                }
            },
            new ContentEntry { Id = "mail", Type = "item", Name = "Mail", Properties = { ["maxDex"] = 3L, ["weight"] = 40L } },
            new ContentEntry { Id = "ring", Type = "item", Name = "Ring" },
            new ContentEntry { Id = "pack", Type = "item", Name = "Pack", Properties = { ["weight"] = 5L } });

        Assert.Equal(3d, ValueOf(result, "cap"));
        Assert.Equal(45d, ValueOf(result, "weight"));
        Assert.Equal(2d, ValueOf(result, "worn"));
        Assert.Equal(true, ValueOf(result, "names"));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void APickTheLibraryDoesNotHave_IsAProblem_AndCountsAsAnItemWithNoProperties()
    {
        var result = Evaluate("""
                lists:
                  - { key: gear, label: Gear, type: item }
                values:
                  - { key: n, formula: "count(gear, g => g.id != null)" }
            """, new Dictionary<string, object> { ["gear"] = new List<object> { "gone" } });

        Assert.Equal(1d, ValueOf(result, "n"));
        Assert.Contains(result.Violations, v => v.FieldKey == "gear" && v.Key == "Actor.Rule.NotInLibrary");
    }

    /// <summary>Highest of a type, lowest of a type, the sum of a stacking type, penalties all together; each reaching modifier
    /// is in the breakdown, marked whether it counted.</summary>
    [Fact]
    public void ModifiersStackByType_PenaltiesAddUp_AndTheBreakdownShowsWhatDidNotCount()
    {
        var result = Evaluate("""
                values:
                  - { key: AC, formula: "10", modifiers: AC }
                modifiers:
                  - { target: AC, amount: 2, type: deflection, source: Ring A }
                  - { target: AC, amount: 3, type: deflection, source: Ring B }
                  - { target: AC, amount: 1, type: dodge, source: Dodge }
                  - { target: AC, amount: 1, type: dodge, source: Mobility }
                  - { target: AC, amount: 4, type: luck, source: Charm }
                  - { target: AC, amount: 1, type: luck, source: Stone }
                  - { target: AC, amount: -1, type: size, source: Large }
                  - { target: AC, amount: -2, source: Slowed }
                  - { target: AC, amount: 5, source: Off, when: "str > 20" }
            """, new Dictionary<string, object>());

        // 10 + deflection 3 + dodge 2 + luck (lowest) 1 - 1 - 2.
        Assert.Equal(13d, ValueOf(result, "AC"));
        var breakdown = result.Modifiers["AC"];
        Assert.False(breakdown.Single(m => m.Source == "Ring A").Counted);
        Assert.True(breakdown.Single(m => m.Source == "Ring B").Counted);
        Assert.False(breakdown.Single(m => m.Source == "Charm").Counted);
        Assert.DoesNotContain(breakdown, m => m.Source == "Off");
        Assert.Equal(8, breakdown.Count);
    }

    /// <summary><c>without</c> leaves a type out; <c>{ type, positiveOnly }</c> leaves out only its bonuses, so a flat-footed
    /// character with a low Dexterity keeps the penalty.</summary>
    [Fact]
    public void Without_LeavesTypesOut_AndPositiveOnlyKeepsThePenalty()
    {
        const string kind = """
                values:
                  - { key: AC,    formula: "10", modifiers: AC }
                  - { key: AC_FF, formula: "10", modifiers: AC, without: [dodge, { type: dex, positiveOnly: true }] }
                modifiers:
                  - { target: AC, amount: "mod(dex)", type: dex, source: Dexterity }
                  - { target: AC, amount: 1, type: dodge, source: Dodge }
            """;

        var nimble = Evaluate(kind, new Dictionary<string, object> { ["dex"] = 16L });
        Assert.Equal(14d, ValueOf(nimble, "AC"));
        Assert.Equal(10d, ValueOf(nimble, "AC_FF"));

        var clumsy = Evaluate(kind, new Dictionary<string, object> { ["dex"] = 6L });
        Assert.Equal(9d, ValueOf(clumsy, "AC"));
        Assert.Equal(8d, ValueOf(clumsy, "AC_FF"));
    }

    [Fact]
    public void AConditionOnTheSheet_BringsItsModifiers()
    {
        const string kind = """
                values:
                  - { key: Speed, formula: "30", modifiers: Speed }
            """;
        Assert.Equal(30d, ValueOf(Evaluate(kind, new Dictionary<string, object>()), "Speed"));
        Assert.Equal(40d, ValueOf(Evaluate(kind, new Dictionary<string, object> { ["conditions"] = new List<object> { "hasted" } }), "Speed"));
    }

    [Fact]
    public void ProgressionsAndNamedFunctions_AreCallableFromAnyFormula()
    {
        var result = Evaluate("""
                values:
                  - { key: p, formula: "progression('fast', 3)" }
                  - { key: f, formula: "twice(str) + 1" }
                  - { key: bad, formula: "progression('nope', 3)" }
            """, new Dictionary<string, object> { ["str"] = 5L });

        Assert.Equal(6d, ValueOf(result, "p"));
        Assert.Equal(11d, ValueOf(result, "f"));
        Assert.Null(ValueOf(result, "bad"));
        Assert.Single(result.Warnings);
    }

    /// <summary>A keyed list: every declared key is an entry even when the sheet has nothing for it (its fields at their
    /// defaults); <c>derive</c> works per entry, <c>value</c> takes the modifiers on the entry and its groups, and a rule that is
    /// a lambda checks every entry.</summary>
    [Fact]
    public void AKeyedList_DerivesPerEntry_TakesGroupModifiers_AndChecksEachEntry()
    {
        var result = Evaluate("""
                keyedLists:
                  - key: skills
                    label: Skills
                    itemFields: [{ key: points, type: Number }]
                    keys:
                      - { key: climb, label: Climb, attribute: str, groups: [heavy] }
                      - { key: sneak, label: Sneak, attribute: dex, groups: [heavy] }
                      - { key: lore,  label: Lore,  attribute: dex, parameter: subject }
                    derive:
                      value: "s => s.points + mod(s.attribute)"
                    rules:
                      - { check: "s => s.points <= 4", key: Too.Many, message: "{label}: at most 4 ({points})." }
                modifiers:
                  - { target: '@heavy', amount: -1, source: Armor }
                  - { target: skills.lore, amount: 1, source: Library }
            """,
            new Dictionary<string, object>
            {
                ["str"] = 14L,
                ["skills"] = new Dictionary<string, object>
                {
                    ["climb"] = new Dictionary<string, object> { ["points"] = 5L },
                    ["lore.history"] = new Dictionary<string, object> { ["points"] = 2L }
                }
            });

        Assert.Equal(6d, ValueOf(result, "skills.climb"));
        Assert.Equal(-1d, ValueOf(result, "skills.sneak"));
        Assert.Equal(3d, ValueOf(result, "skills.lore.history"));
        var problem = Assert.Single(result.Violations);
        Assert.Equal(("skills", "Too.Many", "Climb: at most 4 (5)."), (problem.FieldKey, problem.Key, problem.Message));
    }

    /// <summary>R7: formulas come from content anyone can import. The interpreter knows no types, so nothing reaches .NET.</summary>
    [Theory]
    [InlineData("new string('a', 1000000000)")]
    [InlineData("str.GetType().Assembly.FullName.Length")]
    [InlineData("Math.Floor(1)")]
    [InlineData("System.IO.File.Exists('x') ? 1 : 0")]
    [InlineData("typeof(string)")]
    public void AFormula_CannotReachDotNet(string formula)
    {
        var result = Evaluate($$"""
                values:
                  - { key: x, formula: "{{formula.Replace("\"", "\\\"")}}" }
            """, new Dictionary<string, object> { ["str"] = 10L });

        Assert.Null(ValueOf(result, "x"));
        Assert.Single(result.Warnings);
    }

    /// <summary>R7, the review's finding: DynamicExpresso lets a formula call public instance methods on what it holds, and
    /// each <c>Replace('a', 'aa')</c> doubles a string, so forty of them ask for a terabyte. Every parsed formula is checked
    /// (<see cref="FormulaSandbox"/>): no method calls but the built-ins, no member reads on .NET types, no dynamic calls.</summary>
    [Theory]
    [InlineData("'a'.Replace('a', 'aa').Replace('a', 'aa').Length")]
    [InlineData("str.ToString().Replace('1', '11').Length")]
    [InlineData("'abc'.Length")]
    [InlineData("first(gear).name.Replace('a', 'aa') == 'x' ? 1 : 0")]
    [InlineData("first(gear).name.PadLeft(1000000000) == 'x' ? 1 : 0")]
    [InlineData("str.CompareTo(1)")]
    public void AFormula_CannotCallMethodsOnWhatItHolds(string formula)
    {
        var result = Evaluate($$"""
                lists:
                  - { key: gear, label: Gear }
                values:
                  - { key: x, formula: "{{formula}}" }
            """, new Dictionary<string, object> { ["str"] = 10L, ["gear"] = new List<object> { new Dictionary<string, object> { ["id"] = "a", ["name"] = "aaaa" } } });

        Assert.Null(ValueOf(result, "x"));
        Assert.Single(result.Warnings);
    }

    /// <summary>The parser is recursive and a stack overflow kills the process, so a formula nested too deep, or too long, is
    /// refused before it is parsed: a warning, like any formula problem.</summary>
    [Fact]
    public void AFormulaNestedTooDeep_OrTooLong_IsAWarning_NotACrash()
    {
        var deep = new string('(', 5000) + "1" + new string(')', 5000);
        var long_ = string.Join(" + ", Enumerable.Repeat("1", 1500));
        var result = Evaluate($$"""
                values:
                  - { key: deep, formula: "{{deep}}" }
                  - { key: long, formula: "{{long_}}" }
                  - { key: fine, formula: "{{new string('(', 20) + "2" + new string(')', 20)}}" }
            """, new Dictionary<string, object>());

        Assert.Null(ValueOf(result, "deep"));
        Assert.Null(ValueOf(result, "long"));
        Assert.Equal(2d, ValueOf(result, "fine"));
        Assert.Equal(2, result.Warnings.Count);
    }

    [Theory]
    [InlineData("'it\\'s'", "it's")]
    [InlineData("\"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("'a \"quote\"'", "a \"quote\"")]
    [InlineData("\"it's\"", "it's")]
    public void QuotesInsideStrings_CanBeEscaped(string literal, string expected)
    {
        Assert.Equal(expected, Assert.IsType<string>(SheetFormulas.For(System("")).Run(literal, Array.Empty<(string, object)>())));
    }

    /// <summary>A malformed pick list (a number among the picks) must not make the prerequisite check remove the wrong pick:
    /// the pick judged is taken out by its place in the raw list.</summary>
    [Fact]
    public void Prerequisites_AreJudgedWithoutThePickItself_EvenAfterAMalformedPick()
    {
        var result = Evaluate("""
                lists:
                  - { key: gear, label: Gear, type: item, prerequisites: needs }
                values:
                  - { key: n, formula: "count(gear, g => g.id == 'base')" }
            """,
            new Dictionary<string, object> { ["gear"] = new List<object> { 7L, "base", "advanced" } },
            new ContentEntry { Id = "base", Type = "item", Name = "Base" },
            new ContentEntry
            {
                Id = "advanced", Type = "item", Name = "Advanced",
                Properties = { ["needs"] = new List<object> { new Dictionary<string, object> { ["list"] = "gear", ["contains"] = "base", ["message"] = "needs Base." } } }
            });

        Assert.DoesNotContain(result.Violations, v => v.Key == "Actor.Rule.Prerequisite");
    }

    // ---- Tracks (step 3) ----

    /// <summary>Thresholds add up, several at once; of the bands, the first that matches holds and no other. A track's maximum
    /// reads as <c>{key}_Max</c>, a threshold may be a formula, and a condition a threshold brings brings its modifiers too.</summary>
    [Fact]
    public void Thresholds_AddUp_ButOnlyOneBandHolds_AndTheirConditionsBringModifiers()
    {
        const string kind = """
                values:
                  - { key: Speed, formula: "30", modifiers: Speed }
                tracks:
                  - key: HP
                    max: "str * 2"
                    min: -10
                    thresholds:
                      - { atOrBelow: "div(HP_Max, 2)", condition: Hasted }
                      - { below: 0, condition: Dying }
                    bands:
                      - { atOrAbove: 1 }
                      - { equals: 0, condition: Disabled }
                      - { condition: Down }
            """;
        Dictionary<string, object> At(long hp) => new() { ["str"] = 11L, ["HP"] = hp };

        var healthy = Evaluate(kind, At(20));
        Assert.Equal(22d, ValueOf(healthy, "HP_Max"));
        Assert.Empty(healthy.Conditions);
        Assert.Equal(30d, ValueOf(healthy, "Speed"));

        var hurt = Evaluate(kind, At(11));
        Assert.Equal(new[] { "Hasted" }, hurt.Conditions);
        Assert.Equal(40d, ValueOf(hurt, "Speed"));

        Assert.Equal(new[] { "Hasted", "Disabled" }, Evaluate(kind, At(0)).Conditions);
        Assert.Equal(new[] { "Hasted", "Dying", "Down" }, Evaluate(kind, At(-3)).Conditions);
    }

    /// <summary>A threshold that reads a value whose modifiers come from the condition that threshold brings goes round: the
    /// GM's formula problem, a warning, not a hang or a stack overflow.</summary>
    [Fact]
    public void AThresholdThatDependsOnItsOwnCondition_IsAWarning()
    {
        var result = Evaluate("""
                values:
                  - { key: Speed, formula: "30", modifiers: Speed }
                tracks:
                  - key: HP
                    thresholds: [{ atOrBelow: Speed, condition: Hasted }]
            """, new Dictionary<string, object> { ["HP"] = 5L });

        Assert.NotEmpty(result.Warnings);
    }

    /// <summary><c>each</c> makes a track per item and level, its key and label filled in and its formulas reading
    /// <c>item</c> and <c>level</c>; a slot track holds entries that pass its <c>where</c>, the same one more than once, but
    /// no more than its maximum.</summary>
    [Fact]
    public void EachMakesATrackPerItemAndLevel_AndSlotsHoldOnlyWhatFits()
    {
        const string kind = """
                lists:
                  - key: gear
                    label: Gear
                    type: item
                    itemFields: [{ key: rank, type: Number, default: 1 }]
                tracks:
                  - each: "where(gear, g => g.charged)"
                    levels: "1..2"
                    key: "charges_{item.id}_{level}"
                    label: "{item.name}, tier {level}"
                    display: slots
                    max: "item.rank + level"
                    holds: { type: item, where: "s => s.tier == level", message: "not a tier {level} charge." }
            """;
        var entries = new[]
        {
            new ContentEntry { Id = "wand", Type = "item", Name = "Wand", Properties = { ["charged"] = true } },
            new ContentEntry { Id = "rock", Type = "item", Name = "Rock" },
            new ContentEntry { Id = "spark", Type = "item", Name = "Spark", Properties = { ["tier"] = 1L } },
            new ContentEntry { Id = "blast", Type = "item", Name = "Blast", Properties = { ["tier"] = 2L } }
        };
        var gear = new List<object> { new Dictionary<string, object> { ["id"] = "wand", ["rank"] = 1L }, "rock" };

        var fine = Evaluate(kind, new Dictionary<string, object> { ["gear"] = gear, ["charges_wand_1"] = new List<object> { "spark", "spark" } }, entries);
        Assert.Equal(2d, ValueOf(fine, "charges_wand_1_Max"));
        Assert.Equal(3d, ValueOf(fine, "charges_wand_2_Max"));
        Assert.False(fine.Values.ContainsKey("charges_rock_1_Max"));
        Assert.Empty(fine.Violations);

        var wrong = Evaluate(kind, new Dictionary<string, object> { ["gear"] = gear, ["charges_wand_1"] = new List<object> { "spark", "blast", "spark" } }, entries);
        Assert.Equal(new[] { ("charges_wand_1", "Actor.Rule.SlotHolds"), ("charges_wand_1", "Actor.Rule.TrackFull") },
            wrong.Violations.Select(v => (v.FieldKey, v.Key)).ToArray());
        Assert.Equal("Wand, tier 1: Blast is not a tier 1 charge.", wrong.Violations[0].Message);
    }

    /// <summary>A numeric track stays within its minimum (which may be negative) and maximum; a track with slots in levels
    /// brings the condition of each level that has something in it.</summary>
    [Fact]
    public void ATrackStaysInRange_AndFilledLevelsBringTheirConditions()
    {
        const string kind = """
                tracks:
                  - { key: HP, max: 10, min: -10 }
                  - { key: stress, max: 9 }
                  - key: harm
                    display: slots
                    holds: { type: text }
                    levels:
                      - { level: 1, slots: 2, condition: Less effect }
                      - { level: 2, slots: 2, condition: Reduced dice }
            """;
        var fine = Evaluate(kind, new Dictionary<string, object>
        {
            ["HP"] = -10L, ["stress"] = 9L,
            ["harm"] = new Dictionary<string, object> { ["1"] = new List<object> { "bruised" }, ["2"] = new List<object>() }
        });
        Assert.Empty(fine.Violations);
        Assert.Equal(new[] { "Less effect" }, fine.Conditions);

        var out_ = Evaluate(kind, new Dictionary<string, object> { ["HP"] = -11L, ["stress"] = 10L });
        Assert.Equal(new[] { ("HP", "Actor.Rule.TrackRange"), ("stress", "Actor.Rule.TrackRange") }, out_.Violations.Select(v => (v.FieldKey, v.Key)).ToArray());
        Assert.Equal("HP: -11 is outside -10 to 10.", out_.Violations[0].Message);
    }

    /// <summary>A named function's parameter may share its name with a built-in: <c>bonusSpells(score, level)</c> beside
    /// <c>score(key)</c>. The parser would take the name for the function, so parameters are renamed in the body.</summary>
    [Fact]
    public void AFunctionParameter_MayShareItsNameWithABuiltIn()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: shadow
            name: Shadow
            attributes:
              - { key: wis, label: Wisdom, default: 10, modifier: half-minus-five }
            functions:
              bonus: { formula: "(score, level) => mod(score) >= level ? score(\"wis\") + level : 0" }
            """);
        var model = new SheetModel(system, NullEntryResolver.Instance, new Dictionary<string, object> { ["wis"] = 14L });
        // bonus(13, 1): mod 1 >= 1, so Wisdom 14 + 1 = 15. bonus(8, 1): mod -1, so 0.
        Assert.Equal(15d, model.Formula("bonus(13, 1) + bonus(8, 1)", new Dictionary<string, double>()));
    }

    [Fact]
    public void ASystemWithoutSheetKinds_HasNoModel()
    {
        var system = SystemDefinitionLoader.Parse("id: plain\nname: Plain\n");
        Assert.Null(new SheetRuleEngine(system, NullEntryResolver.Instance).Evaluate(new Dictionary<string, object>()).Model);
    }
}
