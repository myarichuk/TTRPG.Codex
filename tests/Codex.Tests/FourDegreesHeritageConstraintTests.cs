using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The heritage constraint of Four Degrees reads <c>equalsField</c> with <c>orEmpty</c>: a heritage that names no ancestry (a versatile heritage) is open to every ancestry, one that names the sheet's ancestry is too, and
/// one that names another is a warning. Run on made-up heritages, so it holds whatever a pack holds.
/// </summary>
public class FourDegreesHeritageConstraintTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public FourDegreesHeritageConstraintTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition(SystemId)!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Heritages(params (string Id, object? Ancestry)[] heritages) : IEntryResolver
    {
        private readonly List<ContentEntry> _entries = heritages.Select(h =>
        {
            var entry = new ContentEntry { Type = "heritage", Id = h.Id, Name = h.Id, SystemId = SystemId, PackId = "p" };
            if (h.Ancestry != null) entry.Properties["ancestry"] = h.Ancestry;
            return entry;
        }).ToList();

        public ContentEntry? Resolve(string type, string id) => _entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => _entries.Where(e => e.Type == type);
    }

    [Fact]
    public void AHeritageThatNamesNoAncestryIsOpenToEveryAncestry_AndOneOfAnotherIsAWarning()
    {
        var resolver = new Heritages(("open", null), ("blank", ""), ("mine", "elf"), ("theirs", "dwarf"));
        var engine = new SheetRuleEngine(_system, resolver);

        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["ancestry"] = "elf";
        sheet["heritage"] = "open";
        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "blank", "mine", "open" }, evaluation.AllowedOptions["heritage"].Select(e => e.Id).Order());
        Assert.DoesNotContain(evaluation.Violations.Concat(evaluation.Warnings), v => v.FieldKey == "heritage");

        foreach (var chosen in new[] { "blank", "mine" })
        {
            sheet["heritage"] = chosen;
            var each = engine.Recompute(sheet);
            Assert.DoesNotContain(each.Violations.Concat(each.Warnings), v => v.FieldKey == "heritage");
        }

        sheet["heritage"] = "theirs";
        var other = engine.Recompute(sheet);
        Assert.Contains(other.Violations.Concat(other.Warnings), v => v.FieldKey == "heritage");
    }

    [Fact]
    public void WithoutOrEmpty_AnEmptyCandidateIsStillRefused()
    {
        var engine = new SheetRuleEngine(_system, new Heritages(("open", null)));
        var strict = new RuleCondition { Property = "ancestry", EqualsField = "ancestry" };
        var sheet = new Dictionary<string, object> { ["ancestry"] = "elf" };
        var candidate = new ContentEntry { Type = "heritage", Id = "open", Name = "open", SystemId = SystemId, PackId = "p" };

        Assert.False(engine.Holds(strict, candidate, sheet));
        Assert.True(engine.Holds(new RuleCondition { Property = "ancestry", EqualsField = "ancestry", OrEmpty = true }, candidate, sheet));
    }
}
