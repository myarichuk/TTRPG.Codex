using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>srd5e's encounter difficulty block (SRD 5.2.1, Combat Encounter Difficulty): the 20 budget rows hold the table's numbers, and the SRD's three worked
/// examples rate as the band the SRD gives them, with each monster's XP read from the bundled pack.</summary>
public class Srd5eDifficultyTests : IAsyncLifetime
{
    // level, low, moderate, high: the XP Budget per Character table.
    private static readonly int[][] Table =
    [
        [1, 50, 75, 100], [2, 100, 150, 200], [3, 150, 225, 400], [4, 250, 375, 500], [5, 500, 750, 1100],
        [6, 600, 1000, 1400], [7, 750, 1300, 1700], [8, 1000, 1700, 2100], [9, 1300, 2000, 2600], [10, 1600, 2300, 3100],
        [11, 1900, 2900, 4100], [12, 2200, 3700, 4700], [13, 2600, 4200, 5400], [14, 2900, 4900, 6200], [15, 3300, 5400, 7800],
        [16, 3800, 6100, 9800], [17, 4500, 7200, 11700], [18, 5000, 8700, 14200], [19, 5500, 10700, 17200], [20, 6400, 13200, 22000]
    ];

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public Srd5eDifficultyTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private DifficultyRules Rules => _loader.GetDefinition("srd5e")!.Encounters!.Difficulty!;

    private static int[] Party(int size, int level) => Enumerable.Repeat(level, size).ToArray();

    /// <summary>A line of <paramref name="count"/> creatures, each worth what its own pack entry says.</summary>
    private RatedLine Line(string id, int count)
    {
        var entry = _registry.GetEntries("srd5e", "monster", new HashSet<string> { "srd-5.2.1" }).Single(e => e.Id == id);
        return new RatedLine(entry.Name, count, Convert.ToDouble(entry.Properties["xp"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheBlock_LoadsWithNoProblem_AndSystemCheckReportsNone()
    {
        Assert.Equal(DifficultyRules.ShapeSum, Rules.Shape);
        Assert.Equal("xp", Rules.CreatureValue);
        Assert.Equal("XP", Rules.Unit);
        Assert.Equal(DifficultyRules.PickAtMost, Rules.Pick);
        Assert.Equal(new[] { "low", "moderate", "high" }, Rules.Bands.Select(b => b.Key));
        Assert.Equal(new[] { "Low", "Moderate", "High" }, Rules.Bands.Select(b => b.Label));

        var output = new StringWriter();
        var exit = Codex.SystemCheck.SystemCheck.Run(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e"), writeTypes: false, quiet: true, output);

        Assert.True(exit == 0, output.ToString());
        Assert.DoesNotContain("PROBLEM", output.ToString());
        Assert.DoesNotContain("FAIL", output.ToString());
    }

    [Fact]
    public void TheTwentyRows_HoldTheTablesNumbers()
    {
        Assert.Equal(20, Rules.Budget.Count);
        for (var i = 0; i < Table.Length; i++)
        {
            var row = Rules.Budget[i];
            Assert.Equal(Table[i][0], int.Parse(row["level"]));
            Assert.Equal(Table[i][1], int.Parse(row["low"]));
            Assert.Equal(Table[i][2], int.Parse(row["moderate"]));
            Assert.Equal(Table[i][3], int.Parse(row["high"]));
        }
    }

    [Fact]
    public void ABudget_IsTheRowsNumberTimesTheCharacters()
    {
        // Examples 1 to 3: 50 x 4, 225 x 5 and 7,800 x 6.
        Assert.Equal(200, EncounterDifficulty.Rate(Rules, Party(4, 1), [Line("bugbear-warrior", 1)]).Budgets[0].Budget);
        Assert.Equal(1125, EncounterDifficulty.Rate(Rules, Party(5, 3), [Line("stirge", 1)]).Budgets[1].Budget);
        Assert.Equal(46800, EncounterDifficulty.Rate(Rules, Party(6, 15), [Line("stirge", 1)]).Budgets[2].Budget);
    }

    [Fact]
    public void Example1_FourLevelOneCharacters_AgainstABugbearWarrior_IsLow()
    {
        var rating = EncounterDifficulty.Rate(Rules, Party(4, 1), [Line("bugbear-warrior", 1)]);

        Assert.Equal(200, rating.Total);
        Assert.Equal("low", rating.Band!.Key);
        Assert.Equal(BandPosition.In, rating.Position);
    }

    [Fact]
    public void Example1_TwoGiantWasps_IsLow()
    {
        var rating = EncounterDifficulty.Rate(Rules, Party(4, 1), [Line("giant-wasp", 2)]);

        Assert.Equal(200, rating.Total);
        Assert.Equal("low", rating.Band!.Key);
    }

    [Fact]
    public void Example2_FiveLevelThreeCharacters_AgainstTwoDruidsAndNineStirges_IsModerate_AtExactlyTheBudget()
    {
        var rating = EncounterDifficulty.Rate(Rules, Party(5, 3), [Line("druid", 2), Line("stirge", 9)]);

        Assert.Equal(1125, rating.Total);
        Assert.Equal("moderate", rating.Band!.Key);
        Assert.Equal(1125, rating.Band.Budget);
        Assert.Equal(BandPosition.In, rating.Position);
    }

    [Fact]
    public void Example3_SixLevelFifteenCharacters_AgainstTwoAdultRedDragonsAndTwoFireGiants_IsHigh()
    {
        var rating = EncounterDifficulty.Rate(Rules, Party(6, 15), [Line("adult-red-dragon", 2), Line("fire-giant", 2)]);

        Assert.Equal(46000, rating.Total);
        Assert.Equal("high", rating.Band!.Key);
        Assert.Equal(BandPosition.In, rating.Position);
    }

    [Fact]
    public void AMixedParty_AddsEachCharactersOwnRow()
    {
        // Levels 1, 3 and 15: low 50 + 150 + 3,300, moderate 75 + 225 + 5,400, high 100 + 400 + 7,800.
        var rating = EncounterDifficulty.Rate(Rules, [1, 3, 15], [Line("stirge", 1)]);

        Assert.Equal(new long[] { 3500, 5700, 8300 }, rating.Budgets.Select(b => b.Budget));
        Assert.True(rating.LevelsVary);
    }

    [Fact]
    public void Example1_SixGiantRats_UnderTheLowBudget_IsLow()
    {
        var rating = EncounterDifficulty.Rate(Rules, Party(4, 1), [Line("giant-rat", 6)]);

        Assert.Equal(150, rating.Total);
        Assert.Equal("low", rating.Band!.Key);
        Assert.Equal(BandPosition.In, rating.Position);
    }

    [Fact]
    public void Example2_AWightAWarhorseSkeletonAndSixSkeletons_AboveLowButUnderModerate_IsModerate()
    {
        var rating = EncounterDifficulty.Rate(Rules, Party(5, 3), [Line("wight", 1), Line("warhorse-skeleton", 1), Line("skeleton", 6)]);

        Assert.Equal(1100, rating.Total);
        Assert.Equal("moderate", rating.Band!.Key);
    }

    [Fact]
    public void AboveTheHighBudget_IsAboveHigh()
    {
        var rating = EncounterDifficulty.Rate(Rules, Party(4, 1), [Line("bugbear-warrior", 3)]);

        Assert.Equal(BandPosition.Above, rating.Position);
        Assert.Equal("high", rating.Band!.Key);
    }

    [Fact]
    public void TheRussianAndHebrewOverlays_WordTheUnitAndEveryBand()
    {
        var system = _loader.GetDefinition("srd5e")!;

        var ru = system.Localize("ru").Encounters!.Difficulty!;
        Assert.Equal("ОО", ru.Unit);
        Assert.Equal(new[] { "Низкая", "Умеренная", "Высокая" }, ru.Bands.Select(b => b.Label));

        var he = system.Localize("he").Encounters!.Difficulty!;
        Assert.Equal("נקודות ניסיון", he.Unit);
        Assert.Equal(new[] { "נמוכה", "בינונית", "גבוהה" }, he.Bands.Select(b => b.Label));
        Assert.Equal(new[] { "low", "moderate", "high" }, he.Bands.Select(b => b.Key));
    }

    [Fact]
    public void TheArchmage_IsWorthItsOwnStatBlocksXp()
    {
        // The stat block says 8,000; the Challenge Rating table's 8,400 is not what the pack carries.
        Assert.Equal(8000, Line("archmage", 1).Value);
    }
}
