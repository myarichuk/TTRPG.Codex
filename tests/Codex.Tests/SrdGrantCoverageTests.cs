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
        new[] { "class", "subclass", "species", "feat", "background", "spell" }
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
        Assert.Equal(4, coverage.Rows("backgrounds"));
    }
}
