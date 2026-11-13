using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Row choices (docs/FEATURE_GRANTS_DESIGN.md, section 6): the player picks one row of an entry's collection (a feature source's
/// <c>choose</c>) or one of the <c>options</c> of a feature row. The real srd5e pack carries the Dragonborn and the Circle of the Land; a
/// homebrew subclass carries the shapes the pack does not (nested choices, counters that stay or refill).</summary>
public class RowChoiceTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public RowChoiceTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static Dictionary<string, object> Option(string name, params (string Key, object Value)[] grants)
    {
        var row = new Dictionary<string, object> { ["name"] = name, ["description"] = name + " text." };
        foreach (var (key, value) in grants) row[key] = value;
        return row;
    }

    private static Dictionary<string, object> Counter(string key, long max = 3) =>
        Map(("max", max), ("key", key), ("recoversOn", Items("Long Rest")));

    // A subclass with one level 3 row that offers options. Its choose is the dictionary given.
    private static ContentEntry Stacker(Dictionary<string, object> choose, params Dictionary<string, object>[] options)
    {
        var entry = new ContentEntry { Type = "subclass", Id = "stacker", Name = "Stacker", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "sorcerer";
        entry.Properties["features"] = Items(Map(("name", "Surge"), ("level", 3L), ("description", "Pick one."), ("options", options.Cast<object>().ToList()), ("choose", choose)));
        return entry;
    }

    private const string SurgeKey = "choice__stacker__surge";

    private static Dictionary<string, object> SurgeChoose(string onChange = "keep", long count = 1) =>
        Map(("id", "surge"), ("count", count), ("onChange", onChange), ("changesOn", Items("Long Rest")));

    private static Dictionary<string, object> Skillful(string pick) =>
        Map(("to", "skills"), ("choice", pick), ("choose", 1L), ("from", Items("stealth", "arcana")));

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Character(string cls, int level, string species, params ContentEntry[] homebrew)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, homebrew));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = cls;
        if (homebrew.Any(e => e.Type == "subclass")) sheet["subclass"] = "stacker";
        sheet["species"] = species;
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        return (engine, sheet);
    }

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Sorcerer(ContentEntry stacker) => Character("sorcerer", 5, "halfling", stacker);

    // ---- A: one row of an entry's collection (the Dragonborn's ancestry, from the real pack) ----

    [Fact]
    public void Dragonborn_HasAPendingAncestry_AWarningNotAViolation_AndNoRowIsAFeatureYet()
    {
        var (engine, sheet) = Character("fighter", 1, "dragonborn");

        var evaluation = engine.Recompute(sheet);

        var pending = Assert.Single(evaluation.PendingChoices);
        Assert.Equal("choice__dragonborn__lineage", pending.Key);
        Assert.Equal(("Lineage", "Dragonborn", 1, SheetRuleEngine.RowsTarget), (pending.Label, pending.Source, pending.Count, pending.Target));
        Assert.True(pending.IsRow);
        Assert.Equal("keep", pending.Row!.OnChange);
        Assert.Empty(pending.Row.ChangesOn);
        Assert.Equal(10, pending.Options.Count);
        Assert.Contains("Draconic Ancestor: Red", pending.Options.Select(o => o.Key));
        Assert.Contains("Breath Weapon", pending.Options.Single(o => o.Key == "Draconic Ancestor: Red").Description);
        var warning = Assert.Single(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoiceRowPendingKey);
        Assert.True(SheetRuleEngine.IsChoiceNotice(warning));
        Assert.False(evaluation.HasViolations, string.Join("; ", evaluation.Violations.Select(v => v.Message)));
        Assert.DoesNotContain(evaluation.Features, f => f.Name.StartsWith("Draconic Ancestor"));
    }

    [Fact]
    public void Dragonborn_PickingARow_MakesOnlyThatRowAFeature()
    {
        var (engine, sheet) = Character("fighter", 1, "dragonborn");

        var evaluation = engine.ChangeChoice(sheet, "choice__dragonborn__lineage", new[] { "Draconic Ancestor: Red" });

        Assert.Empty(evaluation.PendingChoices);
        Assert.Empty(evaluation.Warnings.Where(SheetRuleEngine.IsChoiceNotice));
        var made = Assert.Single(evaluation.MadeChoices);
        Assert.Equal(new[] { "Draconic Ancestor: Red" }, made.Made);
        Assert.Equal(new[] { "Draconic Ancestor: Red" }, evaluation.Features.Where(f => f.Name.StartsWith("Draconic Ancestor")).Select(f => f.Name));
        Assert.Equal(new[] { "Draconic Ancestor: Red" }, SheetRuleEngine.RawList(sheet, "choice__dragonborn__lineage")); // the row's name as the entry writes it
    }

    [Fact]
    public void AStoredNameThatIsNoRow_IsAViolation_ChooseAgain()
    {
        var (engine, sheet) = Character("fighter", 1, "dragonborn");
        SheetRuleEngine.SetChoice(sheet, "choice__dragonborn__lineage", new[] { "Draconic Ancestor: Plaid" });

        var evaluation = engine.Recompute(sheet);

        var stale = Assert.Single(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.Contains("Plaid", stale.Message);
        Assert.Single(evaluation.PendingChoices);
    }

    [Theory]
    [InlineData("elf", "Elven Lineage: Drow")]
    [InlineData("gnome", "Gnomish Lineage: Rock Gnome")]
    [InlineData("tiefling", "Fiendish Legacy: Infernal")]
    [InlineData("goliath", "Giant Ancestry: Stone's Endurance")]
    public void EverySpeciesWithLineages_AsksForOne_AndOnlyThePickIsAFeature(string species, string pick)
    {
        var (engine, sheet) = Character("fighter", 1, species);

        // An elf's Keen Senses is a second, skill choice; the lineage is the row choice.
        var pending = Assert.Single(engine.Recompute(sheet).PendingChoices, c => c.IsRow);
        Assert.Equal($"choice__{species}__lineage", pending.Key);

        var evaluation = engine.ChangeChoice(sheet, pending.Key, new[] { pick });

        Assert.DoesNotContain(evaluation.PendingChoices, c => c.IsRow);
        var lineageRows = evaluation.Features.Where(f => f.ChosenBy == pending.Key).Select(f => f.Name).ToList();
        Assert.Equal(new[] { pick }, lineageRows);
    }

    [Fact]
    public void ASpeciesWithoutLineages_AsksNothing()
    {
        var (engine, sheet) = Character("fighter", 1, "halfling");

        var evaluation = engine.Recompute(sheet);

        Assert.Empty(evaluation.PendingChoices);
        Assert.Empty(evaluation.Warnings.Where(SheetRuleEngine.IsChoiceNotice));
    }

    // ---- The Circle of the Land, from the real pack ----

    [Fact]
    public void CircleOfTheLand_OffersFourLandTypes_ChangedOnALongRest()
    {
        var (engine, sheet) = Character("druid", 3, "halfling");
        sheet["subclass"] = "circle-of-the-land";

        var evaluation = engine.Recompute(sheet);

        var pending = Assert.Single(evaluation.PendingChoices);
        Assert.Equal("choice__circle-of-the-land__land", pending.Key);
        Assert.Equal("Circle of the Land Spells", pending.Label);
        Assert.Equal(new[] { "Arid", "Polar", "Temperate", "Tropical" }, pending.Options.Select(o => o.Key));
        Assert.Equal(new[] { "Long Rest" }, pending.Row!.ChangesOn);
        Assert.Contains("Fireball", pending.Options[0].Description);
        Assert.Contains(evaluation.Features, f => f.Name == "Circle of the Land Spells"); // the row itself stays a feature
        Assert.DoesNotContain(evaluation.Features, f => f.Name.Contains("Arid"));

        var after = engine.ChangeChoice(sheet, pending.Key, new[] { "Arid" });
        Assert.Contains(after.Features, f => f.Name == "Circle of the Land Spells: Arid");
        Assert.DoesNotContain(after.Features, f => f.Name.Contains("Polar"));
        Assert.Equal("Circle of the Land Spells: Arid", Assert.Single(after.Features, f => f.ChosenBy == pending.Key).Name);
    }

    [Fact]
    public void CircleOfTheLand_AskesNothingBeforeLevelThree()
    {
        var (engine, sheet) = Character("druid", 2, "halfling");
        sheet["subclass"] = "circle-of-the-land";

        Assert.Empty(engine.Recompute(sheet).PendingChoices);
    }

    [Fact]
    public void ChoicesChangingOn_ListsTheChoicesAMomentUsuallyChanges()
    {
        var (engine, sheet) = Character("druid", 3, "halfling");
        sheet["subclass"] = "circle-of-the-land";
        var evaluation = engine.ChangeChoice(sheet, "choice__circle-of-the-land__land", new[] { "Polar" });

        Assert.Equal("choice__circle-of-the-land__land", Assert.Single(SheetRuleEngine.ChoicesChangingOn(evaluation, "Long Rest")).Key);
        Assert.Empty(SheetRuleEngine.ChoicesChangingOn(evaluation, "Short Rest"));
    }

    // ---- B: options inside a feature row ----

    [Fact]
    public void Options_NamedAsFeatureColonOption_OnlyThePickedOneIsAFeature()
    {
        var (engine, sheet) = Sorcerer(Stacker(SurgeChoose(), Option("Fire"), Option("Frost")));

        var open = engine.Recompute(sheet);
        var pending = Assert.Single(open.PendingChoices);
        Assert.Equal((SurgeKey, "Surge", 1), (pending.Key, pending.Label, pending.Count));
        Assert.DoesNotContain(open.Features, f => f.Name.Contains(':'));

        var evaluation = engine.ChangeChoice(sheet, SurgeKey, new[] { "Frost" });
        Assert.Contains(evaluation.Features, f => f.Name == "Surge: Frost");
        Assert.DoesNotContain(evaluation.Features, f => f.Name == "Surge: Fire");
        Assert.Contains(evaluation.Features, f => f.Name == "Surge"); // the row itself remains
    }

    [Fact]
    public void MoreThanOnePick_IsStoredAsAList_AndCappedAtTheCount()
    {
        var (engine, sheet) = Sorcerer(Stacker(SurgeChoose(count: 2), Option("Fire"), Option("Frost"), Option("Storm")));
        SheetRuleEngine.SetChoice(sheet, SurgeKey, new[] { "Fire", "Storm", "Frost" });

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "Surge: Fire", "Surge: Storm" }, evaluation.Features.Where(f => f.Name.StartsWith("Surge:")).Select(f => f.Name));
        Assert.Single(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey); // one too many
        var made = Assert.Single(evaluation.MadeChoices);
        Assert.Equal(2, made.Count);
    }

    [Fact]
    public void ARowWithOptionsAndNoChoose_IsWarnedAbout_AndOffersNothing()
    {
        var entry = Stacker(Map(("count", 1L)), Option("Fire"), Option("Frost")); // no id

        var (engine, sheet) = Sorcerer(entry);
        var evaluation = engine.Recompute(sheet);

        Assert.Empty(evaluation.PendingChoices);
        Assert.Contains(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
    }

    [Fact]
    public void ThePlainStringShorthand_NamesTheChoice()
    {
        var entry = Stacker(Map(("id", "ignored")), Option("Fire"));
        ((Dictionary<string, object>)((List<object>)entry.Properties["features"])[0])["choose"] = "surge";

        var (engine, sheet) = Sorcerer(entry);

        Assert.Equal(SurgeKey, Assert.Single(engine.Recompute(sheet).PendingChoices).Key);
    }

    // ---- Nested grants and nested ids ----

    [Fact]
    public void AChosenOption_CarriesItsOwnGrants_AndItsChoicesAreStoredUnderTheParent()
    {
        var fire = Option("Fire", ("adds", Items(Skillful("trick"))), ("modifiers", Items(Map(("target", "speed"), ("amount", "5")))));
        var (engine, sheet) = Sorcerer(Stacker(SurgeChoose(), fire, Option("Frost", ("adds", Items(Skillful("trick"))))));
        var nestedKey = "choice__stacker__surge__trick";

        // Unchosen options give nothing: no pending skill choice from them.
        var open = engine.Recompute(sheet);
        Assert.DoesNotContain(open.PendingChoices, c => c.Key == nestedKey);

        var picked = engine.ChangeChoice(sheet, SurgeKey, new[] { "Fire" });
        var nested = Assert.Single(picked.PendingChoices, c => c.Key == nestedKey);
        Assert.Equal("skills", nested.Target);

        SheetRuleEngine.SetChoice(sheet, nestedKey, new[] { "stealth" });
        var made = engine.Recompute(sheet);
        Assert.Equal(1, made.Skills.Single(s => s.Key == "stealth").Rank);
        Assert.Contains(made.MadeChoices, c => c.Key == nestedKey);
        Assert.Equal(("stacker", 1, "surge__trick"), SheetRuleEngine.ParseChoiceKey(nestedKey));
    }

    [Fact]
    public void NestedRowChoices_AreNamespacedUnderTheirParent()
    {
        var inner = Option("Inner", ("options", Items(Option("Deep"), Option("Shallow"))), ("choose", Map(("id", "depth"))));
        var (engine, sheet) = Sorcerer(Stacker(SurgeChoose(), inner, Option("Plain")));

        var evaluation = engine.ChangeChoice(sheet, SurgeKey, new[] { "Inner" });

        Assert.Equal("choice__stacker__surge__depth", Assert.Single(evaluation.PendingChoices).Key);
        var after = engine.ChangeChoice(sheet, "choice__stacker__surge__depth", new[] { "Deep" });
        Assert.Contains(after.Features, f => f.Name == "Surge: Inner: Deep");
    }

    [Fact]
    public void ADuplicateChoiceId_InOneEntry_IsWarnedAbout()
    {
        var entry = Stacker(SurgeChoose(), Option("Fire"));
        var rows = (List<object>)entry.Properties["features"];
        rows.Add(Map(("name", "Second"), ("level", 3L), ("options", Items(Option("X"))), ("choose", Map(("id", "surge")))));

        var (engine, sheet) = Sorcerer(entry);
        var evaluation = engine.Recompute(sheet);

        Assert.Single(evaluation.PendingChoices);
        Assert.Contains(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
    }

    // ---- onChange ----

    private static Dictionary<string, object>[] BreathOptions() => new[]
    {
        Option("Red", ("uses", Counter("breath"))),
        Option("Blue", ("uses", Counter("breath"))),
        Option("Lone", ("uses", Counter("lone", 2)))
    };

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) WithBreath(string onChange)
    {
        var (engine, sheet) = Sorcerer(Stacker(SurgeChoose(onChange), BreathOptions()));
        engine.ChangeChoice(sheet, SurgeKey, new[] { "Red" });
        sheet["uses_breath"] = 1L; // two of three spent
        return (engine, sheet);
    }

    [Fact]
    public void OnChangeKeep_ASharedCounterKeepsWhatWasSpent()
    {
        var (engine, sheet) = WithBreath("keep");

        engine.ChangeChoice(sheet, SurgeKey, new[] { "Blue" });

        Assert.Equal(1L, sheet["uses_breath"]);
        Assert.Equal(3L, sheet["uses_breath" + SheetRuleEngine.UsesMaxSuffix]);
    }

    [Fact]
    public void OnChangeRefill_TheNewRowsCountersStartFull()
    {
        var (engine, sheet) = WithBreath("refill");

        engine.ChangeChoice(sheet, SurgeKey, new[] { "Blue" });

        Assert.Equal(3L, sheet["uses_breath"]);
    }

    [Fact]
    public void OnChangeEmpty_TheNewRowsCountersStartAtZero()
    {
        var (engine, sheet) = WithBreath("empty");

        engine.ChangeChoice(sheet, SurgeKey, new[] { "Lone" });

        Assert.Equal(0L, sheet["uses_lone"]);
        Assert.False(sheet.ContainsKey("uses_breath")); // only the old row had it: dropped
    }

    [Fact]
    public void ACounterOnlyTheOldRowHad_IsDropped_AndOneOnlyTheNewRowHasStartsFull()
    {
        var (engine, sheet) = WithBreath("keep");

        engine.ChangeChoice(sheet, SurgeKey, new[] { "Lone" });

        Assert.False(sheet.ContainsKey("uses_breath"));
        Assert.False(sheet.ContainsKey("uses_breath" + SheetRuleEngine.UsesMaxSuffix));
        Assert.Equal(2L, sheet["uses_lone"]);
    }

    [Fact]
    public void TheFirstPick_IsNotAChange_SoEmptyDoesNotApplyToIt()
    {
        var (engine, sheet) = Sorcerer(Stacker(SurgeChoose("empty"), BreathOptions()));

        engine.ChangeChoice(sheet, SurgeKey, new[] { "Red" });

        Assert.Equal(3L, sheet["uses_breath"]);
    }

    [Fact]
    public void ChoosingTheSameRowAgain_ChangesNothing()
    {
        var (engine, sheet) = WithBreath("refill");

        engine.ChangeChoice(sheet, SurgeKey, new[] { "Red" });

        Assert.Equal(1L, sheet["uses_breath"]);
    }

    // ---- The picker's rule for one pick ----

    [Fact]
    public void ARowChoiceOfOne_ReplacesThePick_WhereASkillChoiceDoesNot()
    {
        var (engine, sheet) = Sorcerer(Stacker(SurgeChoose(), Option("Fire"), Option("Frost")));
        var made = engine.ChangeChoice(sheet, SurgeKey, new[] { "Fire" }).MadeChoices.Single();

        Assert.Equal(new[] { "Frost" }, made.Toggled("Frost", true));
        Assert.Empty(made.Toggled("Fire", false));
        Assert.Equal(new[] { "Fire" }, made.Toggled("Nope", true));
    }

    // ---- Pruning ----

    [Fact]
    public void WhenTheSpeciesLeavesTheSheet_ItsRowPicksArePruned()
    {
        var (engine, sheet) = Character("fighter", 1, "dragonborn");
        engine.ChangeChoice(sheet, "choice__dragonborn__lineage", new[] { "Draconic Ancestor: Red" });
        sheet["species"] = "halfling";

        engine.PruneChoices(sheet);

        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith(SheetRuleEngine.ChoicePrefix));
    }

    // ---- Loader checks ----

    private SystemDefinition WithSource(FeatureSource source)
    {
        var definition = _loader.GetDefinition("srd5e")!;
        var copy = (SystemDefinition)definition.GetType().GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(definition, null)!;
        var rules = (SheetRules)typeof(SheetRules).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(definition.SheetRules, null)!;
        rules.Features = rules.Features.Append(source).ToList();
        copy.SheetRules = rules;
        return copy;
    }

    [Fact]
    public void TheSrd5eSystem_PassesTheLoaderChecks()
    {
        Assert.Empty(SystemDefinitionLoader.Validate(_loader.GetDefinition("srd5e")!));
    }

    [Fact]
    public void TheLoader_RefusesAnUnknownOnChange_AndAnUnknownMoment()
    {
        var errors = SystemDefinitionLoader.Validate(WithSource(new FeatureSource
        {
            Field = "species", Property = "lineages", Entries = new() { "elf" },
            Choose = new RowChoose { Id = "x", OnChange = "sometimes", ChangesOn = new() { "Long Rest", "Elevenses", "dawn" } }
        })).ToList();

        Assert.Contains(errors, e => e.Contains("onChange 'sometimes'"));
        Assert.Contains(errors, e => e.Contains("changesOn 'Elevenses'"));
        Assert.DoesNotContain(errors, e => e.Contains("'Long Rest'") || e.Contains("'dawn'"));
    }

    [Fact]
    public void TheLoader_RefusesAChooseOnAListSourceOrAPropertyThatIsNotRows_AndADuplicateId()
    {
        var onList = SystemDefinitionLoader.Validate(WithSource(new FeatureSource { List = "feats", Choose = new RowChoose { Id = "x" } })).ToList();
        var notRows = SystemDefinitionLoader.Validate(WithSource(new FeatureSource { Field = "species", Property = "size", Choose = new RowChoose { Id = "x" } })).ToList();
        var noId = SystemDefinitionLoader.Validate(WithSource(new FeatureSource { Field = "species", Property = "lineages", Choose = new RowChoose() })).ToList();
        var twice = SystemDefinitionLoader.Validate(WithSource(new FeatureSource { Field = "species", Property = "lineages", Choose = new RowChoose { Id = "lineage" } })).ToList();

        Assert.Contains(onList, e => e.Contains("needs a field and a property"));
        Assert.Contains(notRows, e => e.Contains("not a Collection") || e.Contains("is not"));
        Assert.Contains(noId, e => e.Contains("needs an id"));
        Assert.Contains(twice, e => e.Contains("used twice"));
    }

    [Fact]
    public void TheLoader_ReadsChooseAsAStringOrAMap()
    {
        var yaml = File.ReadAllText(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        var shorthand = yaml.Replace("choose: { id: lineage, label: Lineage, onChange: keep }", "choose: lineage");

        var plain = SystemDefinitionLoader.Parse(shorthand).SheetRules!.Features.Single(f => f.Choose != null).Choose!;
        var full = SystemDefinitionLoader.Parse(yaml).SheetRules!.Features.Single(f => f.Choose != null).Choose!;

        Assert.Equal(("lineage", 1, "keep"), (plain.Id, plain.Count, plain.OnChange));
        Assert.Equal(("lineage", "Lineage", "keep"), (full.Id, full.Label, full.OnChange));
    }

    // ---- The draft check, the guide and the editor's data ----

    private IReadOnlyList<string> Problems(ContentEntry entry) => FeatureGrantCheck.Problems(_loader.GetDefinition("srd5e")!, entry);

    [Fact]
    public void TheDraftCheck_AcceptsAWellFormedChoice()
    {
        Assert.Empty(Problems(Stacker(SurgeChoose(), Option("Fire"), Option("Frost"))));
    }

    [Fact]
    public void TheDraftCheck_RefusesOptionsWithoutChoose_AndChooseWithoutId()
    {
        var noChoose = Stacker(SurgeChoose(), Option("Fire"));
        ((Dictionary<string, object>)((List<object>)noChoose.Properties["features"])[0]).Remove("choose");
        var noId = Stacker(Map(("count", 1L)), Option("Fire"));

        Assert.Contains(Problems(noChoose), p => p.Contains("needs \"choose\""));
        Assert.Contains(Problems(noId), p => p.Contains("needs an \"id\""));
    }

    [Fact]
    public void TheDraftCheck_RefusesAnUnknownOnChange_AnUnknownMoment_AndADuplicateId()
    {
        var bad = Stacker(Map(("id", "surge"), ("onChange", "sometimes"), ("changesOn", Items("Long Rest", "Elevenses", "dawn"))), Option("Fire"));
        var rows = (List<object>)bad.Properties["features"];
        rows.Add(Map(("name", "Again"), ("level", 3L), ("options", Items(Option("X"))), ("choose", Map(("id", "surge")))));

        var problems = Problems(bad);

        Assert.Contains(problems, p => p.Contains("\"onChange\" must be keep, refill or empty"));
        Assert.Contains(problems, p => p.Contains("\"Elevenses\""));
        Assert.DoesNotContain(problems, p => p.Contains("\"dawn\"") || p.Contains("\"Long Rest\""));
        Assert.Contains(problems, p => p.Contains("used more than once"));
    }

    [Fact]
    public void TheGuide_TeachesOptionsAndChoose_AndSaysTheIdIsRequired()
    {
        var guide = FeatureGrantGuide.For(_loader.GetDefinition("srd5e")!, "subclass")!;

        Assert.Contains("\"options\" with \"choose\"", guide);
        Assert.Contains("\"id\": required", guide);
        Assert.Contains("onChange", guide);
        Assert.Contains("changesOn", guide);
    }

    [Fact]
    public void TheEditorsData_RoundTripsOptionsChooseAndTheGrantsInsideAnOption()
    {
        var row = Map(("name", "Surge"), ("description", "Pick."), ("options", Items(Option("Fire", ("uses", Counter("breath")), ("custom", "kept")), Option("Frost"))), ("choose", SurgeChoose("refill")));

        var data = FeatureGrantData.Read(row);
        Assert.Equal(2, data.Options.Count);
        Assert.Equal("breath", data.Options[0].Grants.Uses!.Key);
        Assert.Equal(("surge", "refill"), (data.Choose!.Id, data.Choose.OnChange));
        data.Options[1].Name = "Ice";
        var written = new Dictionary<string, object>(row);
        data.Apply(written);

        var options = ((List<object>)written["options"]).Cast<Dictionary<string, object>>().ToList();
        Assert.Equal(new[] { "Fire", "Ice" }, options.Select(o => o["name"]));
        Assert.Equal("kept", options[0]["custom"]);
        Assert.Equal("breath", ((Dictionary<string, object>)options[0]["uses"])["key"]);
        Assert.Equal("refill", ((Dictionary<string, object>)written["choose"])["onChange"]);
        Assert.Equal("Pick.", written["description"]);

        data.Options.Clear();
        data.Apply(written);
        Assert.DoesNotContain("options", written.Keys);
        Assert.DoesNotContain("choose", written.Keys);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
