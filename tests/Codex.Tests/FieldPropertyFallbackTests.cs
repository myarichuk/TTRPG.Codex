using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The optional <c>or</c> of a <c>containsPropertyOfField</c> condition (a class that has no tradition of its own reads its subclass's), over an invented system and invented entries: in the engine's own check,
/// in the properties a model is asked to write, and in the loader, which rejects an <c>or</c> that nothing reads.
/// </summary>
public class FieldPropertyFallbackTests
{
    private const string Yaml = """
        id: fallback
        name: Fallback
        actorFields:
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: subclass, label: Subclass, type: EntityReference, targetEntityType: subclass }
        entityTypes:
          - { type: class, label: Class }
          - { type: subclass, label: Subclass }
          - { type: spell, label: Spell }
        sheetRules:
          enforcement: warn
        """;

    private sealed class Resolver : IEntryResolver
    {
        private readonly List<ContentEntry> _entries = new();

        public Resolver()
        {
            Add("class", "mage", p => p["school"] = "arcane");
            Add("class", "seer", _ => { });
            Add("subclass", "dusk", p => p["school"] = "occult");
            Add("subclass", "blank", _ => { });
        }

        private void Add(string type, string id, Action<Dictionary<string, object>> fill)
        {
            var entry = new ContentEntry { Type = type, Id = id, Name = id, SystemId = "fallback", PackId = "p" };
            fill(entry.Properties);
            _entries.Add(entry);
        }

        public ContentEntry? Resolve(string type, string id) => _entries.FirstOrDefault(e => e.Type == type && e.Id == id);

        public IEnumerable<ContentEntry> Candidates(string type) => _entries.Where(e => e.Type == type);
    }

    private static RuleCondition Condition() => new()
    {
        Property = "schools",
        ContainsPropertyOfField = new FieldProperty { Field = "class", Property = "school", Or = new FieldProperty { Field = "subclass", Property = "school" } },
    };

    private static ContentEntry Spell(string school) => new() { Id = school, Name = school, Type = "spell", Properties = { ["schools"] = new List<object> { school } } };

    [Fact]
    public void TheEngine_ReadsTheSecondFieldOnlyWhenTheFirstEntryHasNoValue()
    {
        var system = SystemDefinitionLoader.Parse(Yaml);
        var engine = new SheetRuleEngine(system, new Resolver());
        var seerWithDusk = new Dictionary<string, object> { ["class"] = "seer", ["subclass"] = "dusk" };

        Assert.True(engine.Holds(Condition(), Spell("occult"), seerWithDusk));
        Assert.False(engine.Holds(Condition(), Spell("arcane"), seerWithDusk));
        // The class has a value of its own: the subclass is never read.
        Assert.True(engine.Holds(Condition(), Spell("arcane"), new Dictionary<string, object> { ["class"] = "mage", ["subclass"] = "dusk" }));
        Assert.False(engine.Holds(Condition(), Spell("occult"), new Dictionary<string, object> { ["class"] = "mage", ["subclass"] = "dusk" }));
        // Neither has one, or the subclass is not chosen.
        Assert.False(engine.Holds(Condition(), Spell("occult"), new Dictionary<string, object> { ["class"] = "seer", ["subclass"] = "blank" }));
        Assert.False(engine.Holds(Condition(), Spell("occult"), new Dictionary<string, object> { ["class"] = "seer" }));
    }

    [Fact]
    public void TheValueAModelIsAskedToWrite_ComesFromTheSameFallback()
    {
        var system = SystemDefinitionLoader.Parse(Yaml);
        var resolver = new Resolver();
        var engine = new SheetRuleEngine(system, resolver);

        var fallback = EntryRequirements.For(new[] { Condition() }, system, resolver, engine, new Dictionary<string, object> { ["class"] = "seer", ["subclass"] = "dusk" });
        Assert.Equal(new object[] { "occult" }, ((IEnumerable<object>)fallback!.Properties["schools"]).ToArray());
        var own = EntryRequirements.For(new[] { Condition() }, system, resolver, engine, new Dictionary<string, object> { ["class"] = "mage", ["subclass"] = "dusk" });
        Assert.Equal(new object[] { "arcane" }, ((IEnumerable<object>)own!.Properties["schools"]).ToArray());
        // Neither has one: nothing to write ahead.
        Assert.Null(EntryRequirements.For(new[] { Condition() }, system, resolver, engine, new Dictionary<string, object> { ["class"] = "seer", ["subclass"] = "blank" }));
    }

    [Fact]
    public void TheLoader_RejectsAnOrThatNothingReads()
    {
        string With(string where) => Yaml.Replace("  enforcement: warn", "  enforcement: warn\n  constraints:\n    - field: subclass\n      where: [" + where + "]", StringComparison.Ordinal);

        // The one that is read loads, and a second one is not.
        Assert.NotNull(SystemDefinitionLoader.Parse(With("{ property: school, containsPropertyOfField: { field: class, property: school, or: { field: subclass, property: school } } }")));
        Assert.Contains("nothing reads", Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(With("{ property: school, equalsPropertyOfField: { field: class, property: school, or: { field: subclass, property: school } } }"))).Message);
        Assert.Contains("nothing reads", Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(With(
            "{ property: school, containsPropertyOfField: { field: class, property: school, or: { field: subclass, property: school, or: { field: class, property: school } } } }"))).Message);
        // The second field must exist, as the first must.
        Assert.Contains("does not exist", Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(With("{ property: school, containsPropertyOfField: { field: class, property: school, or: { field: nope, property: school } } }"))).Message);
    }
}
