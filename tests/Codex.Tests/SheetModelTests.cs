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

    [Fact]
    public void ASystemWithoutSheetKinds_HasNoModel()
    {
        var system = SystemDefinitionLoader.Parse("id: plain\nname: Plain\n");
        Assert.Null(new SheetRuleEngine(system, NullEntryResolver.Instance).Evaluate(new Dictionary<string, object>()).Model);
    }
}
