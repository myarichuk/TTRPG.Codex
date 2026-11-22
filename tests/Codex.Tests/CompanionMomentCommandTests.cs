using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// P11a of docs/COMPANIONS_DESIGN.md: a moment's recoveries reach every creature that has them, but the system's party-wide event handler (a session's hero point)
/// runs only for a member of the party, wherever the moment is applied: the command, the party bar, the sheet's own button (all through
/// <see cref="SystemMoments.RunsHandler"/>).
/// </summary>
[Collection("RavenDb")]
public class CompanionMomentCommandTests : IClassFixture<RavenDbFixture>, IDisposable
{
    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: companion-moments
        name: Companion moments
        moments: [{ key: sessionStart, label: Session start }]
        events:
          sessionStart:
            - { intents: [{ set: { track: heroPoints, to: 1 } }] }
        sheetKinds:
          - kind: hero
            label: Hero
            tracks:
              - { key: heroPoints, max: 3, display: boxes }
          - kind: pet
            label: Pet
            partyMember: false
            tracks:
              - { key: heroPoints, max: 3, display: boxes }
        """);

    public CompanionMomentCommandTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("CompanionMoments_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
    }

    public void Dispose() => _dbService.Dispose();

    private async Task<ActorDocument> ActorAsync(string campaignId, string kind)
    {
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = kind, Kind = ActorKind.PlayerCharacter };
        doc.Sheet["kind"] = kind;
        doc.Sheet["heroPoints"] = 0L;
        doc.Sheet["uses_nip"] = 0L;
        doc.Sheet["uses_nip_Max"] = 2L;
        doc.Sheet["uses_nip_Rest"] = "sessionStart";
        ActorSheetSync.ApplySheetToState(doc, System, _registry);
        await _actors.SaveAsync(doc);
        return doc;
    }

    private static int Pool(CampaignRuntime runtime, string actorId, string key) => runtime.GetEntity(actorId)!.Value.Get<ResourcePoolComponent>().Get(key);

    [Fact]
    public async Task ANonMember_GetsItsOwnCounterRefilledAtTheMoment_ButNotTheSystemsHeroPoint_AMemberGetsBoth()
    {
        var campaignId = Guid.NewGuid().ToString();
        var hero = await ActorAsync(campaignId, "hero");
        var pet = await ActorAsync(campaignId, "pet");
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        await using var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: System);
        runtime.Hydrate(new[] { hero, pet });

        await runtime.EnqueueAsync(new MomentCommand(pet.Id, "sessionStart"));
        await runtime.EnqueueAsync(new MomentCommand(hero.Id, "sessionStart"));

        Assert.Equal(2, Pool(runtime, pet.Id, "uses_nip"));
        Assert.Equal(0, Pool(runtime, pet.Id, "heroPoints"));
        Assert.Equal(2, Pool(runtime, hero.Id, "uses_nip"));
        Assert.Equal(1, Pool(runtime, hero.Id, "heroPoints"));
    }
}
