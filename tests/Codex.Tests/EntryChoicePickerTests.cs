using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Choices over Library entries (docs/ENTRY_CHOICES_DESIGN.md), step 6: the words a picker's title is built from, the picks a tick makes, a
/// value shown translated but stored as the canonical word, and how the homebrew editor reads and writes such a row. There is no component test
/// infrastructure (no bunit), so the parts the components are built from are tested here, as plain data and functions.</summary>
public class EntryChoicePickerTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public EntryChoicePickerTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        ClassSkillsSettled.LeaveOut(_loader.GetDefinition("srd5e")!);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static ContentEntry Feat(string id, params Dictionary<string, object>[] adds)
    {
        var feat = new ContentEntry { Type = "feat", Id = id, Name = "Magic Initiate", SystemId = "srd5e", PackId = "homebrew" };
        feat.Properties["category"] = "general";
        feat.Properties["repeatable"] = true;
        feat.Properties["adds"] = adds.Cast<object>().ToList();
        return feat;
    }

    private static ContentEntry Initiate() => Feat("initiate",
        Map(("choice", "spell-list"), ("choose", 1L), ("from", Items("Cleric", "Druid", "Wizard"))),
        Map(("to", "grantedSpells"), ("choice", "cantrips"), ("choose", 2L), ("where", Items(
            Map(("property", "level"), ("equals", 0L)),
            Map(("property", "classes"), ("containsChoice", "spell-list"))))),
        Map(("to", "grantedSpells"), ("choice", "spell"), ("choose", 1L), ("free", Map(("max", 1L), ("recoversOn", Items("Long Rest")))), ("where", Items(
            Map(("property", "level"), ("equals", 1L)),
            Map(("property", "classes"), ("containsChoice", "spell-list"))))));

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Character(ContentEntry feat)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, feat));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "fighter";
        sheet["species"] = "halfling";
        sheet["level"] = 1L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        sheet["feats"] = new List<object> { feat.Id };
        return (engine, sheet);
    }

    private static PendingChoice Choice(SheetEvaluation evaluation, string key) =>
        evaluation.PendingChoices.Concat(evaluation.MadeChoices).Single(c => c.Key == key);

    // ---- The title: what the choice wants, from its count and conditions ----

    [Fact]
    public void TheTitle_SaysWhatTheChoiceWants_FromItsCountAndLevel_NotFromTheListsLabel()
    {
        var (engine, sheet) = Character(Initiate());
        SheetRuleEngine.SetChoice(sheet, "choice__initiate__spell-list", new[] { "Wizard" });
        var evaluation = engine.Recompute(sheet);

        var spell = EntryChoiceWords.Wants(Choice(evaluation, "choice__initiate__spell"), "Spell", "Spells");
        Assert.Equal(new ChoiceWant(1, 1, "spell", false), spell); // "choose 1 level-1 spell"

        var cantrips = EntryChoiceWords.Wants(Choice(evaluation, "choice__initiate__cantrips"), "Spell", "Spells");
        Assert.Equal(new ChoiceWant(2, 0, "spells", false), cantrips); // the plural fits the count

        var list = EntryChoiceWords.Wants(Choice(evaluation, "choice__initiate__spell-list"), "Spell", "Spells");
        Assert.True(list.IsValue);
        Assert.Null(list.Noun);
        Assert.Null(list.Level);
    }

    [Fact]
    public void TheTitle_UsesTheSingularWhenThereIsNoPlural_AndNoLevelWhenTheConditionsAskForNone()
    {
        var arcanum = Feat("noconditions", Map(("to", "grantedSpells"), ("choice", "any-spell"), ("choose", 3L), ("where", Items(
            Map(("property", "classes"), ("contains", "Warlock"))))));
        var (engine, sheet) = Character(arcanum);
        var choice = Choice(engine.Recompute(sheet), "choice__noconditions__any-spell");

        var want = EntryChoiceWords.Wants(choice, "Spell", null);

        Assert.Equal(new ChoiceWant(3, null, "spell", false), want);
        Assert.Null(EntryChoiceWords.Wants(choice, null, null).Noun);
    }

    [Theory]
    [InlineData(6L, 6)]
    [InlineData(2.0, 2)]
    [InlineData("3", 3)]
    public void ALevelCondition_IsReadWhateverNumberTypeTheDataUsed(object written, int level) =>
        Assert.Equal(level, EntryChoiceWords.LevelOf(new[] { new RuleCondition { Property = "level", Equals = written } }));

    [Fact]
    public void ALevelCondition_IsLeftOutWhenItIsARangeOrNotANumber()
    {
        Assert.Null(EntryChoiceWords.LevelOf(new[] { new RuleCondition { Property = "level", Gte = 1 } }));
        Assert.Null(EntryChoiceWords.LevelOf(new[] { new RuleCondition { Property = "level", Equals = "many" } }));
        Assert.Null(EntryChoiceWords.LevelOf(new[] { new RuleCondition { Property = "school", Equals = 3L } }));
    }

    // ---- The search and the ticks ----

    [Fact]
    public void TheSearch_FiltersByName_IgnoringCaseAndAccents_AndKeepsTheOrder()
    {
        var options = new[] { new ChoiceOption("a", "Fireball"), new ChoiceOption("b", "Fire Bolt"), new ChoiceOption("c", "Café Light"), new ChoiceOption("d", "Shield") };

        Assert.Equal(new[] { "a", "b" }, EntryChoiceWords.Filter(options, "FIRE").Select(o => o.Key));
        Assert.Equal(new[] { "c" }, EntryChoiceWords.Filter(options, "cafe").Select(o => o.Key));
        Assert.Equal(4, EntryChoiceWords.Filter(options, "  ").Count);
        Assert.Empty(EntryChoiceWords.Filter(options, "zzz"));
    }

    [Fact]
    public void TickingAnOption_ReplacesThePickWhenOneIsWanted_AndIsCappedAtTheCountWhenMoreAre()
    {
        var (engine, sheet) = Character(Initiate());
        SheetRuleEngine.SetChoice(sheet, "choice__initiate__spell-list", new[] { "Wizard" });
        var evaluation = engine.Recompute(sheet);
        var one = Choice(evaluation, "choice__initiate__spell");
        var two = Choice(evaluation, "choice__initiate__cantrips");
        var first = two.Options[0].Key;
        var second = two.Options[1].Key;
        var third = two.Options[2].Key;

        Assert.Equal(new[] { one.Options[1].Key }, EntryChoiceWords.Toggled(one, one.Options[1].Key, true));
        Assert.Equal(new[] { first, second }, EntryChoiceWords.Toggled(two with { Made = new[] { first } }, second, true));
        Assert.Equal(new[] { first, second }, EntryChoiceWords.Toggled(two with { Made = new[] { first, second } }, third, true)); // full: not taken
        Assert.Equal(new[] { second }, EntryChoiceWords.Toggled(two with { Made = new[] { first, second } }, first, false));
        Assert.Equal(two.Made, EntryChoiceWords.Toggled(two, "not-offered", true));
    }

    [Fact]
    public void ADependentChoice_SaysWhichChoiceItWaitsOn_InWords()
    {
        var (engine, sheet) = Character(Initiate());

        var cantrips = Choice(engine.Recompute(sheet), "choice__initiate__cantrips");

        Assert.Equal("spell-list", cantrips.BlockedBy);
        Assert.Equal("spell list", EntryChoiceWords.Humanize(cantrips.BlockedBy!));
        Assert.Equal("skill", EntryChoiceWords.Humanize("magic-initiate__skill"));
    }

    // ---- A value shown translated is stored as the canonical word ----

    [Theory]
    [InlineData("ru", "Волшебник")]
    [InlineData("he", "אשף")]
    public void AValueChoiceShownInAnotherLanguage_StillStoresTheCanonicalValue(string culture, string shown)
    {
        var system = _loader.GetDefinition("srd5e")!.Localize(culture);
        var (engine, sheet) = Character(Initiate());
        var list = Choice(engine.Recompute(sheet), "choice__initiate__spell-list");

        Assert.Contains(list.Options, o => o.Key == "Wizard");
        Assert.Equal(shown, system.DisplayValue("Wizard")); // what the picker shows

        var picks = EntryChoiceWords.Toggled(list, "Wizard", true); // what a tick writes
        SheetRuleEngine.SetChoice(sheet, list.Key, picks);

        Assert.Equal(new[] { "Wizard" }, SheetRuleEngine.RawList(sheet, list.Key));
        var evaluation = engine.Recompute(sheet);
        Assert.Equal(new[] { "Wizard" }, Choice(evaluation, list.Key).Made);
        Assert.NotEmpty(Choice(evaluation, "choice__initiate__spell").Options); // and what depends on it is no longer blocked
    }

    // ---- The homebrew editor: entry, value and mark-only choices round-trip ----

    [Fact]
    public void AnEntryChoiceValueChoiceAndMarkOnlyChoice_RoundTripThroughTheEditorUntouched()
    {
        var feature = new Dictionary<string, object>
        {
            ["name"] = "Initiate",
            ["adds"] = Items(
                Map(("choice", "spell-list"), ("choose", 1L), ("from", Items("Cleric", "Wizard"))),
                Map(("to", "grantedSpells"), ("choice", "spell"), ("choose", 1L), ("fromList", "spells"),
                    ("where", Items(Map(("property", "level"), ("equals", 1L)), Map(("property", "classes"), ("containsChoice", "spell-list")))),
                    ("free", Map(("max", 1L), ("recoversOn", Items("Long Rest"))))),
                Map(("choice", "signature"), ("choose", 2L), ("fromList", "spells"), ("free", "atWill")))
        };

        var data = FeatureGrantData.Read(feature);
        data.Apply(feature);

        var written = ((List<object>)feature["adds"]).Cast<IDictionary<string, object>>().ToList();
        Assert.Equal(3, written.Count);
        Assert.False(written[0].ContainsKey("entries"));
        Assert.Equal(new[] { "Cleric", "Wizard" }, ((IEnumerable<object>)written[0]["from"]).Select(v => v.ToString()));
        Assert.Equal("grantedSpells", written[1]["to"]);
        Assert.Equal("spells", written[1]["fromList"]);
        var where = ((IEnumerable<object>)written[1]["where"]).Cast<IDictionary<string, object>>().ToList();
        Assert.Equal(2, where.Count);
        Assert.Equal("spell-list", where[1]["containsChoice"]);
        Assert.Equal(1L, ((IDictionary<string, object>)written[1]["free"])["max"]);
        Assert.Equal("atWill", written[2]["free"]);
        Assert.True(string.IsNullOrEmpty(written[2]["to"]?.ToString())); // mark-only: still no list
        Assert.Equal(3, data.Counts().Adds); // a choice with no list still counts
        Assert.All(data.Adds, a => Assert.Equal(GrantAddEdit.ChoiceMode, GrantAddEdit.ModeOf(a)));
    }

    [Fact]
    public void TheEditor_ReadsTheConditionsAsData_ToShowThemAsASentence()
    {
        var data = FeatureGrantData.Read(new Dictionary<string, object>
        {
            ["adds"] = Items(Map(("to", "grantedSpells"), ("choice", "arcanum"), ("choose", 1L), ("where", Items(
                Map(("property", "level"), ("equals", 6L)),
                Map(("property", "classes"), ("contains", "Warlock")),
                Map(("property", "classes"), ("containsChoice", "spell-list")),
                Map(("property", "level"), ("lteField", "level")),
                Map(("equals", 4L))))))
        });

        var conditions = GrantAddEdit.Conditions(data.Adds[0]);

        Assert.Equal(new[]
        {
            new GrantAddEdit.GrantCondition("level", GrantAddEdit.CondEquals, "6"),
            new GrantAddEdit.GrantCondition("classes", GrantAddEdit.CondContains, "Warlock"),
            new GrantAddEdit.GrantCondition("classes", GrantAddEdit.CondContainsChoice, "spell-list"),
            new GrantAddEdit.GrantCondition("level", GrantAddEdit.CondOther, string.Empty) // a comparison it has no words for; the one with no property is skipped
        }, conditions);
    }

    [Fact]
    public void ChangingTheCountAndTheFreePart_KeepsTheConditionsAndEverythingElse()
    {
        var feature = new Dictionary<string, object>
        {
            ["adds"] = Items(Map(("to", "grantedSpells"), ("choice", "arcanum"), ("choose", 1L), ("note", "kept"),
                ("where", Items(Map(("property", "level"), ("equals", 6L)))),
                ("free", Map(("max", 1L), ("recoversOn", Items("Long Rest")), ("key", "arcanum-pool")))))
        };
        var data = FeatureGrantData.Read(feature);
        var add = data.Adds[0];

        GrantAddEdit.SetNumber(add, "choose", 2);
        GrantAddEdit.SetFreeMax(add, "2");
        GrantAddEdit.SetFreeRecovers(add, "Short Rest", true);
        data.Apply(feature);

        var written = (IDictionary<string, object>)((List<object>)feature["adds"]).Single();
        Assert.Equal(2L, written["choose"]);
        Assert.Equal("kept", written["note"]);
        Assert.Single((IEnumerable<object>)written["where"]);
        var free = (IDictionary<string, object>)written["free"];
        Assert.Equal(2L, free["max"]);
        Assert.Equal("arcanum-pool", free["key"]);
        Assert.Equal(new[] { "Long Rest", "Short Rest" }, ((IEnumerable<object>)free["recoversOn"]).Select(v => v.ToString()));
        Assert.Equal(GrantAddEdit.FreeUses, GrantAddEdit.FreeKind(add));
    }

    [Fact]
    public void TheFreePart_IsNoneAtWillOrUses_AndMovingBetweenThemWritesTheShapeTheEngineReads()
    {
        var add = new GrantAdd { To = "grantedSpells" };
        add.Extra["choice"] = "c";
        Assert.Equal(GrantAddEdit.FreeNone, GrantAddEdit.FreeKind(add));

        GrantAddEdit.SetFreeKind(add, GrantAddEdit.FreeAtWill);
        Assert.Equal("atWill", add.Extra["free"]);
        Assert.Equal(GrantAddEdit.FreeAtWill, GrantAddEdit.FreeKind(add));

        GrantAddEdit.SetFreeKind(add, GrantAddEdit.FreeUses);
        Assert.Equal("1", GrantAddEdit.FreeMax(add));
        GrantAddEdit.SetFreeRecovers(add, "Long Rest", true);
        GrantAddEdit.SetFreeRecovers(add, "Long Rest", false);
        Assert.Empty(GrantAddEdit.FreeRecoversOn(add));
        Assert.False(((IDictionary<string, object>)add.Extra["free"]).ContainsKey("recoversOn"));

        GrantAddEdit.SetFreeKind(add, GrantAddEdit.FreeNone);
        Assert.False(add.Extra.ContainsKey("free"));
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
