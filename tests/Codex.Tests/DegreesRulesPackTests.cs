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
    [InlineData("rule", 36)]      // chapter 1: the appendix's own intro rule, condition values, overriding conditions (3); chapter 4: the chapter's own introduction, the checks' own text, 4 sections of the checks,
                                  // 17 skills' descriptions, the lead of the skills, 2 subsections of Performance, 4 of the general actions, one of Crafting and the income table (33)
    [InlineData("action", 50)]    // chapter 4: the 23 bookmark children (6 general actions and 17 skills) are the 6 action headings and 17 skill headings of their ranges; 50 action headings in all,
                                  // 48 trait lines and 29 action icons (two jumps have no trait line, the activities have no icon), and the overview table of actions lists the same 50 (name, proficiency) pairs
    [InlineData("feat", 126)]     // chapter 3: Chapter 5 of the book has no child bookmarks, so its entries are counted from the document three ways: the 126 header-font blocks that end in the
                                  // type's word and a level, the 126 trait-line blocks (one follows each header) and the 126 level markers read line by line. They agree, so no countsDiffer reason.
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
    public void EveryFeatCarriesItsLevelAndTraits_AndTheLevelsAreThePinnedOnes()
    {
        var feats = Entries("feat", PackId).ToList();
        var traitKeys = Entries("trait", PackId).Select(e => e.Id).ToHashSet();

        Assert.All(feats, f =>
        {
            Assert.True(f.Properties.TryGetValue("level", out var level) && level is long and >= 1, f.Id);
            var traits = Assert.IsAssignableFrom<IEnumerable<object>>(f.Properties["traits"]).Select(t => (string)t).ToList();
            Assert.NotEmpty(traits);
            Assert.All(traits, t => Assert.Matches("^[a-z]+$", t));
            if (f.Properties.TryGetValue("actions", out var actions)) Assert.Contains((string)actions, new[] { "one", "two", "three", "reaction", "free" });
            foreach (var key in new[] { "prerequisites", "frequency", "trigger", "requirements", "cost", "special" })
            {
                if (f.Properties.TryGetValue(key, out var text)) Assert.False(string.IsNullOrWhiteSpace(text as string), $"{f.Id} {key}");
            }
        });

        // Header levels, counted from the document: 67 first-level feats, then 21, 3, 20, 1 and 14.
        Assert.Equal(new long[] { 1, 2, 3, 7, 11, 15 }, feats.Select(f => (long)f.Properties["level"]).Distinct().Order());
        Assert.Equal(new[] { 67, 21, 3, 20, 1, 14 }, new long[] { 1, 2, 3, 7, 11, 15 }.Select(l => feats.Count(f => (long)f.Properties["level"] == l)));

        // A trait line names shipped traits, except the ones the glossary has no definition for (one word on one feat).
        Assert.Single(feats.SelectMany(f => ((IEnumerable<object>)f.Properties["traits"]).Select(t => (string)t)).Where(t => !traitKeys.Contains(t)).Distinct());
    }

    [Fact]
    public void EveryActionCarriesItsProficiency_AndItsSkillOrNone_AndTheCountsAreThePinnedOnes()
    {
        var actions = Entries("action", PackId).ToList();
        var traitKeys = Entries("trait", PackId).Select(e => e.Id).ToHashSet();
        var skillKeys = _system.SheetRules!.Skills!.List.Select(s => s.Key).ToHashSet();

        Assert.All(actions, a =>
        {
            Assert.Contains((string)a.Properties["proficiency"], new[] { "untrained", "trained" });
            if (a.Properties.TryGetValue("skill", out var skill)) Assert.Contains((string)skill, skillKeys); // `lore` is a dropdown option but has no action here: Lore's own prose is a rule
            if (a.Properties.TryGetValue("actions", out var cost)) Assert.Contains((string)cost, new[] { "one", "two" });
            foreach (var key in new[] { "frequency", "trigger", "requirements" })
            {
                if (a.Properties.TryGetValue(key, out var text)) Assert.False(string.IsNullOrWhiteSpace(text as string), $"{a.Id} {key}");
            }

            Assert.False(a.Properties.ContainsKey("category"), a.Id);
            var traits = a.Properties.TryGetValue("traits", out var t) ? Assert.IsAssignableFrom<IEnumerable<object>>(t).Select(x => (string)x).ToList() : new List<string>();
            Assert.All(traits, key => Assert.Contains(key, traitKeys)); // every trait key of an action is a shipped trait
        });

        // Counted from the document: 6 general skill actions (no skill) and 44 in the 17 skills' sections; the overview table lists the same 50 (name, proficiency) pairs.
        var bySkill = actions.GroupBy(a => a.Properties.TryGetValue("skill", out var s) ? (string)s : "").ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int>
        {
            [""] = 6, ["acrobatics"] = 4, ["arcana"] = 1, ["athletics"] = 10, ["crafting"] = 3, ["deception"] = 4, ["diplomacy"] = 3, ["intimidation"] = 2,
            ["medicine"] = 4, ["nature"] = 1, ["performance"] = 1, ["society"] = 1, ["stealth"] = 3, ["survival"] = 3, ["thievery"] = 4,
        }, bySkill);
        Assert.Equal(new[] { 31, 19 }, new[] { "untrained", "trained" }.Select(p => actions.Count(a => (string)a.Properties["proficiency"] == p)));

        // The general ones: Recall Knowledge and Subsist are untrained, the other four trained. A skill with no action of its own (Lore, Occultism, Religion) has none.
        var general = actions.Where(a => !a.Properties.ContainsKey("skill")).ToList();
        Assert.Equal(new[] { "recall-knowledge", "subsist" }, general.Where(a => (string)a.Properties["proficiency"] == "untrained").Select(a => a.Id).Order());
        Assert.Equal(4, general.Count(a => (string)a.Properties["proficiency"] == "trained"));
        Assert.DoesNotContain(actions, a => a.Properties.TryGetValue("skill", out var s) && ((string)s is "lore" or "occultism" or "religion"));

        // 24 cost one action, 5 two; the other 21 have no icon (activities of exploration and downtime, and others that take no set number of actions). 18 print a requirements line.
        Assert.Equal(new[] { 24, 5, 21 }, new[] { "one", "two", null }.Select(c => actions.Count(a => (a.Properties.TryGetValue("actions", out var v) ? (string)v : null) == c)));
        Assert.Equal(18, actions.Count(a => a.Properties.ContainsKey("requirements")));

        // Degrees of success: 145 sections in the 50 actions, as many as the bold degree labels at line starts in the document (the importer counts them and fails on a
        // difference, which is how an action that lost its degrees to a sidebar would show). Every degree keeps its position: none follows text of another kind.
        var degrees = actions.Sum(a => a.Description!.Split('\n').Count(l => Regex.IsMatch(l, @"^\*\*(Critical Success|Success|Failure|Critical Failure)\.\*\*")));
        Assert.Equal(145, degrees);

        // No action id repeats another entry's, and none needed the suffix (no name of an action is a feat's, a condition's or a rule's).
        Assert.DoesNotContain(actions, a => a.Id.EndsWith("-action", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePackHoldsNothingElse_AndTheReportAgrees()
    {
        Assert.Equal(45 + 125 + 126 + 33 + 50, AllEntries(PackId).Count());

        // import-report.yaml holds counts and ids only; its totals are the importer's own and must match what loaded.
        var report = File.ReadAllText(Path.Combine(PackFolder, "import-report.yaml"));
        var reported = Regex.Matches(report, @"^  entries: (\d+)$", RegexOptions.Multiline).Sum(m => int.Parse(m.Groups[1].Value));
        Assert.Equal(45 + 125 + 126 + 33 + 50, reported);
        Assert.DoesNotMatch(@"(?m)^  pageReferencesLeft: [1-9]", report);
    }

    [Fact]
    public void EveryEntryNamesItsChapterAndSection_AndCarriesNoHomebrewTag()
    {
        Assert.All(AllEntries(PackId), e =>
        {
            Assert.Matches(@"^pc > (Conditions Appendix( > [A-Za-z ]+)?|Glossary & Index|Chapter [A-Za-z0-9: ]+( > [A-Za-z ]+){0,2})$", e.Source!); // chapters 1 to 4 (the chapter title is the book's, with its number, then up to a section and a skill); a later chapter widens this
            Assert.DoesNotContain("homebrew", e.Tags);
            Assert.DoesNotMatch(@"(?<!Chapter )\d", e.Source!); // no page numbers (a chapter's own number is not one)
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
