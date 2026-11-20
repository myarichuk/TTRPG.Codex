using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>A slot pool whose <c>recoversOn</c> names a declared moment: the table's moment command and the sheet's moments panel refill it, and the system
/// has no rest button named after the moment.</summary>
[Collection("RavenDb")]
public class SlotPoolMomentTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: slot-moment-test
        name: Slot moment test
        entityTypes:
          - { type: class, label: Class }
        actorFields:
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: level, label: Level, type: Number }
        vitals:
          rests: [{ name: Long Rest }]
        moments: [{ key: downtime, label: Downtime }]
        sheetRules:
          enforcement: warn
          slotPools:
            - { key: slots, label: Spell slots, field: class, byField: level, table: spellcasting, column: slots, recoversOn: [downtime, Long Rest] }
        """);

    public SlotPoolMomentTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("SlotMoment_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private static Dictionary<string, object> SpentSheet() => new()
    {
        ["slots_1"] = 0L, ["slots_1_Max"] = 3L, ["slots_2"] = 1L, ["slots_2_Max"] = 2L
    };

    [Fact]
    public void ADeclaredMomentANamedPoolRecoversOn_IsNotARestButton_AndTheLongRestStays()
    {
        Assert.Equal(new[] { "Long Rest" }, new SheetRuleEngine(System, NullEntryResolver.Instance).RestNames());
        Assert.False(SystemMoments.IsRest(System, "downtime"));
        Assert.True(SystemMoments.IsRest(System, "Long Rest"));
    }

    [Fact]
    public void TheMomentMattersToASheetWithSlots_AndNotToOneWithout_AndSaysWhatItRefills()
    {
        var spent = SpentSheet();
        var none = new Dictionary<string, object> { ["level"] = 1L };

        Assert.True(SystemMoments.Matters(System, spent, "downtime"));
        Assert.False(SystemMoments.Matters(System, none, "downtime"));
        Assert.Equal(new[] { "Spell slots" }, SystemMoments.Effects(System, spent, "downtime").Tracks);

        var after = new Dictionary<string, object>(spent) { ["slots_1"] = 3L, ["slots_2"] = 2L };
        Assert.Equal(new[] { "Spell slots" }, SystemMoments.WhatChanged(System, spent, after));
        Assert.Equal(new[] { "slots_1", "slots_2" }, SystemMoments.SlotKeysRecoveredAt(System, spent.Keys, "downtime").OrderBy(k => k));
    }

    [Fact]
    public async Task TheMomentCommand_FillsTheSlotsToTheirMaximum_LogsIt_AndUndoPutsThemBack()
    {
        var campaignId = Guid.NewGuid().ToString();
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Vex", Kind = ActorKind.PlayerCharacter };
        foreach (var (key, value) in SpentSheet()) doc.Sheet[key] = value;
        ActorSheetSync.ApplySheetToState(doc, System, _registry);
        await _actors.SaveAsync(doc);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        await using var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: System);
        runtime.Hydrate(new[] { doc });
        int Slots(string key) => runtime.GetEntity(doc.Id)!.Value.Get<ResourcePoolComponent>().Get(key);

        await runtime.EnqueueAsync(new MomentCommand(doc.Id, "downtime"));

        Assert.Equal((3, 2), (Slots("slots_1"), Slots("slots_2")));
        Assert.Contains((await _sessions.GetAsync(session.Id))!.Events, e => e.Description.StartsWith("Downtime for Vex", StringComparison.Ordinal));

        await runtime.EnqueueAsync(new UndoLastChangeCommand(doc.Id));
        Assert.Equal((0, 1), (Slots("slots_1"), Slots("slots_2")));
    }
}
