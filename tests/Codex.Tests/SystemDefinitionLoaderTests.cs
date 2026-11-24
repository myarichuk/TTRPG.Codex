using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The loader's checks of the <c>encounters.difficulty</c> block (ENCOUNTER_BUILDER_DESIGN.md 2.1 and 16): each way to write it wrongly fails, with a sentence that names the path,
/// and a good block loads. Every number is invented.</summary>
public class SystemDefinitionLoaderTests
{
    private const string Header = """
        id: t
        name: T
        actorFields:
          - { key: level, label: Level, type: Number }
          - { key: nickname, label: Nickname, type: Text }
        fieldRoles: { level: level }
        entityTypes:
          - type: monster
            folder: monsters
            actorBlueprint: true
            fields:
              - { key: worth, label: Worth, type: Number }

        """;

    private const string SumBlock = """
            shape: sum
            creatureValue: worth
            bands:
              - { key: low, label: Low }
              - { key: high, label: High }
            budget:
              - { level: 1, low: 10, high: 20 }
              - { level: 2, low: 15, high: 30 }

        """;

    private const string ByLevelBlock = """
            shape: byLevel
            creatureLevel: worth
            partySize: 4
            bands:
              - { key: low, label: Low, value: 22, perCharacter: 3 }
              - { key: high, label: High, value: 35, perCharacter: 5 }
            values:
              - { delta: -1, value: 4 }
              - { delta: 0, value: 6 }
              - { delta: 1, value: 9 }

        """;

    private static string Doc(string block, string header = Header) => header + "encounters:\n  difficulty:\n" + block;

    private static string Message(string yaml) => Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml)).Message;

    [Fact]
    public void AGoodBlockOfEitherShape_Loads_WithItsBandsBudgetsAndDefaults()
    {
        var sum = SystemDefinitionLoader.Parse(Doc(SumBlock)).Encounters!.Difficulty!;
        var byLevel = SystemDefinitionLoader.Parse(Doc(ByLevelBlock)).Encounters!.Difficulty!;

        Assert.Equal("sum", sum.Shape);
        Assert.Equal("atMost", sum.Pick);
        Assert.Null(sum.PartyLevel);
        Assert.Equal(new[] { "low", "high" }, sum.Bands.Select(b => b.Key));
        Assert.Equal("15", sum.Budget[1]["low"]);
        Assert.Equal("byLevel", byLevel.Shape);
        Assert.Equal(4, byLevel.PartySize);
        Assert.Equal(new[] { -1, 0, 1 }, byLevel.Values.Select(v => v.Delta!.Value));
        Assert.Equal(5, byLevel.Bands[1].PerCharacter);
        Assert.Equal(22, byLevel.Bands[0].Value);
        Assert.Empty(byLevel.Budget);
    }

    [Fact]
    public void ASystemWithNoBlock_HasNoEncounterRules()
    {
        Assert.Null(SystemDefinitionLoader.Parse(Header).Encounters);
    }

    [Fact]
    public void ACreatureTypeWithoutTheField_IsAllowed_ButOneWithItAsTextIsNot()
    {
        const string without = "  - type: beast\n    folder: beasts\n    actorBlueprint: true\n    fields:\n      - { key: color, label: Color, type: Text }\n";
        const string asText = "  - type: beast\n    folder: beasts\n    actorBlueprint: true\n    fields:\n      - { key: worth, label: Worth, type: Text }\n";

        Assert.NotNull(SystemDefinitionLoader.Parse(Doc(SumBlock, Header + without)).Encounters);
        Assert.Contains("encounters.difficulty.creatureValue names 'worth', which is Text on 'beast'; it must be a Number.", Message(Doc(SumBlock, Header + asText)));
    }

    [Theory]
    [InlineData("shape: sum", "shape: weird", "encounters.difficulty.shape is 'weird'")]
    [InlineData("{ key: low, label: Low }", "{ key: level, label: Low }", "bands[0].key 'level' is reserved")]
    [InlineData("{ key: low, label: Low }", "{ key: low, label: Low, value: 3 }", "bands[0].value belongs to shape 'byLevel'")]
    [InlineData("{ key: low, label: Low }", "{ key: low, label: Low, perCharacter: 3 }", "bands[0].perCharacter belongs to shape 'byLevel'")]
    [InlineData("creatureValue: worth\n", "creatureValue: worth\n    pick: sometimes\n", "encounters.difficulty.pick is 'sometimes'")]
    [InlineData("    creatureValue: worth\n", "", "encounters.difficulty.creatureValue is required for this shape")]
    [InlineData("creatureValue: worth", "creatureValue: gold", "which no creature type (actorBlueprint: true) declares")]
    [InlineData("creatureValue: worth", "creatureValue: Worth", "did you mean 'worth'")]
    [InlineData("bands:\n      - { key: low, label: Low }\n      - { key: high, label: High }\n", "bands: []\n", "encounters.difficulty.bands needs at least one band")]
    [InlineData("{ key: high, label: High }", "{ key: low, label: High }", "bands[1].key 'low' is used by an earlier band")]
    [InlineData("{ key: high, label: High }", "{ key: 2high, label: High }", "bands[1].key '2high' must start with a lower-case letter")]
    [InlineData("{ key: high, label: High }", "{ key: high, label: ' ' }", "bands[1] ('high') needs a label")]
    [InlineData("{ level: 2, low: 15, high: 30 }", "{ level: 1, low: 15, high: 30 }", "budget[1] repeats level 1")]
    [InlineData("{ level: 1, low: 10, high: 20 }", "{ level: 3, low: 10, high: 20 }", "budget[1] is level 2, after level 3")]
    [InlineData("{ level: 2, low: 15, high: 30 }", "{ level: 2, low: 15 }", "budget[1] has no number for band 'high'")]
    [InlineData("{ level: 2, low: 15, high: 30 }", "{ level: 2, low: 15, high: 30, mid: 20 }", "budget[1] has 'mid', which is neither 'level' nor a band key")]
    [InlineData("{ level: 2, low: 15, high: 30 }", "{ level: 2, low: 15, high: 14 }", "budget[1].high is 14, less than the band before it (15)")]
    [InlineData("{ level: 2, low: 15, high: 30 }", "{ level: 2, low: 1.5, high: 30 }", "budget[1].low is '1.5'; a budget is a whole number")]
    [InlineData("{ level: 2, low: 15, high: 30 }", "{ low: 15, high: 30 }", "budget[1] needs a whole-number level")]
    [InlineData("creatureValue: worth\n", "creatureValue: worth\n    values:\n      - { delta: 0, value: 1 }\n", "encounters.difficulty.values belongs to shape 'byLevel'")]
    [InlineData("creatureValue: worth\n", "creatureValue: worth\n    partySize: 4\n", "encounters.difficulty.partySize belongs to shape 'byLevel'")]
    [InlineData("creatureValue: worth\n", "creatureValue: worth\n    creatureLevel: worth\n", "encounters.difficulty.creatureLevel belongs to shape 'byLevel'")]
    public void ASumBlockWrittenWrongly_IsRefused_WithASentenceThatNamesThePath(string from, string to, string expected)
    {
        Assert.Contains(expected, Message(Doc(SumBlock.Replace(from, to))));
    }

    [Theory]
    [InlineData("{ delta: 1, value: 9 }", "{ delta: 2, value: 9 }", "values[2].delta is 2, after 0; the deltas go up by exactly one")]
    [InlineData("{ delta: 1, value: 9 }", "{ delta: 1, value: -1 }", "values[2].value is -1")]
    [InlineData("partySize: 4", "partySize: 0", "partySize is 0; it must be 1 or more")]
    [InlineData("    partySize: 4\n", "", "encounters.difficulty.partySize is required")]
    [InlineData("creatureLevel: worth", "creatureLevel: nickname", "creatureLevel names 'nickname'")]
    [InlineData("{ delta: 1, value: 9 }", "{ value: 9 }", "values[2].delta is required")]
    [InlineData("{ delta: 1, value: 9 }", "{ delta: 1 }", "values[2].value is required")]
    [InlineData("value: 22, ", "", "bands[0].value is required for this shape")]
    [InlineData("perCharacter: 5", "perCharacter: -1", "bands[1].perCharacter is -1")]
    [InlineData("value: 35, perCharacter: 5", "value: 35", "bands[1].perCharacter is required for this shape")]
    // In order at the written size, but at a party of 20 the first band would pass the second: 20 + 16 x 10 = 180 against 35 + 16 x 5 = 115.
    [InlineData("{ key: low, label: Low, value: 22, perCharacter: 3 }", "{ key: low, label: Low, value: 20, perCharacter: 10 }", "bands[1] ('high') would have a smaller budget than the band before it for a party of 20")]
    // The second band starts below the first for a party of 1: 21 - 3 x 5 = 6 against 22 - 3 x 3 = 13.
    [InlineData("value: 35, perCharacter: 5", "value: 21, perCharacter: 5", "for a party of 1;")]
    [InlineData("partySize: 4\n", "partySize: 4\n    budget:\n      - { level: 1, low: 1, high: 2 }\n", "encounters.difficulty.budget belongs to shape 'sum'")]
    [InlineData("creatureLevel: worth\n", "creatureLevel: worth\n    creatureValue: worth\n", "encounters.difficulty.creatureValue belongs to shape 'sum'")]
    [InlineData("creatureLevel: worth\n", "creatureLevel: worth\n    unit: pts\n", "encounters.difficulty.unit belongs to shape 'sum'")]
    public void AByLevelBlockWrittenWrongly_IsRefused_WithASentenceThatNamesThePath(string from, string to, string expected)
    {
        Assert.Contains(expected, Message(Doc(ByLevelBlock.Replace(from, to))));
    }

    [Fact]
    public void WithNoLevelFieldToRead_TheBlockIsRefused_AndAFieldThatIsNotANumberToo()
    {
        var noRole = Header.Replace("fieldRoles: { level: level }\n", string.Empty);

        Assert.Contains("encounters.difficulty.partyLevel is not given, and fieldRoles.level is not set either", Message(Doc(SumBlock, noRole)));
        Assert.Contains("encounters.difficulty.partyLevel names 'nickname', which is Text; it must be a Number.", Message(Doc("    partyLevel: nickname\n" + SumBlock)));
        Assert.Contains("partyLevel names 'rank', which is not an actor field", Message(Doc("    partyLevel: rank\n" + SumBlock)));
        // An empty or blank partyLevel is as if it were not written: the role is read, and without one it is refused.
        Assert.NotNull(SystemDefinitionLoader.Parse(Doc("    partyLevel: ''\n" + SumBlock)).Encounters);
        Assert.Contains("partyLevel is not given", Message(Doc("    partyLevel: ' '\n" + SumBlock, noRole)));
        // Named outright, a level field needs no role.
        Assert.NotNull(SystemDefinitionLoader.Parse(Doc("    partyLevel: level\n" + SumBlock, noRole)).Encounters);
    }

    [Fact]
    public void ABlockForASystemWithNoCreatureType_IsRefused()
    {
        var none = Header.Replace("actorBlueprint: true", "actorBlueprint: false");

        Assert.Contains("this system has no entity type with actorBlueprint: true", Message(Doc(SumBlock, none)));
    }

    [Fact]
    public void AllTheProblemsOfABlock_AreNamedTogether_NotJustTheFirst()
    {
        var message = Message(Doc(SumBlock.Replace("creatureValue: worth", "creatureValue: gold").Replace("{ level: 2, low: 15, high: 30 }", "{ level: 1, low: 15, high: 30 }")));

        Assert.Contains("which no creature type", message);
        Assert.Contains("budget[1] repeats level 1", message);
    }

    [Fact]
    public void TheFixtureSystems_Load_AndTheTranslationWordsTheBandsAndTheUnit_WithoutTouchingTheRules()
    {
        var sum = EncounterWorld.Fixture("difficulty-sum");
        var byLevel = EncounterWorld.Fixture("difficulty-bylevel");

        Assert.Equal("sum", sum.Encounters!.Difficulty!.Shape);
        Assert.Equal("byLevel", byLevel.Encounters!.Difficulty!.Shape);
        Assert.Equal("atLeast", byLevel.Encounters.Difficulty.Pick);

        var ru = sum.Localize("ru").Encounters!.Difficulty!;
        Assert.Equal(new[] { "Лёгкое", "Среднее", "Тяжёлое" }, ru.Bands.Select(b => b.Label));
        Assert.Equal(new[] { "low", "moderate", "high" }, ru.Bands.Select(b => b.Key));
        Assert.Equal("оч.", ru.Unit);
        // The original keeps its own words and shares nothing a translation could change.
        Assert.Equal(new[] { "Low", "Moderate", "High" }, sum.Encounters.Difficulty.Bands.Select(b => b.Label));
        Assert.Equal("pts", sum.Encounters.Difficulty.Unit);
        Assert.Same(sum.Encounters, sum.Localize("en").Encounters);
        Assert.Equal("Moderate", sum.Localize("he").Encounters!.Difficulty!.Bands[1].Label);
    }
}
