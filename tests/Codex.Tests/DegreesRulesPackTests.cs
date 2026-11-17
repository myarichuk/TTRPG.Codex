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
    [InlineData("rule", 78)]      // chapter 1: the appendix's own intro rule, condition values, overriding conditions (3); chapter 4: the chapter's own introduction, the checks' own text, 4 sections of the checks,
                                  // 17 skills' descriptions, the lead of the skills, 2 subsections of Performance, 4 of the general actions, one of Crafting and the income table (33); chapter 7: the chapter's
                                  // own introduction, the 15 spellcasting sections (6 of them with their own text before their children), their 17 children, and two boxes among the spells (35); and the
                                  // rules of casting a ritual: the section's own text, its 5 children (the checks section holds the secondary checks) and the box of costs by creature level among the rituals (7)
    [InlineData("spell", 490)]    // chapter 7: 325 spell headers = 325 trait lines = 325 traditions lines, and the four tradition lists name the same (name, rank) pairs; one spell is dropped by a redaction row (324);
                                  // 147 focus spells and focus cantrips (147 headers = 147 trait lines = 147 level markers, 59 group headings = 59 bookmarks); 19 rituals (19 headers, and the book's list by rank names them)
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
    public void EverySpellCarriesItsKindRankAndTraditions_AndTheCountsAreThePinnedOnes()
    {
        // The spells of the four lists; the focus spells and the rituals are pinned below.
        var spells = Entries("spell", PackId).Where(s => s.Source == "pc > Chapter 7: Spells > Spell Lists > Spells").ToList();
        var traitKeys = Entries("trait", PackId).Select(e => e.Id).ToHashSet();
        var texts = new[] { "castTime", "cost", "range", "area", "targets", "defense", "duration", "trigger", "requirements" };
        var tradition = new[] { "arcane", "divine", "occult", "primal" };

        Assert.All(spells, s =>
        {
            Assert.Contains((string)s.Properties["kind"], new[] { "spell", "cantrip" }); // the focus and ritual kinds are the other two rows'
            Assert.True(s.Properties["rank"] is long and >= 1 and <= 10, s.Id);
            if (s.Properties.TryGetValue("actions", out var cost)) Assert.Contains((string)cost, new[] { "one", "two", "three", "reaction", "free" });
            var traits = Assert.IsAssignableFrom<IEnumerable<object>>(s.Properties["traits"]).Select(t => (string)t).ToList();
            Assert.NotEmpty(traits);
            Assert.All(traits, t => Assert.Matches("^[a-z]+$", t));
            var traditions = Assert.IsAssignableFrom<IEnumerable<object>>(s.Properties["traditions"]).Select(t => (string)t).ToList();
            Assert.NotEmpty(traditions);
            Assert.All(traditions, t => Assert.Contains(t, tradition));
            foreach (var key in texts)
            {
                if (s.Properties.TryGetValue(key, out var text)) Assert.False(string.IsNullOrWhiteSpace(text as string), $"{s.Id} {key}");
            }

            // A cantrip says Cantrip in its header and has the cantrip trait; a spell with no action icon takes minutes or hours, or a range of actions, and says so as its cast time.
            Assert.Equal((string)s.Properties["kind"] == "cantrip", traits.Contains("cantrip"));
            Assert.Equal(!s.Properties.ContainsKey("actions"), s.Properties.ContainsKey("castTime"));
            Assert.False(s.Properties.ContainsKey("category"), s.Id);
            Assert.Equal("pc > Chapter 7: Spells > Spell Lists > Spells", s.Source);
        });

        // Counted from the document: 325 headers (300 spells and 25 cantrips) with one dropped; every cantrip is printed as cantrip 1, its rank when it is cast is half the caster's level.
        Assert.Equal(new[] { 299, 25 }, new[] { "spell", "cantrip" }.Select(k => spells.Count(s => (string)s.Properties["kind"] == k)));
        Assert.Equal(new[] { 58, 55, 34, 33, 39, 24, 20, 15, 12, 9 }, Enumerable.Range(1, 10).Select(r => spells.Count(s => (string)s.Properties["kind"] == "spell" && (long)s.Properties["rank"] == r)));
        Assert.All(spells.Where(s => (string)s.Properties["kind"] == "cantrip"), s => Assert.Equal(1L, s.Properties["rank"]));

        // The four lists name 229, 136, 192 and 166 spells at their ranks (the importer compares them with the parsed spells and fails the run on a difference); the divine one lost the dropped spell.
        Assert.Equal(new[] { 229, 135, 192, 166 }, tradition.Select(t => spells.Count(s => ((IEnumerable<object>)s.Properties["traditions"]).Contains(t))));

        // 229 cost two actions, 35 three, 8 one and 4 are reactions; the other 48 have no icon: 42 take minutes or hours and 6 take one to three actions or two to three.
        Assert.Equal(new[] { 8, 229, 35, 4, 0, 48 }, new[] { "one", "two", "three", "reaction", "free" }.Select(c => spells.Count(s => s.Properties.TryGetValue("actions", out var v) && (string)v == c)).Append(spells.Count(s => !s.Properties.ContainsKey("actions"))));
        Assert.Equal(48, spells.Count(s => s.Properties.ContainsKey("castTime")));
        Assert.Equal(6, spells.Count(s => s.Properties.TryGetValue("castTime", out var v) && ((string)v).EndsWith(" actions", StringComparison.Ordinal)));
        Assert.Equal(new[] { 254, 67, 173, 116, 226, 4, 3, 2 }, new[] { "range", "area", "targets", "defense", "duration", "trigger", "requirements", "cost" }.Select(k => spells.Count(s => s.Properties.ContainsKey(k))));

        // 339 degree sections and 265 heightened entries in the 324 spells (the importer counts the bold labels at line starts in the document and fails on a difference), each its own
        // labelled line in its source order with the text, and a spell's own heightened entries come after its degrees.
        Assert.Equal(339, spells.Sum(s => s.Description!.Split('\n').Count(l => Regex.IsMatch(l, @"^\*\*(Critical Success|Success|Failure|Critical Failure)\.\*\*"))));
        Assert.Equal(265, spells.Sum(s => s.Description!.Split('\n').Count(l => l.StartsWith("**Heightened", StringComparison.Ordinal))));
        Assert.All(spells, s =>
        {
            var lines = s.Description!.Split('\n');
            var firstHeightened = Array.FindIndex(lines, l => l.StartsWith("**Heightened", StringComparison.Ordinal));
            if (firstHeightened >= 0) Assert.DoesNotContain(lines.Skip(firstHeightened), l => Regex.IsMatch(l, @"^\*\*(Critical Success|Success|Failure|Critical Failure)\.\*\*"));
        });

        // A trait line names shipped traits, except eight words the glossary has no definition for (the rarity word, three kinds of changes of form, two luck traits and two more).
        Assert.Equal(new[] { "fortune", "misfortune", "morph", "nonlethal", "polymorph", "sanctified", "summon", "uncommon" },
            spells.SelectMany(s => ((IEnumerable<object>)s.Properties["traits"]).Select(t => (string)t)).Where(t => !traitKeys.Contains(t)).Distinct().Order());

        // Six spells are named like a trait (darkness, fear, light, possession, scrying, sleep), so they take the suffix, and nothing else does.
        Assert.Equal(new[] { "darkness-spell", "fear-spell", "light-spell", "possession-spell", "scrying-spell", "sleep-spell" }, spells.Where(s => s.Id.EndsWith("-spell", StringComparison.Ordinal)).Select(s => s.Id).Order());
        Assert.DoesNotContain(spells, s => s.Id == "avatar"); // dropped: its mechanic is a list named for the setting's deities
    }

    [Fact]
    public void EveryFocusSpellCarriesItsKindRankAndGroup_AndTheCountsAreThePinnedOnes()
    {
        var focus = Entries("spell", PackId).Where(s => s.Source == "pc > Chapter 7: Spells > Spell Lists > Focus Spells").ToList();
        var traitKeys = Entries("trait", PackId).Select(e => e.Id).ToHashSet();
        var classes = new[] { "bard", "cleric", "druid", "ranger", "witch", "wizard" };

        Assert.All(focus, s =>
        {
            Assert.Contains((string)s.Properties["kind"], new[] { "focus", "cantrip" });
            Assert.True(s.Properties["rank"] is long and >= 1 and <= 10, s.Id);
            if (s.Properties.TryGetValue("actions", out var cost)) Assert.Contains((string)cost, new[] { "one", "two", "three", "reaction", "free" });
            var traits = Assert.IsAssignableFrom<IEnumerable<object>>(s.Properties["traits"]).Select(t => (string)t).ToList();
            Assert.Contains("uncommon", traits);
            Assert.All(traits, t => Assert.Matches("^[a-z]+$", t));
            Assert.Single(traits.Intersect(classes)); // each belongs to one class
            Assert.False(string.IsNullOrWhiteSpace(s.Properties["focusGroup"] as string), s.Id);
            Assert.False(s.Properties.ContainsKey("traditions"), s.Id); // no tradition lists them
            Assert.False(s.Properties.ContainsKey("category"), s.Id);

            // A focus cantrip says Cantrip in its header and has the cantrip trait; every focus spell has the focus trait. A spell with no action icon says its range of costs as its cast time.
            Assert.Equal((string)s.Properties["kind"] == "cantrip", traits.Contains("cantrip"));
            Assert.Equal((string)s.Properties["kind"] == "focus", traits.Contains("focus"));
            Assert.Equal(!s.Properties.ContainsKey("actions"), s.Properties.ContainsKey("castTime"));
        });

        // Counted from the document: 147 headers, 131 of them focus spells and 16 focus cantrips. A cantrip's printed number is its rank here too (the book prints several).
        Assert.Equal(new[] { 131, 16 }, new[] { "focus", "cantrip" }.Select(k => focus.Count(s => (string)s.Properties["kind"] == k)));
        Assert.Equal(new[] { 64, 4, 5, 46, 6, 1, 1, 1, 1, 2 }, Enumerable.Range(1, 10).Select(r => focus.Count(s => (string)s.Properties["kind"] == "focus" && (long)s.Properties["rank"] == r)));
        Assert.Equal(new[] { 10, 2, 2, 0, 1, 0, 1 }, Enumerable.Range(1, 7).Select(r => focus.Count(s => (string)s.Properties["kind"] == "cantrip" && (long)s.Properties["rank"] == r)));

        // By class (the class trait of the trait line) and by group (the heading the book sets each under): 59 headings, 3 of them hold no spell of their own (a class's heading above its groups).
        Assert.Equal(new[] { 19, 74, 9, 11, 20, 14 }, classes.Select(c => focus.Count(s => ((IEnumerable<object>)s.Properties["traits"]).Contains(c))));
        var groups = focus.GroupBy(s => (string)s.Properties["focusGroup"]).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(56, groups.Count);
        Assert.Equal(new[] { 19, 1, 13, 7 }, new[] { "Bard Composition Spells", "Druid Order Spells", "Witch Hex Spells", "Patron Hex Cantrips" }.Select(g => groups[g]));
        Assert.Equal(new[] { 3, 4, 2, 2 }, new[] { "Initiate Warden Spells", "Advanced Warden Spells", "Master Warden Spells", "Peerless Warden Spells" }.Select(g => groups[g]));
        Assert.Equal(new[] { 1, 2, 3, 2 }, new[] { "Animal Order", "Leaf Order", "Storm Order", "Untamed Order" }.Select(g => groups[g]));
        // 37 domains of a cleric, two spells each (a rank 1 and a rank 4); the 7 schools of a wizard have two each.
        var domains = focus.Where(s => ((IEnumerable<object>)s.Properties["traits"]).Contains("cleric")).GroupBy(s => (string)s.Properties["focusGroup"]).ToList();
        Assert.Equal(37, domains.Count);
        Assert.All(domains, d => Assert.Equal(new long[] { 1, 4 }, d.Select(s => (long)s.Properties["rank"]).Order()));
        Assert.Equal(7, focus.Where(s => ((IEnumerable<object>)s.Properties["traits"]).Contains("wizard")).Select(s => (string)s.Properties["focusGroup"]).Distinct().Count());

        // 62 take one action, 51 two, 1 three, 21 are reactions and 7 are free; the other 5 have no icon and a range of costs (to or or) as their cast time.
        Assert.Equal(new[] { 62, 51, 1, 21, 7, 5 }, new[] { "one", "two", "three", "reaction", "free" }.Select(c => focus.Count(s => s.Properties.TryGetValue("actions", out var v) && (string)v == c)).Append(focus.Count(s => !s.Properties.ContainsKey("actions"))));
        Assert.Equal(new[] { "1 or 2 actions", "1 to 2 actions", "1 to 2 actions", "1 to 3 actions", "1 to 3 actions" }, focus.Where(s => s.Properties.ContainsKey("castTime")).Select(s => (string)s.Properties["castTime"]).Order());
        Assert.Equal(new[] { 87, 27, 77, 39, 92, 24, 3, 0 }, new[] { "range", "area", "targets", "defense", "duration", "trigger", "requirements", "cost" }.Select(k => focus.Count(s => s.Properties.ContainsKey(k))));

        // 101 degree sections and 73 heightened entries in the 147 (the importer counts the bold labels at line starts in the document, with the other spells', and fails on a difference).
        Assert.Equal(101, focus.Sum(s => s.Description!.Split('\n').Count(l => Regex.IsMatch(l, @"^\*\*(Critical Success|Success|Failure|Critical Failure)\.\*\*"))));
        Assert.Equal(73, focus.Sum(s => s.Description!.Split('\n').Count(l => l.StartsWith("**Heightened", StringComparison.Ordinal))));

        // The trait lines name shipped traits, except nine words the glossary has no definition for.
        Assert.Equal(new[] { "composition", "fortune", "hex", "morph", "nonlethal", "polymorph", "sanctified", "spellshape", "uncommon" },
            focus.SelectMany(s => ((IEnumerable<object>)s.Properties["traits"]).Select(t => (string)t)).Where(t => !traitKeys.Contains(t)).Distinct().Order());

        // The patron each hex cantrip names is the setting's: its line is cut, and so is every deity and place a spell's example named (see the redaction rows).
        Assert.DoesNotContain(focus, s => s.Description!.Contains("**Patron.**", StringComparison.Ordinal));
        Assert.Contains("**Lesson.**", focus.Single(s => s.Id == "curse-of-death").Description);
        Assert.Contains("**Muse.**", focus.Single(s => s.Id == "fortissimo-composition").Description);
    }

    [Fact]
    public void EveryRitualCarriesItsRankChecksAndCasters_AndTheCountsAreThePinnedOnes()
    {
        var rituals = Entries("spell", PackId).Where(s => s.Source == "pc > Chapter 7: Spells > Spell Lists > Rituals > Rituals").ToList();
        var texts = new[] { "castTime", "cost", "requirements", "secondaryCasters", "primaryCheck", "secondaryChecks", "range", "area", "targets", "duration" };

        Assert.Equal(19, rituals.Count);
        Assert.All(rituals, s =>
        {
            Assert.Equal("ritual", s.Properties["kind"]);
            Assert.True(s.Properties["rank"] is long and >= 2 and <= 10, s.Id);
            Assert.False(s.Properties.ContainsKey("actions"), s.Id); // a ritual takes days or hours
            var traits = Assert.IsAssignableFrom<IEnumerable<object>>(s.Properties["traits"]).Select(t => (string)t).ToList();
            Assert.Single(traits.Intersect(new[] { "uncommon", "rare" }));
            Assert.All(traits, t => Assert.Matches("^[a-z]+$", t));
            Assert.False(s.Properties.ContainsKey("traditions"), s.Id);
            Assert.False(s.Properties.ContainsKey("focusGroup"), s.Id);
            Assert.False(s.Properties.ContainsKey("category"), s.Id);
            // The three labelled lines only a ritual prints, and its casting time, are always there.
            foreach (var key in new[] { "castTime", "secondaryCasters", "primaryCheck", "secondaryChecks" }) Assert.True(s.Properties.ContainsKey(key), $"{s.Id} {key}");
            // (A count of one or two secondary casters is a number to the loader: the value is read by its text.)
            foreach (var key in texts) if (s.Properties.TryGetValue(key, out var text)) Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(text, System.Globalization.CultureInfo.InvariantCulture)), $"{s.Id} {key}");
            // The second and third labels of a stat line are cut at the semicolon before them.
            Assert.All(texts.Where(k => s.Properties.ContainsKey(k)), k => Assert.DoesNotMatch("; (Secondary|Primary)", Convert.ToString(s.Properties[k], System.Globalization.CultureInfo.InvariantCulture)!));
        });

        // The book lists the rituals by rank (the importer compares the list with the entries, as sets of (name, rank), and fails on a difference): 3, 2, 3, 3, 4, 2, 1 and 1 of ranks 2 to 8 and 10.
        Assert.Equal(new[] { 3, 2, 3, 3, 4, 2, 1, 0, 1 }, new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10 }.Select(r => rituals.Count(s => (long)s.Properties["rank"] == r)));
        Assert.Equal(18, rituals.Count(s => ((IEnumerable<object>)s.Properties["traits"]).Contains("uncommon")));
        Assert.Equal(new[] { "rare", "uncommon" }, rituals.SelectMany(s => ((IEnumerable<object>)s.Properties["traits"]).Select(t => (string)t)).Where(t => t is "rare" or "uncommon").Distinct().Order());
        Assert.Equal(new[] { 19, 15, 1, 12, 5, 10, 10 }, new[] { "castTime", "cost", "requirements", "range", "area", "targets", "duration" }.Select(k => rituals.Count(s => s.Properties.ContainsKey(k))));
        Assert.Equal(72, rituals.Sum(s => s.Description!.Split('\n').Count(l => Regex.IsMatch(l, @"^\*\*(Critical Success|Success|Failure|Critical Failure)\.\*\*"))));
        Assert.Equal(11, rituals.Sum(s => s.Description!.Split('\n').Count(l => l.StartsWith("**Heightened", StringComparison.Ordinal))));

        // The sentences of three rituals that named a deity, the realm of the dead or a plane of the setting are replaced (the denylist gate checks that no such name is left).
        Assert.Contains("beyond death", rituals.Single(s => s.Id == "resurrect").Description);
        Assert.DoesNotContain("(such as", rituals.Single(s => s.Id == "consecrate").Description, StringComparison.Ordinal); // no parenthetical example is left in the replaced sentence
        // The older label of a saving throw is the defense field (two focus spells print it), and the option lines of a spell that takes one or two actions are paragraphs of their own.
        Assert.All(new[] { "pied-piping", "waking-nightmare" }, id => Assert.Equal("Will", Entries("spell", PackId).Single(s => s.Id == id).Properties["defense"]));
        Assert.DoesNotContain(Entries("spell", PackId), s => s.Description!.Contains("**Saving Throw.**", StringComparison.Ordinal));
        Assert.Contains("\n[1 action] (manipulate) The spell has a range of touch.", "\n" + Entries("spell", PackId).Single(s => s.Id == "heal-companion").Description);
    }

    [Fact]
    public void TheSpellcastingRules_AreRulesOfTheCategorySpells_AndTheTwoBoxesAreTheOnlyOnesAmongTheSpells()
    {
        var casting = Entries("rule", PackId).Where(r => (string)r.Properties["category"] == "Spells").ToList();
        var rules = casting.Where(r => !r.Source!.Contains("Rituals", StringComparison.Ordinal)).ToList();

        // The rules of casting a ritual: the section's own text, its five children (the secondary checks are a paragraph of the checks) and the box of costs by creature level among the rituals.
        Assert.Equal(new[] { "casting-rituals", "checks", "cost", "creature-creation-rituals", "effect", "learning-rituals", "secondary-casters" }, casting.Except(rules).Select(r => r.Id).Order());
        Assert.Equal("pc > Chapter 7: Spells > Spell Lists > Rituals > Casting Rituals", casting.Single(r => r.Id == "casting-rituals").Source);
        Assert.Equal("pc > Chapter 7: Spells > Spell Lists > Rituals > Rituals", casting.Single(r => r.Id == "creature-creation-rituals").Source);
        Assert.Contains("Secondary Checks", casting.Single(r => r.Id == "checks").Description);
        Assert.Equal(42, casting.Count);

        Assert.Equal(35, rules.Count);
        Assert.Contains(rules, r => r.Id == "spellcasting" && r.Source == "pc > Chapter 7: Spells");
        Assert.Contains(rules, r => r.Id == "prepared-spells" && r.Source == "pc > Chapter 7: Spells > Spell Slots > Prepared Spells");
        Assert.Equal(new[] { "phantasmal-minion-statistics", "the-summon-trait" }, rules.Where(r => r.Source == "pc > Chapter 7: Spells > Spell Lists > Spells").Select(r => r.Id).Order());

        // The sections that have child sections are an entry of their own text and an entry for each child: 15 sections, 17 children, the chapter's introduction and the 2 boxes.
        Assert.Equal(1 + 15 + 17 + 2, rules.Count);
        Assert.Equal(15, rules.Count(r => r.Source!.Split(" > ").Length == 3));
        Assert.Equal(17, rules.Count(r => r.Source!.Split(" > ").Length == 4 && !r.Source.Contains("Spell Lists", StringComparison.Ordinal)));

        // The heightened and the unlabelled stat line of a spell example are rules text, so the example of how a spell is set out keeps its labels as bold run-in headings.
        Assert.Contains("**Tradition.**", rules.Single(r => r.Id == "reading-spells").Description);
        Assert.Contains("**Heightened (rank).**", rules.Single(r => r.Id == "reading-spells").Description);
    }

    [Fact]
    public void ThePackHoldsNothingElse_AndTheReportAgrees()
    {
        const int total = 45 + 125 + 126 + 33 + 50 + 324 + 35 + 147 + 19 + 7;
        Assert.Equal(total, AllEntries(PackId).Count());

        // import-report.yaml holds counts and ids only; its totals are the importer's own and must match what loaded.
        var report = File.ReadAllText(Path.Combine(PackFolder, "import-report.yaml"));
        var reported = Regex.Matches(report, @"^  entries: (\d+)$", RegexOptions.Multiline).Sum(m => int.Parse(m.Groups[1].Value));
        Assert.Equal(total, reported);
        Assert.DoesNotMatch(@"(?m)^  pageReferencesLeft: [1-9]", report);
    }

    [Fact]
    public void EveryEntryNamesItsChapterAndSection_AndCarriesNoHomebrewTag()
    {
        Assert.All(AllEntries(PackId), e =>
        {
            Assert.Matches(@"^pc > (Conditions Appendix( > [A-Za-z ]+)?|Glossary & Index|Chapter [A-Za-z0-9: ]+( > [A-Za-z ,-]+){0,4})$", e.Source!); // chapters 1 to 5b (the chapter title is the book's, with its number, then up to a section and a skill or a child section, and the rituals' rules are four deep; a title may have a comma or a hyphen); a later chapter widens this
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
