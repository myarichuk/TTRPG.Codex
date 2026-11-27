using Codex.Core.Models;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using NSubstitute;

namespace Codex.Tests;

/// <summary>Complex hazards in a prepared encounter (ENCOUNTER_BUILDER_DESIGN.md 18), without a table: which placed hazards would join, what the editor's Hazards group lists and what a
/// save does to a hazard added there (it is placed on the plan's place first, or nothing is saved). The world: a cellar with a simple pit, a vault inside it with a press (complex) that already
/// has an actor at the table, and an empty hall; the Library also has a blade (complex) and a homebrew lever (complex).</summary>
public class EncounterHazardJoinTests
{
    private readonly EncounterWorld _w = new();

    public EncounterHazardJoinTests()
    {
        _w.Registry.RegisterEntry(new ContentEntry { Id = "press", Type = "hazard", SystemId = "demo", PackId = "base", Name = "Crushing press", Properties = { ["complex"] = true, ["initiative"] = 5L } }, 0);
        _w.Registry.RegisterEntry(new ContentEntry { Id = "blade", Type = "hazard", SystemId = "demo", PackId = "base", Name = "Swinging blade", Properties = { ["complex"] = true, ["initiative"] = 3L } }, 0);
        _w.Brews.Add(new HomebrewEntryDocument { CampaignId = "c1", SystemId = "demo", Type = "hazard", EntryId = "lever", Name = "Odd lever", Properties = { ["complex"] = true } });
        _w.Region.Locations.Single(l => l.Id == "vault").Feature!.Hazard!.Entry = "hazard/press";
        // The actor of the rolling stone in the shared world becomes the press's: it stands for the vault's placement.
        _w.Actors.Single(a => a.Id == "stone-1").Hazard!.Entry = "hazard/press";
        _w.RegionRepo.GetAsync("r1", Arg.Any<CampaignAccess>()).Returns(ci => ci.Arg<CampaignAccess>().IsDm ? _w.Region : null);
        _w.StubPlacing();
    }

    private LocationDocument Place(string id) => _w.Region.Locations.Single(l => l.Id == id);

    private async Task<IReadOnlyList<string>> JoiningAsync(PreparedEncounterDocument plan) => await _w.Service.JoiningHazardsAsync(_w.Campaign, _w.Gm, plan);

    // ---- Which hazards join ----

    [Fact]
    public async Task ThePlacedComplexHazards_AtThePlaceAndUnderIt_JoinUnlessLeftOutDisarmedSetOffOrAlreadyAtTheTable()
    {
        // The vault's press has an actor at the table (the world's stone-1), so only a hazard with none counts; the cellar's pit is simple.
        _w.Actors.RemoveAll(a => a.Id == "stone-1");
        var plan = _w.Plan("Ambush", "cellar");
        Assert.Equal(new[] { "Crushing press" }, await JoiningAsync(plan));

        // A hazard placed later on the cellar joins as well: the default is on.
        Place("hall").Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/blade" } };
        Place("cellar").Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/blade" } };
        Assert.Equal(new[] { "Swinging blade", "Crushing press" }, await JoiningAsync(plan));

        plan.HazardsLeftOut.Add(PreparedEncounterDocument.HazardKey("r1", "cellar"));
        Assert.Equal(new[] { "Crushing press" }, await JoiningAsync(plan));

        Place("vault").Feature!.Hazard!.State = HazardState.Disabled;
        Assert.Empty(await JoiningAsync(plan));
        Place("vault").Feature!.Hazard!.State = HazardState.Triggered;
        Assert.Empty(await JoiningAsync(plan));
        Place("vault").Feature!.Hazard!.State = HazardState.Noticed;
        Assert.Equal(new[] { "Crushing press" }, await JoiningAsync(plan));

        // The hall's own blade never joined the cellar's (above), and a plan with no place joins none.
        Assert.Empty(await JoiningAsync(_w.Plan("Nowhere", null)));
    }

    [Fact]
    public async Task AHazardWithAnActorAtTheTable_DoesNotJoinAgain_UntilThatActorIsRetired()
    {
        var plan = _w.Plan("Ambush", "cellar");
        Assert.Empty(await JoiningAsync(plan));

        _w.Actors.Single(a => a.Id == "stone-1").Retired = true;
        Assert.Equal(new[] { "Crushing press" }, await JoiningAsync(plan));
    }

    [Fact]
    public async Task AHomebrewComplexHazard_JoinsAsWellAsALibraryOne()
    {
        _w.Actors.RemoveAll(a => a.Id == "stone-1");
        Place("vault").Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/lever", IsHomebrew = true } };

        Assert.Equal(new[] { "Odd lever" }, await JoiningAsync(_w.Plan("Ambush", "vault")));
    }

    [Fact]
    public async Task ThePlayer_IsToldNothing()
    {
        Assert.Empty(await _w.Service.JoiningHazardsAsync(_w.Campaign, _w.Player, _w.Plan("Ambush", "cellar")));
        Assert.Empty(await _w.Service.SearchHazardsAsync(_w.Campaign, _w.Player, ""));
    }

    // ---- The Hazards group of the add search ----

    [Fact]
    public async Task TheHazardsSearch_ListsTheLibrarysAndHomebrewsComplexHazards_NeverASimpleOneNorACreature_AndTheCreatureSearchNeverListsOne()
    {
        var hazards = await _w.Service.SearchHazardsAsync(_w.Campaign, _w.Gm, "");

        // The pit and the rolling stone of the shared world have no complex field: simple. Creatures are not hazards.
        Assert.Equal(new[] { "Odd lever", "Crushing press", "Swinging blade" }.Order(), hazards.Select(h => h.Entry.Name).Order());
        Assert.Contains(hazards, h => h.IsHomebrew && h.Entry.Id == "lever");
        Assert.DoesNotContain(hazards, h => h.Entry.Type == "monster");
        Assert.Equal(new[] { "Swinging blade" }, (await _w.Service.SearchHazardsAsync(_w.Campaign, _w.Gm, "blade")).Select(h => h.Entry.Name));

        var creatures = (await _w.Service.SearchCreaturesAsync(_w.Campaign, _w.Gm, "")).Select(c => c.Entry.Name);
        Assert.DoesNotContain("Swinging blade", creatures);
        Assert.DoesNotContain("Crushing press", creatures);
        Assert.DoesNotContain("Odd lever", creatures);
    }

    // ---- Adding one: it is placed when the plan is saved ----

    [Fact]
    public async Task SavingWithAHazard_PlacesItOnThePlansPlaceFirst_ThenStoresThePlan()
    {
        var plan = _w.Plan("Ambush", "hall", EncounterWorld.Bandits(1));

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan, new HazardToPlace("hazard/blade", false));

        Assert.True(result.Saved, string.Join(",", result.Check.Errors.Select(e => e.Key)));
        var placed = Place("hall").Feature!.Hazard!;
        Assert.Equal(("hazard/blade", false, HazardState.Hidden), (placed.Entry, placed.IsHomebrew, placed.State));
        Assert.Single(_w.Plans.Held);
        Assert.DoesNotContain(_w.Plans.Held.Values.Single().Lines, l => l.Kind == PreparedLineKind.Hazard);
        // A patch of the place's hazard alone, not a rewrite of the place.
        Assert.Equal(1, _w.PlacingCalls);
        await _w.RegionRepo.DidNotReceive().UpsertLocationAsync(Arg.Any<string>(), Arg.Any<LocationDocument>(), Arg.Any<CampaignAccess>());
        // And it joins from now on.
        Assert.Equal(new[] { "Swinging blade" }, await JoiningAsync(plan));
    }

    [Fact]
    public async Task APlaceHoldsOneHazard_ASecondIsRefused_AndNothingIsSaved()
    {
        var plan = _w.Plan("Ambush", "cellar", EncounterWorld.Bandits(1));

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan, new HazardToPlace("hazard/blade", false));

        Assert.False(result.Saved);
        Assert.Equal(new[] { "Encounters.Error.HazardTaken" }, result.Check.Errors.Select(e => e.Key));
        Assert.Empty(_w.Plans.Held);
        Assert.Equal("hazard/spiked-pit", Place("cellar").Feature!.Hazard!.Entry);
        Assert.Equal(0, _w.PlacingCalls);
    }

    [Theory]
    [InlineData("hazard/spiked-pit", false, "Encounters.Error.HazardNotComplex")]
    [InlineData("hazard/nothing-here", false, "Encounters.Error.HazardGone")]
    [InlineData("monster/bandit", false, "Encounters.Error.HazardGone")]
    [InlineData("lever", false, "Encounters.Error.HazardGone")]
    public async Task AHazardThatIsSimpleGoneOrNotOne_IsRefused_AndNothingIsPlacedOrSaved(string entry, bool homebrew, string key)
    {
        var plan = _w.Plan("Ambush", "hall", EncounterWorld.Bandits(1));

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan, new HazardToPlace(entry, homebrew));

        Assert.False(result.Saved);
        Assert.Equal(new[] { key }, result.Check.Errors.Select(e => e.Key));
        Assert.Null(Place("hall").Feature);
        Assert.Empty(_w.Plans.Held);
    }

    [Fact]
    public async Task AHazardForAPlanWithNoPlace_OrOnAPlaceThatIsGone_IsRefused()
    {
        var none = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, _w.Plan("Roadside", null, EncounterWorld.Bandits(1)), new HazardToPlace("hazard/blade", false));
        Assert.Equal(new[] { "Encounters.Error.HazardNoPlace" }, none.Check.Errors.Select(e => e.Key));

        var plan = _w.Plan("Ambush", "hall", EncounterWorld.Bandits(1));
        _w.Region.Locations.RemoveAll(l => l.Id == "hall");
        var gone = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan, new HazardToPlace("hazard/blade", false));
        // The check's warning says the place is gone; placing is refused, and a refused placing saves nothing.
        Assert.False(gone.Saved);
        Assert.Empty(_w.Plans.Held);
    }

    [Fact]
    public async Task ARefusedPlacing_SavesNothing_AndAPlayerCannotPlaceOrSave()
    {
        _w.RegionRepo.PlaceHazardIfNoneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PlacedHazard>(), Arg.Any<CampaignAccess>()).Returns(HazardPlacing.Refused);

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, _w.Plan("Ambush", "hall", EncounterWorld.Bandits(1)), new HazardToPlace("hazard/blade", false));
        Assert.Equal(new[] { "Encounters.Error.HazardPlaceGone" }, result.Check.Errors.Select(e => e.Key));
        Assert.Empty(_w.Plans.Held);

        var player = await _w.Service.SaveAsync(_w.Campaign, _w.Player, _w.Plan("Ambush", "hall", EncounterWorld.Bandits(1)), new HazardToPlace("hazard/blade", false));
        Assert.False(player.Saved);
        await _w.RegionRepo.DidNotReceive().PlaceHazardIfNoneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PlacedHazard>(), Arg.Is<CampaignAccess>(a => !a.IsDm));
    }

    [Fact]
    public async Task WhenThePlansSaveFailsAfterThePlacing_TheResultSaysTheHazardIsPlaced_AndARetryGoesThrough()
    {
        // A plan that was deleted in another tab: its id is set but nothing is stored, so the plan's own save is refused after the hazard was placed.
        var plan = _w.Plan("Ambush", "hall", EncounterWorld.Bandits(1));
        plan.Id = PreparedEncounterDocument.IdPrefix + "deleted-elsewhere";

        var first = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan, new HazardToPlace("hazard/blade", false));

        Assert.False(first.Saved);
        Assert.True(first.HazardPlaced);
        Assert.Equal(new[] { "Encounters.Error.NotSavedHazardPlaced" }, first.Check.Errors.Select(e => e.Key));
        Assert.Equal("hazard/blade", Place("hall").Feature!.Hazard!.Entry);

        // The same hazard again (a page that did not hear about it) is not an error: it is already there, and the plan is stored this time.
        var retry = _w.Plan("Ambush", "hall", EncounterWorld.Bandits(1));
        var second = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, retry, new HazardToPlace("hazard/blade", false));
        Assert.True(second.Saved, string.Join(",", second.Check.Errors.Select(e => e.Key)));
        Assert.False(second.HazardPlaced);
        Assert.Equal(1, _w.PlacingCalls);
        // A different hazard on that place is still refused.
        var other = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, _w.Plan("Other", "hall", EncounterWorld.Bandits(1)), new HazardToPlace("hazard/press", false));
        Assert.Equal(new[] { "Encounters.Error.HazardTaken" }, other.Check.Errors.Select(e => e.Key));
    }

    [Fact]
    public async Task APlanSaveThatFailsWithNoHazard_IsTheOldMessage_AndNotPlaced()
    {
        var plan = _w.Plan("Ambush", "hall", EncounterWorld.Bandits(1));
        plan.Id = PreparedEncounterDocument.IdPrefix + "deleted-elsewhere";

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan);

        Assert.Equal(new[] { "Encounters.Error.NotSaved" }, result.Check.Errors.Select(e => e.Key));
        Assert.False(result.HazardPlaced);
    }

    // ---- The tick is stored, and keys that mean nothing now are dropped quietly ----

    [Fact]
    public async Task ASave_KeepsTheKeysOfHazardsAtThePlaceOrUnderIt_AndDropsTheRest_OnceEach()
    {
        var plan = _w.Plan("Ambush", "cellar", EncounterWorld.Bandits(1));
        plan.HazardsLeftOut = new List<string> { "r1/vault", "r1/vault", "r1/hall", "r1/gone", "r2/vault", "r1/cellar" };

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan);

        Assert.True(result.Saved);
        // The hall is outside the cellar, nothing stands at "gone", and another region's key names no placement here.
        Assert.Equal(new[] { "r1/vault", "r1/cellar" }, _w.Plans.Held[plan.Id].HazardsLeftOut);
        Assert.Empty(result.Check.Warnings);
    }

    [Fact]
    public async Task APlanThatMovesToAnotherPlace_OrLosesIt_DropsTheKeysOfTheOldOne()
    {
        var plan = _w.Plan("Ambush", "cellar", EncounterWorld.Bandits(1));
        plan.HazardsLeftOut = new List<string> { "r1/vault" };
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);

        plan.PlaceId = "hall";
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
        Assert.Empty(_w.Plans.Held[plan.Id].HazardsLeftOut);

        plan.HazardsLeftOut = new List<string> { "r1/hall" };
        plan.PlaceId = plan.RegionId = null;
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
        Assert.Empty(_w.Plans.Held[plan.Id].HazardsLeftOut);
    }

    [Fact]
    public async Task APlanNeverHoldsAHazardLine_ASavedOrCraftedOneIsRefusedByTheCheck()
    {
        var line = new PreparedLine { Kind = PreparedLineKind.Hazard, EntryType = "hazard", EntryId = "blade", Name = "Swinging blade" };

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, _w.Plan("Ambush", "cellar", line));

        Assert.False(result.Saved);
        Assert.Contains(result.Check.Errors, e => e.Key == "Encounters.Check.HazardLine");
    }

    // ---- The readout lists the hazards that join apart ----

    [Fact]
    public async Task TheReadout_ListsTheHazardsThatJoin_ApartFromTheCount()
    {
        var system = EncounterWorld.Fixture("difficulty-sum");
        system.EntityTypes.Add(new ContentTypeDefinition { Type = "hazard", Label = "Hazard", Hazard = new HazardDefinition { Initiative = "initiative", Complex = "complex", Routine = "routine" } });
        var w = new EncounterWorld(system: system);
        w.Registry.RegisterEntry(new ContentEntry { Id = "press", Type = "hazard", SystemId = "demo", PackId = "base", Name = "Crushing press", Properties = { ["complex"] = true } }, 0);
        w.Region.Locations.Single(l => l.Id == "vault").Feature!.Hazard!.Entry = "hazard/press";
        w.Actors.Clear();
        w.Actors.Add(new ActorDocument { Id = "pc-9", CampaignId = "c1", Name = "Tor", Kind = ActorKind.PlayerCharacter, Sheet = { ["kind"] = "hero", ["level"] = 2L } });
        var plan = w.Plan("Ambush", "cellar");

        var rated = await w.Service.RateAsync(w.Campaign, w.Gm, plan);

        Assert.Equal(new[] { "Crushing press" }, rated!.Rating.Hazards);
    }
}
