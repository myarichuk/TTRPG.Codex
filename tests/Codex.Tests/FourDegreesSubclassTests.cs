using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The Four Degrees sheet's subclass (chapter 8a's prerequisite), over made-up classes and subclasses that no pack holds: the subclass choice is limited to the chosen class's, a subclass's feature rows are read by level beside the class's,
/// and a class whose key attribute is a choice of two (written <c>str|dex</c>) has a class DC on the higher modifier.
/// </summary>
public class FourDegreesSubclassTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";

    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public FourDegreesSubclassTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry()));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Row(string name, long level, string to, string rank) => new()
    {
        ["name"] = name,
        ["level"] = level,
        ["description"] = "A made-up feature.",
        ["adds"] = new List<object> { new Dictionary<string, object> { ["to"] = to, ["rank"] = rank } },
    };

    private static readonly List<ContentEntry> Entries = new()
    {
        new ContentEntry
        {
            Id = "pick", Name = "Pick", Type = "class", Properties =
            {
                ["hp"] = 8L, ["keyAttribute"] = "str|dex",
                ["features"] = new List<object> { Row("Trained eye", 1, "perception", "trained"), Row("Class training", 1, "classDc", "trained") },
            },
        },
        new ContentEntry { Id = "other", Name = "Other", Type = "class", Properties = { ["hp"] = 6L, ["keyAttribute"] = "int" } },
        new ContentEntry { Id = "wit", Name = "Wit", Type = "subclass", Properties = { ["class"] = "pick", ["features"] = new List<object> { Row("Sharp edge", 2, "perception", "expert") } } },
        new ContentEntry { Id = "stray", Name = "Stray", Type = "subclass", Properties = { ["class"] = "other" } },
    };

    private sealed class Resolver : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => Entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => Entries.Where(e => e.Type == type);
    }

    private SheetRuleEngine Engine() => new(_system, new Resolver());

    private Dictionary<string, object> Character(string subclass, int level, long str, long dex)
    {
        var sheet = new Dictionary<string, object>();
        Engine().InitializeSheet(sheet);
        sheet["class"] = "pick";
        sheet["subclass"] = subclass;
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = key == "str" ? str : key == "dex" ? dex : 0L;
        return sheet;
    }

    private static List<string> Problems(SheetEvaluation evaluation) => evaluation.Violations.Concat(evaluation.Warnings).Select(v => v.Message).ToList();

    [Fact]
    public void TheSubclassChoice_IsLimitedToTheChosenClasss()
    {
        var evaluation = Engine().Recompute(Character("wit", 1, 0, 0));

        Assert.Equal(new[] { "wit" }, evaluation.AllowedOptions["subclass"].Select(e => e.Id));
        Assert.DoesNotContain(Problems(evaluation), p => p.Contains("another class", StringComparison.Ordinal));

        var wrong = Engine().Recompute(Character("stray", 1, 0, 0));
        Assert.Contains(Problems(wrong), p => p.Contains("that subclass belongs to another class", StringComparison.Ordinal));
    }

    [Fact]
    public void ASubclassesFeatureRows_AreReadByLevelBesideTheClasss()
    {
        var at1 = Engine().Recompute(Character("wit", 1, 0, 0));
        var at2 = Engine().Recompute(Character("wit", 2, 0, 0));

        Assert.Equal(1, at1.Ranked["perception"].Single().Rank); // the class's row
        Assert.Equal(2, at2.Ranked["perception"].Single().Rank); // the subclass's row, from its level
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    [InlineData(2, 2)]
    public void TheClassDcOfAKeyAttributeThatIsAChoiceOfTwo_IsOnTheHigherModifier(long str, long dex)
    {
        var evaluation = Engine().Recompute(Character("wit", 1, str, dex));

        // Ten, the level and the trained rank's 2, and the higher of the two modifiers.
        Assert.Equal(10 + 1 + 2 + Math.Max(str, dex), evaluation.Ranked["classDc"].Single().Bonus);
    }
}
