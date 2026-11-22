using Codex.ContentImport.Degrees;

namespace Codex.Tests;

/// <summary>The counter a class feature's own sentences give (<see cref="EntriesStage.DailyCounter"/>), read from invented sentences of the same shape.</summary>
public class DegreesFeatureCounterTests
{
    [Fact]
    public void ACountThatGrowsAtNamedLevels_BecomesAMaximumRefilledAtTheDailyPreparations()
    {
        const string Text = "You gain 3 additional slots each day. At 4th level, the number of additional slots increases to 4, and at 12th level, the total number of additional slots increases to 7.";
        Assert.Equal("{ key: spareSlots, max: \"level >= 12 ? 7 : level >= 4 ? 4 : 3\", recovers: [{ on: dailyPrep }] }", EntriesStage.DailyCounter("Spare Slots", Text));
    }

    [Fact]
    public void TwoEqualParagraphs_GiveTheSameCounter_ButDifferentOnesGiveNone()
    {
        const string Same = "You gain 2 additional slots each day. At 6th level, the number of additional slots increases to 3.";
        Assert.NotNull(EntriesStage.DailyCounter("Twin", "Red: " + Same + "\nBlue: " + Same));
        Assert.Null(EntriesStage.DailyCounter("Twin", "Red: " + Same + "\nBlue: You gain 2 additional slots each day. At 6th level, the number of additional slots increases to 4."));
    }

    [Fact]
    public void ATextThatIsNotThatShape_GivesNoCounter()
    {
        Assert.Null(EntriesStage.DailyCounter("A", "You gain 2 additional slots each day."));
        Assert.Null(EntriesStage.DailyCounter("A", "You gain 2 additional slots. At 6th level, the number of additional slots increases to 3."));
        Assert.Null(EntriesStage.DailyCounter("A", "You gain 4 additional slots each day. At 6th level, the number of additional slots increases to 3."));
        Assert.Null(EntriesStage.DailyCounter("A", "You gain 2 additional slots each day. At 6th level, the extra damage increases to 3."));
        Assert.Null(EntriesStage.DailyCounter("A", "Your proficiency rank for Perception increases to expert."));
        // A step about another noun is not a step of this count.
        Assert.Null(EntriesStage.DailyCounter("A", "You gain 2 additional slots each day. At 6th level, the number of additional points increases to 3."));
        // Only a whole word matches (lots is not slots).
        Assert.Null(EntriesStage.DailyCounter("A", "You gain 2 additional slots each day. At 6th level, the number of additional lots increases to 3."));
        // Levels that do not climb.
        Assert.Null(EntriesStage.DailyCounter("A", "You gain 2 additional slots each day. At 6th level, the number of additional slots increases to 3, and at 3rd level, the number of additional slots increases to 4."));
    }
}
