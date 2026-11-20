using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>A <c>sheetRules.lists</c> list is a pick list in a sheet kind's formulas too, so a track's maximum can count the entries a feature put on it. The loader
/// refuses a list whose name a kind value or list already has.</summary>
public class SheetRuleListExposureTests
{
    private static string Yaml(string kindExtra = "") => $$"""
        id: list-test
        name: List test
        entityTypes:
          - { type: spell, label: Spell }
        sheetRules:
          enforcement: warn
          lists:
            - { key: tricks, label: Tricks, targetEntityType: spell }
        sheetKinds:
          - kind: hero
            tracks:
              - { key: reserve, label: Reserve, max: "min(3, count(tricks, s => true))", display: boxes }
        {{kindExtra}}
        """;

    private static double ReserveMax(params string[] tricks)
    {
        var system = SystemDefinitionLoader.Parse(Yaml());
        var sheet = new Dictionary<string, object> { ["tricks"] = tricks.Cast<object>().ToList() };
        var evaluation = new SheetRuleEngine(system, NullEntryResolver.Instance).Recompute(sheet);
        return Convert.ToDouble(evaluation.Model!.Values["reserve_Max"]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 3)]
    public void ATrackMaximum_CountsTheEntriesOnASheetRulesList_UpToItsCap(int picked, int expected) =>
        Assert.Equal(expected, ReserveMax(Enumerable.Range(1, picked).Select(i => $"trick-{i}").ToArray()));

    [Fact]
    public void AListWhoseEntriesAreNotInTheLibrary_IsNotReportedTwiceByTheKind()
    {
        var system = SystemDefinitionLoader.Parse(Yaml());
        var sheet = new Dictionary<string, object> { ["tricks"] = new List<object> { "ghost" } };

        var evaluation = new SheetRuleEngine(system, NullEntryResolver.Instance).Recompute(sheet);

        // The sheet rules say it once; the kind's model adds no violation of its own for the same list.
        Assert.Empty(evaluation.Model!.Violations);
    }

    [Fact]
    public void ASheetRulesListNamedLikeAKindValue_OrKindList_IsRefused()
    {
        var value = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(Yaml().Replace(
            "display: boxes }", "display: boxes }\n    values:\n      - { key: tricks, formula: \"1\" }")));
        Assert.Contains("sheetRules.lists 'tricks'", value.Message);
        Assert.Contains("value of sheetKinds.hero", value.Message);

        var list = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(Yaml().Replace(
            "display: boxes }", "display: boxes }\n    lists:\n      - { key: tricks, label: Tricks, type: spell }")));
        Assert.Contains("list of sheetKinds.hero", list.Message);
    }
}
