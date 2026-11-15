using Codex.ContentImport;
using Codex.Core;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The srd5e hazard type, its scaling, the eight traps from the reviewed data file and the two homebrew hazards.</summary>
public class Srd5eHazardsTests : IAsyncLifetime
{
    private static readonly string[] TrapIds =
    [
        "collapsing-roof", "falling-net", "fire-casting-statue", "hidden-pit", "poisoned-darts", "poisoned-needle", "rolling-stone", "spiked-pit"
    ];

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public Srd5eHazardsTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private SystemDefinition System => _loader.GetDefinition("srd5e")!;

    private IEnumerable<ContentEntry> Hazards(params string[] packs) => _registry.GetEntries("srd5e", "hazard", new HashSet<string>(packs));

    [Fact]
    public void TheHazardType_IsDeclaredAndIsNotACreature()
    {
        var type = System.FindType("hazard")!;

        Assert.NotNull(type.Hazard);
        Assert.False(type.ActorBlueprint);
        Assert.False(type.PlayerFacing);
        Assert.Equal("noticeDc", type.Hazard!.Notice!.Dc);
        Assert.True(type.Hazard.Notice.Passive);
    }

    [Fact]
    public void TheScaling_FollowsTheSrdDamageProgression_AndRisesWithLevel()
    {
        var rows = System.Hazards!.Scaling;

        Assert.Equal(new[] { "1", "5", "11", "17" }, rows.Select(r => r["level"]));
        Assert.Equal(new[] { "2d10", "4d10", "10d10", "18d10" }, rows.Select(r => r["damage"]));
        Assert.Equal(rows.Select(r => int.Parse(r["saveDc"])).OrderBy(d => d), rows.Select(r => int.Parse(r["saveDc"])));
    }

    [Fact]
    public void TheSrdPack_HasTheEightTraps_AndEachNamesItsParts()
    {
        var traps = Hazards("srd-5.2.1").ToList();

        Assert.Equal(TrapIds.OrderBy(i => i), traps.Select(t => t.Id).OrderBy(i => i));
        foreach (var trap in traps)
        {
            Assert.Equal("trap", trap.Properties["kind"]);
            foreach (var key in new[] { "severity", "level", "trigger", "noticeCheck", "noticeDc", "disableCheck", "disableDc", "saveAttribute", "saveDc", "effect", "reset" })
            {
                Assert.True(trap.Properties.ContainsKey(key), $"{trap.Id} has no {key}.");
            }

            Assert.IsType<long>(trap.Properties["noticeDc"]);
            Assert.IsType<long>(trap.Properties["saveDc"]);
        }
    }

    [Fact]
    public void EveryTrapButTheRollingStone_ScalesThroughTheThreeHigherBands()
    {
        foreach (var trap in Hazards("srd-5.2.1"))
        {
            var rows = trap.Properties.TryGetValue("higherLevels", out var value) ? value as IList<object> : null;
            if (trap.Id == "rolling-stone")
            {
                Assert.Null(rows);
                continue;
            }

            Assert.NotNull(rows);
            Assert.Equal(new[] { "5-10", "11-16", "17-20" }, rows!.Cast<IDictionary<string, object>>().Select(r => (string)r["levels"]));
        }
    }

    [Fact]
    public void TheTrapNumbers_AreWholeNumbersInsideTheCollections()
    {
        var needle = Traps.Read("""
            - id: t
              name: T
              properties:
                noticeDc: 12
                damage: 1
                methods:
                  - { name: Escape, check: Strength, dc: 10, text: Climb. }
                higherLevels:
                  - { levels: 5-10, saveDc: 14 }
            """).Single();

        Assert.Equal(12L, needle.Properties["noticeDc"]);
        Assert.Equal("1", needle.Properties["damage"]);
        var method = (IDictionary<string, object>)((IList<object>)needle.Properties["methods"]).Single();
        Assert.Equal(10L, method["dc"]);
        var row = (IDictionary<string, object>)((IList<object>)needle.Properties["higherLevels"]).Single();
        Assert.Equal(14L, row["saveDc"]);
        Assert.Equal("5-10", row["levels"]);
    }

    [Fact]
    public void TheTwoHomebrewHazards_AreTaggedAndPrefixed_AndNeverClashWithTheSrd()
    {
        var homebrew = Hazards("codex-hazards").ToList();

        Assert.Equal(2, homebrew.Count);
        Assert.All(homebrew, h =>
        {
            Assert.StartsWith("homebrew-", h.Id);
            Assert.Contains("homebrew", h.Tags);
        });
        Assert.Empty(homebrew.Select(h => h.Id).Intersect(TrapIds));
        Assert.Contains(homebrew, h => h.Properties.TryGetValue("complex", out var complex) && complex is true);
    }

    [Fact]
    public void TheSystemLoadsClean_WithTheHazardsAndTheNewPack()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
    }
}
