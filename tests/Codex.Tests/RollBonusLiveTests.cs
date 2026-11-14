using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>What a ticked roll bonus costs where the actor is live at a table (docs/ROLL_BONUSES_DESIGN.md): a spend from the sheet page is the table's
/// command and the sheet takes the table's number, so the next save does not spend it again; an opt-in's <c>ends</c> removes a condition only
/// when the actor has it. The hosts call these after paying and rolling.</summary>
[Collection("RavenDb")]
public class RollBonusLiveTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private CampaignAccess Dm => TestAccess.Member(_dbService, "camp", "dm", CampaignRole.DM);

    private static readonly SystemDefinition UsesSystem = new() { Resources = { new ResourceDefinition { Key = "HP", MaxKey = "HP_Max" } } };

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly RavenEncounterRepository _encounters;
    private readonly ComponentRegistry _registry;

    public RollBonusLiveTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("RollBonusLive_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _encounters = new RavenEncounterRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    /// <summary>A character with a counter of 3 charm uses (of 3) on both the sheet and the table.</summary>
    private async Task<ActorDocument> SeedAsync(int charmLeft)
    {
        var pool = new ResourcePoolComponent();
        pool.Set("HP", 10);
        pool.Set("HP_Max", 10);
        pool.Set("uses_charm", charmLeft);
        pool.Set("uses_charm_Max", 3);
        var doc = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = "camp",
            Name = "Aria",
            Kind = ActorKind.PlayerCharacter,
            Sheet = new Dictionary<string, object> { ["HP"] = 10L, ["HP_Max"] = 10L, ["uses_charm"] = (long)charmLeft, ["uses_charm_Max"] = 3L },
            State = _registry.Snapshot(new object[] { pool })
        };
        await _actors.SaveAsync(doc);
        return doc;
    }

    private async Task<CampaignRuntime> StartAsync(ActorDocument actor)
    {
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = "camp", Status = SessionStatus.Live, Title = "Live" };
        await _sessions.SaveAsync(session);
        var runtime = new CampaignRuntime("camp", _actors, _registry, NullLogger.Instance, _sessions, session, _encounters);
        runtime.Hydrate(new[] { actor });
        return runtime;
    }

    private Task<ActorDocument?> LoadAsync(ActorDocument actor) => _actors.GetVisibleAsync(actor.Id, Dm);

    [Fact]
    public async Task ASpendFromTheSheetAtALiveTable_IsTheTablesSpend_AndASaveAfterItDoesNotSpendItTwice_Async()
    {
        var actor = await SeedAsync(3);
        await using var runtime = await StartAsync(actor);

        // The sheet page as loaded: the sheet being edited, as stored, and the merge baseline.
        var edit = new Dictionary<string, object>((await LoadAsync(actor))!.Sheet);
        var loaded = new Dictionary<string, object>(edit);
        var baseline = new Dictionary<string, object>(edit);

        Assert.True(await RollBonusLive.SpendAsync(runtime, actor.Id, ("uses_charm", -1, "Lucky charm"), edit, loaded, baseline));

        Assert.Equal(2, runtime.Snapshot.Actor(actor.Id)!.Pool("uses_charm"));
        Assert.Equal(2, YamlValues.GetInt(edit, "uses_charm"));
        Assert.Equal(2, YamlValues.GetInt(loaded, "uses_charm"));
        Assert.Equal(2, YamlValues.GetInt(baseline, "uses_charm"));

        // The player saves the sheet (with a note changed): the table's number and the stored sheet stay at one spend.
        var edited = (await LoadAsync(actor))!;
        foreach (var (key, value) in edit) edited.Sheet[key] = value;
        edited.Sheet["Level"] = 4L;
        await runtime.EnqueueAsync(new ReplaceActorCommand(edited, loaded, Dm, UsesSystem));

        Assert.Equal(2, runtime.GetEntity(actor.Id)!.Value.Get<ResourcePoolComponent>().Get("uses_charm"));
        Assert.Equal(2, YamlValues.GetInt((await LoadAsync(actor))!.Sheet, "uses_charm"));
    }

    [Fact]
    public async Task ASpendTheTableRefuses_ChangesNothing_Async()
    {
        var actor = await SeedAsync(1);
        await using var runtime = await StartAsync(actor);
        var edit = new Dictionary<string, object>((await LoadAsync(actor))!.Sheet);

        Assert.False(await RollBonusLive.SpendAsync(runtime, actor.Id, ("uses_charm", -2, "Lucky charm"), edit));

        Assert.Equal(1, runtime.Snapshot.Actor(actor.Id)!.Pool("uses_charm"));
        Assert.Equal(1, YamlValues.GetInt(edit, "uses_charm"));
    }

    private static RollOptIn Ends(string? condition, string id = "o1") => new(id, "Spend it", "+1", "Source", null, true, condition);

    private static ActorStateSnapshot Holding(params string[] conditions) =>
        new(new Dictionary<string, int>(), conditions.Select(c => new EffectSnapshot(c, "src", -1, EffectExpiry.EndOfTurn)).ToList());

    [Fact]
    public void OnlyAConditionTheActorHasIsEnded_OnceEach_WhateverTheCase()
    {
        var applied = new[] { Ends("raging"), Ends("Frightened", "o2"), Ends("raging", "o3"), Ends(null, "o4") };

        Assert.Equal(new[] { "Raging" }, RollBonusLive.ToEnd(applied, Holding("Raging", "Prone")));
        Assert.Empty(RollBonusLive.ToEnd(applied, Holding("Prone")));
        Assert.Empty(RollBonusLive.ToEnd(applied, null));
        Assert.Empty(RollBonusLive.ToEnd(new[] { Ends(null) }, Holding("Raging")));
    }

    [Fact]
    public async Task AnEndingAtALiveTable_TakesOffTheConditionTheActorHas_AndLogsNothingForOneItLacks_Async()
    {
        var actor = await SeedAsync(3);
        await using var runtime = await StartAsync(actor);
        await runtime.EnqueueAsync(new AddStatusEffectCommand(actor.Id, "Raging", "rage", ActiveEffect.Indefinite, EffectExpiry.EndOfTurn, actor.Id));

        Assert.Equal(0, await RollBonusLive.EndAtTableAsync(runtime, actor.Id, new[] { Ends("Frightened") }));
        Assert.Contains(runtime.Snapshot.Actor(actor.Id)!.Effects, e => e.EffectId == "Raging");

        Assert.Equal(1, await RollBonusLive.EndAtTableAsync(runtime, actor.Id, new[] { Ends("raging"), Ends("Raging", "o2") }));
        Assert.DoesNotContain(runtime.Snapshot.Actor(actor.Id)!.Effects, e => e.EffectId == "Raging");

        // Gone already: nothing more to end, and no second removal is sent.
        Assert.Equal(0, await RollBonusLive.EndAtTableAsync(runtime, actor.Id, new[] { Ends("Raging") }));
    }

    [Fact]
    public void OffTheTable_AnEndedConditionComesOffTheSheet_OneTheSheetLacksIsLeftAlone()
    {
        var sheet = new Dictionary<string, object> { ["conditions"] = new List<object> { "Raging", "Prone" } };

        Assert.False(RollBonusLive.EndOnSheet(new SystemDefinition(), NullEntryResolver.Instance, sheet, new[] { Ends("Frightened") }, "me"));
        Assert.Equal(new[] { "Raging", "Prone" }, SheetRuleEngine.RawList(sheet, "conditions"));

        Assert.True(RollBonusLive.EndOnSheet(new SystemDefinition(), NullEntryResolver.Instance, sheet, new[] { Ends("raging"), Ends(null, "o2") }, "me"));
        Assert.Equal(new[] { "Prone" }, SheetRuleEngine.RawList(sheet, "conditions"));

        // A sheet with no conditions at all (a sheet-rules sheet) is not touched.
        var plain = new Dictionary<string, object> { ["HP"] = 5L };
        Assert.False(RollBonusLive.EndOnSheet(new SystemDefinition(), NullEntryResolver.Instance, plain, new[] { Ends("Raging") }, "me"));
        Assert.False(plain.ContainsKey("conditions"));
    }

    [Fact]
    public async Task ARollThatCannotPay_IsNotMade_SoNothingIsEnded_Async()
    {
        var ends = Ends("Raging") with { Spends = new RollOptInSpend("uses_charm", 1) };
        var spent = new List<string>();

        // The host ends conditions only after PayAsync says true; a refusal stops it first.
        var paid = await RollBonusTray.PayAsync(new[] { ends }, change => { spent.Add(change.Key); return Task.FromResult(false); });

        Assert.False(paid);
        Assert.Equal(new[] { "uses_charm" }, spent);
    }
}
