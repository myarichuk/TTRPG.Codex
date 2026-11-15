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

/// <summary>H4b (docs/HAZARDS_DESIGN.md 4.2, 11): <see cref="HazardCommand"/> on a runtime with a scripted die, the real srd5e traps and a real place in the database.</summary>
[Collection("RavenDb")]
public class HazardCommandTests : IClassFixture<RavenDbFixture>, IAsyncLifetime, IDisposable
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

    public HazardCommandTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("HazardCommand_" + Guid.NewGuid());
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

    private ContentEntry Trap(string id) => _content.GetEntries("srd5e", "hazard", new HashSet<string> { "srd-5.2.1" }).Single(e => e.Id == id);

    /// <summary>A die that rolls the faces it is told to, in order (a term such as 2d10 takes one and counts it whole), and does the sums of the rest (an empty queue is a 1).</summary>
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

    private sealed record Table(CampaignRuntime Runtime, ScriptedRoller Dice, ActorDocument Aria, ActorDocument Brin, ActorDocument Goblin, SessionDocument Session);

    private static Dictionary<string, object> Hero(bool perceptive) => new()
    {
        ["HP"] = 30L, ["HP_Max"] = 30L, ["level"] = 5L, ["acMode"] = "auto",
        ["str"] = 10L, ["dex"] = 16L, ["con"] = 14L, ["int"] = 10L, ["wis"] = 14L, ["cha"] = 10L,
        ["skillProficiencies"] = perceptive ? new List<object> { "Perception" } : new List<object>(),
        ["saveProficiencies"] = new List<object> { "dex" }
    };

    private async Task<Table> TableAsync()
    {
        var system = System;
        var campaignId = Guid.NewGuid().ToString();
        async Task<ActorDocument> Seed(string name, ActorKind kind, Dictionary<string, object> sheet, string? blueprint = null)
        {
            var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = kind, OwnerUserId = kind == ActorKind.PlayerCharacter ? "player-" + name : null, Sheet = sheet, BlueprintId = blueprint };
            ActorSheetSync.ApplySheetToState(doc, system, _registry);
            await _actors.SaveAsync(doc);
            return doc;
        }

        var aria = await Seed("Aria", ActorKind.PlayerCharacter, Hero(true));
        var brin = await Seed("Brin", ActorKind.PlayerCharacter, Hero(false));
        var goblin = await Seed("Goblin", ActorKind.Monster, new Dictionary<string, object>
        {
            ["HP"] = 7L, ["HP_Max"] = 7L, ["dex"] = 14L, ["wis"] = 8L, ["con"] = 10L, ["armorClass"] = 15L,
            [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod, [SheetRuleEngine.BasePrefix + "dex"] = 14L, [SheetRuleEngine.BasePrefix + "wis"] = 8L
        }, "srd5e:goblin");
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var dice = new ScriptedRoller();
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, dice, system) { Places = _regions };
        runtime.Hydrate(new[] { aria, brin, goblin });
        return new Table(runtime, dice, aria, brin, goblin, session);
    }

    private sealed record Placed(RegionDocument Region, LocationDocument Place);

    /// <summary>A region with one place that holds the hazard, stored the way the place form stores it.</summary>
    private async Task<Placed> PlaceAsync(Table table, PlacedHazard placed)
    {
        var place = new LocationDocument { Id = "pit-room", Name = "Pit room", Description = "Damp.", Feature = new PlaceFeature { Hazard = placed } };
        var region = new RegionDocument { Id = "regions/" + Guid.NewGuid(), CampaignId = table.Runtime.CampaignId, Name = "Dungeon", Locations = { place } };
        using var session = _dbService.Store.OpenAsyncSession();
        await session.StoreAsync(region);
        await session.SaveChangesAsync();
        return new Placed(region, place);
    }

    private async Task<RegionDocument> RegionAsync(Placed placed)
    {
        using var session = _dbService.Store.OpenAsyncSession();
        return (await session.LoadAsync<RegionDocument>(placed.Region.Id))!;
    }

    private async Task<PlacedHazard> StoredAsync(Placed placed) => (await RegionAsync(placed)).Locations.Single().Feature!.Hazard!;

    private HazardSite SiteOf(Placed placed, ContentEntry entry, PlacedHazard stored) => new(placed.Region.Id, placed.Place.Id, entry.Name, stored, entry, HazardType);

    private static PlacedHazard Hidden(ContentEntry entry, string? levels = null, params string[] found) =>
        new() { Entry = PlacedHazard.Reference("hazard", entry.Id), Levels = levels, Found = found.ToList() };

    private static async Task<HazardCommand> RunAsync(Table table, HazardCommand command)
    {
        await table.Runtime.EnqueueAsync(command);
        return command;
    }

    private static HazardCommand Cmd(HazardSite site, HazardAction action, string[] actors, string? route = null, IReadOnlyDictionary<string, int>? bonuses = null,
        HazardState? state = null, string[]? routes = null) =>
        new(site, action, actors, Gm, route, routes, state, bonuses);

    private static int Hp(Table table, ActorDocument actor) => table.Runtime.Snapshot.Actor(actor.Id)!.Pool("HP")!.Value;

    // ---- Notice ----

    [Fact]
    public async Task ANoticeSweep_ComparesPassiveScores_RollsNothing_AndRecordsNothing_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle));

        var command = await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Notice, new[] { table.Aria.Id, table.Brin.Id }));

        // Passive Perception 15 (proficient) meets the DC 15; Brin's 12 does not.
        Assert.Equal(new[] { new HazardPass(table.Aria.Id, "needle") }, command.Passes);
        Assert.Empty(table.Dice.Expressions);
        Assert.Empty(table.Session.RollLog);
        Assert.Equal(HazardState.Hidden, (await StoredAsync(placed)).State);
        Assert.Empty((await StoredAsync(placed)).Found);
    }

    [Fact]
    public async Task ANoticeSweep_NeverCompletesARouteWithNoCheck_AndRollsARouteTheSheetHasNoPassiveFor_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var site = SiteOf(await PlaceAsync(table, Hidden(statue)), statue, Hidden(statue));

        var first = await RunAsync(table, Cmd(site, HazardAction.Notice, new[] { table.Aria.Id }));

        // Detect Magic (aura) has no check, and Read the glyph comes after the glyph: only the glyph (DC 10) and the plate (DC 15) are tried, by passive Perception 15.
        Assert.Equal(new[] { "glyph", "plate" }, first.Passes.Select(p => p.RouteKey).Order());
        Assert.DoesNotContain(first.Passes, p => p.RouteKey == "aura");

        // Once the glyph is found, Arcana (no passive score on the sheet) is rolled, not compared.
        var found = Hidden(statue, null, "glyph");
        table.Dice.Faces.Enqueue(12);
        var second = await RunAsync(table, Cmd(SiteOf(await PlaceAsync(table, found), statue, found), HazardAction.Notice, new[] { table.Aria.Id }));

        Assert.Contains("1d20+0", table.Dice.Expressions);
        Assert.DoesNotContain(second.Passes, p => p.RouteKey == "aura");
        Assert.Contains(second.Passes, p => p.RouteKey == "plate");
        Assert.Single(table.Session.RollLog);
    }

    [Fact]
    public async Task ANoCheckDetectRoute_IsDoneWhenPicked_RecordsTheRouteAndWhoFoundIt_AndNeverDisables_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var placed = await PlaceAsync(table, Hidden(statue));

        var command = await RunAsync(table, Cmd(SiteOf(placed, statue, Hidden(statue)), HazardAction.Find, new[] { table.Brin.Id }, "aura"));

        var stored = await StoredAsync(placed);
        Assert.Null(command.Refused);
        Assert.Equal(HazardState.Noticed, stored.State);
        Assert.Equal(new[] { "aura" }, stored.Found);
        Assert.Equal(new[] { table.Brin.Id }, stored.NoticedBy);
        Assert.Empty(table.Dice.Expressions);
    }

    [Fact]
    public async Task ARouteWithAfter_IsRefusedUntilItsRouteIsFound_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var placed = await PlaceAsync(table, Hidden(statue));

        var early = await RunAsync(table, Cmd(SiteOf(placed, statue, Hidden(statue)), HazardAction.Find, new[] { table.Aria.Id }, "meaning"));
        var earlyDisarm = await RunAsync(table, Cmd(SiteOf(placed, statue, Hidden(statue)), HazardAction.Disable, new[] { table.Aria.Id }, "deface"));
        table.Dice.Faces.Enqueue(15);
        var glyph = await RunAsync(table, Cmd(SiteOf(placed, statue, Hidden(statue)), HazardAction.Find, new[] { table.Aria.Id }, "glyph"));
        var late = await RunAsync(table, Cmd(SiteOf(placed, statue, await StoredAsync(placed)), HazardAction.Disable, new[] { table.Aria.Id }, "deface"));

        Assert.Equal(("Locked", "Locked"), (early.Refused, earlyDisarm.Refused));
        Assert.Null(glyph.Refused);
        // Deface needs no check and comes after the glyph: done by what it needs, and a successful disarm route disarms.
        Assert.Null(late.Refused);
        Assert.Equal(HazardState.Disabled, (await StoredAsync(placed)).State);
    }

    [Fact]
    public async Task ADetectRouteWithACheck_IsRolledByTheChosenCharacter_AndAFailureRecordsNothing_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle));
        table.Dice.Faces.Enqueue(5);

        var missed = await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Find, new[] { table.Brin.Id }, "needle"));

        Assert.Empty((await StoredAsync(placed)).Found);
        Assert.Equal(HazardState.Hidden, (await StoredAsync(placed)).State);
        Assert.Null(missed.Refused);

        table.Dice.Faces.Enqueue(14);
        await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Find, new[] { table.Brin.Id }, "needle"));

        var stored = await StoredAsync(placed);
        Assert.Equal((HazardState.Noticed, "needle"), (stored.State, Assert.Single(stored.Found)));
    }

    [Fact]
    public async Task MarkingNoticed_RevealsItToThoseCharacters_AndRecordsOnlyOpenRoutes_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var placed = await PlaceAsync(table, Hidden(statue));

        await RunAsync(table, Cmd(SiteOf(placed, statue, Hidden(statue)), HazardAction.MarkNoticed, new[] { table.Aria.Id }, routes: new[] { "glyph", "meaning" }));

        var stored = await StoredAsync(placed);
        // "meaning" comes after the glyph, which was not found before this call listed both: only the glyph is recorded.
        Assert.Equal(HazardState.Noticed, stored.State);
        Assert.Equal(new[] { "glyph" }, stored.Found);
        Assert.Equal(new[] { table.Aria.Id }, stored.NoticedBy);
    }

    // ---- Disable ----

    [Fact]
    public async Task ADisableRouteWithACheck_OneSuccessDisarms_AndAFailureThatTriggersOnIsNot_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle, null, "needle"));
        table.Dice.Faces.Enqueue(15);

        var done = await RunAsync(table, Cmd(SiteOf(placed, needle, await StoredAsync(placed)), HazardAction.Disable, new[] { table.Aria.Id }, "disarm"));

        // Sleight of Hand: d20 15 + Dexterity 3 against DC 15.
        Assert.Contains("1d20+3", table.Dice.Expressions);
        Assert.Equal((HazardState.Disabled, 1), ((await StoredAsync(placed)).State, (await StoredAsync(placed)).Successes));
        Assert.Null(done.Refused);
        Assert.Equal(30, Hp(table, table.Aria));
    }

    [Fact]
    public async Task AFailedDisarm_ThatTheRouteListsInTriggersOn_SetsItOffOnTheOneWhoTried_AndThatDamageUndoes_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle, null, "needle"));
        // The failed Sleight of Hand (5 + 3 against 15), then the damage die (1d10 = 8), then the Constitution save (12 + 2 against DC 11: a pass, so half).
        foreach (var face in new[] { 5, 8, 12 }) table.Dice.Faces.Enqueue(face);

        await RunAsync(table, Cmd(SiteOf(placed, needle, await StoredAsync(placed)), HazardAction.Disable, new[] { table.Aria.Id }, "disarm"));

        Assert.Equal(HazardState.Triggered, (await StoredAsync(placed)).State);
        Assert.Equal(26, Hp(table, table.Aria));
        Assert.Equal("Damage", table.Runtime.Snapshot.Actor(table.Aria.Id)!.UndoKind);

        await table.Runtime.EnqueueAsync(new UndoLastChangeCommand(table.Aria.Id));

        Assert.Equal(30, Hp(table, table.Aria));
    }

    [Fact]
    public async Task AFailureTheRouteDoesNotListInTriggersOn_SetsNothingOff_AndSuccessesAddUp_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Synthetic("homebrew-vault", new Dictionary<string, object>
        {
            ["disarm"] = new List<object> { new Dictionary<string, object> { ["id"] = "pick", ["name"] = "Pick it", ["check"] = "Sleight of Hand", ["dc"] = 12L, ["successes"] = 2L } },
            ["saveAttribute"] = "Dexterity", ["saveDc"] = 12L, ["damage"] = new List<object> { new Dictionary<string, object> { ["dice"] = "1d6", ["type"] = "fire" } }
        });
        var placed = await PlaceAsync(table, Hidden(entry));
        table.Dice.Faces.Enqueue(2);
        await RunAsync(table, Cmd(SiteOf(placed, entry, Hidden(entry)), HazardAction.Disable, new[] { table.Aria.Id }, "pick"));
        Assert.Equal((HazardState.Hidden, 0), ((await StoredAsync(placed)).State, (await StoredAsync(placed)).Successes));

        table.Dice.Faces.Enqueue(15);
        await RunAsync(table, Cmd(SiteOf(placed, entry, await StoredAsync(placed)), HazardAction.Disable, new[] { table.Aria.Id }, "pick"));
        Assert.Equal((HazardState.Hidden, 1), ((await StoredAsync(placed)).State, (await StoredAsync(placed)).Successes));

        table.Dice.Faces.Enqueue(15);
        await RunAsync(table, Cmd(SiteOf(placed, entry, await StoredAsync(placed)), HazardAction.Disable, new[] { table.Aria.Id }, "pick"));
        Assert.Equal((HazardState.Disabled, 2), ((await StoredAsync(placed)).State, (await StoredAsync(placed)).Successes));
        Assert.Equal(30, Hp(table, table.Aria));
    }

    // ---- Trigger ----

    [Fact]
    public async Task ATriggerWithASave_RollsTheDamageOnce_FullOnAFailedSave_HalfOnAPassed_AndEachTargetsDamageUndoes_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle));
        // Damage 1d10 = 8 once. Aria's Constitution save 12 + 2 = 14 passes DC 11 (half: 4); Brin's 3 + 2 = 5 fails (all 8).
        foreach (var face in new[] { 8, 12, 3 }) table.Dice.Faces.Enqueue(face);

        await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Trigger, new[] { table.Aria.Id, table.Brin.Id }));

        Assert.Equal((26, 22), (Hp(table, table.Aria), Hp(table, table.Brin)));
        Assert.Equal(HazardState.Triggered, (await StoredAsync(placed)).State);
        Assert.Equal(1, table.Dice.Expressions.Count(e => e == "1d10"));
        await table.Runtime.EnqueueAsync(new UndoLastChangeCommand(table.Aria.Id));
        await table.Runtime.EnqueueAsync(new UndoLastChangeCommand(table.Brin.Id));
        Assert.Equal((30, 30), (Hp(table, table.Aria), Hp(table, table.Brin)));
    }

    [Fact]
    public async Task ATriggerWithNoSave_HitsEveryTarget_WithEachDamagePart_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var pit = Trap("spiked-pit");
        var placed = await PlaceAsync(table, Hidden(pit));
        table.Dice.Faces.Enqueue(4); // 1d6 bludgeoning
        table.Dice.Faces.Enqueue(9); // 2d8 piercing

        await RunAsync(table, Cmd(SiteOf(placed, pit, Hidden(pit)), HazardAction.Trigger, new[] { table.Aria.Id }));

        Assert.Equal(30 - 4 - 9, Hp(table, table.Aria));
        Assert.Equal(new[] { "1d6", "2d8" }, table.Dice.Expressions);

        // Both parts are one change to the creature: its Undo takes them back together, not just the last part.
        await table.Runtime.EnqueueAsync(new UndoLastChangeCommand(table.Aria.Id));
        Assert.Equal(30, Hp(table, table.Aria));
    }

    [Fact]
    public async Task TheHazardsOwnLastChange_IsTakenBackWithWhatItRecorded_OnlyWhileNothingHasChangedItSince_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var placed = await PlaceAsync(table, Hidden(statue));
        var key = HazardLedger.KeyOf(placed.Region.Id, placed.Place.Id);
        await RunAsync(table, Cmd(SiteOf(placed, statue, Hidden(statue)), HazardAction.Find, new[] { table.Aria.Id }, "aura"));
        Assert.Contains(key, table.Runtime.Snapshot.HazardUndo);

        var undo = new UndoHazardCommand(SiteOf(placed, statue, await StoredAsync(placed)));
        await table.Runtime.EnqueueAsync(undo);

        var stored = await StoredAsync(placed);
        Assert.True(undo.Done);
        Assert.Equal((HazardState.Hidden, 0, 0), (stored.State, stored.Found.Count, stored.NoticedBy.Count));
        Assert.DoesNotContain(key, table.Runtime.Snapshot.HazardUndo);

        // A second Find, then the game master changes the hazard on the place's page: the undo is refused and nothing is put back over it.
        await RunAsync(table, Cmd(SiteOf(placed, statue, Hidden(statue)), HazardAction.Find, new[] { table.Aria.Id }, "aura"));
        await _regions.PatchPlacedHazardAsync(table.Runtime.CampaignId, placed.Region.Id, placed.Place.Id, h => { h.State = HazardState.Triggered; return h; });
        var refused = new UndoHazardCommand(SiteOf(placed, statue, await StoredAsync(placed)));
        await table.Runtime.EnqueueAsync(refused);

        Assert.False(refused.Done);
        Assert.Equal(HazardState.Triggered, (await StoredAsync(placed)).State);
        Assert.Contains("cannot be taken back", refused.Said!.Description);
        Assert.DoesNotContain(SessionEventRedactor.ForPlayers(table.Session.Events, new[] { table.Aria }), e => e.Type == "Hazard");
    }

    [Fact]
    public async Task InAnEncounter_TheHazardsLastChange_IsTakenBackInTheEncounter_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle, null, "needle"));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 12));
        table.Dice.Faces.Enqueue(15);
        var key = HazardLedger.KeyOf(placed.Region.Id, placed.Place.Id);
        await RunAsync(table, Cmd(SiteOf(placed, needle, await StoredAsync(placed)), HazardAction.Disable, new[] { table.Aria.Id }, "disarm"));
        Assert.Equal(HazardState.Disabled, table.Runtime.Snapshot.LiveHazards[key].State);

        var undo = new UndoHazardCommand(SiteOf(placed, needle, await StoredAsync(placed)));
        await table.Runtime.EnqueueAsync(undo);

        Assert.True(undo.Done);
        Assert.Equal((HazardState.Hidden, 0), (table.Runtime.Snapshot.LiveHazards[key].State, table.Runtime.Snapshot.LiveHazards[key].Successes));
    }

    [Fact]
    public async Task ADamagePartWithACount_RollsItsDiceThatManyTimes_AndNoHalfMeansNoneOnAPassedSave_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var darts = Trap("poisoned-darts");
        var placed = await PlaceAsync(table, Hidden(darts));
        // The count 1d3 comes up 2; two darts of 1d6 hit for 3 and 4; Aria's Dexterity save (proficient: 4 + 3 + 3) of 4 fails DC 13.
        foreach (var face in new[] { 2, 3, 4, 4 }) table.Dice.Faces.Enqueue(face);

        await RunAsync(table, Cmd(SiteOf(placed, darts, Hidden(darts)), HazardAction.Trigger, new[] { table.Aria.Id }));

        Assert.Equal(30 - 7, Hp(table, table.Aria));
        Assert.Equal(new[] { "1d3", "1d6", "1d6" }, table.Dice.Expressions.Take(3));
    }

    [Fact]
    public async Task APlacementsLevelRow_GivesItsDcAndDamage_NotTheBaseOnes_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle, "5-10"));
        foreach (var face in new[] { 8, 14 }) table.Dice.Faces.Enqueue(face);

        var command = await RunAsync(table, Cmd(SiteOf(placed, needle, await StoredAsync(placed)), HazardAction.Trigger, new[] { table.Aria.Id }));

        // The 5-10 row: save DC 13 and 2d10 (not DC 11 and 1d10).
        Assert.Equal(13, Assert.Single(command.Checks).Dc);
        Assert.Equal("2d10", table.Dice.Expressions[0]);
        Assert.Equal(30 - 8 / 2, Hp(table, table.Aria));
    }

    [Fact]
    public async Task AnAttack_RollsAgainstArmorClass_AMissDealsNothing_AndAPrintedTargetsNumberIsAskedFor_Async()
    {
        // srd5e's traps have no attack; a system whose hazards do (Pathfinder's) names the field in its hazard block, which this stands in for.
        var effect = HazardType.Hazard!.Effect!;
        effect.Attack = "attackBonus";
        try
        {
            await AttackAsync();
        }
        finally
        {
            effect.Attack = null;
        }
    }

    private async Task AttackAsync()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Synthetic("homebrew-dart-wall", new Dictionary<string, object>
        {
            ["attackBonus"] = 5L, ["damage"] = new List<object> { new Dictionary<string, object> { ["dice"] = "1d6", ["type"] = "piercing" } }
        });
        var placed = await PlaceAsync(table, Hidden(entry));
        // Damage 1d6 = 5 once. Aria (armor class 13): 5 + 5 = 10 misses; Goblin's printed armor class 15: 12 + 5 = 17 hits.
        foreach (var face in new[] { 5, 5, 12 }) table.Dice.Faces.Enqueue(face);

        await RunAsync(table, Cmd(SiteOf(placed, entry, Hidden(entry)), HazardAction.Trigger, new[] { table.Aria.Id, table.Goblin.Id }));

        Assert.Equal((30, 2), (Hp(table, table.Aria), Hp(table, table.Goblin)));
    }

    [Fact]
    public async Task APrintedCreaturesSave_NeedsItsNumberTyped_ThenRollsOnIt_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle));
        table.Dice.Faces.Enqueue(6);

        var asked = await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Trigger, new[] { table.Goblin.Id }));

        Assert.Equal(new[] { table.Goblin.Id }, asked.NeedsNumber);
        Assert.Equal(7, Hp(table, table.Goblin));

        foreach (var face in new[] { 6, 4 }) table.Dice.Faces.Enqueue(face);
        await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Trigger, new[] { table.Goblin.Id }, bonuses: new Dictionary<string, int> { [asked.Asks.Single().Key] = 1 }));

        // 4 + 1 = 5 fails DC 11: the whole 6.
        Assert.Equal(1, Hp(table, table.Goblin));
    }

    [Fact]
    public async Task ASweepOfAPrintedCreature_AsksForItsPassivePerceptionScoreAndItsArcanaBonusSeparately_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var found = Hidden(statue, null, "glyph");
        var placed = await PlaceAsync(table, found);
        var site = SiteOf(placed, statue, found);

        var asked = await RunAsync(table, Cmd(site, HazardAction.Notice, new[] { table.Goblin.Id }));

        var passive = Assert.Single(asked.Asks, a => a.Kind == HazardAskKind.Passive);
        var arcana = Assert.Single(asked.Asks, a => a.Kind == HazardAskKind.Bonus);
        Assert.NotEqual(passive.Key, arcana.Key);
        Assert.Equal(2, asked.Asks.Count);

        // A passive score of 16 passes the plate (DC 15); the Arcana bonus of 4 is added to the die. Neither number is used for the other.
        table.Dice.Faces.Enqueue(9);
        var sent = await RunAsync(table, Cmd(site, HazardAction.Notice, new[] { table.Goblin.Id }, bonuses: new Dictionary<string, int> { [passive.Key] = 16, [arcana.Key] = 4 }));

        Assert.Empty(sent.Asks);
        Assert.Contains(sent.Passes, p => p.ActorId == table.Goblin.Id && p.RouteKey == "plate");
        Assert.Contains("1d20+4", table.Dice.Expressions);
    }

    [Fact]
    public async Task AnAttackOnAPrintedCreature_AsksForItsArmorClassAndItsSaveBonusSeparately_Async()
    {
        var effect = HazardType.Hazard!.Effect!;
        effect.Attack = "attackBonus";
        try
        {
            await ArmorAndSaveAsync();
        }
        finally
        {
            effect.Attack = null;
        }
    }

    private async Task ArmorAndSaveAsync()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        // A printed creature with no armor class on its sheet, so the table has to be told.
        var ogre = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = table.Runtime.CampaignId, Name = "Ogre", Kind = ActorKind.Monster,
            Sheet = new Dictionary<string, object> { ["HP"] = 30L, ["HP_Max"] = 30L, [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod }
        };
        ActorSheetSync.ApplySheetToState(ogre, System, _registry);
        await _actors.SaveAsync(ogre);
        table.Runtime.Hydrate(new[] { ogre });
        var entry = Synthetic("homebrew-dart-wall", new Dictionary<string, object>
        {
            ["attackBonus"] = 5L, ["saveAttribute"] = "Dexterity", ["saveDc"] = 12L,
            ["damage"] = new List<object> { new Dictionary<string, object> { ["dice"] = "1d6", ["type"] = "piercing" } }
        });
        var placed = await PlaceAsync(table, Hidden(entry));
        var site = SiteOf(placed, entry, Hidden(entry));
        HazardCommand Trigger(Dictionary<string, int>? bonuses = null) => Cmd(site, HazardAction.Trigger, new[] { ogre.Id }, bonuses: bonuses);

        table.Dice.Faces.Enqueue(3);
        var first = await RunAsync(table, Trigger());
        var armor = Assert.Single(first.Asks);
        Assert.Equal(HazardAskKind.ArmorClass, armor.Kind);

        // Armor class 16: the attack (10 + 5 = 15) misses, and no save is asked for.
        foreach (var face in new[] { 4, 10 }) table.Dice.Faces.Enqueue(face);
        var miss = await RunAsync(table, Trigger(new Dictionary<string, int> { [armor.Key] = 16 }));
        Assert.Empty(miss.Asks);
        Assert.Equal(30, Hp(table, ogre));

        // Armor class 15: it hits, and now the save bonus is its own question.
        foreach (var face in new[] { 4, 10 }) table.Dice.Faces.Enqueue(face);
        var hit = await RunAsync(table, Trigger(new Dictionary<string, int> { [armor.Key] = 15 }));
        var save = Assert.Single(hit.Asks);
        Assert.Equal(HazardAskKind.Bonus, save.Kind);
        Assert.NotEqual(armor.Key, save.Key);

        // Save bonus 1 (8 + 1 = 9 against DC 12 fails: the whole 4); had the armor class 15 been used as the bonus, the save would have passed.
        foreach (var face in new[] { 4, 10, 8 }) table.Dice.Faces.Enqueue(face);
        await RunAsync(table, Trigger(new Dictionary<string, int> { [armor.Key] = 15, [save.Key] = 1 }));
        Assert.Equal(26, Hp(table, ogre));
        Assert.Contains("1d20+1", table.Dice.Expressions);
    }

    [Fact]
    public async Task TheStatePicker_IsALoggedCommand_AndHiddenStartsOver_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, new PlacedHazard { Entry = "hazard/poisoned-needle", State = HazardState.Disabled, Successes = 2, NoticedBy = { "x" }, Found = { "needle" } });

        await RunAsync(table, Cmd(SiteOf(placed, needle, await StoredAsync(placed)), HazardAction.SetState, Array.Empty<string>(), state: HazardState.Hidden));

        var stored = await StoredAsync(placed);
        Assert.Equal((HazardState.Hidden, 0, 0, 0), (stored.State, stored.Successes, stored.NoticedBy.Count, stored.Found.Count));
        Assert.Contains(table.Session.Events, e => e.Type == "Hazard" && e.Description.Contains("hidden"));
    }

    [Fact]
    public async Task TheEncountersPlace_IsKeptOnTheEncounter_AndLoggedForTheGmOnly_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 12));

        await table.Runtime.EnqueueAsync(new SetEncounterPlaceCommand("pit-room", "Pit room"));

        Assert.Equal("pit-room", table.Runtime.Snapshot.Encounter!.LocationId);
        Assert.Equal("pit-room", (await _encounters.GetAsync(table.Runtime.Snapshot.Encounter.Id))!.LocationId);
        Assert.DoesNotContain(SessionEventRedactor.ForPlayers(table.Session.Events, new[] { table.Aria }), e => e.Type == "Hazard");
        Assert.Contains(table.Session.Events, e => e.Type == "Hazard" && e.Description.Contains("Pit room"));
    }

    // ---- The table's write ----

    [Fact]
    public async Task ATableWrite_PatchesOnlyTheHazard_APlaceEditedMeanwhileKeepsItsEdits_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var placed = await PlaceAsync(table, Hidden(statue));
        var site = SiteOf(placed, statue, Hidden(statue));
        using (var session = _dbService.Store.OpenAsyncSession())
        {
            var region = await session.LoadAsync<RegionDocument>(placed.Region.Id);
            region.Locations.Single().Description = "Edited while the table read it.";
            region.Locations.Single().Feature!.Lock = "A rusty lock.";
            await session.SaveChangesAsync();
        }

        await RunAsync(table, Cmd(site, HazardAction.Find, new[] { table.Aria.Id }, "aura"));

        var after = (await RegionAsync(placed)).Locations.Single();
        Assert.Equal(("Edited while the table read it.", "A rusty lock.", HazardState.Noticed), (after.Description, after.Feature!.Lock, after.Feature.Hazard!.State));
    }

    [Fact]
    public async Task InAnEncounter_TheStateIsKeptInTheEncounter_AndWrittenToThePlaceWhenItEnds_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var placed = await PlaceAsync(table, Hidden(statue));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 12));
        var key = HazardLedger.KeyOf(placed.Region.Id, placed.Place.Id);

        await RunAsync(table, Cmd(SiteOf(placed, statue, Hidden(statue)), HazardAction.Find, new[] { table.Aria.Id }, "aura"));

        Assert.Equal(HazardState.Hidden, (await StoredAsync(placed)).State);
        Assert.Equal(HazardState.Noticed, table.Runtime.Snapshot.LiveHazards[key].State);
        // A table that restarts reads it back from the stored encounter (the database gives the object back as plain JSON).
        var encounterId = table.Runtime.Snapshot.Encounter!.Id;
        var reloaded = HazardLedger.Read((await _encounters.GetAsync(encounterId))!.ActiveEffects[key]);
        Assert.Equal(("aura", HazardState.Noticed), (Assert.Single(reloaded!.Current.Found), reloaded.Current.State));

        await table.Runtime.EnqueueAsync(new EndEncounterCommand());

        var stored = await StoredAsync(placed);
        Assert.Equal(HazardState.Noticed, stored.State);
        Assert.Equal(new[] { "aura" }, stored.Found);
        Assert.DoesNotContain((await _encounters.GetAsync(encounterId))!.ActiveEffects.Keys, k => k.StartsWith(HazardLedger.KeyPrefix));
    }

    [Fact]
    public async Task AtEncounterEnd_NothingChanged_WritesNothing_AndAPlaceChangedMeanwhile_IsNotWrittenOver_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var untouched = await PlaceAsync(table, Hidden(statue));
        var changed = await PlaceAsync(table, Hidden(statue));
        await table.Runtime.EnqueueAsync(new SetInitiativeCommand(table.Aria.Id, 12));
        // The encounter takes its copies: one hazard is touched without a change (a sweep reports only), the other is found.
        await RunAsync(table, Cmd(SiteOf(untouched, statue, Hidden(statue)), HazardAction.SetState, Array.Empty<string>(), state: HazardState.Hidden));
        await RunAsync(table, Cmd(SiteOf(changed, statue, Hidden(statue)), HazardAction.Find, new[] { table.Aria.Id }, "aura"));
        var before = (await RegionAsync(untouched)).UpdatedAt;
        // Meanwhile the game master sets the second place's hazard by hand on the place's page.
        await _regions.PatchPlacedHazardAsync(table.Runtime.CampaignId, changed.Region.Id, changed.Place.Id, h => { h.State = HazardState.Triggered; return h; });
        var changedBefore = (await RegionAsync(changed)).UpdatedAt;

        await table.Runtime.EnqueueAsync(new EndEncounterCommand());

        Assert.Equal(before, (await RegionAsync(untouched)).UpdatedAt);
        Assert.Equal(HazardState.Triggered, (await StoredAsync(changed)).State);
        Assert.Empty((await StoredAsync(changed)).Found);
        Assert.Equal(changedBefore, (await RegionAsync(changed)).UpdatedAt);
    }

    [Fact]
    public async Task APlaceFormSave_BetweenTheHazardWritesLoadAndSave_IsNotLost_AndNeitherIsTheHazardChange_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var statue = Trap("fire-casting-statue");
        var placed = await PlaceAsync(table, Hidden(statue));
        var calls = 0;

        var patched = await _regions.PatchPlacedHazardAsync(table.Runtime.CampaignId, placed.Region.Id, placed.Place.Id, h =>
        {
            // The place form saves now: the hazard write has loaded the place and has not saved it yet.
            if (calls++ == 0)
            {
                Task.Run(async () =>
                {
                    using var session = _dbService.Store.OpenAsyncSession();
                    var region = await session.LoadAsync<RegionDocument>(placed.Region.Id);
                    region.Locations.Single().Description = "Saved by the place form in between.";
                    await session.SaveChangesAsync();
                }).GetAwaiter().GetResult();
            }

            h.State = HazardState.Noticed;
            return h;
        });

        var after = (await RegionAsync(placed)).Locations.Single();
        Assert.True(patched);
        Assert.Equal(2, calls);
        Assert.Equal(("Saved by the place form in between.", HazardState.Noticed), (after.Description, after.Feature!.Hazard!.State));
    }

    [Fact]
    public async Task ATablesWriteThatWasReadBeforeAnotherTablesWrite_IsNotWrittenOver_AndTheGmIsTold_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var entry = Synthetic("homebrew-vault", new Dictionary<string, object>
        {
            ["disarm"] = new List<object> { new Dictionary<string, object> { ["id"] = "pick", ["name"] = "Pick it", ["check"] = "Sleight of Hand", ["dc"] = 12L, ["successes"] = 2L } }
        });
        var placed = await PlaceAsync(table, Hidden(entry));
        var stale = SiteOf(placed, entry, Hidden(entry));
        foreach (var face in new[] { 15, 15 }) table.Dice.Faces.Enqueue(face);

        // Two tabs both read no successes; the second one's write would put one success over the first one's.
        await RunAsync(table, Cmd(stale, HazardAction.Disable, new[] { table.Aria.Id }, "pick"));
        await RunAsync(table, Cmd(stale, HazardAction.Disable, new[] { table.Brin.Id }, "pick"));

        Assert.Equal(1, (await StoredAsync(placed)).Successes);
        Assert.Contains(table.Session.Events, e => e.Type == "Hazard" && e.Description.Contains("the table's state was not written"));
    }

    [Fact]
    public async Task APrintedCreatureWaitingForANumber_IsFinishedWithTheSameRoll_AndTheOnesAlreadyHitAreNotHitTwice_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle));
        // Damage 1d10 = 8 once; Aria's save 12 + 2 passes DC 11 (half: 4).
        foreach (var face in new[] { 8, 12 }) table.Dice.Faces.Enqueue(face);
        var first = await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Trigger, new[] { table.Aria.Id, table.Goblin.Id }));
        Assert.Equal(new[] { table.Goblin.Id }, first.NeedsNumber);
        Assert.Equal((26, 7), (Hp(table, table.Aria), Hp(table, table.Goblin)));

        // The repeat is for the creature that waited, with the damage already rolled: Aria is not touched and no new damage is rolled.
        table.Dice.Faces.Enqueue(12); // the goblin's save: 12 + 1 passes, half of 8
        var repeat = await RunAsync(table, new HazardCommand(SiteOf(placed, needle, await StoredAsync(placed)), HazardAction.Trigger, new[] { table.Goblin.Id }, Gm, null, null, null,
            new Dictionary<string, int> { [first.Asks.Single().Key] = 1 }, first.DamageRolled));

        Assert.Empty(repeat.NeedsNumber);
        Assert.Equal((26, 3), (Hp(table, table.Aria), Hp(table, table.Goblin)));
        Assert.Equal(1, table.Dice.Expressions.Count(e => e == "1d10"));
        // Both hits are one undo point for the hazard.
        var undo = new UndoHazardCommand(SiteOf(placed, needle, await StoredAsync(placed)));
        await table.Runtime.EnqueueAsync(undo);
        Assert.True(undo.Done);
        Assert.Equal((30, 7), (Hp(table, table.Aria), Hp(table, table.Goblin)));
    }

    [Fact]
    public async Task TheHazardsUndo_AfterATrigger_TakesBackEveryTargetsDamageWithTheState_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle));
        foreach (var face in new[] { 8, 12, 3 }) table.Dice.Faces.Enqueue(face);
        await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Trigger, new[] { table.Aria.Id, table.Brin.Id }));
        Assert.Equal((26, 22), (Hp(table, table.Aria), Hp(table, table.Brin)));

        var undo = new UndoHazardCommand(SiteOf(placed, needle, await StoredAsync(placed)));
        await table.Runtime.EnqueueAsync(undo);

        Assert.True(undo.Done);
        Assert.Equal((30, 30), (Hp(table, table.Aria), Hp(table, table.Brin)));
        Assert.Equal(HazardState.Hidden, (await StoredAsync(placed)).State);
        Assert.DoesNotContain(HazardLedger.KeyOf(placed.Region.Id, placed.Place.Id), table.Runtime.Snapshot.HazardUndo);
    }

    [Fact]
    public async Task TheHazardsUndo_AfterAFailedDisarmThatSetItOff_TakesBackTheDamageToo_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle, null, "needle"));
        foreach (var face in new[] { 5, 8, 12 }) table.Dice.Faces.Enqueue(face);
        await RunAsync(table, Cmd(SiteOf(placed, needle, await StoredAsync(placed)), HazardAction.Disable, new[] { table.Aria.Id }, "disarm"));
        Assert.Equal(26, Hp(table, table.Aria));

        var undo = new UndoHazardCommand(SiteOf(placed, needle, await StoredAsync(placed)));
        await table.Runtime.EnqueueAsync(undo);

        Assert.True(undo.Done);
        Assert.Equal(30, Hp(table, table.Aria));
        Assert.Equal(HazardState.Hidden, (await StoredAsync(placed)).State);
    }

    [Fact]
    public async Task TheHazardsUndo_IsRefusedWhole_WhenACreatureItHurtChangedSince_NothingIsLeftHalfUndone_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle));
        foreach (var face in new[] { 8, 12, 3 }) table.Dice.Faces.Enqueue(face);
        await RunAsync(table, Cmd(SiteOf(placed, needle, Hidden(needle)), HazardAction.Trigger, new[] { table.Aria.Id, table.Brin.Id }));
        // Brin takes another hit after the trap: her last change is no longer the trap's damage.
        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(table.Brin.Id, "HP", 1, string.Empty));

        var undo = new UndoHazardCommand(SiteOf(placed, needle, await StoredAsync(placed)));
        await table.Runtime.EnqueueAsync(undo);

        Assert.False(undo.Done);
        Assert.Equal((26, 21), (Hp(table, table.Aria), Hp(table, table.Brin)));
        Assert.Equal(HazardState.Triggered, (await StoredAsync(placed)).State);
        Assert.Contains("has changed since", undo.Said!.Description);
    }

    // ---- Secrecy ----

    [Fact]
    public async Task EverythingAHazardCommandSays_IsGmOnly_APlayerSeesNoEventAndNoRoll_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var placed = await PlaceAsync(table, Hidden(needle, null, "needle"));
        var site = SiteOf(placed, needle, await StoredAsync(placed));
        foreach (var face in new[] { 15, 7, 12, 8, 9, 4, 20 }) table.Dice.Faces.Enqueue(face);

        await RunAsync(table, Cmd(site, HazardAction.Notice, new[] { table.Aria.Id }));
        await RunAsync(table, Cmd(site, HazardAction.Find, new[] { table.Aria.Id }, "needle"));
        await RunAsync(table, Cmd(site, HazardAction.MarkNoticed, new[] { table.Aria.Id }));
        await RunAsync(table, Cmd(site, HazardAction.Disable, new[] { table.Aria.Id }, "disarm"));
        await RunAsync(table, Cmd(site, HazardAction.Trigger, new[] { table.Aria.Id, table.Brin.Id }));
        await RunAsync(table, Cmd(site, HazardAction.SetState, Array.Empty<string>(), state: HazardState.Hidden));

        var events = table.Session.Events.Where(e => e.Type == "Hazard").ToList();
        Assert.NotEmpty(events);
        var visible = new[] { table.Aria, table.Brin };
        Assert.DoesNotContain(SessionEventRedactor.ForPlayers(table.Session.Events, visible), e => e.Type == "Hazard");
        var asPlayer = SessionEventRedactor.ForViewer(table.Session.Events, new CampaignAccess(table.Session.CampaignId, "player-Aria", CampaignRole.Player), visible);
        Assert.DoesNotContain(asPlayer, e => e.Type == "Hazard");
        // Not one word of the hazard's name, or of a check made against it, reaches a player.
        Assert.DoesNotContain(asPlayer, e => e.Description.Contains("Needle", StringComparison.OrdinalIgnoreCase) || e.Description.Contains("Sleight", StringComparison.OrdinalIgnoreCase));
        var seen = new HashSet<string> { table.Aria.Id, table.Brin.Id, table.Goblin.Id };
        Assert.NotEmpty(table.Runtime.Snapshot.RollLog);
        Assert.All(table.Runtime.Snapshot.RollLog, r => Assert.True(r.IsSecret));
        Assert.Empty(TableVisibility.Rolls(table.Runtime.Snapshot.RollLog, isDm: false, seen));
        Assert.NotEmpty(TableVisibility.Rolls(table.Runtime.Snapshot.RollLog, isDm: true, seen));
    }

    [Fact]
    public async Task AHazardThatIsGoneFromTheLibraryType_IsRefusedWithoutHarm_Async()
    {
        var table = await TableAsync();
        await using var _ = table.Runtime;
        var needle = Trap("poisoned-needle");
        var site = SiteOf(await PlaceAsync(table, Hidden(needle)), needle, Hidden(needle)) with { Type = new ContentTypeDefinition { Type = "hazard" } };

        var command = await RunAsync(table, Cmd(site, HazardAction.Trigger, new[] { table.Aria.Id }));

        Assert.Equal("NoRoute", command.Refused);
        Assert.Equal(30, Hp(table, table.Aria));
    }

    /// <summary>An entry of the srd5e hazard type built in the test, for a shape the SRD's traps do not have (an attack, a route with two successes).</summary>
    private static ContentEntry Synthetic(string id, Dictionary<string, object> properties) =>
        new() { Id = id, Type = "hazard", Name = id, Description = string.Empty, PackId = "test", Properties = properties };
}
