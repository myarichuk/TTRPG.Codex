using Codex.ContentImport;
using Codex.Core;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The reviewed grants file (tools/Codex.ContentImport/Data/srd521-grants.yaml) against the SRD pack in the repository: every row is
/// classified, and the counts say what the file does (docs/SRD_GRANTS_IMPORT_DESIGN.md, section 7). A change to the file moves the counts,
/// and this test with them.</summary>
public class SrdGrantCoverageTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public SrdGrantCoverageTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private List<ContentEntry> SrdEntries() =>
        new[] { "class", "subclass", "species", "feat", "invocation", "background", "spell" }
            .SelectMany(type => _registry.GetEntries("srd5e", type, new HashSet<string> { "srd-5.2.1" })).ToList();

    [Fact]
    public void EveryRowOfThePackIsClassified_AndTheCountsAreTheDocumentedOnes()
    {
        var coverage = GrantTable.Embedded().Classify(SrdEntries(), _loader.GetDefinition("srd5e"));

        Assert.Equal(258, coverage.Rows("classes"));
        Assert.Equal(58, coverage.Rows("subclasses"));
        Assert.Equal(30, coverage.Rows("traits"));
        Assert.Equal(24, coverage.Rows("lineages"));
        Assert.Equal(17, coverage.Rows("feats"));
        Assert.Equal(28, coverage.Rows("invocations"));
        Assert.Equal(4, coverage.Rows("backgrounds"));
    }

    /// <summary>Row counts by status, per group: grant, covered, already, text. Change them with the file, and say why in
    /// docs/SRD_GRANTS_IMPORT_DESIGN.md (section 7).</summary>
    [Theory]
    [InlineData("classes", 53, 3, 124, 78)]
    [InlineData("invocations", 12, 0, 0, 16)]
    [InlineData("subclasses", 26, 1, 1, 30)]
    [InlineData("traits", 9, 0, 8, 13)]
    [InlineData("lineages", 24, 0, 0, 0)]
    [InlineData("feats", 3, 0, 3, 11)]
    [InlineData("backgrounds", 0, 0, 4, 0)]
    public void TheStatusCountsAreTheDocumentedOnes(string group, int grant, int covered, int already, int text)
    {
        var coverage = GrantTable.Embedded().Classify(SrdEntries(), _loader.GetDefinition("srd5e"));

        Assert.Equal(grant, coverage.Count(group, GrantStatus.Grant));
        Assert.Equal(covered, coverage.Count(group, GrantStatus.Covered));
        Assert.Equal(already, coverage.Count(group, GrantStatus.Already));
        Assert.Equal(text, coverage.Count(group, GrantStatus.Text));
    }

    [Fact]
    public void TheEmbeddedFile_IsTheOneTheImporterReads_AndEveryGrantInThePackCameFromIt()
    {
        // The pack on disk carries a counter on exactly the rows the file gives one to (checked by name, so a hand edit of the pack shows).
        var classes = SrdEntries().Where(e => e.Type == "class").ToList();
        var carrying = classes.SelectMany(c => ((IEnumerable<object>)c.Properties["features"]).OfType<IDictionary<string, object>>()
            .Where(r => r.ContainsKey("uses")).Select(r => $"{c.Id}/{r["name"]}/{(r.TryGetValue("level", out var at) ? at : null)}")).ToList();

        Assert.Contains("barbarian/Rage/1", carrying);
        Assert.Contains("fighter/Action Surge/2", carrying);
        Assert.DoesNotContain("fighter/Action Surge/17", carrying); // covered by the level 2 row: one owner per pool
        Assert.DoesNotContain("fighter/Indomitable/13", carrying);
    }
}
