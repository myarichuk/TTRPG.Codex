using System.Globalization;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// An imported ancestry and heritage (the generated <c>rules-core</c> pack, chapter 7a of the book import) on a Four Degrees character sheet at level 1: a dwarf with the rock heritage and the homebrew warden class (nine hit
/// points a level and a boost to Strength). Every number the sheet is expected to show is worked out from the property values the pack file holds (read from the loaded entries in the test), never from memory of the book.
/// No heritage effect is modelled: a heritage's rules are text, and the importer writes no <c>modifiers</c> (the sheet reads them from a heritage the way it does from an ancestry, so a hand-written one would reach it).
/// </summary>
public class FourDegreesAncestryTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "four-degrees-core", "four-degrees-rules" };

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public FourDegreesAncestryTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries(SystemId, type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(SystemId, type, Packs);
    }

    private SheetRuleEngine Engine() => new(_system, new PackResolver(_registry));

    private ContentEntry Entry(string type, string id) => _registry.GetEntries(SystemId, type, Packs).Single(e => e.Id == id);

    private static long Number(ContentEntry entry, string key) => Convert.ToInt64(entry.Properties[key], CultureInfo.InvariantCulture);

    private static string[] Words(ContentEntry entry, string key) => ((IEnumerable<object>)entry.Properties[key]).Select(o => (string)o).ToArray();

    private Dictionary<string, object> Character(string ancestry, string? heritage)
    {
        var sheet = new Dictionary<string, object>();
        Engine().InitializeSheet(sheet);
        sheet["ancestry"] = ancestry;
        if (heritage != null) sheet["heritage"] = heritage;
        sheet["background"] = "ferry-hand";
        sheet["class"] = "warden";
        sheet["level"] = 1L;
        sheet[SheetRuleEngine.MethodKey] = "boosts";
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet.Remove(SheetRuleEngine.BasePrefix + key);
        return sheet;
    }

    [Fact]
    public void ADwarfWithTheRockHeritage_HasTheAncestrysHitPoints_AndItsBoostsAndFlaw()
    {
        var dwarf = Entry("ancestry", "dwarf-ancestry");
        var rock = Entry("heritage", "rock-dwarf");
        var warden = Entry("class", "warden");
        Assert.Equal("dwarf-ancestry", (string)rock.Properties["ancestry"]);

        // The pack's boost words are a fixed Constitution and Wisdom, one free boost and a Charisma flaw; the one free boost is the ancestry's slot b1.
        Assert.Equal(new[] { "con", "wis", "free", "-cha" }, Words(dwarf, "boosts"));
        var sheet = Character("dwarf-ancestry", "rock-dwarf");
        sheet["boost__ancestry__dwarf-ancestry.b1"] = new List<object> { "dex" };
        // The four free boosts that finish the 1st-level build, each to a different modifier (Charisma stays at the flaw).
        foreach (var (slot, key) in new[] { ("m1", "con"), ("m2", "int"), ("m3", "wis"), ("m4", "str") }) sheet[$"boost__lvl1__{slot}"] = new List<object> { key };

        var evaluation = Engine().Recompute(sheet);

        Assert.Empty(evaluation.Violations);
        // Constitution: the ancestry's fixed boost and a free one; Wisdom the same; Strength: the class's boost and a free one; Dexterity: the ancestry's free boost; Intelligence: a free boost; Charisma: the flaw.
        Assert.Equal(2L, sheet["con"]);
        Assert.Equal(2L, sheet["wis"]);
        Assert.Equal(2L, sheet["str"]);
        Assert.Equal(1L, sheet["dex"]);
        Assert.Equal(1L, sheet["int"]);
        Assert.Equal(-1L, sheet["cha"]);
        // Hit points: the class's number a level, plus the Constitution modifier, plus the ancestry's number once.
        var expected = Number(warden, "hp") + 2 + Number(dwarf, "hp");
        Assert.Equal(expected, Convert.ToInt64(sheet["HP_Max"], CultureInfo.InvariantCulture));
        Assert.Equal(21L, expected);
    }

    [Fact]
    public void TheHeritageChoice_IsLimitedToTheChosenAncestrys()
    {
        var evaluation = Engine().Recompute(Character("dwarf-ancestry", "rock-dwarf"));
        var allowed = evaluation.AllowedOptions["heritage"].Select(e => e.Id).Order().ToArray();
        var dwarfs = _registry.GetEntries(SystemId, "heritage", Packs).Where(h => (string)h.Properties["ancestry"] == "dwarf-ancestry").Select(h => h.Id).Order().ToArray();

        Assert.Equal(5, dwarfs.Length);
        Assert.Equal(dwarfs, allowed);
        Assert.DoesNotContain(Problems(evaluation), v => v.FieldKey == "heritage");

        // A heritage of another ancestry is a problem (the system's enforcement is `warn`, so the sheet says it as a warning), and the choice is the elf's six for an elf.
        var other = Engine().Recompute(Character("dwarf-ancestry", "arctic-elf"));
        Assert.Contains(Problems(other), v => v.FieldKey == "heritage");
        var elf = Engine().Recompute(Character("elf-ancestry", "arctic-elf"));
        Assert.Equal(6, elf.AllowedOptions["heritage"].Count);
        Assert.DoesNotContain(Problems(elf), v => v.FieldKey == "heritage");
    }

    [Theory]
    [InlineData("dwarf-ancestry", 20d)]
    [InlineData("elf-ancestry", 30d)]
    [InlineData("goblin-ancestry", 25d)]
    [InlineData("marshkin", 25d)] // the homebrew ancestry prints no speed: the fallback
    public void TheCharactersSpeed_IsTheChosenAncestrys_OrTwentyFiveWithoutOne(string ancestry, double speed)
    {
        var sheet = Character(ancestry, null);
        Engine().Recompute(sheet);

        var model = new SheetModel(_system, new PackResolver(_registry), sheet).Evaluate();
        Assert.True(model.Warnings.Count == 0, string.Join("|", model.Warnings.Select(w => w.Message)));
        Assert.Equal(speed, model.Values["speed"]);
        if (ancestry.EndsWith("-ancestry", StringComparison.Ordinal)) Assert.Equal(speed, Convert.ToDouble(Entry("ancestry", ancestry).Properties["speed"], CultureInfo.InvariantCulture));
    }

    private static List<RuleViolation> Problems(SheetEvaluation evaluation) => evaluation.Violations.Concat(evaluation.Warnings).ToList();
}
