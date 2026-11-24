using Codex.Core.Models;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using NSubstitute;

namespace Codex.Tests;

/// <summary>The difficulty readout in the service (ENCOUNTER_BUILDER_DESIGN.md 16): <see cref="PreparedEncounterService.RateAsync"/> reads the party and the lines' numbers and hands them to
/// the pure rating. The campaign is the <c>difficulty-sum</c> fixture (per character, level 2 is 15/30/45, so two characters have 30/60/90); every number is invented.</summary>
public class PreparedEncounterRatingTests
{
    private readonly EncounterWorld _w = new(system: EncounterWorld.Fixture("difficulty-sum"));

    public PreparedEncounterRatingTests()
    {
        _w.Registry.RegisterEntry(new ContentEntry { Id = "goblin", Type = "monster", SystemId = "demo", PackId = "base", Name = "Goblin", Properties = { ["worth"] = 10L } }, 0);
        // The bandit has no worth at all: a line of it cannot be valued.
        _w.Actors.Clear();
        Hero("pc-1", "Mira", 2);
        Hero("pc-2", "Tor", 2);
        _w.Actors.Add(new ActorDocument { Id = "pet-1", CampaignId = "c1", Name = "Mira's hawk", Kind = ActorKind.PlayerCharacter, Sheet = { ["kind"] = "pet", ["level"] = 9L } });
        _w.Actors.Add(new ActorDocument { Id = "pc-old", CampaignId = "c1", Name = "Old hand", Kind = ActorKind.PlayerCharacter, Retired = true, Sheet = { ["kind"] = "hero", ["level"] = 1L } });
        _w.Actors.Add(new ActorDocument { Id = "npc-1", CampaignId = "c1", Name = "Wendel Tarr", Kind = ActorKind.NonPlayerCharacter, Sheet = { ["worth"] = 4L } });
    }

    private void Hero(string id, string name, object? level) =>
        _w.Actors.Add(new ActorDocument { Id = id, CampaignId = "c1", Name = name, Kind = ActorKind.PlayerCharacter, Sheet = level == null ? new() { ["kind"] = "hero" } : new() { ["kind"] = "hero", ["level"] = level } });

    private static PreparedLine Goblins(int count) => new() { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "goblin", Name = "Goblin", Count = count };

    private async Task<EncounterRating> RateAsync(PreparedEncounterDocument plan, PartyOverride? party = null, string? culture = null) =>
        (await _w.Service.RateAsync(_w.Campaign, _w.Gm, plan, party, culture))!;

    [Fact]
    public async Task TheParty_IsThePlayerCharacters_NotACompanionNorARetiredOne_EachAtTheirOwnLevel()
    {
        var rated = await RateAsync(_w.Plan("Ambush", "cellar", Goblins(2)));

        Assert.Equal(PartySource.Campaign, rated.Source);
        Assert.Equal(2, rated.Rating.PartySize);
        Assert.Equal(2, rated.Rating.PartyLevel);
        Assert.False(rated.Rating.LevelsVary);
        Assert.Equal(new long[] { 30, 60, 90 }, rated.Rating.Budgets.Select(b => b.Budget));
        Assert.Equal(20, rated.Rating.Total);
        Assert.Equal("low", rated.Rating.Band!.Key);
        Assert.Equal(1, rated.LevelFrom);
        Assert.Equal(3, rated.LevelTo);
    }

    [Fact]
    public async Task ACreatureLine_IsWorthItsEntrysNumberTimesItsCount_AndAnEntryWithoutOneIsNamed()
    {
        var bandit = EncounterWorld.Bandits(2);

        var rated = await RateAsync(_w.Plan("Ambush", "cellar", Goblins(5), bandit));

        // 5 x 10 = 50 of 30/60/90: the second band. The bandits have no worth, so they are named, and the rest is still rated.
        Assert.Equal(50, rated.Rating.Total);
        Assert.Equal("moderate", rated.Rating.Band!.Key);
        var skipped = Assert.Single(rated.Rating.NotCounted);
        Assert.Equal("Bandit", skipped.Name);
        Assert.Equal("Encounters.Difficulty.NoValue", skipped.Reason.Key);
        Assert.Equal(new object[] { "Bandit", "Worth" }, skipped.Reason.Args);
    }

    [Fact]
    public async Task ANegativeNumber_OnAnEntryOrASheet_IsNamedAndNotSummed()
    {
        _w.Registry.RegisterEntry(new ContentEntry { Id = "glitch", Type = "monster", SystemId = "demo", PackId = "base", Name = "Glitch", Properties = { ["worth"] = -3L } }, 0);
        _w.Actors.Add(new ActorDocument { Id = "odd-1", CampaignId = "c1", Name = "Odd", Kind = ActorKind.Monster, Sheet = { ["worth"] = -1L } });
        var glitch = new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "glitch", Name = "Glitch", Count = 2 };

        var rated = await RateAsync(_w.Plan("Ambush", "cellar", Goblins(1), glitch, EncounterWorld.Cast("odd-1", "Odd")));

        Assert.Equal(10, rated.Rating.Total);
        Assert.Equal(new[] { "Glitch", "Odd" }, rated.Rating.NotCounted.Select(n => n.Name));
        Assert.All(rated.Rating.NotCounted, n => Assert.Equal("Encounters.Difficulty.Negative", n.Reason.Key));
    }

    [Fact]
    public async Task ABlankPartyLevel_ReadsTheLevelRole()
    {
        var system = EncounterWorld.Fixture("difficulty-sum");
        system.Encounters!.Difficulty!.PartyLevel = "  ";
        var world = new EncounterWorld(system: system);
        world.Actors.Clear();
        world.Actors.Add(new ActorDocument { Id = "pc-1", CampaignId = "c1", Name = "Mira", Kind = ActorKind.PlayerCharacter, Sheet = { ["kind"] = "hero", ["level"] = 2L } });

        var rated = (await world.Service.RateAsync(world.Campaign, world.Gm, world.Plan("Ambush", "cellar")))!;

        Assert.Equal(PartySource.Campaign, rated.Source);
        Assert.Equal(2, rated.Rating.PartyLevel);
    }

    [Fact]
    public async Task AFaultWhileRating_DoesNotHideTheList_ItComesWithoutRatings()
    {
        var plan = _w.Plan("Ambush", "cellar", Goblins(1));
        plan.Status = PreparedStatus.Ready;
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
        _w.ActorRepo.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(Task.FromException<IEnumerable<ActorDocument>>(new InvalidOperationException("The database is down.")));

        var rows = await _w.Service.ListAsync(_w.Campaign, _w.Gm);
        var choices = await _w.Service.StartChoicesAsync(_w.Campaign, _w.Gm, null);

        Assert.Equal("Ambush", Assert.Single(rows).Plan.Name);
        Assert.Null(rows[0].Rating);
        Assert.Null(Assert.Single(choices).Rating);
    }

    [Fact]
    public async Task ACreatureLineWhoseEntryIsGone_IsNamedAsSuch_AndDoesNotCount()
    {
        var gone = new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "dragon", Name = "Old dragon", Count = 1 };

        var rated = await RateAsync(_w.Plan("Ambush", "cellar", Goblins(1), gone));

        Assert.Equal(10, rated.Rating.Total);
        Assert.Equal("Encounters.Difficulty.NoEntry", Assert.Single(rated.Rating.NotCounted).Reason.Key);
    }

    [Fact]
    public async Task ACastMemberLine_IsWorthTheNumberOnTheirSheet_ElseTheEntryTheyWereMadeFrom_ElseNothing()
    {
        _w.Registry.RegisterActor(new Codex.Core.Models.ActorDefinition { Id = "wolf", SystemId = "demo", Name = "Wolf", Properties = { ["worth"] = 6L } }, 0);
        _w.Actors.Add(new ActorDocument { Id = "wolf-1", CampaignId = "c1", Name = "Fang", Kind = ActorKind.Monster, BlueprintId = "demo:wolf" });
        _w.Actors.Add(new ActorDocument { Id = "rat-1", CampaignId = "c1", Name = "Cellar rat", Kind = ActorKind.Monster });

        var rated = await RateAsync(_w.Plan("Ambush", "cellar", EncounterWorld.Cast("npc-1", "Wendel Tarr"), EncounterWorld.Cast("wolf-1", "Fang"), EncounterWorld.Cast("rat-1", "Cellar rat"),
            EncounterWorld.Cast("gone", "Captain Vell")));

        // 4 from the sheet, 6 from the entry; the rat has neither, and the captain is not in the cast.
        Assert.Equal(10, rated.Rating.Total);
        Assert.Equal(new[] { ("Cellar rat", "Encounters.Difficulty.NoValue"), ("Captain Vell", "Encounters.Difficulty.NoActor") }, rated.Rating.NotCounted.Select(n => (n.Name, n.Reason.Key)));
    }

    [Fact]
    public async Task APartyOverride_WinsOverTheCampaignsParty_ForThatCallOnly()
    {
        var plan = _w.Plan("Ambush", "cellar", Goblins(5));

        var different = await RateAsync(plan, new PartyOverride(4, 3));
        var again = await RateAsync(plan);

        // Four characters of level 3: 80/160/240.
        Assert.Equal(PartySource.Override, different.Source);
        Assert.Equal(new long[] { 80, 160, 240 }, different.Rating.Budgets.Select(b => b.Budget));
        Assert.Equal("low", different.Rating.Band!.Key);
        Assert.Equal(PartySource.Campaign, again.Source);
        Assert.Equal(2, again.Rating.PartySize);
    }

    [Fact]
    public async Task AnOverrideLevelTheTableHasNoRowFor_GivesNoReadout_AndSizeIsKeptToTwenty()
    {
        var plan = _w.Plan("Ambush", "cellar", Goblins(1));

        var unrated = await RateAsync(plan, new PartyOverride(4, 9));
        var big = await RateAsync(plan, new PartyOverride(99, 1));

        Assert.Equal("Encounters.Difficulty.NoBudgetRow", unrated.Rating.Unavailable!.Key);
        Assert.Equal(20, big.Rating.PartySize);
    }

    [Fact]
    public async Task WithNoCharactersAtAll_TheReadoutIsForAStandInParty_ThatSaysSo()
    {
        _w.Actors.RemoveAll(a => a.Kind == ActorKind.PlayerCharacter);

        var rated = await RateAsync(_w.Plan("Ambush", "cellar", Goblins(2)));

        Assert.Equal(PartySource.Default, rated.Source);
        Assert.Equal(4, rated.DefaultSize);
        Assert.Equal(1, rated.DefaultLevel);
        Assert.Equal(4, rated.Rating.PartySize);
        Assert.Equal(new long[] { 40, 80, 120 }, rated.Rating.Budgets.Select(b => b.Budget));
    }

    [Fact]
    public async Task ACharacterWithNoLevel_MakesTheReadoutUnavailable_NamingThem()
    {
        Hero("pc-3", "Nameless", null);
        Hero("pc-4", "Odd", "high");

        var rated = await RateAsync(_w.Plan("Ambush", "cellar", Goblins(2)));

        Assert.Equal("Encounters.Difficulty.LevelUnknown", rated.Rating.Unavailable!.Key);
        Assert.Equal(new[] { "Nameless", "Odd" }, rated.UnknownLevels);
        Assert.Equal("Nameless, Odd", rated.Rating.Unavailable.Args[0]);
        // The different-party form still works.
        Assert.True((await RateAsync(_w.Plan("Ambush", "cellar", Goblins(2)), new PartyOverride(2, 2))).Rating.Available);
    }

    [Fact]
    public async Task TheBandsAndTheUnit_AreWordedInTheReadersLanguage_WithoutChangingTheKeys()
    {
        var plan = _w.Plan("Ambush", "cellar", Goblins(5));

        var ru = await RateAsync(plan, culture: "ru");
        var en = await RateAsync(plan);

        Assert.Equal("Среднее", ru.Rating.Band!.Label);
        Assert.Equal("оч.", ru.Rating.Unit);
        Assert.Equal("moderate", ru.Rating.Band.Key);
        Assert.Equal("Moderate", en.Rating.Band!.Label);
        Assert.Equal("pts", en.Rating.Unit);
    }

    [Fact]
    public async Task ASystemWithNoBlock_GivesNoRating_AndSoDoesAnyoneButTheGameMaster()
    {
        var plain = new EncounterWorld();
        Assert.Null(await plain.Service.RateAsync(plain.Campaign, plain.Gm, plain.Plan("Ambush", "cellar", EncounterWorld.Bandits())));

        Assert.Null(await _w.Service.RateAsync(_w.Campaign, _w.Player, _w.Plan("Ambush", "cellar", Goblins(1))));
    }

    [Fact]
    public async Task TheList_CarriesEachPlansRatingForTheCampaignsParty_ReadingTheCastAndTheLibraryOnce()
    {
        foreach (var name in new[] { "A", "B", "C" }) Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, _w.Plan(name, "cellar", Goblins(name == "A" ? 1 : name == "B" ? 5 : 20)))).Saved);
        _w.ActorRepo.ClearReceivedCalls();
        _w.HomebrewRepo.ClearReceivedCalls();

        var rows = (await _w.Service.ListAsync(_w.Campaign, _w.Gm)).ToDictionary(r => r.Plan.Name);

        Assert.Equal("low", rows["A"].Rating!.Band!.Key);
        Assert.Equal("moderate", rows["B"].Rating!.Band!.Key);
        Assert.Equal(BandPosition.Above, rows["C"].Rating!.Position);
        _ = _w.ActorRepo.Received(1).GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>());
        _ = _w.HomebrewRepo.Received(1).GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>());
    }

    [Fact]
    public async Task TheList_HasNoRatingWhenTheSystemHasNoBlock_OrTheCampaignNoCharacters_OrTheLevelsAreUnknown()
    {
        var plain = new EncounterWorld();
        Assert.True((await plain.Service.SaveAsync(plain.Campaign, plain.Gm, plain.Plan("Ambush", "cellar", EncounterWorld.Bandits()))).Saved);
        Assert.Null(Assert.Single(await plain.Service.ListAsync(plain.Campaign, plain.Gm)).Rating);

        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, _w.Plan("Ambush", "cellar", Goblins(1)))).Saved);
        Hero("pc-3", "Nameless", null);
        Assert.False(Assert.Single(await _w.Service.ListAsync(_w.Campaign, _w.Gm)).Rating!.Available);

        _w.Actors.RemoveAll(a => a.Kind == ActorKind.PlayerCharacter);
        Assert.Null(Assert.Single(await _w.Service.ListAsync(_w.Campaign, _w.Gm)).Rating);
    }
}

/// <summary>The <c>byLevel</c> shape through the service, on the <c>difficulty-bylevel</c> fixture: four characters of level 3 (10/20/30 to spend), and a creature of tier N is worth N.</summary>
public class PreparedEncounterByLevelRatingTests
{
    private readonly EncounterWorld _w = new(system: EncounterWorld.Fixture("difficulty-bylevel"));

    public PreparedEncounterByLevelRatingTests()
    {
        _w.Actors.Clear();
        for (var i = 1; i <= 4; i++)
        {
            _w.Actors.Add(new ActorDocument { Id = $"pc-{i}", CampaignId = "c1", Name = $"Hero {i}", Kind = ActorKind.PlayerCharacter, Sheet = { ["kind"] = "hero", ["level"] = 3L } });
        }

        _w.Registry.RegisterEntry(new ContentEntry { Id = "goblin", Type = "monster", SystemId = "demo", PackId = "base", Name = "Goblin", Properties = { ["tier"] = 3L } }, 0);
        _w.Registry.RegisterActor(new Codex.Core.Models.ActorDefinition { Id = "wolf", SystemId = "demo", Name = "Wolf", Properties = { ["tier"] = 2L } }, 0);
    }

    private static PreparedLine Goblins(int count) => new() { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "goblin", Name = "Goblin", Count = count };

    [Fact]
    public async Task ACastMembersLevel_ComesFromTheirSheet_AndTheSheetWinsOverTheEntryTheyWereMadeFrom()
    {
        // Fang's sheet says tier 5 (worth 5) and the wolf entry says 2: the sheet wins. Rex has no tier of his own, so the wolf entry gives 2 (worth 2).
        _w.Actors.Add(new ActorDocument { Id = "fang", CampaignId = "c1", Name = "Fang", Kind = ActorKind.Monster, BlueprintId = "demo:wolf", Sheet = { ["tier"] = 5L } });
        _w.Actors.Add(new ActorDocument { Id = "rex", CampaignId = "c1", Name = "Rex", Kind = ActorKind.Monster, BlueprintId = "demo:wolf" });
        _w.Actors.Add(new ActorDocument { Id = "npc-4", CampaignId = "c1", Name = "Captain", Kind = ActorKind.NonPlayerCharacter, Sheet = { ["tier"] = 4L } });

        var rated = (await _w.Service.RateAsync(_w.Campaign, _w.Gm, _w.Plan("Ambush", "cellar", EncounterWorld.Cast("fang", "Fang"), EncounterWorld.Cast("rex", "Rex"), EncounterWorld.Cast("npc-4", "Captain"))))!;

        Assert.Equal(5 + 2 + 4, rated.Rating.Total);
        Assert.Equal(3, rated.Rating.PartyLevel);
        Assert.Equal("easy", rated.Rating.Band!.Key);
        Assert.Equal(new long[] { 10, 20, 30 }, rated.Rating.Budgets.Select(b => b.Budget));
        Assert.True(rated.Rating.ByLevel);
    }

    [Fact]
    public async Task ACreatureTooFarFromThePartyForTheTable_IsNamed_ThroughTheService()
    {
        _w.Registry.RegisterEntry(new ContentEntry { Id = "dragon", Type = "monster", SystemId = "demo", PackId = "base", Name = "Dragon", Properties = { ["tier"] = 9L } }, 0);
        var dragon = new PreparedLine { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "dragon", Name = "Dragon", Count = 1 };

        var rated = (await _w.Service.RateAsync(_w.Campaign, _w.Gm, _w.Plan("Ambush", "cellar", Goblins(7), dragon)))!;

        Assert.Equal(21, rated.Rating.Total);
        Assert.Equal("fair", rated.Rating.Band!.Key);
        Assert.Equal("Encounters.Difficulty.TooFarAbove", Assert.Single(rated.Rating.NotCounted).Reason.Key);
    }

    [Fact]
    public async Task ThePartyForm_ForByLevel_UsesTheLevelFieldsOwnBounds_AndASizeChangesTheBudgets()
    {
        var rated = (await _w.Service.RateAsync(_w.Campaign, _w.Gm, _w.Plan("Ambush", "cellar", Goblins(1)), new PartyOverride(6, 3)))!;

        Assert.Equal(1, rated.LevelFrom);
        Assert.Equal(20, rated.LevelTo);
        Assert.Equal(4, rated.DefaultSize);
        Assert.Equal(new long[] { 12, 24, 36 }, rated.Rating.Budgets.Select(b => b.Budget));
    }
}
