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

/// <summary>
/// The dying condition at the live table, on the real four-degrees system and its <c>rules.js</c> (rules getting-knocked-out, dying-rule, wounded,
/// instant-death and hero-points): damage and healing raise <c>damaged</c> and <c>healed</c>, a hazard raises one <c>damaged</c> for a whole blow,
/// and heroic recovery is a button. A scripted die stands in for the host's.
/// </summary>
[Collection("RavenDb")]
public class FourDegreesDyingTableTests : IClassFixture<RavenDbFixture>, IAsyncLifetime, IDisposable
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

    public FourDegreesDyingTableTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("FourDegreesDying_" + Guid.NewGuid());
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

    private sealed class ScriptedRoller : IDiceRoller
    {
        public Queue<int> Faces { get; } = new();

        public DiceRollResult Roll(string expression)
        {
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

    private sealed record Table(CampaignRuntime Runtime, ScriptedRoller Dice, SessionDocument Session);

    private async Task<(Table Table, ActorDocument Actor)> OneAsync(int hp, int max, ActorKind kind = ActorKind.PlayerCharacter, int dying = 0, int wounded = 0, int heroPoints = 0, int doomed = 0)
    {
        var campaignId = Guid.NewGuid().ToString();
        var sheet = new Dictionary<string, object>
        {
            ["HP"] = (long)hp, ["HP_Max"] = (long)max, ["level"] = 5L,
            ["str"] = 0L, ["dex"] = 0L, ["con"] = 0L, ["int"] = 0L, ["wis"] = 0L, ["cha"] = 0L
        };
        if (dying > 0) sheet["condition_dying"] = (long)dying;
        if (wounded > 0) sheet["condition_wounded"] = (long)wounded;
        if (doomed > 0) sheet["condition_doomed"] = (long)doomed;
        if (heroPoints > 0) sheet["heroPoints"] = (long)heroPoints;
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Brin", Kind = kind, OwnerUserId = "player-brin", Sheet = sheet };
        ActorSheetSync.ApplySheetToState(doc, System, _registry);
        await _actors.SaveAsync(doc);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var dice = new ScriptedRoller();
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, dice, System) { Places = _regions };
        runtime.Hydrate(new[] { doc });
        return (new Table(runtime, dice, session), doc);
    }

    private static int Hp(Table table, ActorDocument actor) => table.Runtime.Snapshot.Actor(actor.Id)!.Pool("HP")!.Value;

    private static int Condition(Table table, ActorDocument actor, string key) =>
        (int)(YamlValues.ToNumber(table.Runtime.GetActorDocument(actor.Id)!.Sheet.GetValueOrDefault(key)) ?? 0);

    private static int Dying(Table table, ActorDocument actor) => Condition(table, actor, "condition_dying");

    private static bool Dead(Table table, ActorDocument actor) => CodexWorld.HasStatus(table.Runtime.GetEntity(actor.Id)!.Value, "Dead");

    private async Task<List<string>> LogAsync(Table table) => (await _sessions.GetAsync(table.Session.Id))!.Events.Select(e => e.Description).ToList();

    // ---- Damage ----

    [Fact]
    public async Task BroughtToZero_APlayerCharacterIsDying1_AndOnACriticalHitDying2()
    {
        var (table, brin) = await OneAsync(10, 10);
        await using var _ = table.Runtime;
        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 10, "dm"));
        Assert.Equal((0, 1), (Hp(table, brin), Dying(table, brin)));

        var (second, vex) = await OneAsync(10, 10);
        await using var _2 = second.Runtime;
        await second.Runtime.EnqueueAsync(new ApplyDamageCommand(vex.Id, "HP", 12, "dm", IsCritical: true));
        Assert.Equal((0, 2), (Hp(second, vex), Dying(second, vex)));
    }

    [Fact]
    public async Task Wounded_AddsItsValueToTheDyingValueOfAKnockOut()
    {
        var (table, brin) = await OneAsync(10, 10, wounded: 1);
        await using var _ = table.Runtime;

        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 10, "dm"));

        Assert.Equal((2, 1), (Dying(table, brin), Condition(table, brin, "condition_wounded")));
    }

    [Fact]
    public async Task AHitWhileDying_RaisesItByOne_ACriticalByTwo_AndTheDeathValueKills()
    {
        var (table, brin) = await OneAsync(0, 10, dying: 2);
        await using var _ = table.Runtime;

        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 3, "dm"));
        Assert.Equal((3, false), (Dying(table, brin), Dead(table, brin)));

        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 3, "dm", IsCritical: true));
        Assert.True(Dead(table, brin));
        Assert.Equal(0, Dying(table, brin));
    }

    [Fact]
    public async Task DoomedLowersTheValueAHitKillsAt()
    {
        var (table, brin) = await OneAsync(0, 10, dying: 1, doomed: 1);
        await using var _ = table.Runtime;

        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 2, "dm"));
        Assert.Equal((2, false), (Dying(table, brin), Dead(table, brin)));

        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 2, "dm"));
        Assert.True(Dead(table, brin));
    }

    [Fact]
    public async Task MassiveDamage_KillsAtTwiceTheMaximum_AndNotBelowIt()
    {
        var (table, brin) = await OneAsync(10, 10);
        await using var _ = table.Runtime;
        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 19, "dm"));
        Assert.Equal((false, 1), (Dead(table, brin), Dying(table, brin)));

        var (second, vex) = await OneAsync(10, 10);
        await using var _2 = second.Runtime;
        await second.Runtime.EnqueueAsync(new ApplyDamageCommand(vex.Id, "HP", 20, "dm"));
        Assert.True(Dead(second, vex));
        Assert.Contains(await LogAsync(second), l => l.Contains("Massive damage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHitAtZero_OnACharacterThatIsNotDying_GivesDying1()
    {
        var (table, brin) = await OneAsync(0, 10);
        await using var _ = table.Runtime;

        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 2, "dm"));

        Assert.Equal(1, Dying(table, brin));
    }

    [Fact]
    public async Task ACreatureThatIsNotAPlayerCharacter_DiesAtZero_AndTheScriptAddsNothing()
    {
        var (table, wolf) = await OneAsync(5, 5, ActorKind.Monster);
        await using var _ = table.Runtime;

        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(wolf.Id, "HP", 6, "dm"));

        Assert.True(Dead(table, wolf));
        Assert.Equal(0, Dying(table, wolf));
    }

    [Fact]
    public async Task OneUndo_TakesBackTheKnockOutTheDyingAndTheHitPoints()
    {
        var (table, brin) = await OneAsync(10, 10);
        await using var _ = table.Runtime;
        await table.Runtime.EnqueueAsync(new ApplyDamageCommand(brin.Id, "HP", 10, "dm"));

        await table.Runtime.EnqueueAsync(new UndoLastChangeCommand(brin.Id));

        Assert.Equal((10, 0), (Hp(table, brin), Dying(table, brin)));
    }

    // ---- Healing ----

    [Fact]
    public async Task HealedToOneHitPoint_WhileDying_LosesTheCondition_AndWoundedRises()
    {
        var (table, brin) = await OneAsync(0, 10, dying: 2, wounded: 1);
        await using var _ = table.Runtime;

        await table.Runtime.EnqueueAsync(new ApplyHealingCommand(brin.Id, "HP", 4, "dm"));

        Assert.Equal((4, 0, 2), (Hp(table, brin), Dying(table, brin), Condition(table, brin, "condition_wounded")));
    }

    // ---- Hazards ----

    private static ContentEntry Blast(string save = "Constitution") => new()
    {
        Id = "two-part-blast", Type = "hazard", Name = "two-part-blast", Description = string.Empty, PackId = "test",
        Properties = new Dictionary<string, object>
        {
            ["saveAttribute"] = save, ["saveDc"] = 19L, ["onSave"] = "half",
            ["damage"] = new List<object>
            {
                new Dictionary<string, object> { ["dice"] = "2d6", ["type"] = "fire" },
                new Dictionary<string, object> { ["dice"] = "1d6", ["type"] = "piercing" }
            }
        }
    };

    private async Task TriggerAsync(Table table, ActorDocument target)
    {
        var entry = Blast();
        var place = new LocationDocument { Id = "room", Name = "Room", Description = "Dim.", Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = PlacedHazard.Reference("hazard", entry.Id) } } };
        var region = new RegionDocument { Id = "regions/" + Guid.NewGuid(), CampaignId = table.Runtime.CampaignId, Name = "Hold", Locations = { place } };
        using (var session = _dbService.Store.OpenAsyncSession())
        {
            await session.StoreAsync(region);
            await session.SaveChangesAsync();
        }

        var site = new HazardSite(region.Id, place.Id, entry.Name, place.Feature!.Hazard!, entry, System.FindType("hazard")!);
        await table.Runtime.EnqueueAsync(new HazardCommand(site, HazardAction.Trigger, new[] { target.Id }, Gm));
    }

    [Fact]
    public async Task ATwoPartHazard_ThatKnocksACharacterOut_GivesOneDying_AndACriticalFailedSaveMakesItTwo_AndOneUndoTakesItBack()
    {
        var (table, brin) = await OneAsync(8, 8);
        await using var _ = table.Runtime;
        // Two damage parts (6 and 3), then the Constitution save: a 2 against DC 19 is a critical failure.
        foreach (var face in new[] { 6, 3, 2 }) table.Dice.Faces.Enqueue(face);

        await TriggerAsync(table, brin);

        Assert.Equal((0, 2), (Hp(table, brin), Dying(table, brin)));
        await table.Runtime.EnqueueAsync(new UndoLastChangeCommand(brin.Id));
        Assert.Equal((8, 0), (Hp(table, brin), Dying(table, brin)));
    }

    [Fact]
    public async Task ATwoPartHazard_OnADyingCharacter_RaisesTheDyingValueByOneForTheWholeBlow()
    {
        var (table, brin) = await OneAsync(0, 8, dying: 1);
        await using var _ = table.Runtime;
        // The parts (6 and 3), then a save of 12: a failure, not a critical one.
        foreach (var face in new[] { 6, 3, 12 }) table.Dice.Faces.Enqueue(face);

        await TriggerAsync(table, brin);

        Assert.Equal((2, false), (Dying(table, brin), Dead(table, brin)));
    }

    // ---- Heroic recovery ----

    [Fact]
    public async Task HeroicRecovery_ClearsDying_KeepsWounded_AndSpendsAllTheHeroPoints()
    {
        var (table, brin) = await OneAsync(0, 10, dying: 3, wounded: 1, heroPoints: 2);
        await using var _ = table.Runtime;

        await table.Runtime.EnqueueAsync(new MomentCommand(brin.Id, "heroicRecovery"));

        Assert.Equal((0, 1, 0), (Dying(table, brin), Condition(table, brin, "condition_wounded"), Condition(table, brin, "heroPoints")));
        Assert.Equal(0, Hp(table, brin));
        await table.Runtime.EnqueueAsync(new UndoLastChangeCommand(brin.Id));
        Assert.Equal((3, 2), (Dying(table, brin), Condition(table, brin, "heroPoints")));
    }

    [Fact]
    public async Task HeroicRecovery_WithNoHeroPoint_ChangesNothing()
    {
        var (table, brin) = await OneAsync(0, 10, dying: 3, wounded: 1);
        await using var _ = table.Runtime;

        await table.Runtime.EnqueueAsync(new MomentCommand(brin.Id, "heroicRecovery"));

        Assert.Equal((3, 1), (Dying(table, brin), Condition(table, brin, "condition_wounded")));
        Assert.Contains(await LogAsync(table), l => l.Contains("No hero points", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHeroicRecoveryButton_IsOfferedOnlyWhileDying_AndIsNoPartyMoment()
    {
        var moment = SystemMoments.Find(System, "heroicRecovery")!;
        var well = new Dictionary<string, object> { ["HP"] = 10L, ["HP_Max"] = 10L };
        var dying = new Dictionary<string, object> { ["HP"] = 0L, ["HP_Max"] = 10L, ["condition_dying"] = 1L };

        Assert.True(moment.ActorOnly);
        Assert.Equal("Dying", moment.While);
        Assert.False(SystemMoments.Offered(System, well, "heroicRecovery"));
        Assert.True(SystemMoments.Offered(System, dying, "heroicRecovery"));
        Assert.DoesNotContain("heroicRecovery", SystemMoments.RefillWords(System));
        Assert.DoesNotContain("heroicRecovery", SystemMoments.ChangeMoments(System));
    }
}
