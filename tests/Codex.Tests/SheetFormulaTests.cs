using Codex.Core.Rules;
using Codex.Core.Systems;

namespace Codex.Tests;

/// <summary>R7: derived formulas from a content pack, which anyone can import.</summary>
public class SheetFormulaTests
{
    private static SheetEvaluation Evaluate(string derived, params (string Key, object Value)[] values)
    {
        var system = SystemDefinitionLoader.Parse($$"""
            id: formulas
            name: Formulas
            attributes:
              - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: half-minus-five }
            actorFields:
              - { key: level, label: Level, type: Number, defaultValue: 1 }
            sheetRules:
              attributes: { methods: [manual], default: manual }
              derived:
            {{derived}}
            """);
        var sheet = new Dictionary<string, object> { ["attrMethod"] = "manual" };
        foreach (var (key, value) in values) sheet[key] = value;
        return new SheetRuleEngine(system, NullEntryResolver.Instance).Evaluate(sheet);
    }

    [Fact]
    public void AFormulaMayUseAnEarlierOne()
    {
        var result = Evaluate("""
                - { key: pb, label: Proficiency Bonus, formula: "2 + (level - 1) / 4" }
                - { key: passive, label: Passive, formula: "10 + mod(dex) + pb" }
            """, ("level", 5L), ("dex", 14L));

        Assert.Equal(3L, result.Derived["pb"]);
        Assert.Equal(15L, result.Derived["passive"]);
    }

    [Theory]
    [InlineData("new string('a', 1000000000).Length")]
    [InlineData("10 / (level - level)")]
    [InlineData("level + nosuchfield")]
    [InlineData("System.IO.File.Exists(\"x\") ? 1 : 0")]
    public void ABrokenFormula_IsAWarningThePlayerCanSave_WithoutTheParsersText(string formula)
    {
        var result = Evaluate($$"""
                - { key: broken, label: Broken, formula: '{{formula.Replace("'", "''")}}' }
                - { key: fine, label: Fine, formula: "level * 2" }
            """, ("level", 3L));

        Assert.DoesNotContain(result.Violations, v => v.FieldKey == "broken");
        var warning = Assert.Single(result.Warnings, w => w.FieldKey == "broken");
        Assert.Equal("Actor.Rule.FormulaError", warning.Key);
        Assert.False(result.Derived.ContainsKey("broken"));
        Assert.Equal(6L, result.Derived["fine"]);
    }

    // R7: a number box never wraps 1e20 round to long.MinValue, and text that is no number is not read as one.
    [Theory]
    [InlineData("1e20", 1_000_000_000L)]
    [InlineData("-1e20", -1_000_000_000L)]
    [InlineData("25", 20L)]
    [InlineData("12", 12L)]
    [InlineData("abc", null)]
    [InlineData("Infinity", null)]
    public void ANumberTyped_StaysWithinWhatTheFieldHolds(string typed, long? expected)
    {
        var level = new Codex.Plugin.Abstractions.FieldDefinition { Key = "level", Type = Codex.Plugin.Abstractions.FieldType.Number, Max = 20 };
        var unbounded = new Codex.Plugin.Abstractions.FieldDefinition { Key = "gold", Type = Codex.Plugin.Abstractions.FieldType.Number };

        var field = typed.Contains('e') ? unbounded : level;
        Assert.Equal(expected, Codex.Web.Components.Shared.SchemaForm.ParseNumber(typed, field) is { } number ? (long?)Convert.ToInt64(number) : null);
    }

    // F26: a whole number is stored as a long, and only a fraction as a double.
    [Fact]
    public void AWholeNumberTyped_IsStoredAsALong()
    {
        var gold = new Codex.Plugin.Abstractions.FieldDefinition { Key = "gold", Type = Codex.Plugin.Abstractions.FieldType.Number };
        Assert.IsType<long>(Codex.Web.Components.Shared.SchemaForm.ParseNumber("12", gold));
        Assert.IsType<double>(Codex.Web.Components.Shared.SchemaForm.ParseNumber("1.5", gold));
    }
}
