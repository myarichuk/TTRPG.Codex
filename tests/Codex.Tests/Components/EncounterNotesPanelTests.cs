using Bunit;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Codex.Tests.Components;

/// <summary>The notes of the prepared encounter the table is running (ENCOUNTER_BUILDER_DESIGN.md 4.2): the game master's, folded, and nothing at all for anyone else.</summary>
public class EncounterNotesPanelTests : ComponentTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private IRenderedComponent<EncounterNotesPanel> Draw(EncounterWorld world, CampaignAccess access, string? planId)
    {
        Services.AddSingleton(world.Service);
        Services.AddSingleton(Localizer.For("en"));
        return Render<EncounterNotesPanel>(p => p
            .Add(x => x.Campaign, world.Campaign)
            .Add(x => x.Access, access)
            .Add(x => x.PreparedEncounterId, planId));
    }

    [Fact]
    public async Task AGameMaster_SeesTheNotesFolded_WithTheLineNotesAndALinkToThePlan()
    {
        var world = new EncounterWorld();
        var line = EncounterWorld.Bandits(2);
        line.Note = "waits behind the door";
        var plan = world.Plan("Ambush", "cellar", line);
        plan.Notes = "They strike when the lamp goes out.";
        Assert.True((await world.Service.SaveAsync(world.Campaign, world.Gm, plan)).Saved);

        var cut = Draw(world, world.Gm, plan.Id);

        cut.WaitForState(() => cut.FindAll(Id("encounter-notes")).Count > 0, Patience);
        Assert.Null(cut.Find(Id("encounter-notes") + " details").GetAttribute("open"));
        Assert.Contains("They strike when the lamp goes out.", cut.Find(Id("encounter-notes-text")).TextContent);
        Assert.Contains("Bandit: waits behind the door", cut.Find(Id("encounter-notes-lines")).TextContent);
        Assert.Equal("/campaigns/c1/encounters/" + PreparedEncounterService.RouteOf(plan.Id), cut.Find(Id("encounter-notes-plan")).GetAttribute("href"));
    }

    [Fact]
    public async Task TheHeadingIsOutsideTheFold_AndTheNotesAreReadAgainWhenTheFoldMoves()
    {
        var world = new EncounterWorld();
        var plan = world.Plan("Ambush", "cellar", EncounterWorld.Bandits(2));
        plan.Notes = "First tactics.";
        Assert.True((await world.Service.SaveAsync(world.Campaign, world.Gm, plan)).Saved);
        var cut = Draw(world, world.Gm, plan.Id);
        cut.WaitForState(() => cut.FindAll(Id("encounter-notes-text")).Count > 0, Patience);
        Assert.Empty(cut.FindAll("summary h3"));
        Assert.Equal("Encounter notes", cut.Find("#encounter-notes-heading").TextContent);
        Assert.Contains("First tactics.", cut.Find(Id("encounter-notes-text")).TextContent);

        // The GM edited the notes after the start; opening the fold shows them.
        world.Plans.Held[plan.Id].Notes = "Second tactics.";
        await cut.Find("details").TriggerEventAsync("ontoggle", EventArgs.Empty);

        cut.WaitForAssertion(() => Assert.Contains("Second tactics.", cut.Find(Id("encounter-notes-text")).TextContent), Patience);
    }

    [Fact]
    public async Task APlayersAccess_DrawsNothing_AndTheServiceIsNotAsked()
    {
        var plans = Substitute.For<IPreparedEncounterRepository>();
        var world = new EncounterWorld(plans);

        var cut = Draw(world, world.Player, "preparedencounters/x");
        await Task.Delay(100);

        Assert.Empty(cut.FindAll(Id("encounter-notes")));
        Assert.Equal(string.Empty, cut.Markup.Trim());
        await plans.DidNotReceiveWithAnyArgs().GetAsync(default!, default!);
    }

    [Fact]
    public async Task WithNoPlanId_NothingIsDrawn_AndNothingIsRead()
    {
        var plans = Substitute.For<IPreparedEncounterRepository>();
        var world = new EncounterWorld(plans);

        var cut = Draw(world, world.Gm, null);
        await Task.Delay(100);

        Assert.Empty(cut.FindAll(Id("encounter-notes")));
        await plans.DidNotReceiveWithAnyArgs().GetAsync(default!, default!);
    }
}
