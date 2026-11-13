using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The names a feature may use (what the writer's guide and the feature editor both read), and the editor's edits of a
/// feature's dictionary, which write what the sheet rules read.</summary>
public class FeatureGrantVocabularyTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public FeatureGrantVocabularyTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private FeatureGrantVocabulary Srd() => FeatureGrantVocabulary.For(_loader.GetDefinition("srd5e"))!;

    [Fact]
    public void TheSrdVocabulary_HasTheSystemsTargetsListsAndRests()
    {
        var vocabulary = Srd();

        Assert.Contains(vocabulary.Targets, t => t.Key == "HP_Max");
        Assert.Contains(vocabulary.Targets, t => t.Key == "darkvision");
        Assert.Equal(vocabulary.Targets.Count, vocabulary.Targets.Select(t => t.Key).Distinct().Count());
        Assert.Contains(vocabulary.Lists, l => l.Key == "grantedSpells");
        Assert.DoesNotContain(vocabulary.Lists, l => l.Key == "cantrips"); // a list with a count is the player's to fill
        Assert.NotEmpty(vocabulary.DamageListKeys);
        Assert.All(vocabulary.DamageListKeys, key => Assert.Contains(vocabulary.Lists, l => l.Key == key));
        Assert.Contains("Long Rest", vocabulary.Rests);
        Assert.Contains("base", vocabulary.ModifierTypes);
        Assert.Contains("base", vocabulary.FloorTypes);
        Assert.NotEmpty(vocabulary.FormulaNames);
    }

    [Fact]
    public void ForEntriesOfAType_TheVocabularyKnowsRowPropertiesAndWholeEntries()
    {
        var vocabulary = Srd();

        Assert.Contains("features", vocabulary.CarrierFor("subclass")!.RowProperties);
        Assert.False(vocabulary.CarrierFor("subclass")!.WholeEntry);
        Assert.True(vocabulary.CarrierFor("feat")!.WholeEntry);
        Assert.Null(vocabulary.CarrierFor("spell"));
        Assert.Null(FeatureGrantVocabulary.For(_loader.GetDefinition("simple-d6"))?.CarrierFor("calling"));
    }

    [Fact]
    public void AFormula_IsCheckedWithTheEnginesParser()
    {
        var vocabulary = Srd();

        Assert.Null(vocabulary.CheckFormula(null));
        Assert.Null(vocabulary.CheckFormula(""));
        Assert.Null(vocabulary.CheckFormula("2"));
        Assert.Null(vocabulary.CheckFormula("mod(cha) + 1"));
        Assert.Null(vocabulary.CheckFormula("level >= 3"));
        Assert.NotNull(vocabulary.CheckFormula("mod(cha) +"));
        Assert.NotNull(vocabulary.CheckFormula("(2 + "));
    }

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void ARowsGrants_AreReadAsTheSheetRulesReadThem()
    {
        var row = Map(("name", "Shadowed Blood"),
            ("modifiers", new List<object> { Map(("target", "darkvision"), ("amount", 120L), ("type", "base")), Map(("target", "HP_Max"), ("amount", "level"), ("when", "level >= 3")) }),
            ("adds", Map(("to", "grantedSpells"), ("entries", "darkness, fear"), ("level", 3.0))),
            ("uses", Map(("max", "mod(cha)"), ("recoversOn", new List<object> { "Long Rest" }), ("key", "blood"))));

        var data = FeatureGrantData.Read(row);

        Assert.Equal(2, data.Modifiers.Count);
        Assert.Equal(("darkvision", "120", "base", ""), (data.Modifiers[0].Target, data.Modifiers[0].Amount, data.Modifiers[0].Type, data.Modifiers[0].When));
        Assert.Equal("level >= 3", data.Modifiers[1].When);
        Assert.Equal(new[] { "darkness", "fear" }, data.Adds.Single().Entries);
        Assert.Equal(3, data.Adds.Single().Level);
        Assert.Equal("mod(cha)", data.Uses!.Max);
        Assert.Equal(new[] { "Long Rest" }, data.Uses.RecoversOn);
        Assert.Equal((2, 1, 1), data.Counts());
    }

    [Fact]
    public void APlainCounter_IsReadAsAMaximum()
    {
        Assert.Equal("2", FeatureGrantData.Read(Map(("uses", 2L))).Uses!.Max);
        Assert.Null(FeatureGrantData.Read(Map(("name", "x"))).Uses);
        Assert.Empty(FeatureGrantData.Read(null).Modifiers);
    }

    [Fact]
    public void WritingGrantsBack_KeepsNumbersAsNumbersAndFormulasAsText_AndEverythingElse()
    {
        var feature = Map(("name", "Shadowed Blood"), ("description", "Dark."), ("level", 3L));
        var data = FeatureGrantData.Read(feature);
        data.Modifiers.Add(new GrantModifier { Target = "darkvision", Amount = "120", Type = "base" });
        data.Modifiers.Add(new GrantModifier { Target = "HP_Max", Amount = "mod(con) * level", When = "level >= 3" });
        data.Adds.Add(new GrantAdd { To = "grantedSpells", Entries = FeatureGrantData.SplitEntries("darkness, , fear "), Level = FeatureGrantData.ParseLevel("3") });
        data.Uses = new GrantUses { Max = "2", RecoversOn = new List<string> { "Long Rest" } };

        data.Apply(feature);

        Assert.Equal("Dark.", feature["description"]);
        Assert.Equal(3L, feature["level"]);
        var modifiers = Assert.IsType<List<object>>(feature["modifiers"]);
        var first = Assert.IsType<Dictionary<string, object>>(modifiers[0]);
        Assert.Equal(120L, first["amount"]);
        Assert.Equal("base", first["type"]);
        Assert.False(first.ContainsKey("when"));
        var second = Assert.IsType<Dictionary<string, object>>(modifiers[1]);
        Assert.Equal("mod(con) * level", second["amount"]);
        Assert.Equal("level >= 3", second["when"]);
        Assert.False(second.ContainsKey("type"));
        var add = Assert.IsType<Dictionary<string, object>>(Assert.Single(Assert.IsType<List<object>>(feature["adds"])));
        Assert.Equal(new object[] { "darkness", "fear" }, Assert.IsType<List<object>>(add["entries"]));
        Assert.Equal(3L, add["level"]);
        var uses = Assert.IsType<Dictionary<string, object>>(feature["uses"]);
        Assert.Equal(2L, uses["max"]);
        Assert.Equal(new object[] { "Long Rest" }, Assert.IsType<List<object>>(uses["recoversOn"]));

        // What was written is what the engine reads back.
        var again = FeatureGrantData.Read(feature);
        Assert.Equal((2, 1, 1), again.Counts());
    }

    [Fact]
    public void EmptyStructures_AreRemovedNotSavedEmpty()
    {
        var feature = Map(("name", "x"),
            ("modifiers", new List<object> { Map(("target", "darkvision"), ("amount", 1L)) }),
            ("adds", new List<object> { Map(("to", "grantedSpells"), ("entries", new List<object> { "darkness" })) }),
            ("uses", Map(("max", 1L))));
        var data = FeatureGrantData.Read(feature);

        data.Modifiers.Clear();
        data.Adds.Clear();
        data.Uses = null;
        data.Apply(feature);

        Assert.Equal(new[] { "name" }, feature.Keys);
    }

    [Fact]
    public void KeysTheEditorDoesNotKnow_SurviveAnEdit()
    {
        var feature = Map(("uses", Map(("max", 1L), ("key", "blood"))), ("modifiers", new List<object> { Map(("target", "ac"), ("amount", 1L), ("stacks", true)) }));
        var data = FeatureGrantData.Read(feature);

        data.Uses!.Max = "3";
        data.Modifiers[0].Amount = "2";
        data.Apply(feature);

        Assert.Equal("blood", ((Dictionary<string, object>)feature["uses"])["key"]);
        Assert.Equal(true, ((Dictionary<string, object>)((List<object>)feature["modifiers"])[0])["stacks"]);
        Assert.Equal(3L, ((Dictionary<string, object>)feature["uses"])["max"]);
    }

    [Theory]
    [InlineData("3", 3L)]
    [InlineData(" -2 ", -2L)]
    [InlineData("1.5", 1.5)]
    public void ANumberTyped_StaysANumber(string text, object expected) => Assert.Equal(expected, FeatureGrantData.Value(text));

    [Theory]
    [InlineData("mod(cha)")]
    [InlineData("level / 2")]
    public void AFormulaTyped_StaysText(string text) => Assert.Equal(text, FeatureGrantData.Value(text));

    [Theory]
    [InlineData("3", 3)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData("x", null)]
    public void ALevelTyped_IsAWholeNumberFromOne(string text, int? expected) => Assert.Equal(expected, FeatureGrantData.ParseLevel(text));
}
