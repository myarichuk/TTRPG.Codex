using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The daily preparations at a live table, on a Four Degrees bard: one moment command fills the focus points and the spell slots, and one undo puts both back.</summary>
[Collection("RavenDb")]
public class FourDegreesDailyPrepTableTests : IClassFixture<RavenDbFixture>, IAsyncLifetime, IDisposable
{
    private const string SystemId = "four-degrees";
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "four-degrees-core", "four-degrees-rules" };

    private readonly RavenDbService _dbService;
    private readonly ActorRepository _actors;
    private readonly RavenSessionRepository _sessions;
    private readonly ComponentRegistry _registry;
    private readonly ContentRegistry _content = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public FourDegreesDailyPrepTableTests(RavenDbFixture fixture)
    {
        _dbService = fixture.Open("FourDegreesDailyPrep_" + Guid.NewGuid());
        _actors = new ActorRepository(_dbService);
        _sessions = new RavenSessionRepository(_dbService);
        _registry = new ComponentRegistry();
        _registry.Register<ResourcePoolComponent>();
        _registry.Register<ActiveEffectsComponent>();
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_content));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _dbService.Dispose();

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries(SystemId, type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(SystemId, type, Packs);
    }

    [Fact]
    public async Task TheMomentCommand_FillsFocusPointsAndSlotsTogether_AndOneUndoRestoresBoth()
    {
        var resolver = new PackResolver(_content);
        var sheet = new Dictionary<string, object>();
        new SheetRuleEngine(_system, resolver).InitializeSheet(sheet);
        sheet["ancestry"] = "dwarf-ancestry";
        sheet["background"] = "acolyte";
        sheet["class"] = "bard-class";
        sheet["subclass"] = "maestro";
        sheet["level"] = 5L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 12L;
        new SheetRuleEngine(_system, resolver).Recompute(sheet); // writes the slot maxima and the granted focus spell
        var slotKeys = sheet.Keys.Where(k => k.StartsWith("slots_", StringComparison.Ordinal) && !k.EndsWith("_Max", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(slotKeys);
        foreach (var key in slotKeys) sheet[key] = 0L;
        sheet["focusPoints"] = 0L;

        var campaignId = Guid.NewGuid().ToString();
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Brin", Kind = ActorKind.PlayerCharacter, Sheet = sheet };
        ActorSheetSync.ApplySheetToState(doc, _system, _registry);
        await _actors.SaveAsync(doc);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        await using var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: _system) { Resolver = resolver };
        runtime.Hydrate(new[] { doc });
        int Pool(string key) => runtime.GetEntity(doc.Id)!.Value.Get<ResourcePoolComponent>().Get(key);

        await runtime.EnqueueAsync(new MomentCommand(doc.Id, "dailyPrep"));

        Assert.Equal(1, Pool("focusPoints")); // the bard's one granted focus spell
        Assert.All(slotKeys, key => Assert.Equal(Pool(key + "_Max"), Pool(key)));
        Assert.True(Pool(slotKeys[0] + "_Max") > 0);

        await runtime.EnqueueAsync(new UndoLastChangeCommand(doc.Id));

        Assert.Equal(0, Pool("focusPoints"));
        Assert.All(slotKeys, key => Assert.Equal(0, Pool(key)));
    }

    private Dictionary<string, object> ClericSheet(long level)
    {
        var resolver = new PackResolver(_content);
        var sheet = new Dictionary<string, object>();
        new SheetRuleEngine(_system, resolver).InitializeSheet(sheet);
        sheet["ancestry"] = "dwarf-ancestry";
        sheet["background"] = "acolyte";
        sheet["class"] = "cleric-class";
        sheet["level"] = level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 12L;
        new SheetRuleEngine(_system, resolver).Recompute(sheet);
        return sheet;
    }

    [Theory]
    [InlineData(1L, 4L)]
    [InlineData(4L, 4L)]
    [InlineData(5L, 5L)]
    [InlineData(14L, 5L)]
    [InlineData(15L, 6L)]
    [InlineData(20L, 6L)]
    public void AClericsDivineFont_HasTheCountTheBookStepsUpToAtEachLevel(long level, long expected)
    {
        var sheet = ClericSheet(level);
        Assert.Equal(expected, Convert.ToInt64(sheet["uses_divinefont_Max"]));
    }

    [Fact]
    public async Task TheDailyPreparations_RefillAClericsDivineFont()
    {
        var resolver = new PackResolver(_content);
        var sheet = ClericSheet(5L);
        sheet["uses_divinefont"] = 0L;

        var campaignId = Guid.NewGuid().ToString();
        var doc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Name = "Ilse", Kind = ActorKind.PlayerCharacter, Sheet = sheet };
        ActorSheetSync.ApplySheetToState(doc, _system, _registry);
        await _actors.SaveAsync(doc);
        var session = new SessionDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaignId, Status = SessionStatus.Live };
        await _sessions.SaveAsync(session);
        await using var runtime = new CampaignRuntime(campaignId, _actors, _registry, NullLogger.Instance, _sessions, session, system: _system) { Resolver = resolver };
        runtime.Hydrate(new[] { doc });
        int Pool(string key) => runtime.GetEntity(doc.Id)!.Value.Get<ResourcePoolComponent>().Get(key);
        Assert.Equal(0, Pool("uses_divinefont"));

        await runtime.EnqueueAsync(new MomentCommand(doc.Id, "dailyPrep"));

        Assert.Equal(5, Pool("uses_divinefont"));
    }
}
