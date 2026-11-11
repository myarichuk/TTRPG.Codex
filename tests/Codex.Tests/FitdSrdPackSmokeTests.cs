using Codex.Core;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Loads the real <c>content/</c> folder and checks the Forged in the Dark SRD system and its pack: the publisher's
/// attribution sentence travels with the text, the name avoids the trademark, the SRD's own lists became entries, and
/// nothing from outside the SRD (the playbooks and setting of the published book) crept in.
/// </summary>
public class FitdSrdPackSmokeTests : IAsyncLifetime
{
    private const string PackId = "srd-fitd-2017";

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public FitdSrdPackSmokeTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void LoadsWithoutWarnings()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
        Assert.Contains("fitd-srd", _loader.LoadedSystemIds);
    }

    [Fact]
    public void SystemIsNamedAfterTheDocument_NotTheTrademark()
    {
        var system = _loader.Systems.Single(s => s.Id == "fitd-srd");
        Assert.Equal("Forged in the Dark SRD", system.Name);
        Assert.Equal("Forged in the Dark", system.Compatibility);
        Assert.DoesNotContain("Blades", system.Name);
        Assert.DoesNotContain("Blades", system.Compatibility);
        Assert.DoesNotContain("Blades", _registry.GetPack(PackId)!.Name);
    }

    [Theory]
    [InlineData("action", 12)]
    [InlineData("trauma", 8)]
    [InlineData("special-ability", 42)]
    [InlineData("crew-ability", 16)]
    [InlineData("crew-upgrade", 18)]
    [InlineData("rule", 201)]
    public void Pack_HasTheSrdsLists(string type, int expected)
    {
        Assert.Equal(expected, _registry.GetEntries("fitd-srd", type, new HashSet<string> { PackId }).Count());
    }

    [Fact]
    public void EveryProperty_IsAFieldTheSystemDeclaresForItsType()
    {
        // A property the system does not declare is never shown, so a key mismatch would drop the value silently.
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "fitd-srd", "system.yaml"));
        var fields = system.EntityTypes.ToDictionary(t => t.Type, t => t.Fields.Select(f => f.Key).ToHashSet());
        foreach (var type in fields.Keys)
        {
            foreach (var entry in _registry.GetEntries("fitd-srd", type, new HashSet<string> { PackId }))
            {
                Assert.All(entry.Properties.Keys, key => Assert.Contains(key, fields[type]));
            }
        }
    }

    [Theory]
    [InlineData("composed", 3)]
    [InlineData("hardened", 3)]
    [InlineData("mastery", 4)]
    public void CrewUpgrades_CarryTheCostTheSrdStates(string id, int cost)
    {
        var upgrade = _registry.GetEntry("fitd-srd", "crew-upgrade", id);
        Assert.NotNull(upgrade);
        Assert.Equal(cost, Convert.ToInt32(upgrade!.Properties["cost"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SpecialAbilities_AllSitUnderTheSrdsSpecialAbilitiesSection()
    {
        // The SRD sets "Fortitude" one heading level too high; it must not become the parent of the abilities after it.
        Assert.All(_registry.GetEntries("fitd-srd", "special-ability", new HashSet<string> { PackId }),
            e => Assert.Equal($"Character playbook > Special abilities > {e.Name}", e.Source));
        Assert.All(_registry.GetEntries("fitd-srd", "crew-ability", new HashSet<string> { PackId }),
            e => Assert.Equal($"Crew playbook > Crew special abilities > {e.Name}", e.Source));
    }

    [Fact]
    public void Trauma_IsTheSrdsEight()
    {
        var names = _registry.GetEntries("fitd-srd", "trauma", new HashSet<string> { PackId }).Select(e => e.Name).Order().ToArray();
        Assert.Equal(new[] { "Cold", "Haunted", "Obsessed", "Paranoid", "Reckless", "Soft", "Unstable", "Vicious" }, names);
    }

    [Fact]
    public void Actions_AreTwelveInThreeAttributes()
    {
        var actions = _registry.GetEntries("fitd-srd", "action", new HashSet<string> { PackId }).ToList();
        Assert.Equal(new[] { "Insight", "Prowess", "Resolve" }, actions.Select(a => Convert.ToString(a.Properties["attribute"])!).Distinct().Select(Capitalize).Order().ToArray());
        Assert.All(new[] { "hunt", "study", "survey", "tinker", "finesse", "prowl", "skirmish", "wreck", "attune", "command", "consort", "sway" },
            id => Assert.NotNull(_registry.GetEntry("fitd-srd", "action", id)));
    }

    [Fact]
    public void Pack_CarriesThePublishersAttributionVerbatim()
    {
        var pack = _registry.GetPack(PackId);
        Assert.NotNull(pack);
        Assert.Equal("CC-BY-3.0", pack!.License!.Name);
        Assert.True(pack.License.Modified);
        Assert.Equal(
            "This work is based on Blades in the Dark (found at http://www.bladesinthedark.com/), product of One Seven Design, developed and authored by John Harper, and licensed for our use under the Creative Commons Attribution 3.0 Unported license (http://creativecommons.org/licenses/by/3.0/).",
            pack.License.Attribution);
        Assert.Contains(pack.License.Notices, n => n.Contains("f141ed2c4ed8061b6833a57e37a314a03e4166ce"));
        Assert.Contains(pack.License.Notices, n => n.Contains("not affiliated with or endorsed by One Seven Design"));
    }

    [Fact]
    public void SystemAndPack_CarryTheSameAttribution()
    {
        var system = _loader.Systems.Single(s => s.Id == "fitd-srd");
        Assert.Equal(_registry.GetPack(PackId)!.License!.Attribution, system.License!.Attribution);
    }

    [Fact]
    public void Pack_HoldsNoMaterialFromOutsideTheSrd()
    {
        // The SRD's playbooks are templates; the book's playbooks, heritages and setting are not licensed.
        foreach (var banned in new[] { "cutter", "hound", "leech", "lurk", "slide", "spider", "whisper" })
        {
            Assert.Null(_registry.GetEntry("fitd-srd", "special-ability", banned));
            Assert.DoesNotContain(_registry.GetEntries("fitd-srd", "rule", new HashSet<string> { PackId }), e => e.Id == banned);
        }
    }

    [Fact]
    public void System_IsNotBundledWithTheApp()
    {
        var bundled = File.ReadAllLines(Path.Combine(TestPaths.ContentRoot, "systems", "bundled.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
        Assert.DoesNotContain("fitd-srd", bundled);
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
