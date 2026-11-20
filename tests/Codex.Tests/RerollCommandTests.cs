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

/// <summary>The generic reroll at the table, on an invented system (a luck track and three kinds of roll): each kind of roll made again with seeded dice,
/// what is kept, what is refused, and undo.</summary>
[Collection("RavenDb")]
public class RerollCommandTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    private static SystemDefinition Invented(string keep = "new") => SystemDefinitionLoader.Parse($$"""
        id: reroll-table
        name: Reroll table
        dice: { check: 1d20, checkRoll: degreeCheck }
        rerolls:
          - { key: lucky, label: Lucky reroll, spend: { track: luck, by: 1 }, keep: {{keep}} }
        sheetKinds:
          - kind: hero
            modifiers:
              - { target: '@lucky', amount: 1, optIn: { label: Lucky charm, spends: { key: charm, amount: 1 } } }
            tracks:
              - { key: luck, max: 3 }
          - kind: npc
        rolls:
          - { key: charmed, dice: 1d20, add: ["modifiers('@lucky')"] }
          - key: degreeCheck
            dice: 1d20
            add: ["bonus"]
            inputs: [{ key: bonus }, { key: target, from: gm }]
            outcomes:
              - { name: criticalSuccess, when: "total >= target + 10", mark: critical }
              - { name: success, when: "total >= target" }
              - { name: failure, when: "total > target - 10" }
              - { name: criticalFailure, mark: fumble }
          - key: plain
            dice: 1d20
            add: ["bonus"]
            inputs: [{ key: bonus }]
            outcomes: [{ name: high, when: "total >= 12" }, { name: low }]
          - key: costly
            dice: 1d20
            then: [{ intents: [{ adjust: { track: luck, by: -1 } }] }]
        """);

    public RerollCommandTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("Reroll_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
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

    private (string Key, object Value)[] _extra = Array.Empty<(string, object)>();
    private string _kind = "hero";

    private async Task<(CampaignRuntime Runtime, SessionDocument Session, ActorDocument Hero)> TableAsync(SystemDefinition system, long luck, params int[] faces)
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter, OwnerUserId = "player-1" };
        hero.Sheet["kind"] = _kind;
        hero.Sheet["luck"] = luck;
        foreach (var (key, value) in _extra) hero.Sheet[key] = value;
        ActorSheetSync.ApplySheetToState(hero, system, _registry);
        await _actors.SaveAsync(hero);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, diceRoller: new ScriptedRoller(faces), system: system);
        runtime.Hydrate(new[] { hero });
        return (runtime, session, hero);
    }

    private static int Luck(CampaignRuntime runtime, string actorId) => runtime.GetEntity(actorId)!.Value.Get<ResourcePoolComponent>().Get("luck");

    private async Task<ActorDocument> StoredAsync(ActorDocument hero) =>
        (await _actors.GetVisibleAsync(hero.Id, new CampaignAccess(hero.CampaignId, "dm", CampaignRole.DM)))!;

    private async Task<List<string>> LogAsync(SessionDocument session) => (await _sessions.GetAsync(session.Id))!.Events.Select(e => e.Description).ToList();

    [Fact]
    public async Task ADiceExpression_IsRolledAgain_GradedAgainstTheSameDc_AndTheNewRollStands()
    {
        var (runtime, session, hero) = await TableAsync(Invented(), 2, 4, 19);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var first = Assert.Single(runtime.Snapshot.RollLog);
        Assert.Equal("failure", first.Outcome);

        var command = new RerollCommand(hero.Id, first.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(command);

        Assert.Equal(RerollFailure.None, command.Failed);
        Assert.Equal(1, Luck(runtime, hero.Id));
        Assert.Equal(2, runtime.Snapshot.RollLog.Count);
        var again = command.Made!;
        Assert.Equal((24, "success", 15, first.Id, "lucky"), (again.Total, again.Outcome, again.Dc, again.RerollOf, again.RerollKey));
        Assert.Equal(again.Id, first.RerolledBy);
        Assert.Same(again, command.Standing);
        Assert.Null(again.Replay);
        var log = await LogAsync(session);
        Assert.Contains(log, l => l.Contains("Vex used Lucky reroll on Strike: 24, was 9.") && l.Contains("Vex: luck 2 → 1."));
        // The first roll's mark reaches the stored log too.
        var stored = (await _sessions.GetAsync(session.Id))!.RollLog;
        Assert.Equal(again.Id, stored.Single(r => r.Id == first.Id).RerolledBy);
        Assert.Equal(2, stored.Count);
        var storedAgain = stored.Single(r => r.Id == again.Id);
        Assert.Equal((first.Id, "lucky"), (storedAgain.RerollOf, storedAgain.RerollKey));
        // The point is gone from the table's sheet and from the stored actor.
        Assert.Equal(1L, runtime.GetActorDocument(hero.Id)!.Sheet["luck"]);
        Assert.Equal(1, Codex.Core.YamlValues.GetInt((await StoredAsync(hero)).Sheet, "luck"));
    }

    [Fact]
    public async Task KeepHigher_LeavesTheHigherRollStanding_AndBothStayInTheLog()
    {
        var (runtime, session, hero) = await TableAsync(Invented("higher"), 2, 16, 3);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var first = runtime.Snapshot.RollLog[0];

        var command = new RerollCommand(hero.Id, first.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(command);

        Assert.Equal(8, command.Made!.Total);
        Assert.Same(first, command.Standing);
        Assert.Equal(2, runtime.Snapshot.RollLog.Count);
        Assert.Equal(1, Luck(runtime, hero.Id));
        Assert.Contains(await LogAsync(session), l => l.Contains("Vex used Lucky reroll on Strike: 8, was 21. The higher roll stands."));
    }

    [Fact]
    public async Task KeepNew_KeepsTheSecondRollEvenWhenItIsLower()
    {
        var (runtime, _, hero) = await TableAsync(Invented(), 2, 16, 3);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var command = new RerollCommand(hero.Id, runtime.Snapshot.RollLog[0].Id, "lucky", "player-1");

        await runtime.EnqueueAsync(command);

        Assert.Same(command.Made, command.Standing);
        Assert.Equal("failure", command.Made!.Outcome);
    }

    [Fact]
    public async Task ANamedRoll_IsRolledAgainWithTheSameInputs_AndCostsNothingElse()
    {
        var (runtime, _, hero) = await TableAsync(Invented(), 2);
        await using var _ = runtime;
        var inputs = new Dictionary<string, object?> { ["bonus"] = 3L, ["target"] = 15L };
        await runtime.EnqueueAsync(new MakeRollCommand(hero.Id, "player-1", new SheetRollRequest { Definition = "degreeCheck", Inputs = inputs }, Dice: new SequenceDiceSource(new[] { 5 })));
        var first = runtime.Snapshot.RollLog[0];
        Assert.Equal(("failure", 8), (first.Outcome, first.Total));

        var command = new RerollCommand(hero.Id, first.Id, "lucky", "player-1", Dice: new SequenceDiceSource(new[] { 18 }));
        await runtime.EnqueueAsync(command);

        Assert.Equal(RerollFailure.None, command.Failed);
        Assert.Equal(("success", 21, first.Id), (command.Made!.Outcome, command.Made.Total, command.Made.RerollOf));
        Assert.Equal(1, Luck(runtime, hero.Id));
    }

    [Fact]
    public async Task ACheck_IsMadeAgainThroughTheCheckService_AndKeepsItsDc()
    {
        var (runtime, _, hero) = await TableAsync(Invented(), 1, 2, 14);
        await using var _ = runtime;
        var request = new CheckRequest { ActorId = hero.Id, Check = "Lockpicking", Bonus = 4, Dc = 15, Secret = false };
        await runtime.EnqueueAsync(new MakeCheckCommand(request, "dm"));
        var first = runtime.Snapshot.RollLog[0];
        Assert.Equal("failure", first.Outcome);

        var command = new RerollCommand(hero.Id, first.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(command);

        Assert.Equal(RerollFailure.None, command.Failed);
        Assert.Equal((18, "success"), (command.Made!.Total, command.Made.Outcome));
        Assert.Equal(0, Luck(runtime, hero.Id));
    }

    [Fact]
    public async Task ARollIsRerolledOnce_AndARerollIsNeverRerolled()
    {
        var (runtime, _, hero) = await TableAsync(Invented(), 3, 4, 5, 6);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var first = runtime.Snapshot.RollLog[0];
        var second = new RerollCommand(hero.Id, first.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(second);
        var system = runtime.System;

        Assert.Empty(Rerolls.Offered(system, first));
        Assert.Empty(Rerolls.Offered(system, second.Made!));
        var again = new RerollCommand(hero.Id, first.Id, "lucky", "player-1");
        var ofReroll = new RerollCommand(hero.Id, second.Made!.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(again);
        await runtime.EnqueueAsync(ofReroll);

        Assert.Equal((RerollFailure.NotOffered, RerollFailure.NotOffered), (again.Failed, ofReroll.Failed));
        Assert.Equal(2, runtime.Snapshot.RollLog.Count);
        Assert.Equal(2, Luck(runtime, hero.Id));
    }

    [Fact]
    public async Task ShortOfPoints_Refuses_AndChangesNothing()
    {
        var (runtime, session, hero) = await TableAsync(Invented(), 0, 4);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var first = runtime.Snapshot.RollLog[0];

        var command = new RerollCommand(hero.Id, first.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(command);

        Assert.Equal(RerollFailure.Short, command.Failed);
        Assert.Null(command.Made);
        Assert.Null(first.RerolledBy);
        Assert.Single(runtime.Snapshot.RollLog);
        Assert.Equal(0, Luck(runtime, hero.Id));
        Assert.Contains(await LogAsync(session), l => l.Contains("Vex cannot pay for Lucky reroll."));
    }

    [Fact]
    public async Task ANamedRollWithAFollowUp_IsNotOffered_AndAnOlderEntryWithNoReplayIsNotEither()
    {
        var (runtime, _, hero) = await TableAsync(Invented(), 3);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new MakeRollCommand(hero.Id, "player-1", new SheetRollRequest { Definition = "costly" }, Dice: new SequenceDiceSource(new[] { 5 })));
        var costly = runtime.Snapshot.RollLog[0];
        var older = new RollLogEntry { ActorId = hero.Id, Expression = "1d20+5", CheckDieIndex = 0 };

        Assert.Null(costly.Replay);
        Assert.Empty(Rerolls.Offered(runtime.System, costly));
        Assert.Empty(Rerolls.Offered(runtime.System, older));
        var command = new RerollCommand(hero.Id, costly.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(command);
        Assert.Equal(RerollFailure.NotOffered, command.Failed);
    }

    [Fact]
    public void ARollWithNoCheckDie_IsNotOffered()
    {
        var system = Invented();
        var damage = new RollLogEntry { ActorId = "a", Expression = "2d6+3", Replay = new RollReplay { Kind = RollReplay.OfExpression } };
        var check = new RollLogEntry { ActorId = "a", Expression = "1d20+3", CheckDieIndex = 0, Replay = new RollReplay { Kind = RollReplay.OfExpression } };

        Assert.Empty(Rerolls.Offered(system, damage));
        Assert.Single(Rerolls.Offered(system, check));
        Assert.Empty(Rerolls.Offered(Invented().Also(s => s.Rerolls.Clear()), check));
    }

    [Fact]
    public async Task OnlyTheOwnerOrTheGm_MayReroll_AndASecretRollIsTheGmsAlone()
    {
        var (runtime, _, hero) = await TableAsync(Invented(), 3, 4, 5, 6);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "dm", true, "Strike", Dc: 15));
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var secret = runtime.Snapshot.RollLog[0];
        var open = runtime.Snapshot.RollLog[1];

        var byOwnerOnSecret = new RerollCommand(hero.Id, secret.Id, "lucky", "player-1");
        var byStranger = new RerollCommand(hero.Id, open.Id, "lucky", "player-2");
        var byGm = new RerollCommand(hero.Id, secret.Id, "lucky", "dm", AsGm: true);
        await runtime.EnqueueAsync(byOwnerOnSecret);
        await runtime.EnqueueAsync(byStranger);
        await runtime.EnqueueAsync(byGm);

        Assert.Equal((RerollFailure.NotAllowed, RerollFailure.NotAllowed, RerollFailure.None), (byOwnerOnSecret.Failed, byStranger.Failed, byGm.Failed));
        Assert.True(byGm.Made!.IsSecret);
        Assert.Equal(2, Luck(runtime, hero.Id));
    }

    [Fact]
    public async Task Undo_GivesThePointBack_ClearsTheMark_AndTheLogKeepsBothRolls()
    {
        var (runtime, session, hero) = await TableAsync(Invented(), 2, 4, 19, 7);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "player-1", false, "Strike", Dc: 15));
        var first = runtime.Snapshot.RollLog[0];
        await runtime.EnqueueAsync(new RerollCommand(hero.Id, first.Id, "lucky", "player-1"));
        Assert.Equal(1, Luck(runtime, hero.Id));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(hero.Id));

        Assert.Equal(2, Luck(runtime, hero.Id));
        Assert.Equal(2L, runtime.GetActorDocument(hero.Id)!.Sheet["luck"]);
        Assert.Equal(2, Codex.Core.YamlValues.GetInt((await StoredAsync(hero)).Sheet, "luck"));
        Assert.Equal(2, runtime.Snapshot.RollLog.Count);
        // The first roll can be rerolled again, and the stored log says so too.
        Assert.Null(first.RerolledBy);
        Assert.Null((await _sessions.GetAsync(session.Id))!.RollLog.Single(r => r.Id == first.Id).RerolledBy);
        var again = new RerollCommand(hero.Id, first.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(again);
        Assert.Equal(RerollFailure.None, again.Failed);
        Assert.Equal(3, runtime.Snapshot.RollLog.Count);
    }

    [Fact]
    public async Task ARollThatKeptTheBetterOfSeveralDice_IsNotOffered()
    {
        var (runtime, _, hero) = await TableAsync(Invented(), 2);
        await using var _ = runtime;
        var kept = new RollLogEntry { ActorId = hero.Id, Expression = "2d20kh1+5", CheckDieIndex = 0, Kept = { 15 }, Dropped = { 3 }, Replay = new RollReplay { Kind = RollReplay.OfExpression } };
        runtime.RecordRoll(kept);

        Assert.Empty(Rerolls.Offered(runtime.System, kept, runtime.GetActorDocument(hero.Id)!.Sheet));
        var command = new RerollCommand(hero.Id, kept.Id, "lucky", "player-1");
        await runtime.EnqueueAsync(command);
        Assert.Equal(RerollFailure.NotOffered, command.Failed);
    }

    [Fact]
    public async Task AnActorWhoseKindHasNoSuchTrack_IsOfferedNothing()
    {
        _kind = "npc";
        var (runtime, _, hero) = await TableAsync(Invented(), 0, 4);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new RollDiceCommand("1d20+5", hero.Id, "dm", false, "Strike", Dc: 15));
        var entry = runtime.Snapshot.RollLog[0];

        Assert.Empty(Rerolls.Offered(runtime.System, entry, runtime.GetActorDocument(hero.Id)!.Sheet));
        Assert.Single(Rerolls.Offered(runtime.System, entry, new Dictionary<string, object> { ["kind"] = "hero" }));
        var command = new RerollCommand(hero.Id, entry.Id, "lucky", "dm", AsGm: true);
        await runtime.EnqueueAsync(command);
        Assert.Equal(RerollFailure.NotOffered, command.Failed);
    }

    [Fact]
    public async Task ARollWhoseTickedBonusSpentTheLastPoint_IsStillRolledAgain_AndTheBonusStillCounts()
    {
        _extra = new (string, object)[] { ("uses_charm", 1L), ("uses_charm_Max", 1L) };
        var (runtime, _, hero) = await TableAsync(Invented(), 2);
        await using var _ = runtime;
        await runtime.EnqueueAsync(new MakeRollCommand(hero.Id, "player-1", new SheetRollRequest { Definition = "charmed", OptIns = new[] { "kind:hero#0" } }, Dice: new SequenceDiceSource(new[] { 4 })));
        var first = runtime.Snapshot.RollLog[0];
        Assert.Equal(5, first.Total);
        Assert.Equal(0, runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>().Get("uses_charm"));

        var command = new RerollCommand(hero.Id, first.Id, "lucky", "player-1", Dice: new SequenceDiceSource(new[] { 10 }));
        await runtime.EnqueueAsync(command);

        Assert.Equal(RerollFailure.None, command.Failed);
        Assert.Equal(11, command.Made!.Total);
        Assert.Equal(0, runtime.GetEntity(hero.Id)!.Value.Get<ResourcePoolComponent>().Get("uses_charm"));
        Assert.Equal(1, Luck(runtime, hero.Id));
    }

    [Fact]
    public async Task ARollThatCannotBeMadeAgain_RefusesAndTheCostGoesBack()
    {
        var (runtime, session, hero) = await TableAsync(Invented(), 2);
        await using var _ = runtime;
        // A named roll whose required input nobody supplies: it cannot be worked out a second time.
        var broken = new RollLogEntry { ActorId = hero.Id, Expression = "degreeCheck", Replay = new RollReplay { Kind = RollReplay.OfDefinition, RollKey = "degreeCheck" } };
        runtime.RecordRoll(broken);

        var command = new RerollCommand(hero.Id, broken.Id, "lucky", "player-1", Dice: new SequenceDiceSource(new[] { 10 }));
        await runtime.EnqueueAsync(command);

        Assert.Equal(RerollFailure.CouldNotRoll, command.Failed);
        Assert.Null(command.Made);
        Assert.Null(broken.RerolledBy);
        Assert.Single(runtime.Snapshot.RollLog);
        Assert.Equal(2, Luck(runtime, hero.Id));
        Assert.Equal(2L, runtime.GetActorDocument(hero.Id)!.Sheet["luck"]);
        Assert.Equal(2, Codex.Core.YamlValues.GetInt((await StoredAsync(hero)).Sheet, "luck"));
        Assert.Contains(await LogAsync(session), l => l.Contains("cannot be rolled again"));
    }

    [Fact]
    public void WithKeepHigher_OnlyTheRollThatCountsIsMarked()
    {
        var higher = Invented("higher");
        var first = new RollLogEntry { Id = "a", Total = 21, RerolledBy = "b" };
        var second = new RollLogEntry { Id = "b", Total = 8, RerollOf = "a", RerollKey = "lucky" };
        RollLogEntry? Find(string id) => id == "a" ? first : id == "b" ? second : null;

        Assert.True(Rerolls.Counts(higher, first, Find));
        Assert.False(Rerolls.Counts(higher, second, Find));
        Assert.False(Rerolls.Counts(Invented(), first, Find));
        Assert.True(Rerolls.Counts(Invented(), second, Find));
        // Taken back: the first roll is no longer marked, so it counts again and the second does not.
        first.RerolledBy = null;
        Assert.True(Rerolls.Counts(Invented(), first, Find));
        Assert.False(Rerolls.Counts(Invented(), second, Find));
    }
}

internal static class RerollTestExtensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
