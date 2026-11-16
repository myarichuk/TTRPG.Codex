using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>P6: a roll made against a DC at the table says its degree in the roll log and in the session's event feed, and a system without a
/// <c>dice.checkRoll</c> logs what it always did. The system is srd5e with a four-degree check roll added in memory; no shipped content changes.</summary>
[Collection("RavenDb")]
public class RollDegreeAtTheTableTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    public RollDegreeAtTheTableTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("RollDegree_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<AttributesComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    /// <summary>A die that rolls the faces it is told to: the first die of the expression is the next face.</summary>
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

    private static SystemDefinition System(bool withDegrees)
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        if (!withDegrees) return system;
        system.Dice.CheckRoll = "degreeCheck";
        system.Rolls.Add(new RollDefinition
        {
            Key = "degreeCheck", Dice = "1d20", Add = { "bonus" },
            Inputs = { new RollInputDefinition { Key = "bonus" }, new RollInputDefinition { Key = "target", From = "gm" } },
            Outcomes =
            {
                new RollOutcomeDefinition { Name = "criticalSuccess", When = "total >= target + 10", Mark = "critical" },
                new RollOutcomeDefinition { Name = "success", When = "total >= target" },
                new RollOutcomeDefinition { Name = "failure", When = "total > target - 10" },
                new RollOutcomeDefinition { Name = "criticalFailure", Mark = "fumble" }
            }
        });
        return system;
    }

    private async Task<(CampaignRuntime Runtime, SessionDocument Session, ActorDocument Hero)> TableAsync(SystemDefinition system, params int[] faces)
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Aria", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1",
            Sheet = new Dictionary<string, object> { ["HP"] = 30L, ["HP_Max"] = 30L, ["level"] = 5L, ["acMode"] = "auto" }
        };
        ActorSheetSync.ApplySheetToState(hero, system, _registry);
        await _actors.SaveAsync(hero);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, _encounters, new ScriptedRoller(faces), system);
        runtime.Hydrate(new[] { hero });
        return (runtime, session, hero);
    }

    private async Task<List<string>> EventsAsync(SessionDocument session) => (await _sessions.GetAsync(session.Id))!.Events.Select(e => e.Description).ToList();

    [Fact]
    public async Task ARollAgainstADc_IsLoggedWithItsDegreeMarkAndNumber_AndTheEventFeedSaysIt()
    {
        var (runtime, session, hero) = await TableAsync(System(withDegrees: true), 20, 4);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var entry = Assert.Single(runtime.Snapshot.RollLog);
        Assert.Equal(("criticalSuccess", "critical", 15), (entry.Outcome, entry.Mark, entry.Dc));
        Assert.Contains(await EventsAsync(session), e => e.Contains("Aria rolled Strike 1d20+5 against 15: 25 (critical success)."));

        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 30));
        Assert.Equal(("criticalFailure", "fumble"), (runtime.Snapshot.RollLog[1].Outcome, runtime.Snapshot.RollLog[1].Mark));
    }

    [Fact]
    public async Task ARollWithNoDc_IsLoggedAsItAlwaysWas()
    {
        var (runtime, session, hero) = await TableAsync(System(withDegrees: true), 12);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike"));
        var entry = Assert.Single(runtime.Snapshot.RollLog);
        Assert.Equal((null, null, null), (entry.Outcome, entry.Mark, entry.Dc));
        Assert.Contains(await EventsAsync(session), e => e.Contains("Aria rolled Strike 1d20+5: 17."));
    }

    [Fact]
    public async Task ASystemWithoutACheckRoll_GivesNoDegree_EvenWithADc()
    {
        var (runtime, session, hero) = await TableAsync(System(withDegrees: false), 12);
        await using var _ = runtime;

        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var entry = Assert.Single(runtime.Snapshot.RollLog);
        Assert.Equal((null, null, null), (entry.Outcome, entry.Mark, entry.Dc));
        Assert.DoesNotContain(await EventsAsync(session), e => e.Contains("against"));
    }
}
