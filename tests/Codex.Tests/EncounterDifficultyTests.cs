using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The difficulty readout's arithmetic (ENCOUNTER_BUILDER_DESIGN.md 2.1 and 16), worked by hand on the invented numbers of the two fixture systems.
/// <c>difficulty-sum</c>: per character, level 1 is 10/20/30, level 2 is 15/30/45, level 3 is 20/40/60 (low/moderate/high), most-allowed bands.
/// <c>difficulty-bylevel</c>: a creature is worth 1, 2, 3, 4 or 5 at 2 below to 2 above the party's level; for four characters the bands start at
/// 10/20/30 (easy/fair/hard), with 1/2/3 more for each character above four.</summary>
public class EncounterDifficultyTests
{
    private static DifficultyRules Sum() => EncounterWorld.Fixture("difficulty-sum").Encounters!.Difficulty!;

    private static DifficultyRules ByLevel() => EncounterWorld.Fixture("difficulty-bylevel").Encounters!.Difficulty!;

    private static int[] Party(int size, int level) => Enumerable.Repeat(level, size).ToArray();

    private static RatedLine[] Worth(double total) => new[] { new RatedLine("Rat", 1, total) };

    private static DifficultyRating Rate(DifficultyRules rules, int[] party, params RatedLine[] lines) => EncounterDifficulty.Rate(rules, party, lines);

    // ---- sum ----

    [Fact]
    public void Sum_AddsEachCharactersBudget_ByTheirLevel_ForEveryBand()
    {
        var rating = Rate(Sum(), Party(4, 2), Worth(10));

        // Four characters of level 2: 4 x 15, 4 x 30, 4 x 45.
        Assert.Equal(new long[] { 60, 120, 180 }, rating.Budgets.Select(b => b.Budget));
        Assert.Equal(new[] { "low", "moderate", "high" }, rating.Budgets.Select(b => b.Key));
        Assert.Equal(new[] { "Low", "Moderate", "High" }, rating.Budgets.Select(b => b.Label));
        Assert.Equal("pts", rating.Unit);
        Assert.Equal(4, rating.PartySize);
        Assert.Equal(2, rating.PartyLevel);
        Assert.False(rating.LevelsVary);
    }

    [Fact]
    public void Sum_WithCharactersOfDifferentLevels_AddsOneRowPerCharacter()
    {
        var rating = Rate(Sum(), new[] { 1, 2, 3 }, Worth(10));

        // 10 + 15 + 20, 20 + 30 + 40, 30 + 45 + 60.
        Assert.Equal(new long[] { 45, 90, 135 }, rating.Budgets.Select(b => b.Budget));
        Assert.True(rating.LevelsVary);
        Assert.Equal(1, rating.MinLevel);
        Assert.Equal(3, rating.MaxLevel);
    }

    [Theory]
    [InlineData(1, "low")]
    [InlineData(59, "low")]
    [InlineData(60, "low")]
    [InlineData(61, "moderate")]
    [InlineData(120, "moderate")]
    [InlineData(121, "high")]
    [InlineData(180, "high")]
    public void Sum_AtMost_PutsATotalInTheFirstBandWhoseBudgetItDoesNotPass_SoAnExactBudgetIsStillThatBand(double total, string band)
    {
        var rating = Rate(Sum(), Party(4, 2), Worth(total));

        Assert.Equal(BandPosition.In, rating.Position);
        Assert.Equal(band, rating.Band!.Key);
    }

    [Fact]
    public void Sum_AtMost_ATotalAboveTheLastBudget_IsAboveTheLastBand()
    {
        var rating = Rate(Sum(), Party(4, 2), Worth(181));

        Assert.Equal(BandPosition.Above, rating.Position);
        Assert.Equal("high", rating.Band!.Key);
    }

    [Fact]
    public void ATotalOfZero_SaysNothingIsCounted_WhileASmallTotalIsTheFirstBand()
    {
        var none = Rate(Sum(), Party(4, 2));
        var zero = Rate(Sum(), Party(4, 2), Worth(0));
        var small = Rate(Sum(), Party(4, 2), Worth(0.5));

        Assert.Equal(BandPosition.Nothing, none.Position);
        Assert.Null(none.Band);
        Assert.Equal(BandPosition.Nothing, zero.Position);
        Assert.Equal(BandPosition.In, small.Position);
        Assert.Equal("low", small.Band!.Key);
        Assert.Empty(none.NotCounted);
        Assert.True(none.Available);
    }

    [Theory]
    [InlineData(59, null, BandPosition.Below)]
    [InlineData(60, "low", BandPosition.In)]
    [InlineData(119, "low", BandPosition.In)]
    [InlineData(120, "moderate", BandPosition.In)]
    [InlineData(179, "moderate", BandPosition.In)]
    [InlineData(180, "high", BandPosition.In)]
    [InlineData(5000, "high", BandPosition.In)]
    public void Sum_AtLeast_PutsATotalInTheLastBandItReaches_AndBelowTheFirstOtherwise(double total, string? band, BandPosition position)
    {
        var rules = Sum();
        rules.Pick = DifficultyRules.PickAtLeast;

        var rating = Rate(rules, Party(4, 2), Worth(total));

        Assert.Equal(position, rating.Position);
        Assert.Equal(band ?? "low", rating.Band!.Key);
        Assert.False(rating.AtMost);
    }

    [Fact]
    public void Sum_MultipliesEachLinesValueByItsCount_AndAddsTheLines()
    {
        var rating = Rate(Sum(), Party(4, 2), new RatedLine("Rat", 3, 7), new RatedLine("Wolf", 2, 20));

        Assert.Equal(61, rating.Total);
        Assert.Equal("moderate", rating.Band!.Key);
    }

    [Fact]
    public void ALineWithNoValue_IsNamedWithItsReason_AndTheRestIsStillRated()
    {
        var reason = new DifficultyIssue("Encounters.Difficulty.NoValue", "Goblin", "Worth");

        var rating = Rate(Sum(), Party(4, 2), new RatedLine("Rat", 3, 7), new RatedLine("Goblin", 1, null, reason), new RatedLine("Nameless", 1, null));

        Assert.Equal(21, rating.Total);
        Assert.Equal("low", rating.Band!.Key);
        Assert.Equal(new[] { "Goblin", "Nameless" }, rating.NotCounted.Select(n => n.Name));
        Assert.Same(reason, rating.NotCounted[0].Reason);
        Assert.Equal("Encounters.Difficulty.NoValue", rating.NotCounted[1].Reason.Key);
    }

    [Fact]
    public void AHazardLine_IsNotCounted_ButListedApart()
    {
        var rating = Rate(Sum(), Party(4, 2), new RatedLine("Rat", 1, 7), new RatedLine("Pit", 1, 99, Hazard: true));

        Assert.Equal(7, rating.Total);
        Assert.Equal(new[] { "Pit" }, rating.Hazards);
        Assert.Empty(rating.NotCounted);
    }

    [Theory]
    [InlineData(new[] { 1, 4 }, 4)]
    [InlineData(new[] { 0 }, 0)]
    [InlineData(new[] { 25, 1 }, 25)]
    public void ALevelOutsideTheBudgetRows_GivesNoReadout_AndSaysWhichLevel(int[] party, int level)
    {
        var rating = Rate(Sum(), party, Worth(10));

        Assert.False(rating.Available);
        Assert.Equal("Encounters.Difficulty.NoBudgetRow", rating.Unavailable!.Key);
        Assert.Equal(level, rating.Unavailable.Args[0]);
    }

    [Fact]
    public void NoCharacters_GiveNoReadout()
    {
        var rating = Rate(Sum(), Array.Empty<int>(), Worth(10));

        Assert.Equal("Encounters.Difficulty.NoParty", rating.Unavailable!.Key);
    }

    // ---- byLevel ----

    [Fact]
    public void ByLevel_ReadsTheBandBudgetsForThePartySize_AddingOrTakingPerCharacterForEachCharacterMoreOrFewer()
    {
        long[] Budgets(int size) => Rate(ByLevel(), Party(size, 3), Worth(0)).Budgets.Select(b => b.Budget).ToArray();

        Assert.Equal(new long[] { 10, 20, 30 }, Budgets(4));
        Assert.Equal(new long[] { 11, 22, 33 }, Budgets(5));
        Assert.Equal(new long[] { 9, 18, 27 }, Budgets(3));
        Assert.Equal(new long[] { 7, 14, 21 }, Budgets(1));
        Assert.Equal(new long[] { 26, 52, 78 }, Budgets(20));
    }

    [Fact]
    public void ByLevel_ABudgetNeverGoesBelowZero()
    {
        var rules = ByLevel();
        rules.Bands[0].PerCharacter = 5;

        var rating = Rate(rules, Party(1, 3), Worth(0));

        // 10 - 3 x 5 would be -5.
        Assert.Equal(new long[] { 0, 14, 21 }, rating.Budgets.Select(b => b.Budget));
    }

    [Fact]
    public void ByLevel_ABandThatWouldCrossTheOneBefore_IsHeldAtItsBudget_ByARunningMaximum()
    {
        // The loader refuses this block; if one got here anyway, the readout must not put a bigger total in an easier band.
        var rules = ByLevel();
        rules.Bands[0].Value = 20;
        rules.Bands[0].PerCharacter = 10;
        rules.Bands[1].Value = 30;
        rules.Bands[1].PerCharacter = 2;

        var big = Rate(rules, Party(20, 3), Worth(0)).Budgets.Select(b => b.Budget).ToArray();

        // Band 0 is 20 + 16 x 10 = 180; band 1 would be 62, band 2 is 30 + 48 = 78.
        Assert.Equal(new long[] { 180, 180, 180 }, big);
    }

    [Fact]
    public void Sum_ALevelBetweenTwoRows_TakesTheRowBelow()
    {
        var rules = Sum();
        rules.Budget.RemoveAt(1);

        var rating = Rate(rules, Party(4, 2), Worth(10));

        // Rows 1 and 3 are left; level 2 reads level 1's 10/20/30, four times.
        Assert.Equal(new long[] { 40, 80, 120 }, rating.Budgets.Select(b => b.Budget));
    }

    [Fact]
    public void ANegativeValue_IsNamedAndNotSummed()
    {
        var rating = Rate(Sum(), Party(4, 2), new RatedLine("Rat", 2, 7), new RatedLine("Glitch", 1, -5));

        Assert.Equal(14, rating.Total);
        var skipped = Assert.Single(rating.NotCounted);
        Assert.Equal(("Glitch", "Encounters.Difficulty.Negative"), (skipped.Name, skipped.Reason.Key));
    }

    [Fact]
    public void ByLevel_ACreatureIsWorthWhatItsLevelMinusThePartysSays()
    {
        // Party level 3: tier 1 is two below (worth 1) up to tier 5 two above (worth 5).
        double Total(double tier) => Rate(ByLevel(), Party(4, 3), new RatedLine("C", 1, tier)).Total;

        Assert.Equal(new double[] { 1, 2, 3, 4, 5 }, new[] { 1.0, 2, 3, 4, 5 }.Select(Total));
        Assert.Equal(12, Rate(ByLevel(), Party(4, 3), new RatedLine("C", 4, 3)).Total);
    }

    // One creature of each tier listed, for a party of level 3: a creature of tier N is worth N (tier 1 is two below the party, tier 5 two above).
    [Theory]
    [InlineData(new[] { 5, 4 }, 9, BandPosition.Below, "easy")]
    [InlineData(new[] { 5, 5 }, 10, BandPosition.In, "easy")]
    [InlineData(new[] { 5, 5, 5, 4 }, 19, BandPosition.In, "easy")]
    [InlineData(new[] { 5, 5, 5, 5 }, 20, BandPosition.In, "fair")]
    [InlineData(new[] { 5, 5, 5, 5, 5, 4 }, 29, BandPosition.In, "fair")]
    [InlineData(new[] { 5, 5, 5, 5, 5, 5 }, 30, BandPosition.In, "hard")]
    [InlineData(new[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }, 50, BandPosition.In, "hard")]
    public void ByLevel_AtLeast_NamesTheLastBandTheTotalReaches_AndBelowTheFirstBeforeThat(int[] tiers, int total, BandPosition position, string band)
    {
        var rating = Rate(ByLevel(), Party(4, 3), tiers.Select(t => new RatedLine("C", 1, t)).ToArray());

        Assert.Equal(total, rating.Total);
        Assert.Equal(position, rating.Position);
        Assert.Equal(band, rating.Band!.Key);
    }

    [Fact]
    public void ByLevel_ACreatureTooFarFromThePartyForTheTable_IsNamedAndTheRestIsStillRated()
    {
        var rating = Rate(ByLevel(), Party(4, 3), new RatedLine("Dragon", 1, 6), new RatedLine("Mouse", 1, 0), new RatedLine("Half", 1, 3.5), new RatedLine("Guard", 4, 3));

        Assert.Equal(12, rating.Total);
        Assert.Equal("easy", rating.Band!.Key);
        Assert.Equal(
            new[] { ("Dragon", "Encounters.Difficulty.TooFarAbove"), ("Mouse", "Encounters.Difficulty.TooFarBelow"), ("Half", "Encounters.Difficulty.NotWhole") },
            rating.NotCounted.Select(n => (n.Name, n.Reason.Key)));
        Assert.Equal(3, rating.NotCounted.Count);
    }

    [Fact]
    public void ByLevel_ThePartyLevelIsTheOneMostCharactersHave_AndATieTakesTheLower()
    {
        var tie = Rate(ByLevel(), new[] { 3, 3, 2, 2 }, new RatedLine("C", 1, 4));
        var most = Rate(ByLevel(), new[] { 2, 3, 3, 4 }, new RatedLine("C", 1, 4));

        // Tied at 2 and 3: level 2, so a tier 4 creature is two above (5), not one (4).
        Assert.Equal(2, tie.PartyLevel);
        Assert.Equal(5, tie.Total);
        Assert.True(tie.LevelsVary);
        Assert.Equal(3, most.PartyLevel);
        Assert.Equal(4, most.Total);
        Assert.Equal(2, most.MinLevel);
        Assert.Equal(4, most.MaxLevel);
        Assert.Null(tie.Unit);
        Assert.True(tie.ByLevel);
    }

    [Fact]
    public void TheBandList_SaysUpTo_ForAtMost_AndFrom_ForAtLeast_InEachLanguage()
    {
        var atMost = Rate(Sum(), Party(4, 2), Worth(10));
        var atLeast = Rate(ByLevel(), Party(4, 3), Worth(0));

        Assert.Equal("Low up to 60 · Moderate up to 120 · High up to 180", Codex.Web.Services.DifficultyText.Budgets(atMost, Codex.Localization.Localizer.For("en")));
        Assert.Equal("Easy from 10 · Fair from 20 · Hard from 30", Codex.Web.Services.DifficultyText.Budgets(atLeast, Codex.Localization.Localizer.For("en")));
        Assert.Equal("Easy: от 10 · Fair: от 20 · Hard: от 30", Codex.Web.Services.DifficultyText.Budgets(atLeast, Codex.Localization.Localizer.For("ru")));
    }

    [Fact]
    public void Breaking_TheBandPick_ByOneStep_WouldFailTheEdgeCases()
    {
        // The pairs below only hold if the comparison is "budget >= total" for atMost and "budget <= total" for atLeast: a "<" or an off-by-one swaps one of them.
        Assert.Equal("low", Rate(Sum(), Party(4, 2), Worth(60)).Band!.Key);
        Assert.Equal("moderate", Rate(Sum(), Party(4, 2), Worth(60.5)).Band!.Key);
        // 20 reaches fair (20); 19 does not.
        Assert.Equal("fair", Rate(ByLevel(), Party(4, 3), new RatedLine("C", 5, 3), new RatedLine("D", 1, 5)).Band!.Key);
        Assert.Equal("easy", Rate(ByLevel(), Party(4, 3), new RatedLine("C", 5, 3), new RatedLine("D", 1, 4)).Band!.Key);
    }
}
