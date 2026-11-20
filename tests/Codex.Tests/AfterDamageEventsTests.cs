using Codex.Core.Components;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The generic <c>damaged</c> and <c>healed</c> events (overhaul step 6, events table): raised after the table's own damage and
/// healing, only when the system declares a handler, with the pool's numbers before and after. Written on invented systems that
/// name no game.
/// </summary>
[Collection("RavenDb")]
public class AfterDamageEventsTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    // Built-in damage and healing, then a handler for each follow-up that writes down what it was told.
    private static readonly SystemDefinition Follows = SystemDefinitionLoader.Parse("""
        id: after-damage
        name: After damage
        conditions: [Down, Dead]
        resources:
          - { key: HP, label: Hit Points, maxKey: HP_Max, default: 10, isPrimary: true }
        vitals:
          down: { condition: Down, deadCondition: Dead, nonPlayersDieAtZero: true }
        sheetKinds:
          - kind: hero
            label: Hero
            tracks:
              - { key: sawBefore, max: 99 }
              - { key: sawAfter, max: 99 }
              - { key: sawMax, max: 99 }
              - { key: sawDealt, max: 99 }
              - { key: sawHealed, max: 99 }
              - { key: hits, max: 99 }
              - { key: heals, max: 99 }
        events:
          damaged:
            - intents:
                - { set: { track: sawBefore, to: "ev.before" } }
                - { set: { track: sawAfter, to: "ev.after" } }
                - { set: { track: sawMax, to: "ev.max" } }
                - { set: { track: sawDealt, to: "ev.dealt" } }
                - { adjust: { track: hits, by: 1 } }
                - { log: { key: Blow noted } }
          healed:
            - intents:
                - { set: { track: sawHealed, to: "ev.healed" } }
                - { adjust: { track: heals, by: 1 } }
        """);

    // The same, with a handler that takes the damage itself: the built-in path never runs, so nothing follows it.
    private static readonly SystemDefinition Replaces = SystemDefinitionLoader.Parse("""
        id: replaces-damage
        name: Replaces damage
        conditions: [Down, Dead]
        resources:
          - { key: HP, label: Hit Points, maxKey: HP_Max, default: 10, isPrimary: true }
        sheetKinds:
          - kind: hero
            label: Hero
            tracks:
              - { key: HP, max: 10, min: -5, start: max }
              - { key: hits, max: 99 }
        events:
          damage: { builtin: damage }
          damaged:
            - intents:
                - { adjust: { track: hits, by: 1 } }
        """);

    public AfterDamageEventsTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("AfterDamage_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<ActorDocument> ActorAsync(SystemDefinition system, string campaignId, string name, ActorKind kind = ActorKind.PlayerCharacter)
    {
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = name, Kind = kind };
        doc.Sheet["kind"] = "hero";
        doc.Sheet["HP"] = 10L;
        doc.Sheet["HP_Max"] = 10L;
        ActorSheetSync.ApplySheetToState(doc, system, _registry);
        await _actors.SaveAsync(doc);
        return doc;
    }

    private async Task<(CampaignRuntime Runtime, SessionDocument Session)> TableAsync(SystemDefinition system, string campaignId, params ActorDocument[] actors)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: system);
        runtime.Hydrate(actors);
        return (runtime, session);
    }

    private static int Pool(CampaignRuntime runtime, string actorId, string key) => runtime.GetEntity(actorId)!.Value.Get<ResourcePoolComponent>().Get(key);

    private async Task<List<string>> LogAsync(SessionDocument session) =>
        (await _sessions.GetAsync(session.Id))!.Events.Select(e => e.Description).ToList();

    [Fact]
    public async Task Damaged_IsRaisedAfterTheBuiltInDamage_WithBeforeAfterMax_AndIsLogged()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await ActorAsync(Follows, campaignId, "Vex");
        var (runtime, session) = await TableAsync(Follows, campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 4, "dm"));

        Assert.Equal(6, Pool(runtime, hero.Id, "HP"));
        Assert.Equal((10, 6, 10, 4, 1), (Pool(runtime, hero.Id, "sawBefore"), Pool(runtime, hero.Id, "sawAfter"), Pool(runtime, hero.Id, "sawMax"), Pool(runtime, hero.Id, "sawDealt"), Pool(runtime, hero.Id, "hits")));
        Assert.Contains(await LogAsync(session), l => l.Contains("Blow noted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Damaged_IsNotRaised_WhenTheSystemHandlesDamage_OrNothingWasDealt()
    {
        var campaignId = Guid.NewGuid().ToString();
        var replaced = await ActorAsync(Replaces, campaignId, "Ash");
        var (replacing, _) = await TableAsync(Replaces, campaignId, replaced);
        await using var _a = replacing;
        await replacing.EnqueueAsync(new ApplyDamageCommand(replaced.Id, "HP", 4, "dm"));
        Assert.Equal(0, Pool(replacing, replaced.Id, "hits"));

        var other = Guid.NewGuid().ToString();
        var hero = await ActorAsync(Follows, other, "Vex");
        var (runtime, _) = await TableAsync(Follows, other, hero);
        await using var _b = runtime;
        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 0, "dm"));
        Assert.Equal(0, Pool(runtime, hero.Id, "hits"));
    }

    [Fact]
    public async Task ADamageCommandThatAsksNotToRaiseAfter_RaisesNothing()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await ActorAsync(Follows, campaignId, "Vex");
        var (runtime, _) = await TableAsync(Follows, campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 4, "dm", RaiseAfter: false));

        Assert.Equal((6, 0), (Pool(runtime, hero.Id, "HP"), Pool(runtime, hero.Id, "hits")));
    }

    [Fact]
    public async Task Healed_IsRaisedAfterHealing_WithTheAmountThatWasHealed_NotOnTheDead()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await ActorAsync(Follows, campaignId, "Vex");
        var beast = await ActorAsync(Follows, campaignId, "Wolf", ActorKind.Monster);
        var (runtime, _) = await TableAsync(Follows, campaignId, hero, beast);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 6, "dm"));
        // Healing 9 stops at the maximum: 6 were healed.
        await runtime.EnqueueAsync(new ApplyHealingCommand(hero.Id, "HP", 9, "dm"));
        Assert.Equal((1, 6, 10), (Pool(runtime, hero.Id, "heals"), Pool(runtime, hero.Id, "sawHealed"), Pool(runtime, hero.Id, "HP")));

        // A creature that died at 0 is revived by the game master first, so healing it heals nothing and raises nothing.
        await runtime.EnqueueAsync(new ApplyDamageCommand(beast.Id, "HP", 10, "dm"));
        await runtime.EnqueueAsync(new ApplyHealingCommand(beast.Id, "HP", 5, "dm"));
        Assert.Equal(0, Pool(runtime, beast.Id, "heals"));
    }

    [Fact]
    public async Task OneUndo_TakesBackTheHitPoints_AndWhatTheFollowUpDid()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await ActorAsync(Follows, campaignId, "Vex");
        var (runtime, _) = await TableAsync(Follows, campaignId, hero);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new ApplyDamageCommand(hero.Id, "HP", 4, "dm"));
        Assert.Equal((6, 1), (Pool(runtime, hero.Id, "HP"), Pool(runtime, hero.Id, "hits")));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));

        Assert.Equal((10, 0), (Pool(runtime, hero.Id, "HP"), Pool(runtime, hero.Id, "hits")));
    }
}
