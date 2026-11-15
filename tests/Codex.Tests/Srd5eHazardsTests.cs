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

    private static string Flat(object? value) => value switch
    {
        null => "null",
        // A whole number is a number whether the file wrote it bare (damage: 1) or the data file read it as text.
        string s => long.TryParse(s, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var n) ? "n:" + n : "s:" + s,
        bool b => "b:" + b,
        sbyte or byte or short or ushort or int or uint or long or ulong => "n:" + global::System.Convert.ToInt64(value, global::System.Globalization.CultureInfo.InvariantCulture),
        global::System.Collections.IDictionary map => "{" + string.Join(";", map.Keys.Cast<object>().Select(k => $"{k}={Flat(map[k])}").OrderBy(x => x, StringComparer.Ordinal)) + "}",
        global::System.Collections.IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(Flat)) + "]",
        _ => "?:" + value
    };

    [Fact]
    public void TheShippedTrapFiles_AreExactlyWhatTheDataFileSays()
    {
        var shipped = Hazards("srd-5.2.1").ToDictionary(h => h.Id);
        var source = Traps.Entries();

        Assert.Equal(source.Select(e => e.Id).Order(), shipped.Keys.Order());
        foreach (var entry in source)
        {
            var file = shipped[entry.Id];
            Assert.Equal(entry.Name, file.Name);
            Assert.Equal(entry.Description, file.Description);
            Assert.Equal(entry.Properties.Keys.Order(), file.Properties.Keys.Order());
            foreach (var (key, value) in entry.Properties)
            {
                Assert.True(Flat(value) == Flat(file.Properties[key]), $"{entry.Id}.{key}: the data file says {Flat(value)}, the shipped file {Flat(file.Properties[key])}. Rewrite srd-5.2.1/hazards from Data/srd521-traps.yaml.");
            }
        }
    }

    // Checks the sheet cannot roll: the table takes a typed bonus for these (HAZARDS_DESIGN.md, H4a). Anything else must name a skill or an attribute,
    // by key or label, so a misspelling fails here.
    private static readonly string[] NeedsTypedBonus = ["Thieves' Tools", "Spellcasting", "Attack"];

    [Fact]
    public void EveryCheckTheHazardsName_IsASkillOrAnAttribute_OrNeedsATypedBonus()
    {
        var skills = System.SheetRules!.Skills!.List.SelectMany(s => new[] { s.Key, s.Label, s.CanonicalLabel });
        var names = skills.Concat(System.Attributes.SelectMany(a => new[] { a.Key, a.Label, a.CanonicalLabel })).Where(n => n != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var hazard in Hazards("srd-5.2.1", "codex-hazards"))
        {
            var checks = new List<string?> { Text(hazard, "noticeCheck"), Text(hazard, "disableCheck"), Text(hazard, "saveAttribute") };
            checks.AddRange(Rows(hazard, "methods").Select(m => m.TryGetValue("check", out var c) ? c?.ToString() : null));
            checks.AddRange(Rows(hazard, "routine").Select(_ => (string?)null));
            unknown.AddRange(checks.Where(c => !string.IsNullOrEmpty(c) && !names.Contains(c) && !NeedsTypedBonus.Contains(c)).Select(c => $"{hazard.Id}: {c}"));
        }

        Assert.Empty(unknown);
    }

    private static string? Text(ContentEntry entry, string key) => entry.Properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static IEnumerable<IDictionary<string, object?>> Rows(ContentEntry entry, string key) =>
        entry.Properties.TryGetValue(key, out var value) && value is IEnumerable<object> rows
            ? rows.OfType<IDictionary<string, object>>().Select(r => (IDictionary<string, object?>)r.ToDictionary(p => p.Key, p => (object?)p.Value))
            : Enumerable.Empty<IDictionary<string, object?>>();

    [Fact]
    public void ThePlacedNumbers_TakeTheChosenRowOverTheBase_AndNeverTheOthers()
    {
        var type = System.FindType("hazard")!;
        var pit = Hazards("srd-5.2.1").Single(h => h.Id == "spiked-pit");

        var baseNumbers = Codex.Core.Systems.HazardNumbers.For(pit, type, null);
        var midNumbers = Codex.Core.Systems.HazardNumbers.For(pit, type, "5-10");

        Assert.Equal("2d10", baseNumbers["damage"]);
        Assert.Equal("4d10", midNumbers["damage"]);
        Assert.Equal(15L, Convert.ToInt64(midNumbers["saveDc"]));
        Assert.Equal(pit.Properties["trigger"], midNumbers["trigger"]);
        Assert.Equal("2d10", Codex.Core.Systems.HazardNumbers.For(pit, type, "no such row")["damage"]);
        Assert.Equal(new[] { "5-10", "11-16", "17-20" }, Codex.Core.Systems.HazardNumbers.Rows(pit, type));
        Assert.Empty(Codex.Core.Systems.HazardNumbers.Rows(Hazards("srd-5.2.1").Single(h => h.Id == "rolling-stone"), type));
    }

    [Fact]
    public void AScaledRowWithAMisspeltKey_OrAnUnnamedMethod_IsReported()
    {
        var type = System.FindType("hazard")!;
        var entry = Traps.Read("""
            - id: t
              name: T
              properties:
                higherLevels:
                  - { levels: 5-10, saveDC: 17 }
                  - { levels: 11-16, bogus: 1 }
                methods:
                  - { check: Strength }
            """).Single();

        var problems = Codex.Core.Systems.HazardNumbers.Problems(entry, type).ToList();

        Assert.Contains(problems, p => p.Contains("saveDC") && p.Contains("saveDc"));
        Assert.Contains(problems, p => p.Contains("bogus"));
        Assert.Contains(problems, p => p.Contains("no name"));
        Assert.Empty(Codex.Core.Systems.HazardNumbers.Problems(Hazards("srd-5.2.1").First(), type));
    }

    [Fact]
    public void TheSystemLoadsClean_WithTheHazardsAndTheNewPack()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
    }
}
