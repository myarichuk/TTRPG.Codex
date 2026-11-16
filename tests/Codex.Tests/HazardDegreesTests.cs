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
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>P12 (docs/HAZARDS_DESIGN.md 11): hazards on the four-degrees system. A hazard check grades through <c>dice.checkRoll</c>, a route can need a rank and
/// several successes, <c>triggersOn</c> reads outcome keys, and a set-off reaches the characters the game master chose. The real pack's three hazards, a scripted die.</summary>
[Collection("RavenDb")]
public class HazardDegreesTests : IClassFixture<RavenDbFixture>, IAsyncLifetime, IDisposable
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

    public HazardDegreesTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("HazardDegrees_" + Guid.NewGuid());
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

    private SystemDefinition System => _loader.GetDefinition("four-degrees")!;

    private ContentTypeDefinition HazardType => System.FindType("hazard")!;

    private ContentEntry Hazard(string id) => _content.GetEntries("four-degrees", "hazard", new HashSet<string> { "four-degrees-core" }).Single(e => e.Id == id);

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

    private sealed record Table(CampaignRuntime Runtime, ScriptedRoller Dice, ActorDocument Aria, ActorDocument Brin);

    // Level 5, every attribute 10 (+0). A trained rank is worth 7, expert 9. Aria has no ranks; Brin is trained in Craft, expert in Athletics.
    private static Dictionary<string, object> Hero(params (string List, string Skill)[] ranks)
    {
        var sheet = new Dictionary<string, object>
        {
            ["HP"] = 30L, ["HP_Max"] = 30L, ["level"] = 5L,
            ["str"] = 10L, ["dex"] = 10L, ["con"] = 10L, ["int"] = 10L, ["wis"] = 10L, ["cha"] = 10L
        };
        foreach (var group in ranks.GroupBy(r => r.List)) sheet[group.Key] = group.Select(r => (object)r.Skill).ToList();
        return sheet;
    }

    private async Task<Table> TableAsync()
    {
        var system = System;
        var campaignId = Guid.NewGuid().ToString();
        async Task<ActorDocument> Seed(string name, Dictionary<string, object> sheet)
        {
            var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-" + name, Sheet = sheet };
            ActorSheetSync.ApplySheetToState(doc, system, _registry);
            await _actors.SaveAsync(doc);
            return doc;
        }

        var aria = await Seed("Aria", Hero());
        var brin = await Seed("Brin", Hero(("skillTrained", "craft"), ("skillTrained", "arcana"), ("skillExpert", "athletics"), ("perceptionExpert", "perception")));
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var dice = new ScriptedRoller();
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, dice, system) { Places = _regions };
        runtime.Hydrate(new[] { aria, brin });
        return new Table(runtime, dice, aria, brin);
    }

    private sealed record Placed(RegionDocument Region, LocationDocument Place);

    private async Task<Placed> PlaceAsync(Table table, PlacedHazard placed)
    {
        var place = new LocationDocument { Id = "room", Name = "Room", Description = "Dim.", Feature = new PlaceFeature { Hazard = placed } };
        var region = new RegionDocument { Id = "regions/" + Guid.NewGuid(), CampaignId = table.Runtime.CampaignId, Name = "Hold", Locations = { place } };
        using var session = _dbService.Store.OpenAsyncSession();
        await session.StoreAsync(region);
        await session.SaveChangesAsync();
        return new Placed(region, place);
    }

    private async Task<PlacedHazard> StoredAsync(Placed placed)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return (await session.LoadAsync<RegionDocument>(placed.Region.Id))!.Locations.Single().Feature!.Hazard!;
    }

    private HazardSite SiteOf(Placed placed, ContentEntry entry, PlacedHazard stored) => new(placed.Region.Id, placed.Place.Id, entry.Name, stored, entry, HazardType);

    private static PlacedHazard Hidden(ContentEntry entry, params string[] found) =>
        new() { Entry = PlacedHazard.Reference("hazard", entry.Id), Found = found.ToList() };

    private async Task<HazardCommand> DisableAsync(Table table, Placed placed, ContentEntry entry, string route, ActorDocument who, params ActorDocument[] reach)
    {
        var command = new HazardCommand(SiteOf(placed, entry, await StoredAsync(placed)), HazardAction.Disable, new[] { who.Id }, Gm, route, null, null, null, null,
            reach.Select(a => a.Id).ToList());
        await table.Runtime.EnqueueAsync(command);
        return command;
    }

    private static int Hp(Table table, ActorDocument actor) => table.Runtime.Snapshot.Actor(actor.Id)!.Pool("HP")!.Value;

    [Fact]
    public async Task AHazardCheck_GradesThroughTheSystemsCheckRoll_AndAgreesWithThePureGrade_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        // A natural 1 with a total of 15 against DC 15 is a failure here, as it is from the Actions panel; a natural 20 with 25 is a critical success.
        table.Dice.Faces.Enqueue(1);
        var low = table.Runtime.Checks.Make(new CheckRequest { ActorId = table.Aria.Id, Check = "Athletics", Dc = 15, Bonus = 14 }, Gm);
        table.Dice.Faces.Enqueue(20);
        var high = table.Runtime.Checks.Make(new CheckRequest { ActorId = table.Aria.Id, Check = "Athletics", Dc = 15, Bonus = 5 }, Gm);

        Assert.Equal(new CheckOutcome("failure", false, null), low.Outcome! with { Mark = null });
        Assert.Equal("failure", low.Logged!.Outcome);
        Assert.Equal(("criticalSuccess", true), (high.Outcome!.Key, high.Outcome.Passed));
        Assert.Equal("criticalSuccess", high.Logged!.Outcome);
        var pure = ActorChecks.Grade(System, NullEntryResolver.Instance, new Dictionary<string, object>(), 15, 1, 15);
        Assert.Equal((low.Outcome.Key, low.Outcome.Passed), (pure.Key, pure.Passed));
    }

    [Fact]
    public async Task ARouteThatNeedsARank_IsRefusedForAnActorWithoutIt_AndRollsNothing_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var snare = Hazard("lantern-snare");
        var placed = await PlaceAsync(table, Hidden(snare, "cord"));

        var refused = await DisableAsync(table, placed, snare, "cut", table.Aria);

        Assert.Equal("RankTooLow", refused.Refused);
        Assert.Empty(table.Dice.Expressions);
        Assert.Equal(0, (await StoredAsync(placed)).Successes);
    }

    [Fact]
    public async Task ARouteThatNeedsSeveralSuccesses_CountsThem_AndDisablesOnTheLast_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var snare = Hazard("lantern-snare");
        var placed = await PlaceAsync(table, Hidden(snare, "cord"));

        // Craft, trained at level 5 is 7: 12 + 7 = 19 against DC 16 passes.
        table.Dice.Faces.Enqueue(12);
        await DisableAsync(table, placed, snare, "cut", table.Brin);
        var half = await StoredAsync(placed);
        Assert.Equal((HazardState.Hidden, 1), (half.State, half.Successes));

        table.Dice.Faces.Enqueue(12);
        await DisableAsync(table, placed, snare, "cut", table.Brin);
        var done = await StoredAsync(placed);
        Assert.Equal((HazardState.Disabled, 2), (done.State, done.Successes));
    }

    [Theory]
    [InlineData(5, true)]  // 5 + 7 = 12: a failure
    [InlineData(1, true)]  // 1 + 7 = 8: a critical failure; "failure" means failure or worse
    [InlineData(12, false)]
    public async Task TriggersOnFailure_FiresOnAFailureAndOnACriticalFailure_Async(int face, bool setOff)
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var snare = Hazard("lantern-snare");
        var placed = await PlaceAsync(table, Hidden(snare, "cord"));
        // The check, then 2d6 (one face), then Brin's Dexterity save (3 + 0 against DC 16: whole damage).
        foreach (var f in new[] { face, 9, 3 }) table.Dice.Faces.Enqueue(f);

        await DisableAsync(table, placed, snare, "cut", table.Brin);

        Assert.Equal(setOff ? HazardState.Triggered : HazardState.Hidden, (await StoredAsync(placed)).State);
        Assert.Equal(setOff ? 21 : 30, Hp(table, table.Brin));
    }

    [Fact]
    public async Task TriggersOnCriticalFailure_FiresOnThatOutcomeAlone_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var vent = Hazard("cinder-vent");
        var placed = await PlaceAsync(table, Hidden(vent, "heat", "wheel"));

        // Athletics, expert at level 5 is 9: 5 + 9 = 14 against DC 19 is a plain failure, so the vent stays quiet.
        table.Dice.Faces.Enqueue(5);
        await DisableAsync(table, placed, vent, "jam", table.Brin);
        Assert.Equal(HazardState.Hidden, (await StoredAsync(placed)).State);
        Assert.Equal(30, Hp(table, table.Brin));

        // A natural 1 (1 + 9 = 10) is a critical failure: it sets the vent off, on the one who tried, then (a ticked reach) the one beside.
        foreach (var f in new[] { 1, 10, 2, 2 }) table.Dice.Faces.Enqueue(f);
        var off = await DisableAsync(table, placed, vent, "jam", table.Brin, table.Aria);

        Assert.Equal(HazardState.Triggered, (await StoredAsync(placed)).State);
        Assert.Equal((20, 20), (Hp(table, table.Brin), Hp(table, table.Aria)));
        Assert.Contains(off.Said!.Parts, p => p.Key == "Hazard.SetOffAlso");
    }

    [Fact]
    public async Task ASetOff_WithNoReachChosen_HitsOnlyTheOneWhoTried_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var vent = Hazard("cinder-vent");
        var placed = await PlaceAsync(table, Hidden(vent, "heat", "wheel"));
        foreach (var f in new[] { 1, 10, 2 }) table.Dice.Faces.Enqueue(f);

        await DisableAsync(table, placed, vent, "jam", table.Brin);

        Assert.Equal((20, 30), (Hp(table, table.Brin), Hp(table, table.Aria)));
    }

    [Fact]
    public async Task SettingTheStateBackToNoticed_OrASetOff_StartsTheSuccessesOver_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var snare = Hazard("lantern-snare");
        var placed = await PlaceAsync(table, Hidden(snare, "cord"));

        foreach (var f in new[] { 12, 12 }) { table.Dice.Faces.Enqueue(f); await DisableAsync(table, placed, snare, "cut", table.Brin); }
        Assert.Equal((HazardState.Disabled, 2), ((await StoredAsync(placed)).State, (await StoredAsync(placed)).Successes));

        await table.Runtime.EnqueueAsync(new HazardCommand(SiteOf(placed, snare, await StoredAsync(placed)), HazardAction.SetState, Array.Empty<string>(), Gm, State: HazardState.Noticed));
        Assert.Equal((HazardState.Noticed, 0), ((await StoredAsync(placed)).State, (await StoredAsync(placed)).Successes));

        // One success is progress, not a disarm; a set-off then wipes that progress.
        table.Dice.Faces.Enqueue(12);
        await DisableAsync(table, placed, snare, "cut", table.Brin);
        Assert.Equal((HazardState.Noticed, 1), ((await StoredAsync(placed)).State, (await StoredAsync(placed)).Successes));
        foreach (var f in new[] { 1, 9, 3 }) table.Dice.Faces.Enqueue(f);
        await DisableAsync(table, placed, snare, "cut", table.Brin);
        Assert.Equal((HazardState.Triggered, 0), ((await StoredAsync(placed)).State, (await StoredAsync(placed)).Successes));
    }

    [Fact]
    public async Task AReach_HitsEachCreatureOnce_IgnoresUnknownAndRetiredOnes_AndNamesThemInTheEvent_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var vent = Hazard("cinder-vent");
        var placed = await PlaceAsync(table, Hidden(vent, "heat", "wheel"));
        // The check (a natural 1: critical failure), the damage, then a save for Brin and one for Aria (Brin is also ticked: hit once).
        foreach (var f in new[] { 1, 10, 2, 2 }) table.Dice.Faces.Enqueue(f);
        var site = SiteOf(placed, vent, await StoredAsync(placed));

        var command = new HazardCommand(site, HazardAction.Disable, new[] { table.Brin.Id }, Gm, "jam", null, null, null, null,
            new[] { table.Brin.Id, table.Aria.Id, table.Aria.Id, "nobody" });
        await table.Runtime.EnqueueAsync(command);

        Assert.Equal((20, 20), (Hp(table, table.Brin), Hp(table, table.Aria)));
        Assert.Contains(table.Aria.Id, command.Said!.ActorIds);
    }

    [Fact]
    public async Task ARankOnPerception_IsReadFromTheRankedList_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;

        // Brin is expert in Perception (the second rank), Aria has no rank in it.
        Assert.Equal(2, table.Runtime.Checks.RankOf(table.Brin.Id, "Perception"));
        Assert.True((table.Runtime.Checks.RankOf(table.Aria.Id, "Perception") ?? 0) < 2);
        Assert.Equal(1, table.Runtime.Checks.RankOf(table.Brin.Id, "Craft"));
    }

    [Fact]
    public void AMisspelledRank_IsALoaderProblem_AndARankByLabelOrNumberIsFound()
    {
        var entry = new ContentEntry
        {
            Id = "typo", Type = "hazard", Name = "typo", Description = string.Empty, PackId = "test",
            Properties = new Dictionary<string, object>
            {
                ["disarm"] = new List<object> { new Dictionary<string, object> { ["id"] = "x", ["name"] = "X", ["check"] = "Craft", ["dc"] = 15L, ["rank"] = "expret" } }
            }
        };

        Assert.Contains(HazardNumbers.Problems(entry, HazardType, System), p => p.Contains("expret", StringComparison.Ordinal));
        Assert.Equal((2, 2, 4), (HazardNumbers.RankPlace(System, "Expert")!.Value, HazardNumbers.RankPlace(System, "2")!.Value, HazardNumbers.RankPlace(System, "legendary")!.Value));
        Assert.Null(HazardNumbers.RankPlace(System, "5"));
    }

    [Fact]
    public void ThePacksHazards_LoadWithNoProblem_AndUseRanksSuccessesAndACriticalFailureTrigger()
    {
        foreach (var id in new[] { "lantern-snare", "cinder-vent", "gravity-bell" }) Assert.Empty(HazardNumbers.Problems(Hazard(id), HazardType, System));

        var routes = new[] { "lantern-snare", "cinder-vent", "gravity-bell" }
            .SelectMany(id => HazardNumbers.NoticeRoutes(HazardNumbers.For(Hazard(id), HazardType, null), HazardType.Hazard!).Concat(HazardNumbers.DisableRoutes(HazardNumbers.For(Hazard(id), HazardType, null), HazardType.Hazard!)))
            .ToList();

        Assert.Contains(routes, r => r.Rank != null);
        Assert.Contains(routes, r => r.Successes > 1);
        Assert.Contains(routes, r => r.TriggersOn.Contains("criticalFailure"));
        Assert.All(routes.Where(r => r.Rank != null), r => Assert.NotNull(HazardNumbers.RankPlace(System, r.Rank!)));
    }
}
