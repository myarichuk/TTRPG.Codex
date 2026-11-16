using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The boosts attribute method (docs/ATTRIBUTE_BOOSTS_DESIGN.md): loader checks, and a few cases the degrees fixture's spec does not cover.</summary>
public class AttributeBoostTests
{
    private static string Yaml(string boosts = "sources: [{ key: ancestry, label: Ancestry, field: ancestry }]", string modifier = "half-minus-five") => $$"""
        id: boosty
        name: Boosty
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 10, modifier: {{modifier}} }
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: {{modifier}} }
        actorFields:
          - { key: ancestry, label: Ancestry, type: EntityReference, targetEntityType: ancestry }
        entityTypes:
          - { type: ancestry, label: Ancestry }
        sheetRules:
          enforcement: warn
          attributes:
            methods: [manual, boosts]
            default: boosts
            boosts: { {{boosts}} }
        """;

    private static void Rejects(string yaml, string fragment) =>
        Assert.Contains(fragment, Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml)).Message);

    [Fact]
    public void TheLoaderRefusesBoostsWithoutRulesSourcesOrANumericModifier()
    {
        Rejects(Yaml().Replace("boosts: { sources: [{ key: ancestry, label: Ancestry, field: ancestry }] }", ""), "boosts is not defined");
        Rejects(Yaml("sources: []"), "list at least one source");
        Rejects(Yaml(modifier: "none"), "half-minus-five modifier");
    }

    [Fact]
    public void TheLoaderChecksEachSource()
    {
        Rejects(Yaml("sources: [{ key: a, label: A }]"), "a field or a level");
        Rejects(Yaml("sources: [{ key: a, label: A, field: nowhere }]"), "not an EntityReference actor field");
        Rejects(Yaml("sources: [{ key: a, label: A, level: 5, free: 3 }]"), "different attributes");
        Rejects(Yaml("sources: [{ key: a__b, label: A, level: 5, free: 1 }]"), "without '__'");
        Rejects(Yaml("sources: [{ key: a, label: A, level: 5, free: 1 }, { key: a, label: B, level: 6, free: 1 }]"), "duplicate key");
        Rejects(Yaml("sources: [{ key: a, label: A, level: 5, free: 1, trade: { fixed: 2, free: 1 } }]"), "needs a field source");
    }

    [Fact]
    public void AValidBoostSystemLoads()
    {
        var definition = SystemDefinitionLoader.Parse(Yaml("partialFrom: 4, sources: [{ key: ancestry, label: Ancestry, field: ancestry, trade: { fixed: 2, free: 1 } }, { key: m5, label: Five, level: 5, free: 2 }]"));
        Assert.Equal(2, definition.SheetRules!.Attributes!.Boosts!.Sources.Count);
    }
}
