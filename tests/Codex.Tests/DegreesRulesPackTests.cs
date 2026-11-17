using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Loads the real <c>content/</c> folder and checks the generated <c>rules-core</c> pack of the Four Degrees system (chapter 1 of the book import):
/// its counts, its required fields, its provenance lines, its licence block and that it does not clash with the homebrew pack. The pack is the
/// importer's output, so none of its text is pinned here; the counts come from the document, not from the importer, two ways: the bookmark
/// children of the taken section (46, of which 4 are sidebars) and the entry-header-font count in the taken range (42) agree on the 42 conditions,
///. The rules are the appendix's own intro rule (taken by an `own` path, never dropped) and its two rule sections: 3.
/// The 125 traits (chapter 2) are the glossary's trait definitions. The glossary has no child bookmarks and no entry-header font, so its two counts are the 129 definitions
/// plus the trait lines that only point elsewhere (24), and the occurrences of the trait mark in the range (153); they agree. Four definitions are dropped by redaction rows.
/// </summary>
public class DegreesRulesPackTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";
    private const string PackId = "four-degrees-rules";
    private const string HomebrewId = "four-degrees-core";

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public DegreesRulesPackTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string PackFolder => Path.Combine(TestPaths.ContentRoot, "systems", SystemId, "packs", "rules-core");

    private IEnumerable<ContentEntry> Entries(string type, params string[] packs) => _registry.GetEntries(SystemId, type, new HashSet<string>(packs));

    private IEnumerable<ContentEntry> AllEntries(params string[] packs) => _system.EntityTypes.SelectMany(t => Entries(t.Type, packs));

    [Fact]
    public void LoadsWithoutWarnings()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
        Assert.NotNull(_registry.GetPack(PackId));
    }

    [Theory]
    [InlineData("condition", 42)] // 46 bookmark children minus 4 sidebars, and 42 entry headers in the taken range
    [InlineData("rule", 3)]       // the appendix's own intro rule, condition values, overriding conditions
    [InlineData("trait", 125)]    // chapter 2: the glossary's trait definitions. The importer finds 129 definitions and 24 trait lines that only point to a page or another book
                                  // (153), and reads 153 occurrences of the trait mark in the range: the two counts agree, so no countsDiffer reason is needed.
                                  // Four definitions are dropped by redaction rows (names the setting invented), so the pack holds 125.
    public void ThePackHasItsCounts_AndRequiredFields(string type, int expected)
    {
        var entries = Entries(type, PackId).ToList();

        Assert.Equal(expected, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Id));
            Assert.False(string.IsNullOrWhiteSpace(e.Name), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Id);
            if (type == "rule") Assert.False(string.IsNullOrWhiteSpace(e.Properties["category"] as string), e.Id);
        });
    }

    [Fact]
    public void ThePackHoldsNothingElse_AndTheReportAgrees()
    {
        Assert.Equal(45 + 125, AllEntries(PackId).Count());

        // import-report.yaml holds counts and ids only; its totals are the importer's own and must match what loaded.
        var report = File.ReadAllText(Path.Combine(PackFolder, "import-report.yaml"));
        var reported = Regex.Matches(report, @"^  entries: (\d+)$", RegexOptions.Multiline).Sum(m => int.Parse(m.Groups[1].Value));
        Assert.Equal(45 + 125, reported);
        Assert.DoesNotMatch(@"(?m)^  pageReferencesLeft: [1-9]", report);
    }

    [Fact]
    public void EveryEntryNamesItsChapterAndSection_AndCarriesNoHomebrewTag()
    {
        Assert.All(AllEntries(PackId), e =>
        {
            Assert.Matches(@"^pc > (Conditions Appendix( > [A-Za-z ]+)?|Glossary & Index)$", e.Source!); // chapters 1 and 2; a later chapter widens this
            Assert.DoesNotContain("homebrew", e.Tags);
            Assert.DoesNotMatch(@"\d", e.Source!); // no page numbers
        });
    }

    [Fact]
    public void EveryFourDegreesEntryIsEitherHomebrewOrSourced_Never_Both_NorNeither()
    {
        var entries = AllEntries(PackId, HomebrewId).ToList();

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            var homebrew = e.Tags.Contains("homebrew");
            var sourced = !string.IsNullOrWhiteSpace(e.Source);
            Assert.True(homebrew ^ sourced, $"{e.PackId}:{e.Id} must have exactly one of the homebrew tag and a source line");
        });
    }

    [Fact]
    public void NoIdClashesWithTheHomebrewPack_OrRepeatsAcrossTypes()
    {
        var rules = _system.EntityTypes.SelectMany(t => Entries(t.Type, PackId).Select(e => (t.Type, e.Id))).ToList();
        var homebrew = _system.EntityTypes.SelectMany(t => Entries(t.Type, HomebrewId).Select(e => (t.Type, e.Id))).ToList();

        Assert.Empty(rules.Intersect(homebrew));
        Assert.Empty(rules.Select(r => r.Id).Intersect(homebrew.Select(h => h.Id)));
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void NoTraitIdClashesWithAnyOtherEntryOfTheFourDegreesPacks_OrWithAConditionOrSkillKey()
    {
        var traits = Entries("trait", PackId).Select(e => e.Id).ToList();
        var others = _system.EntityTypes.Where(t => t.Type != "trait").SelectMany(t => Entries(t.Type, PackId, HomebrewId).Select(e => e.Id)).ToHashSet();
        var keys = _system.Conditions.Concat(_system.SheetRules?.Skills?.List.Select(s => s.Key) ?? Enumerable.Empty<string>()).Select(k => k.ToLowerInvariant()).ToHashSet();

        Assert.Equal(traits.Count, traits.Distinct().Count());
        Assert.Empty(traits.Where(others.Contains));
        Assert.Empty(traits.Where(keys.Contains));
        Assert.Empty(Entries("trait", HomebrewId).Select(e => e.Id).Intersect(traits));
    }

    [Fact]
    public void ThePackCarriesTheOrcLicenceBlock_AndDesignatesNoReservedMaterial()
    {
        var license = _registry.GetPack(PackId)!.License;

        Assert.NotNull(license);
        Assert.Equal("ORC", license!.Name);
        Assert.True(license.Modified);
        Assert.Equal(_system.License!.Attribution, license.Attribution); // copied from the system's, which is copied from the book's notice
        Assert.StartsWith("This product is based on the following Licensed Material: ", license.Attribution);
        foreach (var prefix in new[] { "ORC Notice:", "Expressly Designated Licensed Material:" })
        {
            Assert.Equal(_system.License.Notices.Single(n => n.StartsWith(prefix, StringComparison.Ordinal)), license.Notices.Single(n => n.StartsWith(prefix, StringComparison.Ordinal)));
        }

        Assert.Contains(license.Notices, n => n.StartsWith("Reserved Material:", StringComparison.Ordinal) && n.Contains("designates no Reserved Material of its own"));
        Assert.DoesNotContain(license.Notices, n => n.Contains("homebrew", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThePackIsNotBundled_AndStaysOutOfReleasesWithItsSystem()
    {
        var bundled = File.ReadAllLines(Path.Combine(TestPaths.ContentRoot, "systems", "bundled.txt")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

        Assert.DoesNotContain(SystemId, bundled);
        Assert.DoesNotContain("rules-core", bundled);
        Assert.DoesNotContain(PackId, bundled);
    }
}
