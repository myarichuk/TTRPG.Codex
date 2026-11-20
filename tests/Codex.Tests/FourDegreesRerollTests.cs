using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The hero point reroll on the real four-degrees system (rules hero-points and fortune-and-misfortune): one hero point makes a check again, the second
/// result stands, the degree is worked out again against the same DC, and a check is rerolled once. A scripted die stands in for the host's.
/// </summary>
[Collection("RavenDb")]
public class FourDegreesRerollTests : IClassFixture<RavenDbFixture>, IAsyncLifetime, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;
    private readonly ContentRegistry _content = new();
    private readonly PluginLoader _loader;

    public FourDegreesRerollTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("FourDegreesReroll_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
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

    private sealed class ScriptedRoller(params int[] faces) : IDiceRoller
    {
        private readonly Queue<int> _faces = new(faces);

        public DiceRollResult Roll(string expression)
        {
            var bonus = int.Parse(Regex.Match(expression, @"[+-]\d+$").Value, CultureInfo.InvariantCulture);
            var face = _faces.Dequeue();
            return new DiceRollResult(expression, new List<int> { face }, Array.Empty<int>(), bonus, face + bonus) { CheckDieIndex = 0 };
        }
    }

    private async Task<(CampaignRuntime Runtime, ActorDocument Hero)> TableAsync(int heroPoints, params int[] faces)
    {
        var campaignId = Guid.NewGuid().ToString();
        var sheet = new Dictionary<string, object> { ["HP"] = 30L, ["HP_Max"] = 30L, ["level"] = 5L, ["heroPoints"] = (long)heroPoints };
        var hero = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Brin", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-brin", Sheet = sheet };
        ActorSheetSync.ApplySheetToState(hero, System, _registry);
        await _actors.SaveAsync(hero);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, new RavenEncounterRepository(_dbService), new ScriptedRoller(faces), System);
        runtime.Hydrate(new[] { hero });
        return (runtime, hero);
    }

    private static int HeroPoints(CampaignRuntime runtime, string actorId) => runtime.GetEntity(actorId)!.Value.Get<ResourcePoolComponent>().Get("heroPoints");

    [Fact]
    public void TheSystemDeclaresOneReroll_ForOneHeroPoint_KeepingTheNewResult()
    {
        var reroll = Assert.Single(System.Rerolls);

        Assert.Equal(("heroPoint", "heroPoints", 1, false), (reroll.Key, reroll.Spend!.Track, reroll.Spend.By, reroll.KeepsHigher));
    }

    [Fact]
    public async Task ACheck_IsRerolledForAHeroPoint_TheSecondResultStands_AndItsDegreeIsWorkedOutAgainstTheSameDc()
    {
        var (runtime, hero) = await TableAsync(1, 5, 19);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+7", hero.Id, "player-brin", false, "Perception", Dc: 18));
        var first = Assert.Single(runtime.Snapshot.RollLog);
        Assert.Equal("failure", first.Outcome);
        Assert.Single(Rerolls.Offered(System, first));

        var command = new RerollCommand(hero.Id, first.Id, "heroPoint", "player-brin");
        await runtime.EnqueueAsync(command);

        Assert.Equal(RerollFailure.None, command.Failed);
        Assert.Equal(("success", 26, 18), (command.Made!.Outcome, command.Made.Total, command.Made.Dc));
        Assert.Same(command.Made, command.Standing);
        Assert.Equal(0, HeroPoints(runtime, hero.Id));

        // A check is rerolled once, and a reroll is never rerolled.
        Assert.Empty(Rerolls.Offered(System, first));
        Assert.Empty(Rerolls.Offered(System, command.Made));
    }

    [Fact]
    public async Task ANaturalTwenty_OnTheSecondRoll_RaisesTheDegreeAsTheRulesSayAfterAReroll()
    {
        var (runtime, hero) = await TableAsync(1);
        await using var _ = runtime;
        var inputs = new Dictionary<string, object?> { ["bonus"] = 7L, ["target"] = 18L };
        await runtime.EnqueueAsync(new MakeRollCommand(hero.Id, "player-brin", new SheetRollRequest { Definition = "degreeCheck", Inputs = inputs }, Dice: new SequenceDiceSource(new[] { 5 })));
        var first = runtime.Snapshot.RollLog[0];
        Assert.Equal("failure", first.Outcome);

        var command = new RerollCommand(hero.Id, first.Id, "heroPoint", "player-brin", Dice: new SequenceDiceSource(new[] { 20 }));
        await runtime.EnqueueAsync(command);

        Assert.Equal(("criticalSuccess", "critical", 27), (command.Made!.Outcome, command.Made.Mark, command.Made.Total));
        Assert.Equal(0, HeroPoints(runtime, hero.Id));
    }

    [Fact]
    public async Task WithNoHeroPoint_TheRerollIsRefused_AndNothingChanges()
    {
        var (runtime, hero) = await TableAsync(0, 5);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+7", hero.Id, "player-brin", false, "Perception", Dc: 18));
        var first = runtime.Snapshot.RollLog[0];

        var command = new RerollCommand(hero.Id, first.Id, "heroPoint", "player-brin");
        await runtime.EnqueueAsync(command);

        Assert.Equal(RerollFailure.Short, command.Failed);
        Assert.Single(runtime.Snapshot.RollLog);
        Assert.Null(first.RerolledBy);
    }
}
