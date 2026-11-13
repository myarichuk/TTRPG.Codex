using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>A sheet kind's own feature sources (docs/SHEETKINDS_FEATURES_DESIGN.md): the picks of a kind's list carry counters,
/// spends, row choices and modifiers, through the engine <c>sheetRules</c> systems use. The system is small and has no Blades names.</summary>
public class SheetKindFeaturesTests
{
    private sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static ContentEntry Ability(string id, params (string Key, object Value)[] properties)
    {
        var entry = new ContentEntry { Type = "ability", Id = id, Name = char.ToUpperInvariant(id[0]) + id[1..], SystemId = "kind-features", PackId = "test", Description = id + " text." };
        foreach (var (key, value) in properties) entry.Properties[key] = value;
        return entry;
    }

    private static readonly ContentEntry Foresight = Ability("foresight", ("uses", Map(("key", "foresight"), ("max", 2L), ("recovers", Items(Map(("on", "downtime")))))));
    private static readonly ContentEntry Scaled = Ability("scaled", ("uses", Map(("max", "tier + 1"), ("recoversOn", Items("Long Rest")))));
    private static readonly ContentEntry Spender = Ability("spender", ("spends", Map(("key", "foresight"), ("amount", 1L))));
    private static readonly ContentEntry Lost = Ability("lost", ("spends", Map(("key", "nothing"), ("amount", 1L))));
    private static readonly ContentEntry Mule = Ability("mule", ("modifiers", Items(Map(("target", "LoadMax"), ("amount", 2L)))));
    private static readonly ContentEntry Looker = Ability("looker", ("options", Items(
        Map(("name", "Sharp"), ("description", "Sharp.")), Map(("name", "Quiet"), ("description", "Quiet."), ("uses", Map(("key", "quiet"), ("max", 1L), ("recoversOn", Items("Long Rest"))))))),
        ("choose", Map(("id", "style"), ("onChange", "keep"))));
    private static readonly ContentEntry Reliquary = Ability("reliquary", ("features", Items(
        Map(("name", "Hush"), ("description", "Hush."), ("uses", Map(("key", "hush"), ("max", 1L)))), Map(("name", "Roar"), ("description", "Roar.")))));
    private static readonly ContentEntry Upgrade = new() { Type = "upgrade", Id = "vault", Name = "Vault", SystemId = "kind-features", PackId = "test", Properties = { ["uses"] = Map(("key", "vault"), ("max", 1L)) } };

    private static readonly ContentEntry[] All = { Foresight, Scaled, Spender, Lost, Mule, Looker, Reliquary, Upgrade };

    private const string Yaml = """
        id: kind-features
        name: Kind features
        entityTypes:
          - { type: ability, label: Ability }
          - { type: upgrade, label: Upgrade }
        moments: [{ key: downtime }]
        sheetKinds:
          - kind: hero
            features:
              - { list: abilities }
              - { list: upgrades }
              - { list: relics, property: features }
            values:
              - { key: tier, input: true, default: 1 }
              - { key: LoadMax, formula: "5" }
            lists:
              - { key: abilities, label: Abilities, type: ability }
              - { key: upgrades, label: Upgrades, type: upgrade }
              - { key: relics, label: Relics, type: ability }
              - { key: notes, label: Notes }
          - kind: crew
            label: Crew
            lists:
              - { key: abilities, label: Abilities, type: ability }
        """;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse(Yaml);

    private static SheetRuleEngine Engine(SystemDefinition? system = null) => new(system ?? System, new Entries(All));

    private static Dictionary<string, object> Sheet(params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "hero" };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    [Fact]
    public void AKindWithAListSource_CollectsOneFeaturePerPick_ForStringAndMapPicks()
    {
        var sheet = Sheet(("abilities", Items("foresight", Map(("id", "mule"), ("note", "x")), "unknown-id")), ("upgrades", Items("vault")));

        var features = Engine().ActiveFeatures(sheet);

        Assert.Equal(new[] { "Foresight", "Mule", "Vault" }, features.Select(f => f.Name));
        Assert.All(features, f => Assert.True(f.OnKind));
    }

    [Fact]
    public void ARowsPropertySource_GivesOneFeaturePerRow()
    {
        var features = Engine().ActiveFeatures(Sheet(("relics", Items("reliquary"))));

        Assert.Equal(new[] { "Hush", "Roar" }, features.Select(f => f.Name));
    }

    [Fact]
    public void ASheetWithNoPicks_OrAKindWithNoSources_HasNoFeatures()
    {
        Assert.Empty(Engine().ActiveFeatures(Sheet()));
        Assert.Empty(Engine().ActiveFeatures(new Dictionary<string, object> { ["kind"] = "crew", ["abilities"] = Items("foresight") }));
        Assert.Empty(SheetRuleEngine.KindFeatures(SystemDefinitionLoader.Parse("id: plain\nname: Plain\n"), new Entries(All), Sheet(("abilities", Items("foresight")))));
    }

    [Fact]
    public void Uses_OnAKind_WritesTheCounterKeys_StartsFull_AndLeavesWithTheAbility()
    {
        var engine = Engine();
        var sheet = Sheet(("abilities", Items("foresight")));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(2L, sheet["uses_foresight"]);
        Assert.Equal(2L, sheet["uses_foresight_Max"]);
        Assert.Equal("downtime", sheet["uses_foresight_Rest"]);
        Assert.Equal("Foresight", sheet["uses_foresight_Label"]);
        var counter = Assert.Single(evaluation.Counters);
        Assert.Equal("uses_foresight", counter.Key);

        sheet["uses_foresight"] = 1L; // half spent
        engine.Recompute(sheet);
        Assert.Equal(1L, sheet["uses_foresight"]);

        sheet["abilities"] = Items();
        engine.Recompute(sheet);
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith("uses_", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeclaredMoment_IsNotARest_SoNoRestButtonNamesIt_ButARestIs()
    {
        var engine = Engine();
        var sheet = Sheet(("abilities", Items("foresight", "scaled")));

        var evaluation = engine.Recompute(sheet);

        Assert.Empty(evaluation.Counters.Single(c => c.Key == "uses_foresight").RecoversOn);
        Assert.Equal(new[] { "Long Rest" }, evaluation.Counters.Single(c => c.Key.Contains("scaled")).RecoversOn);
        Assert.Empty(SheetRuleEngine.UsesRests(System, sheet, "uses_foresight"));
        Assert.Single(FeatureRecovery.For(sheet, "uses_foresight", "downtime"));
    }

    [Fact]
    public void AMaxThatIsAFormulaOverTheKindsValues_IsWorkedOut_AndNothingLoops()
    {
        var engine = Engine();
        var sheet = Sheet(("abilities", Items("scaled")), ("tier", 3L));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(4L, sheet.Single(p => p.Key.EndsWith("_Max", StringComparison.Ordinal)).Value);
        Assert.Empty(evaluation.Warnings.Where(w => w.Message.Contains("can't be worked out")));
    }

    [Fact]
    public void ASpender_OfAFeatureCounter_GetsAButton_AndOneOfAPoolTheKindLacks_IsAWarningNotACrash()
    {
        var evaluation = Engine().Recompute(Sheet(("abilities", Items("foresight", "spender", "lost"))));

        var spender = Assert.Single(evaluation.Spenders);
        Assert.Equal("Spender", spender.Feature.Name);
        Assert.Equal("uses_foresight", spender.Cost.Key);
        Assert.Contains(evaluation.Warnings, w => w.Key == "Actor.Rule.SpendNoPool");
    }

    [Fact]
    public void AKindFeaturesModifiers_NeverReachKindOf_SoNoFeatureErrorIsWarned()
    {
        var evaluation = Engine().Recompute(Sheet(("abilities", Items("mule", "foresight"))));

        Assert.DoesNotContain(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
        Assert.Empty(evaluation.Granted);
    }

    [Fact]
    public void ARowChoice_OnAKind_IsPending_UntilThePlayerPicks_AndThePickedOptionIsTheFeature()
    {
        var engine = Engine();
        var sheet = Sheet(("abilities", Items("looker")));

        var open = engine.Recompute(sheet);
        var pending = Assert.Single(open.PendingChoices);
        Assert.Equal("choice__looker__style", pending.Key);
        Assert.Equal(new[] { "Looker" }, open.Features.Where(f => f.Name == "Looker").Select(f => f.Name));
        Assert.DoesNotContain("uses_quiet", sheet.Keys);

        engine.ChangeChoice(sheet, "choice__looker__style", new[] { "Quiet" });
        var made = engine.Evaluate(sheet);

        Assert.Empty(made.PendingChoices);
        Assert.Contains(made.Features, f => f.Name == "Looker: Quiet" && f.OnKind);
        Assert.Equal(1L, sheet["uses_quiet"]);
    }

    [Fact]
    public void ARepeatedPick_IsCounted_AsASecondCopy()
    {
        var features = Engine().ActiveFeatures(Sheet(("abilities", Items("foresight", "foresight"))));

        Assert.Equal(new[] { 1, 2 }, features.Select(f => f.Copy));
    }

    [Fact]
    public void TheLoader_RefusesAFieldAByFieldAnUnknownList_AndAListThatIsAlsoModifiersFrom()
    {
        string Errors(string source) => string.Join("\n", SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(Yaml.Replace("- { list: abilities }\n", source + "\n", StringComparison.Ordinal)))));

        Assert.Contains("no reference fields", Errors("- { field: species }"));
        Assert.Contains("byField", Errors("- { list: abilities, byField: level }"));
        Assert.Contains("not one of the kind's lists", Errors("- { list: nowhere }"));
        Assert.Contains("plain records", Errors("- { list: notes }"));
        Assert.Contains("needs a list", Errors("- { property: features }"));
        Assert.Contains("modifiersFrom", string.Join("\n", SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() =>
            SystemDefinitionLoader.Parse(Yaml.Replace("{ key: abilities, label: Abilities, type: ability }", "{ key: abilities, label: Abilities, type: ability, modifiersFrom: { property: modifiers } }", StringComparison.Ordinal))))));
    }

    [Fact]
    public void TheLoader_ChecksAChooseOnAKindSource()
    {
        var noProperty = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() =>
            SystemDefinitionLoader.Parse(Yaml.Replace("- { list: abilities }\n", "- { list: abilities, choose: pick }\n", StringComparison.Ordinal))));
        var badMoment = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() =>
            SystemDefinitionLoader.Parse(Yaml.Replace("- { list: relics, property: features }", "- { list: relics, property: features, choose: { id: pick, changesOn: [nowhen] } }", StringComparison.Ordinal))));

        Assert.Contains(noProperty, e => e.Contains("needs a property"));
        Assert.Contains(badMoment, e => e.Contains("changesOn 'nowhen'"));
        Assert.Empty(SystemDefinitionLoader.Validate(SystemDefinitionLoader.Parse(Yaml.Replace("- { list: relics, property: features }", "- { list: relics, property: features, choose: { id: pick, changesOn: [downtime] } }", StringComparison.Ordinal))));
    }
}
