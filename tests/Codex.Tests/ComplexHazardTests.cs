using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>H5 (docs/HAZARDS_DESIGN.md 3.3, 11, 12.6): a complex hazard is an actor with a turn in initiative. Real srd5e Rolling Stone, a runtime with a scripted die and a real place.</summary>
[Collection("RavenDb")]
public class ComplexHazardTests : IClassFixture<RavenDbFixture>, IAsyncLifetime, IDisposable
{
    private const string Gm = "dm-user";

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly RegionRepository _regions;
    private readonly ComponentRegistry _registry;
    private readonly ContentRegistry _content = new();
    private readonly PluginLoader _loader;

    public ComplexHazardTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("ComplexHazard_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _regions = new RegionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_content));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _dbService.Dispose();

    private SystemDefinition System => _loader.GetDefinition("srd5e")!;
    private ContentTypeDefinition HazardType => System.FindType("hazard")!;
    private ContentEntry Entry(string id) => _content.GetEntries("srd5e", "hazard", new HashSet<string> { "srd-5.2.1" }).Single(e => e.Id == id);

    // ---- Registration ----

    [Fact]
    public void OnlyComplexHazards_AreRegisteredAsActors_TaggedHazard_WithHitPointsFromDurability()
    {
        var hazards = _content.GetActorsBySystem("srd5e").Where(a => a.Tags.Contains("hazard")).ToList();

        // The Rolling Stone is the only complex trap in the SRD pack; no simple trap becomes an actor.
        Assert.Contains(hazards, a => a.Id == "rolling-stone" && a.PackId == "srd-5.2.1");
        Assert.DoesNotContain(hazards, a => a.Id is "spiked-pit" or "poisoned-needle" or "collapsing-roof");
        var stone = hazards.Single(a => a.Id == "rolling-stone");
        Assert.Equal("hazard", stone.Metadata["entryType"]);
        Assert.Equal(100, stone.Resources["HP"]);
        Assert.Equal(100, stone.Resources["HP_Max"]);
        Assert.Equal(8, Convert.ToInt32(stone.Properties["initiative"], CultureInfo.InvariantCulture));
        // The type does not carry the flag, so the Bestiary, the book and the homebrew wizard never read a hazard.
        Assert.False(HazardType.ActorBlueprint);
        Assert.All(_content.GetActorsBySystem("srd5e").Where(a => !a.Tags.Contains("hazard")), a => Assert.False(a.Metadata.TryGetValue("entryType", out var kind) && Equals(kind, "hazard")));
    }

    [Fact]
    public void TheHazardOverload_ReadsItsHitPointsFromTheHazardBlock_NotFromHitPointsProperty()
    {
        var entry = Entry("rolling-stone");

        var actor = YamlContentPackLoader.ToActorBlueprint(entry, HazardType, HazardType.Hazard!, System.PrimaryResource);

        Assert.Equal("hazard", actor.Tags[0]);
        Assert.Equal(100, actor.Resources["HP"]);
        Assert.True(HazardNumbers.IsComplex(entry, HazardType));
        Assert.False(HazardNumbers.IsComplex(Entry("spiked-pit"), HazardType));
        Assert.Equal(8, HazardNumbers.InitiativeOf(HazardNumbers.For(entry, HazardType, null), HazardType.Hazard!));
        Assert.Equal(new[] { "Roll", "Through spaces", "Crush" }, HazardNumbers.Routine(HazardNumbers.For(entry, HazardType, null), HazardType.Hazard!).Select(s => s.Name));
    }

    [Fact]
    public async Task TheEncountersPlace_OffersItsComplexHazardsAndThoseInsideIt_NotSimpleOnesNorDisabledOnes()
    {
        var stone = Entry("rolling-stone");
        var pit = Entry("spiked-pit");
        RegionDocument Region(params LocationDocument[] places) => new() { Id = "r1", Name = "Dungeon", Locations = places.ToList() };
        LocationDocument Place(string id, string? parent, PlacedHazard? hazard) =>
            new() { Id = id, Name = id, ParentId = parent, Feature = hazard == null ? null : new PlaceFeature { Hazard = hazard } };
        var disabled = StoneAt(stone);
        disabled.State = HazardState.Disabled;
        var region = Region(Place("wing", null, null), Place("passage", "wing", StoneAt(stone)), Place("pit-room", "wing", StoneAt(pit)), Place("old", "wing", disabled), Place("elsewhere", null, StoneAt(stone)));
        Task<ContentEntry?> Read(PlacedHazard placed) => Task.FromResult<ContentEntry?>(placed.EntryId == "rolling-stone" ? stone : pit);

        var offers = await HazardOffers.ForPlaceAsync(new[] { region }, "wing", System, HazardType, Array.Empty<ActorDocument>(), Read);

        // The passage inside the wing, and nothing else: the pit is simple, the old one is disarmed, the other stone is somewhere else.
        var offer = Assert.Single(offers);
        Assert.Equal("passage", offer.Link.LocationId);
        Assert.Equal("r1", offer.Link.RegionId);
        Assert.Equal(8, offer.Initiative);
        Assert.Equal(100, offer.Definition.Resources["HP"]);
        Assert.Contains("hazard", offer.Definition.Tags);

        // Once its actor is at the table it is not offered twice, and with no place there is nothing to offer.
        var atTable = new ActorDocument { Hazard = new HazardActorLink { RegionId = "r1", LocationId = "passage" } };
        Assert.Empty(await HazardOffers.ForPlaceAsync(new[] { region }, "wing", System, HazardType, new[] { atTable }, Read));
        Assert.Empty(await HazardOffers.ForPlaceAsync(new[] { region }, string.Empty, System, HazardType, Array.Empty<ActorDocument>(), Read));
    }

    // ---- The table ----

    private sealed class ScriptedRoller : IDiceRoller
    {
        public Queue<int> Faces { get; } = new();
        public List<string> Expressions { get; } = new();

        public DiceRollResult Roll(string expression)
        {
            Expressions.Add(expression);
            var kept = new List<int>();
            var modifier = 0;
            var total = 0;
            foreach (var term in Regex.Matches(expression, @"[+-]?[^+-]+").Select(m => m.Value))
            {
                var sign = term.StartsWith('-') ? -1 : 1;
                var body = term.TrimStart('+', '-');
                if (body.Contains('d'))
                {
                    var face = Faces.Count > 0 ? Faces.Dequeue() : 1;
                    kept.Add(face);
                    total += sign * face;
                }
                else
                {
                    modifier += sign * int.Parse(body, CultureInfo.InvariantCulture);
                    total += sign * int.Parse(body, CultureInfo.InvariantCulture);
                }
            }

            return new DiceRollResult(expression, kept, Array.Empty<int>(), modifier, total) { CheckDieIndex = 0 };
        }
    }

    private sealed record Table(CampaignRuntime Runtime, ScriptedRoller Dice, ActorDocument Aria, ActorDocument Brin, SessionDocument Session);

    private async Task<Table> TableAsync()
    {
        var system = System;
        var campaignId = Guid.NewGuid().ToString();
        async Task<ActorDocument> Hero(string name)
        {
            var doc = new ActorDocument
            {
                Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-" + name,
                Sheet = new Dictionary<string, object>
                {
                    ["HP"] = 30L, ["HP_Max"] = 30L, ["level"] = 5L, ["acMode"] = "auto", ["str"] = 10L, ["dex"] = 16L, ["con"] = 14L, ["int"] = 10L, ["wis"] = 14L, ["cha"] = 10L,
                    ["skillProficiencies"] = new List<object> { "Perception" }, ["saveProficiencies"] = new List<object> { "dex" }
                }
            };
            ActorSheetSync.ApplySheetToState(doc, system, _registry);
            await _actors.SaveAsync(doc);
            return doc;
        }

        var aria = await Hero("Aria");
        var brin = await Hero("Brin");
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var dice = new ScriptedRoller();
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, dice, system) { Places = _regions };
        runtime.Hydrate(new[] { aria, brin });
        return new Table(runtime, dice, aria, brin, session);
    }

    private sealed record Placed(RegionDocument Region, LocationDocument Place);

    private async Task<Placed> PlaceAsync(Table table, PlacedHazard placed)
    {
        var place = new LocationDocument { Id = "plain", Name = "The long passage", Description = "Sloping.", Feature = new PlaceFeature { Hazard = placed } };
        var region = new RegionDocument { Id = "regions/" + Guid.NewGuid(), CampaignId = table.Runtime.CampaignId, Name = "Dungeon", Locations = { place } };
        using var session = _dbService.Store.OpenAsyncSession();
        await session.StoreAsync(region);
        await session.SaveChangesAsync();
        return new Placed(region, place);
    }

    private static PlacedHazard StoneAt(ContentEntry entry, params string[] found) => new() { Entry = PlacedHazard.Reference("hazard", entry.Id), Found = found.ToList() };

    private static PlacedHazard Disabled(PlacedHazard placed)
    {
        placed.State = HazardState.Disabled;
        return placed;
    }

    private HazardSite SiteOf(Placed placed, ContentEntry entry, PlacedHazard stored) => new(placed.Region.Id, placed.Place.Id, entry.Name, stored, entry, HazardType);

    /// <summary>The stone's actor, made as the console makes it (Hidden, with the link), and its secret initiative roll (the stone's die, then its bonus +8).</summary>
    private async Task<ActorDocument> JoinAsync(Table table, HazardActorLink link, int die)
    {
        var definition = _content.GetActorsBySystem("srd5e").Single(a => a.Id == "rolling-stone");
        var stone = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = table.Runtime.CampaignId, Kind = ActorKind.Monster, BlueprintId = "srd5e:rolling-stone", Name = definition.Name,
            Visibility = ActorVisibility.Hidden, Hazard = link
        };
        ActorSheetSync.SeedFromBlueprint(stone, definition, System, _registry);
        await _actors.SaveAsync(stone);
        await table.Runtime.EnqueueAsync(new HydrateActorCommand(stone));
        table.Dice.Faces.Enqueue(die);
        await table.Runtime.EnqueueAsync(new JoinInitiativeCommand(stone.Id, "1d20+8", Gm));
        return stone;
    }

    private static HazardActorLink LinkOf(Placed placed) => new() { RegionId = placed.Region.Id, LocationId = placed.Place.Id, Entry = "hazard/rolling-stone" };

    private static IReadOnlyList<string> Order(Table table) => table.Runtime.Snapshot.Encounter?.Participants.Select(p => p.ActorId).ToList() ?? new List<string>();

    private static int Hp(Table table, ActorDocument actor) => table.Runtime.Snapshot.Actor(actor.Id)!.Pool("HP")!.Value;

    private static HazardCommand Cmd(HazardSite site, HazardAction action, string[] actors, string? route = null) => new(site, action, actors, Gm, route, null, null, null);

    // ---- Joining initiative ----

    [Fact]
    public async Task AComplexHazard_JoinsInitiative_WithItsOwnBonus_AndRollsInSecret_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));

        var stone = await JoinAsync(table, LinkOf(placed), die: 12);

        // 1d20 + the Rolling Stone's initiative bonus (+8): 12 + 8 = 20, ahead of Aria's 15.
        Assert.Contains("1d20+8", table.Dice.Expressions);
        var participants = table.Runtime.Snapshot.Encounter!.Participants;
        Assert.Equal(new[] { stone.Id, table.Aria.Id }, participants.Select(p => p.ActorId));
        Assert.Equal(20, participants[0].InitiativeRoll);
        Assert.All(table.Runtime.Snapshot.RollLog, r => Assert.True(r.IsSecret));
        Assert.Equal(100, Hp(table, stone));
    }

    // ---- A routine turn, and Crush on another creature's turn ----

    [Fact]
    public async Task ARoutineStep_IsSaidOnTheHazardsTurn_AndANamedStepOrADisabledHazardIsRefused_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry, "plate"));
        var site = SiteOf(placed, entry, StoneAt(entry, "plate"));
        var stone = await JoinAsync(table, LinkOf(placed), die: 12);
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));
        Assert.Equal(stone.Id, table.Runtime.Snapshot.Encounter!.Current!.ActorId);

        var roll = new HazardCommand(site, HazardAction.Routine, Array.Empty<string>(), Gm, "Roll");
        await table.Runtime.EnqueueAsync(roll);
        var unknown = new HazardCommand(site, HazardAction.Routine, Array.Empty<string>(), Gm, "Dance");
        await table.Runtime.EnqueueAsync(unknown);

        Assert.Null(roll.Refused);
        Assert.Contains("Roll", roll.Said!.Description);
        Assert.Equal("NoRoute", unknown.Refused);
        // Said only: no roll was made and nobody was hurt.
        Assert.Equal((30, 30), (Hp(table, table.Aria), Hp(table, table.Brin)));

        await table.Runtime.EnqueueAsync(Cmd(site, HazardAction.Disable, new[] { table.Aria.Id }, "wedge"));
        var late = new HazardCommand(SiteOf(placed, entry, Disabled(StoneAt(entry, "plate"))), HazardAction.Routine, Array.Empty<string>(), Gm, "Roll");
        await table.Runtime.EnqueueAsync(late);
        Assert.Equal("AlreadyDisabled", late.Refused);
    }

    [Fact]
    public async Task Crush_AppliesOnAnotherCreaturesTurn_ThroughTheSameStrikeAsATrigger_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry, "plate"));
        var site = SiteOf(placed, entry, StoneAt(entry, "plate"));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));
        var stone = await JoinAsync(table, LinkOf(placed), die: 12);
        // The stone has gone, and it is Aria's turn now.
        await table.Runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Equal(table.Aria.Id, table.Runtime.Snapshot.Encounter!.Current!.ActorId);
        table.Dice.Expressions.Clear();
        // Damage is rolled first (10d10 = 20), then Aria's Dexterity save (2 + 6 = 8 against DC 15: a failure).
        table.Dice.Faces.Enqueue(20);
        table.Dice.Faces.Enqueue(2);

        var crush = new HazardCommand(site, HazardAction.Routine, new[] { table.Aria.Id }, Gm, "Crush");
        await table.Runtime.EnqueueAsync(crush);

        Assert.Null(crush.Refused);
        Assert.Equal(table.Aria.Id, table.Runtime.Snapshot.Encounter!.Current!.ActorId);
        Assert.Contains("10d10", table.Dice.Expressions);
        Assert.Equal(10, Hp(table, table.Aria));
        Assert.Equal(30, Hp(table, table.Brin));
        Assert.Equal("Damage", table.Runtime.Snapshot.Actor(table.Aria.Id)!.UndoKind);
        Assert.Contains(stone.Id, Order(table));
    }

    [Fact]
    public async Task APassedSaveAgainstCrush_TakesNothing_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry, "plate"));
        var site = SiteOf(placed, entry, StoneAt(entry, "plate"));
        await JoinAsync(table, LinkOf(placed), die: 12);
        table.Dice.Faces.Enqueue(20);
        table.Dice.Faces.Enqueue(19);

        await table.Runtime.EnqueueAsync(new HazardCommand(site, HazardAction.Routine, new[] { table.Brin.Id }, Gm, "Crush"));

        // There is no half damage (onSave: none).
        Assert.Equal(30, Hp(table, table.Brin));
    }

    // ---- Leaving the order, and coming back ----

    [Fact]
    public async Task DisarmingAComplexHazard_TakesItOutOfInitiative_AndItsUndoPutsItBackAtTheSamePlace_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry, "plate"));
        var site = SiteOf(placed, entry, StoneAt(entry, "plate"));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Brin.Id, 5));
        var stone = await JoinAsync(table, LinkOf(placed), die: 12);
        Assert.Equal(new[] { stone.Id, table.Aria.Id, table.Brin.Id }, Order(table));

        // Wedging the plate needs no check and, once the plate is found, disarms it.
        var wedge = Cmd(site, HazardAction.Disable, new[] { table.Aria.Id }, "wedge");
        await table.Runtime.EnqueueAsync(wedge);

        Assert.Null(wedge.Refused);
        Assert.Equal(new[] { table.Aria.Id, table.Brin.Id }, Order(table));
        Assert.Equal(table.Aria.Id, table.Runtime.Snapshot.Encounter!.Current!.ActorId);

        var undo = new UndoHazardCommand(site);
        await table.Runtime.EnqueueAsync(undo);

        Assert.True(undo.Done);
        Assert.Equal(new[] { stone.Id, table.Aria.Id, table.Brin.Id }, Order(table));
        Assert.Equal(20, table.Runtime.Snapshot.Encounter!.Participants[0].InitiativeRoll);
    }

    [Fact]
    public async Task DestroyingAComplexHazard_TakesItOutOfInitiative_AndUndoingTheDamagePutsItBack_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));
        var stone = await JoinAsync(table, LinkOf(placed), die: 12);

        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(stone.Id, "HP", 100, "Aria"));

        Assert.Equal(new[] { table.Aria.Id }, Order(table));

        await table.Runtime.EnqueueAsync(new UndoLastChangeCommand(stone.Id));

        Assert.Equal(100, Hp(table, stone));
        Assert.Equal(new[] { stone.Id, table.Aria.Id }, Order(table));
    }

    [Fact]
    public async Task AHazardTheGameMasterTookOutByHand_IsNotPutBackBySomeOtherCommand_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));
        var stone = await JoinAsync(table, LinkOf(placed), die: 12);

        await table.Runtime.EnqueueAsync(new RemoveParticipantCommand(stone.Id));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Brin.Id, 5));

        Assert.Equal(new[] { table.Aria.Id, table.Brin.Id }, Order(table));
    }

    [Fact]
    public async Task AHazardTakenFromTheLibraryAlone_HasItsOwnState_WritesNoPlace_AndLeavesAndReturnsToo_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));
        var link = new HazardActorLink { Entry = "hazard/rolling-stone" };
        var stone = await JoinAsync(table, link, die: 12);
        var site = new HazardSite(string.Empty, stone.Id, entry.Name, StoneAt(entry, "plate"), entry, HazardType);

        await table.Runtime.EnqueueAsync(Cmd(site, HazardAction.Disable, new[] { table.Aria.Id }, "wedge"));

        Assert.Equal(new[] { table.Aria.Id }, Order(table));
        Assert.DoesNotContain(table.Session.Events, e => e.Description.Contains("could not be written", StringComparison.OrdinalIgnoreCase));
        var undo = new UndoHazardCommand(site);
        await table.Runtime.EnqueueAsync(undo);
        Assert.True(undo.Done);
        Assert.Equal(new[] { stone.Id, table.Aria.Id }, Order(table));
    }

    // ---- Secrecy ----

    [Fact]
    public async Task PassingTheTurnToAHiddenHazard_NamesItToNoPlayer_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));
        var stone = await JoinAsync(table, LinkOf(placed), die: 12);

        // Aria's turn, then back round to the stone's.
        await table.Runtime.EnqueueAsync(new NextTurnCommand());
        await table.Runtime.EnqueueAsync(new NextTurnCommand());
        Assert.Equal(stone.Id, table.Runtime.Snapshot.Encounter!.Current!.ActorId);

        var visible = new[] { table.Aria, table.Brin };
        var asPlayer = SessionEventRedactor.ForViewer(table.Session.Events, new CampaignAccess(table.Session.CampaignId, "player-Aria", CampaignRole.Player), visible);
        Assert.DoesNotContain(asPlayer, e => e.Description.Contains("Rolling Stone", StringComparison.OrdinalIgnoreCase));
        // The count a player is shown leaves the stone out, and gives none while it acts.
        var seen = new HashSet<string> { table.Aria.Id, table.Brin.Id };
        Assert.Null(TableVisibility.TurnPosition(table.Runtime.Snapshot.Encounter!, isDm: false, seen));
        // The game master's own log does name it.
        Assert.Contains(table.Session.Events, e => e.Description.Contains("Rolling Stone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheLinkToTheHazard_SurvivesTheConsolesSave_AndTheActorIsFoundAgain_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var definition = _content.GetActorsBySystem("srd5e").Single(a => a.Id == "rolling-stone");
        var stone = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = table.Runtime.CampaignId, Kind = ActorKind.Monster, BlueprintId = "srd5e:rolling-stone", Name = definition.Name,
            Visibility = ActorVisibility.Hidden, Hazard = new HazardActorLink { RegionId = "r1", LocationId = "passage", Entry = "hazard/rolling-stone", Levels = "17-20" }
        };
        ActorSheetSync.SeedFromBlueprint(stone, definition, System, _registry);
        var dm = TestAccess.Member(_dbService, table.Runtime.CampaignId, Gm, CampaignRole.DM);

        // The way the console saves a new actor.
        Assert.True(await _actors.TrySaveAsync(stone, dm));

        var loaded = (await _actors.GetVisibleAsync(stone.Id, dm))!;
        Assert.Equal(("r1", "passage", "hazard/rolling-stone", "17-20"), (loaded.Hazard!.RegionId, loaded.Hazard.LocationId, loaded.Hazard.Entry, loaded.Hazard.Levels));
        Assert.True(loaded.Hazard.IsPlaced);
        Assert.Equal(ActorVisibility.Hidden, loaded.Visibility);
    }

    [Fact]
    public void AnyReaderOfTheActorRegistry_MustSplitByTheHazardTag_SoTheCreatureListHasNoHazard()
    {
        // Pins 3.3: GetActorsBySystem returns complex hazards too; a reader that lists creatures drops the `hazard` tag, as the quick-add panel does. The Bestiary, the book and
        // the homebrew wizard read only the ActorBlueprint flag, which no hazard type has.
        var all = _content.GetActorsBySystem("srd5e").ToList();
        var creatures = all.Where(a => !a.Tags.Contains("hazard")).ToList();

        Assert.Contains(all, a => a.Tags.Contains("hazard"));
        Assert.DoesNotContain(creatures, a => a.Id == "rolling-stone");
        Assert.All(System.EntityTypes.Where(t => t.Hazard != null), t => Assert.False(t.ActorBlueprint));
        Assert.Equal(340, creatures.Count(a => a.PackId == "srd-5.2.1"));
    }

    [Fact]
    public async Task AComplexHazardInInitiative_IsGmOnly_NoEventNoRollAndNoRowForAPlayer_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Entry("rolling-stone");
        var placed = await PlaceAsync(table, StoneAt(entry, "plate"));
        var site = SiteOf(placed, entry, StoneAt(entry, "plate"));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 15));
        var stone = await JoinAsync(table, LinkOf(placed), die: 12);
        table.Dice.Faces.Enqueue(20);
        table.Dice.Faces.Enqueue(2);
        await table.Runtime.EnqueueAsync(new HazardCommand(site, HazardAction.Routine, Array.Empty<string>(), Gm, "Roll"));
        await table.Runtime.EnqueueAsync(new HazardCommand(site, HazardAction.Routine, new[] { table.Aria.Id }, Gm, "Crush"));
        await table.Runtime.EnqueueAsync(Cmd(site, HazardAction.Disable, new[] { table.Aria.Id }, "wedge"));

        var visible = new[] { table.Aria, table.Brin };
        var asPlayer = SessionEventRedactor.ForViewer(table.Session.Events, new CampaignAccess(table.Session.CampaignId, "player-Aria", CampaignRole.Player), visible);
        Assert.DoesNotContain(asPlayer, e => e.Type == "Hazard");
        Assert.DoesNotContain(asPlayer, e => e.Description.Contains("Rolling Stone", StringComparison.OrdinalIgnoreCase) || e.Description.Contains("Crush", StringComparison.OrdinalIgnoreCase));
        // Every roll, including its initiative, is secret; and a Hidden actor is not in a player's list, so the order never counts it for them.
        var seen = new HashSet<string> { table.Aria.Id, table.Brin.Id };
        Assert.Empty(TableVisibility.Rolls(table.Runtime.Snapshot.RollLog, isDm: false, seen));
        Assert.Equal(ActorVisibility.Hidden, table.Runtime.GetActorDocument(stone.Id)!.Visibility);
        Assert.Contains(table.Session.Events, e => e.Type == "Hazard" && e.Description.Contains("joins the initiative order", StringComparison.OrdinalIgnoreCase));
    }
}
