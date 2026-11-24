using Bunit;
using Codex.Core.WorldBuilder;
using Codex.Web.Components.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Codex.Tests.Components;

/// <summary>How the world builder's review panel shows a prepared encounter (ENCOUNTER_BUILDER_DESIGN.md 6 and 17): a new one, a change to one, and a delete, which arrives unticked with a warning.</summary>
public class WorldProposalEncounterTests : ComponentTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private const string PlanKey = "existing:encounter:6f1c7a5e-2b0d-4f43-9d0e-5b1f4a7c9d10";

    private static WorldBundle Proposal()
    {
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft
        {
            Ref = "g", Kind = DraftKinds.Edit, Target = PlanKey, Name = "Wolves", OutlineName = "Wolves", PlaceGiven = true, PlaceName = null, BeforePlace = "Cellar",
            LineupGiven = true, Lineup = { new DraftLine { Entry = "monster/wolf", Type = "monster", Id = "wolf", Count = 2, Name = "Wolf" } }, BeforeLineup = "Wolf ×4",
            Status = "ready", BeforeStatus = "draft", Body = "They circle the camp.", Before = "They wait."
        });
        bundle.Drafts.Add(new WorldDraft { Ref = "d", Kind = DraftKinds.Edit, Target = PlanKey, Name = "Ambush", OutlineName = "Ambush", Remove = true, Selected = false });
        bundle.Drafts.Add(new WorldDraft
        {
            Ref = "f", Kind = DraftKinds.Encounter, Name = "Ambush in the cellar", OutlineName = "Ambush in the cellar", Place = "c", PlaceName = "Wine cellar", Status = "ready",
            Body = "They want the key.", Tags = { "grey-coast" }, Readout = "Moderate: 250 XP for 4 characters of level 3", PlaceHazards = { "Spiked pit, Cellar" },
            Lineup =
            {
                new DraftLine { Entry = "monster/bandit", Type = "monster", Id = "bandit", Count = 3, Name = "Bandit", Note = "behind the casks" },
                new DraftLine { Entry = "h", Ref = "h", Type = "monster", Homebrew = true, Count = 1, Name = "Cask mimic", Hidden = true }
            }
        });
        return bundle;
    }

    private IRenderedComponent<WorldProposalPanel> Draw(WorldBundle bundle, string culture = "en")
    {
        Services.AddSingleton(Codex.Localization.Localizer.For(culture));
        return Render<WorldProposalPanel>(p => p.Add(x => x.Proposal, bundle));
    }

    private void Open(IRenderedComponent<WorldProposalPanel> cut, string name)
    {
        var index = cut.FindAll(Id("world-proposal-entry")).ToList().FindIndex(e => e.QuerySelector(".wb-entry-name")!.TextContent == name);
        cut.FindAll(Id("world-proposal-open"))[index].Click();
        cut.WaitForState(() => cut.FindAll(Id("world-reader")).Count == 1, Patience);
    }

    [Fact]
    public void TheListShowsTheEncounterWithItsPlaceAndLineup_AndTheDeleteUnticked()
    {
        var cut = Draw(Proposal());

        var entries = cut.FindAll(Id("world-proposal-entry"));
        Assert.Equal(3, entries.Count);
        Assert.Contains("Encounters", cut.Markup);
        var encounter = entries.Single(e => e.QuerySelector(".wb-entry-name")!.TextContent == "Ambush in the cellar");
        Assert.Contains("Set in Wine cellar", encounter.QuerySelector(".wb-entry-detail")!.TextContent);
        Assert.Equal("Bandit ×3, Cask mimic", encounter.QuerySelector(".wb-entry-pitch")!.TextContent);
        var ticks = entries.ToDictionary(e => e.QuerySelector(".wb-entry-name")!.TextContent, e => e.QuerySelector(Id("world-proposal-tick"))!.HasAttribute("checked"));
        Assert.True(ticks["Ambush in the cellar"]);
        Assert.True(ticks["Wolves"]);
        Assert.False(ticks["Ambush"]); // the delete is the game master's to tick
    }

    [Fact]
    public void AnEncounterEntry_ShowsItsPlace_Lineup_Notes_Status_Readout_AndTheHazardsAtItsPlace()
    {
        var cut = Draw(Proposal());

        Open(cut, "Ambush in the cellar");

        Assert.Contains("Set in Wine cellar", cut.Find(Id("world-encounter-place")).TextContent);
        var lineup = cut.FindAll(Id("world-encounter-lineup") + " li").Select(l => l.TextContent.Trim()).ToList();
        Assert.Equal(2, lineup.Count);
        Assert.Contains("Bandit", lineup[0]);
        Assert.Contains("×3", lineup[0]);
        Assert.Contains("behind the casks", lineup[0]);
        Assert.Contains("Cask mimic", lineup[1]);
        Assert.Contains("starts hidden", lineup[1]);
        Assert.Equal("Status: Ready", cut.Find(Id("world-encounter-status")).TextContent);
        Assert.Contains("Difficulty: Moderate: 250 XP for 4 characters of level 3", cut.Find(Id("world-encounter-readout")).TextContent);
        Assert.Contains("Spiked pit, Cellar", cut.Find(Id("world-encounter-hazards")).TextContent);
        Assert.Contains("They want the key.", cut.Find(Id("world-review-body")).TextContent);
        Assert.Contains("grey-coast", cut.Markup);
        Assert.Empty(cut.FindAll(Id("world-proposal-delete")));
    }

    [Fact]
    public void AnEdit_ShowsWhatItChanges_NotWhatItLeavesAlone()
    {
        var cut = Draw(Proposal());

        Open(cut, "Wolves");

        Assert.Contains("Place: changes from Cellar to No place", cut.Find(Id("world-encounter-place")).TextContent);
        Assert.Contains("Now: Wolf ×4", cut.Find(Id("world-encounter-before")).TextContent);
        Assert.Contains("Wolf", cut.Find(Id("world-encounter-lineup")).TextContent);
        Assert.Equal("Status: changes from Draft to Ready", cut.Find(Id("world-encounter-status")).TextContent);
        Assert.DoesNotContain("→", cut.Find(Id("world-encounter-place")).TextContent + cut.Find(Id("world-encounter-status")).TextContent);
        Assert.NotEmpty(cut.FindAll(Id("world-diff")));
        Assert.Empty(cut.FindAll(Id("world-proposal-delete")));
    }

    [Fact]
    public void AnEditThatOnlyRenames_ShowsNoLineupPlaceOrStatus()
    {
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "r", Kind = DraftKinds.Edit, Target = PlanKey, Name = "Ambush", OutlineName = "Ambush", Rename = "Dusk ambush" });
        var cut = Draw(bundle);

        Open(cut, "Ambush");

        Assert.Contains("Renamed to “Dusk ambush”", cut.Markup);
        Assert.Empty(cut.FindAll(Id("world-encounter-place")));
        Assert.Empty(cut.FindAll(Id("world-encounter-lineup")));
        Assert.Empty(cut.FindAll(Id("world-encounter-status")));
    }

    [Fact]
    public void ADelete_IsShownUnticked_WithTheWarningThatItCannotBeUndone()
    {
        var cut = Draw(Proposal());

        Open(cut, "Ambush");

        Assert.Equal("Deletes the encounter “Ambush”. This cannot be undone.", cut.Find(Id("world-proposal-delete")).TextContent.Trim());
        Assert.Empty(cut.FindAll(Id("world-encounter-lineup")));
        Assert.Contains("review-state-Skipped", cut.Markup);
    }

    [Theory]
    [InlineData("ru", "Удаляет столкновение «Ambush». Это нельзя отменить.")]
    [InlineData("he", "מוחק את מפגש הקרב „Ambush”. אי אפשר לבטל את זה.")]
    public void TheDeleteWarning_SpeaksTheReadersLanguage(string culture, string text)
    {
        var cut = Draw(Proposal(), culture);

        Open(cut, "Ambush");

        Assert.Equal(text, cut.Find(Id("world-proposal-delete")).TextContent.Trim());
    }
}
