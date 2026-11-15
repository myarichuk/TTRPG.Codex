using Codex.Persistence;

namespace Codex.Tests;

/// <summary>What the place form writes back for a placed hazard (<see cref="PlacedHazardForm"/>), and how a pack entry and a homebrew one with the same id differ.</summary>
public class PlacedHazardFormTests
{
    private static PlacedHazard Pit(HazardState state = HazardState.Noticed, int successes = 1, bool homebrew = false, string? levels = null) => new()
    {
        Entry = "hazard/spiked-pit", IsHomebrew = homebrew, State = state, Successes = successes, NoticedBy = { "ana" }, Levels = levels
    };

    [Fact]
    public void AnUntouchedHazard_KeepsWhatIsStoredNow_NotWhatTheFormOpenedWith()
    {
        var form = new PlacedHazardForm(Pit(HazardState.Hidden, 0));
        var movedOnByTheTable = Pit(HazardState.Disabled, 2);

        var saved = form.Result(movedOnByTheTable);

        Assert.Same(movedOnByTheTable, saved);
        Assert.False(form.Changed);
    }

    [Fact]
    public void ChangingTheState_KeepsTheCountsOfTheSameEntry()
    {
        var form = new PlacedHazardForm(Pit());
        form.State = HazardState.Triggered;

        var saved = form.Result(Pit(HazardState.Noticed, 3))!;

        Assert.Equal(HazardState.Triggered, saved.State);
        Assert.Equal(3, saved.Successes);
        Assert.Equal(new[] { "ana" }, saved.NoticedBy);
    }

    [Fact]
    public void ChangingTheState_KeepsTheFoundRoutesOfTheSameEntry_AndAnotherEntryHasNone()
    {
        var stored = Pit(HazardState.Noticed, 1);
        stored.Found.Add("lid");
        var same = new PlacedHazardForm(Pit()) { State = HazardState.Hidden };
        var other = new PlacedHazardForm(Pit()) { Key = "hazard/falling-net" };

        Assert.Equal(new[] { "lid" }, same.Result(stored)!.Found);
        Assert.Empty(other.Result(stored)!.Found);
        Assert.Equal(new[] { "lid" }, PlacedHazard.Tidy(new PlacedHazard { Entry = "hazard/a", Found = { " lid ", "", "lid" } })!.Found);
    }

    [Fact]
    public void ChoosingAnotherHazard_StartsOver_StateIncluded()
    {
        var form = new PlacedHazardForm(Pit(HazardState.Disabled, 2));
        form.Key = "hazard/falling-net";

        var saved = form.Result(Pit(HazardState.Disabled, 2))!;

        Assert.Equal("hazard/falling-net", saved.Entry);
        Assert.Equal(HazardState.Hidden, saved.State);
        Assert.Equal(0, saved.Successes);
        Assert.Empty(saved.NoticedBy);
        Assert.Null(saved.Levels);
    }

    [Fact]
    public void ChoosingTheOriginalHazardAgain_PutsItsStateBack()
    {
        var form = new PlacedHazardForm(Pit(HazardState.Disabled, 2, levels: "5-10"));
        form.Key = "hazard/falling-net";
        form.Key = "hazard/spiked-pit";

        Assert.Equal(HazardState.Disabled, form.State);
        Assert.Equal("5-10", form.Levels);
        Assert.False(form.Changed);
    }

    [Fact]
    public void APackEntryAndAHomebrewOneWithTheSameId_AreTwoChoices()
    {
        var form = new PlacedHazardForm(Pit());
        form.Key = PlacedHazardForm.KeyOf(isHomebrew: true, "hazard/spiked-pit");

        var saved = form.Result(Pit())!;

        Assert.True(saved.IsHomebrew);
        Assert.Equal("hazard/spiked-pit", saved.Entry);
        Assert.Equal(0, saved.Successes);
        Assert.NotEqual(PlacedHazardForm.KeyOf(Pit()), PlacedHazardForm.KeyOf(Pit(homebrew: true)));
    }

    [Fact]
    public void ThePickedScaledVersion_IsStored_AndNoneClearsIt()
    {
        var form = new PlacedHazardForm(Pit());
        form.Levels = "11-16";
        Assert.Equal("11-16", form.Result(Pit())!.Levels);

        var scaled = new PlacedHazardForm(Pit(levels: "11-16"));
        scaled.Levels = "";
        Assert.Null(scaled.Result(Pit(levels: "11-16"))!.Levels);
    }

    [Fact]
    public void ClearingTheHazard_SavesNone_AndAnOldPlacementReadsAsAPackEntryWithNoVersion()
    {
        var form = new PlacedHazardForm(Pit());
        form.Key = string.Empty;

        Assert.Null(form.Result(Pit()));
        var old = new PlacedHazard { Entry = "hazard/spiked-pit" };
        Assert.False(old.IsHomebrew);
        Assert.Null(old.Levels);
    }
}
