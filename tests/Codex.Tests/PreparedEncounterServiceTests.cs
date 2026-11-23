using Codex.Core.Models;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using NSubstitute;

namespace Codex.Tests;

/// <summary>Encounter builder B2 (ENCOUNTER_BUILDER_DESIGN.md 4.1 and 14): the service that lists, views, checks, saves and deletes prepared encounters, for the GM only.</summary>
public class PreparedEncounterServiceTests
{
    private readonly EncounterWorld _w = new();

    private async Task<PreparedEncounterDocument> StoredAsync(PreparedEncounterDocument plan)
    {
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
        return plan;
    }

    private static string[] Keys(IEnumerable<EncounterIssue> issues) => issues.Select(i => i.Key).ToArray();

    // ---- List ----

    [Fact]
    public async Task TheList_GivesEachPlanItsPlaceAndPath_ALineSummary_TheLineCount_TheStatus_AndTheLastRun()
    {
        var ambush = await StoredAsync(_w.Plan("Ambush", "vault", EncounterWorld.Bandits(3), EncounterWorld.Cast("npc-1", "Wendel Tarr")));
        var empty = await StoredAsync(_w.Plan("Idea", placeId: null));
        _w.Plans.Held[ambush.Id].Status = PreparedStatus.Ran;
        _w.Plans.Held[ambush.Id].RunEncounterIds = ["enc-1", "enc-2"];

        var rows = (await _w.Service.ListAsync(_w.Campaign, _w.Gm)).ToDictionary(r => r.Plan.Name);

        var row = rows["Ambush"];
        Assert.Equal("Vault", row.PlaceName);
        Assert.Equal("Greywater > Vault", row.PlacePath);
        Assert.Equal("Bandit ×3, Wendel Tarr", row.Summary);
        Assert.Equal(2, row.LineCount);
        Assert.Equal(PreparedStatus.Ran, row.Status);
        Assert.Equal(2, row.RunCount);
        Assert.Equal(PreparedEncounterService.RouteOf(ambush.Id), row.Route);
        Assert.False(row.PlaceGone);
        Assert.Null(rows["Idea"].PlacePath);
        Assert.Equal(0, rows["Idea"].RunCount);
        Assert.Equal(string.Empty, rows["Idea"].Summary);
        Assert.Equal(PreparedStatus.Draft, rows["Idea"].Status);
        Assert.NotNull(empty.Id);
    }

    [Fact]
    public async Task APlanWhosePlaceWasDeleted_StaysOnTheList_SayingItsPlaceIsGone()
    {
        await StoredAsync(_w.Plan("Ambush", "cellar"));
        _w.Region.Locations.RemoveAll(l => l.Id == "cellar");

        var row = Assert.Single(await _w.Service.ListAsync(_w.Campaign, _w.Gm));

        Assert.True(row.PlaceGone);
        Assert.Null(row.PlacePath);
    }

    // ---- View ----

    [Fact]
    public async Task AView_ResolvesEveryLine_AgainstTheLibraryHomebrewAndTheCast()
    {
        _w.Brews.Add(new HomebrewEntryDocument { CampaignId = "c1", SystemId = "demo", Type = "monster", EntryId = "bandit", Name = "Cellar bandit" });
        var homebrew = new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "bandit", IsHomebrew = true, Name = "Old name" };
        var plan = await StoredAsync(_w.Plan("Ambush", "cellar", EncounterWorld.Bandits(), homebrew, EncounterWorld.Cast("npc-1", "Stored name")));

        var view = (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, PreparedEncounterService.RouteOf(plan.Id)))!;

        Assert.All(view.Lines, l => Assert.True(l.Found));
        Assert.Equal(new[] { "Bandit", "Cellar bandit", "Wendel Tarr" }, view.Lines.Select(l => l.Name));
        Assert.Equal("/campaigns/c1/library/monster/bandit", view.Lines[0].Href);
        Assert.Equal("/campaigns/c1/actors/npc-1", view.Lines[2].Href);
        Assert.Empty(view.Check.Warnings);
    }

    [Fact]
    public async Task AView_KeepsALineWhoseEntryCastMemberOrPlaceIsGone_WithItsStoredName_AndAWarning()
    {
        var gone = new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "dragon", Name = "Old dragon", Count = 1 };
        var plan = await StoredAsync(_w.Plan("Ambush", "cellar", gone, EncounterWorld.Cast("npc-gone", "Captain Vell")));
        _w.Region.Locations.RemoveAll(l => l.Id == "cellar");

        var view = (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, PreparedEncounterService.RouteOf(plan.Id)))!;

        Assert.Equal(new[] { "Old dragon", "Captain Vell" }, view.Lines.Select(l => l.Name));
        Assert.All(view.Lines, l => Assert.False(l.Found));
        Assert.All(view.Lines, l => Assert.Null(l.Href));
        Assert.Equal(new[] { "Encounters.Warn.EntryGone", "Encounters.Warn.CastGone", "Encounters.Warn.PlaceGone" }, Keys(view.Check.Warnings));
        Assert.True(view.PlaceGone);
        Assert.Empty(view.Hazards);
        Assert.Equal(2, view.Plan.Lines.Count);
    }

    [Fact]
    public async Task AView_ReadsTheHazardsAtThePlaceAndUnderIt_Live_WithTheirState()
    {
        var plan = await StoredAsync(_w.Plan("Ambush", "cellar"));
        var route = PreparedEncounterService.RouteOf(plan.Id);

        var before = (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, route))!.Hazards;

        Assert.Equal(new[] { "Spiked pit", "Rolling stone" }, before.Select(h => h.Name));
        Assert.Equal(new[] { HazardState.Noticed, HazardState.Hidden }, before.Select(h => h.State));
        Assert.Equal(new[] { "Cellar", "Vault" }, before.Select(h => h.PlaceName));
        Assert.Equal(new[] { "cellar", "vault" }, before.Select(h => h.PlaceId));

        // Read live: a state changed on the place since is what the next view shows.
        _w.Region.Locations.First(l => l.Id == "vault").Feature!.Hazard!.State = HazardState.Disabled;
        var after = (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, route))!.Hazards;
        Assert.Equal(HazardState.Disabled, after.Single(h => h.PlaceId == "vault").State);
    }

    [Fact]
    public async Task APlanAtAPlaceInsideAnother_SeesOnlyItsOwnHazards_NotTheOnesAround()
    {
        var plan = await StoredAsync(_w.Plan("Ambush", "vault"));

        var hazards = (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, PreparedEncounterService.RouteOf(plan.Id)))!.Hazards;
        var none = await _w.Service.HazardsAtAsync(_w.Campaign, _w.Gm, "r1", "hall");

        Assert.Equal(new[] { "vault" }, hazards.Select(h => h.PlaceId));
        Assert.Empty(none);
    }

    // ---- Check ----

    [Fact]
    public async Task TheCheck_RefusesACountOutsideOneToTwenty_ACastMemberCountAboveOne_APlayerCharacterAndACompanion_AHazardLine_AndNoName()
    {
        var plan = _w.Plan("", "cellar",
            EncounterWorld.Bandits(0), EncounterWorld.Bandits(21),
            new PreparedLine { Kind = PreparedLineKind.CastMember, ActorId = "npc-1", Name = "Wendel Tarr", Count = 2 },
            EncounterWorld.Cast("pc-1", "Mira"), EncounterWorld.Cast("pet-1", "Mira's hawk"),
            new PreparedLine { Kind = PreparedLineKind.Hazard, EntryType = "hazard", EntryId = "spiked-pit", Name = "Spiked pit" });

        var check = await _w.Service.CheckAsync(_w.Campaign, _w.Gm, plan);

        Assert.False(check.Ok);
        Assert.Equal(new[]
        {
            "Encounters.Check.NoName", "Encounters.Check.CountRange", "Encounters.Check.CountRange", "Encounters.Check.CastCount",
            "Encounters.Check.CastIsPlayer", "Encounters.Check.CastIsPlayer", "Encounters.Check.HazardLine"
        }, Keys(check.Errors));
        Assert.Equal(new object[] { "Bandit", 20 }, check.Errors[1].Args);
        Assert.Equal("Mira's hawk", check.Errors[5].Args[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task ACountOfOneAndOfTwenty_AreFine_AndANpcCastMemberIsFine(int count)
    {
        var plan = _w.Plan("Ambush", "cellar", EncounterWorld.Bandits(count), EncounterWorld.Cast("npc-1", "Wendel Tarr"));

        var check = await _w.Service.CheckAsync(_w.Campaign, _w.Gm, plan);

        Assert.True(check.Ok);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public async Task ACreatureLine_ThatIsAHazardOrOfANonCreatureType_IsAnError_AndAMissingEntryOnlyAWarning()
    {
        _w.Registry.RegisterEntry(new ContentEntry { Id = "trapdoor", Type = "monster", SystemId = "demo", PackId = "base", Name = "Trapdoor", Tags = new List<string> { "hazard" } }, 0);
        _w.Registry.RegisterActor(new ActorDefinition { Id = "boulder", SystemId = "demo", PackId = "base", Name = "Boulder", Tags = new List<string> { "hazard" }, Metadata = new Dictionary<string, object> { ["entryType"] = "monster" } }, 0);
        _w.Registry.RegisterEntry(new ContentEntry { Id = "boulder", Type = "monster", SystemId = "demo", PackId = "base", Name = "Boulder" }, 0);
        PreparedLine Line(string type, string id) => new() { Kind = PreparedLineKind.Creature, EntryType = type, EntryId = id, Name = id };
        var plan = _w.Plan("Ambush", "cellar",
            Line("hazard", "spiked-pit"),   // a hazard type
            Line("monster", "trapdoor"),    // an entry tagged hazard
            Line("monster", "boulder"),     // an entry the registry holds as a hazard actor
            Line("monster", "ghost"),       // not in the Library at all
            Line("monster", "wolf"));

        var check = await _w.Service.CheckAsync(_w.Campaign, _w.Gm, plan);

        Assert.Equal(new[] { "Encounters.Check.HazardLine", "Encounters.Check.HazardLine", "Encounters.Check.HazardLine" }, Keys(check.Errors));
        Assert.Equal(new[] { "Encounters.Warn.EntryGone" }, Keys(check.Warnings));
        Assert.False((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
    }

    [Fact]
    public async Task ACastLine_NamingAHazardActor_IsAnError_ButAMonsterCastFromTheLibraryIsFine()
    {
        var plan = _w.Plan("Ambush", "cellar", EncounterWorld.Cast("stone-1", "Rolling stone"), EncounterWorld.Cast("rat-1", "Cellar rat"), EncounterWorld.Cast("npc-1", "Wendel Tarr"));

        var check = await _w.Service.CheckAsync(_w.Campaign, _w.Gm, plan);

        Assert.Equal(new[] { "Encounters.Check.HazardLine" }, Keys(check.Errors));
        Assert.Equal("Rolling stone", check.Errors[0].Args[0]);
    }

    [Fact]
    public async Task TheCheck_OnlyWarns_ForAnUnknownEntry_ACastMemberThatIsGone_AndAPlaceThatIsGone()
    {
        var plan = _w.Plan("Ambush", "nowhere",
            new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "dragon", Name = "Dragon" },
            EncounterWorld.Cast("npc-gone", "Captain Vell"));

        var check = await _w.Service.CheckAsync(_w.Campaign, _w.Gm, plan);

        Assert.True(check.Ok);
        Assert.Equal(new[] { "Encounters.Warn.EntryGone", "Encounters.Warn.CastGone", "Encounters.Warn.PlaceGone" }, Keys(check.Warnings));
    }

    // ---- Save ----

    [Fact]
    public async Task ASaveWithErrors_IsRefused_AndStoresNothing()
    {
        var plan = _w.Plan("Ambush", "cellar", EncounterWorld.Cast("pc-1", "Mira"));

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan);

        Assert.False(result.Saved);
        Assert.Null(result.Id);
        Assert.Equal(new[] { "Encounters.Check.CastIsPlayer" }, Keys(result.Check.Errors));
        Assert.Empty(_w.Plans.Held);
        Assert.Equal(string.Empty, plan.Id);
    }

    [Fact]
    public async Task ANewPlan_HasNoId_AndTheSaveWritesTheNewOneBack_SoTheEditorCanOpenIt()
    {
        var plan = _w.Plan("  Ambush  ", "cellar", EncounterWorld.Bandits(2));
        plan.Lines[0].Note = "   ";
        Assert.Equal(string.Empty, plan.Id);

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan);

        Assert.True(result.Saved);
        Assert.StartsWith("preparedencounters/", result.Id);
        Assert.Equal(result.Id, plan.Id);
        var stored = _w.Plans.Held[result.Id!];
        Assert.Equal("Ambush", stored.Name);
        Assert.Null(stored.Lines[0].Note);
        Assert.Equal("c1", stored.CampaignId);
        Assert.Equal("gm", stored.CreatedBy);

        // Saving it again is an edit of the same plan.
        plan.Name = "Ambush at the cellar";
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
        Assert.Single(_w.Plans.Held);
        Assert.Equal("Ambush at the cellar", _w.Plans.Held[result.Id!].Name);
    }

    [Fact]
    public async Task ASaveWithNoPlace_ClearsTheRegionToo()
    {
        var plan = _w.Plan("Ambush", placeId: null);
        plan.RegionId = "r1";

        await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan);

        Assert.Null(_w.Plans.Held[plan.Id].RegionId);
        Assert.Null(_w.Plans.Held[plan.Id].PlaceId);
    }

    [Fact]
    public async Task ASaveTheRepositoryRefuses_SaysSo_InsteadOfPretendingItWorked()
    {
        var plan = _w.Plan("Ambush");
        plan.Id = "preparedencounters/never-stored";

        var result = await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan);

        Assert.False(result.Saved);
        Assert.Equal(new[] { "Encounters.Error.NotSaved" }, Keys(result.Check.Errors));
    }

    // ---- The two shapes of id ----

    [Fact]
    public async Task APlanIsFoundByItsRoute_WhetherItsStoredIdHasThePrefixOrIsABareGuid()
    {
        var prefixed = await StoredAsync(_w.Plan("Prefixed"));
        var bare = Guid.NewGuid().ToString();
        // What an imported copy of a campaign holds: the plan under a bare GUID.
        await _w.Plans.RestoreAsync(new PreparedEncounterDocument { Id = bare, CampaignId = "c1", Name = "Copied" });

        Assert.Equal(prefixed.Id["preparedencounters/".Length..], PreparedEncounterService.RouteOf(prefixed.Id));
        Assert.Equal(bare, PreparedEncounterService.RouteOf(bare));
        Assert.Equal("Prefixed", (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, PreparedEncounterService.RouteOf(prefixed.Id)))!.Plan.Name);
        Assert.Equal("Copied", (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, bare))!.Plan.Name);
        Assert.Null(await _w.Service.ViewAsync(_w.Campaign, _w.Gm, Guid.NewGuid().ToString()));
        Assert.Null(await _w.Service.ViewAsync(_w.Campaign, _w.Gm, null));

        Assert.True(await _w.Service.DeleteAsync(_w.Campaign, _w.Gm, bare));
        Assert.True(await _w.Service.DeleteAsync(_w.Campaign, _w.Gm, PreparedEncounterService.RouteOf(prefixed.Id)));
        Assert.False(await _w.Service.DeleteAsync(_w.Campaign, _w.Gm, bare));
        Assert.Empty(_w.Plans.Held);
    }

    // ---- Players ----

    [Fact]
    public async Task APlayer_GetsNothing_FromAnyMethod_AndSavesNothing()
    {
        var plan = await StoredAsync(_w.Plan("Ambush", "cellar", EncounterWorld.Bandits()));
        var route = PreparedEncounterService.RouteOf(plan.Id);

        Assert.Empty(await _w.Service.ListAsync(_w.Campaign, _w.Player));
        Assert.Null(await _w.Service.ViewAsync(_w.Campaign, _w.Player, route));
        Assert.Equal(new[] { "Encounters.Error.NotAllowed" }, Keys((await _w.Service.CheckAsync(_w.Campaign, _w.Player, plan)).Errors));
        Assert.False((await _w.Service.SaveAsync(_w.Campaign, _w.Player, _w.Plan("Sneaky"))).Saved);
        Assert.False(await _w.Service.DeleteAsync(_w.Campaign, _w.Player, route));
        Assert.Empty(await _w.Service.PlacesAsync(_w.Player));
        Assert.Empty(await _w.Service.HazardsAtAsync(_w.Campaign, _w.Player, "r1", "cellar"));
        Assert.Empty(await _w.Service.SearchCreaturesAsync(_w.Campaign, _w.Player, ""));
        Assert.Empty(await _w.Service.CastChoicesAsync(_w.Player, ""));
        Assert.Single(_w.Plans.Held);
    }

    // ---- The pickers ----

    [Fact]
    public async Task ThePlacePicker_ListsTheRegionsPlacesAsATree_ParentsFirst()
    {
        var places = await _w.Service.PlacesAsync(_w.Gm);

        Assert.Equal(3, places.Count);
        Assert.Equal(0, places.First(p => p.PlaceId == "cellar").Depth);
        Assert.Equal(1, places.First(p => p.PlaceId == "vault").Depth);
        Assert.True(places.ToList().FindIndex(p => p.PlaceId == "cellar") < places.ToList().FindIndex(p => p.PlaceId == "vault"));
        Assert.Equal("r1|cellar", places.First(p => p.PlaceId == "cellar").Value);
    }

    [Fact]
    public async Task TheCastTab_OffersNpcsThenLibraryCreaturesInTheCast_NeverAPlayerCharacterACompanionOrAHazard()
    {
        _w.Actors.Add(new ActorDocument { Id = "npc-0", CampaignId = "c1", Name = "Zed", Kind = ActorKind.NonPlayerCharacter });
        _w.Actors.Add(new ActorDocument { Id = "retired", CampaignId = "c1", Name = "Old goblin", Kind = ActorKind.Monster, Retired = true });

        var all = await _w.Service.CastChoicesAsync(_w.Gm, "");
        var some = await _w.Service.CastChoicesAsync(_w.Gm, "tarr");

        Assert.Equal(new[] { "Wendel Tarr", "Zed", "Cellar rat" }, all.Select(a => a.Name));
        Assert.Equal(new[] { "Wendel Tarr" }, some.Select(a => a.Name));
        Assert.Empty(await _w.Service.CastChoicesAsync(_w.Gm, "mira"));
        Assert.Empty(await _w.Service.CastChoicesAsync(_w.Gm, "stone"));
    }

    [Fact]
    public async Task ANameShowsInTheReadersLanguage_OnViewAndOnTheHazardsList_AndInEnglishWithNoCulture()
    {
        var plan = await StoredAsync(_w.Plan("Ambush", "cellar", EncounterWorld.Bandits()));
        var route = PreparedEncounterService.RouteOf(plan.Id);

        var ru = (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, route, "ru"))!;
        var en = (await _w.Service.ViewAsync(_w.Campaign, _w.Gm, route))!;

        Assert.Equal("Бандит", ru.Lines[0].Name);
        Assert.Equal("Шипастая яма", ru.Hazards[0].Name);
        Assert.Equal("Bandit", en.Lines[0].Name);
        Assert.Equal("Spiked pit", en.Hazards[0].Name);
        Assert.Equal("Шипастая яма", (await _w.Service.HazardsAtAsync(_w.Campaign, _w.Gm, "r1", "cellar", "ru"))[0].Name);
        // The Library's own lookup agrees, for a pack entry and for homebrew.
        Assert.Equal("Бандит", (await _w.Library.GetAsync(_w.Campaign, _w.Gm, "monster", "bandit", false, "ru"))!.Entry.Name);
        Assert.Equal("Бандит", (await _w.Library.LookupAsync(_w.Campaign, _w.Gm, "ru"))("monster", "bandit", false)!.Entry.Name);
    }

    [Fact]
    public async Task ACheckAndAView_ReadTheHomebrewAndTheRegionsOncePerCall_NotOncePerLine()
    {
        var plan = _w.Plan("Ambush", "cellar", EncounterWorld.Bandits(), EncounterWorld.Bandits(), EncounterWorld.Cast("npc-1", "Wendel Tarr"), EncounterWorld.Cast("rat-1", "Cellar rat"));
        var stored = await StoredAsync(plan);
        _w.RegionRepo.ClearReceivedCalls();
        _w.ActorRepo.ClearReceivedCalls();
        _w.HomebrewRepo.ClearReceivedCalls();

        await _w.Service.CheckAsync(_w.Campaign, _w.Gm, stored);
        await _w.Service.ViewAsync(_w.Campaign, _w.Gm, PreparedEncounterService.RouteOf(stored.Id));

        // One read of each per call (the check, then the view), however many lines there are.
        Assert.Equal(2, _w.RegionRepo.ReceivedCalls().Count());
        Assert.Equal(2, _w.ActorRepo.ReceivedCalls().Count());
        Assert.Equal(2, _w.HomebrewRepo.ReceivedCalls().Count());
    }

    [Fact]
    public void ALineFromTheBestiaryShortlist_KeepsItsKeyCountAndNote()
    {
        var entry = new BestiaryEntry { EntryType = "monster", EntryId = "bandit", IsHomebrew = true, Name = "Bandit", Count = 40, Note = "behind the casks" };

        var line = PreparedEncounterService.LineFrom(entry);

        Assert.Equal(entry.Key, PreparedEncounterService.KeyOf(line));
        Assert.Equal("homebrew:monster:bandit", PreparedEncounterService.KeyOf(line));
        Assert.Equal(20, line.Count);
        Assert.Equal("behind the casks", line.Note);
        Assert.Equal(PreparedLineKind.Creature, line.Kind);
    }
}
