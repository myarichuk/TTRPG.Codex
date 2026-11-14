using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>What a roll bonus is called in the viewer's language: a tick box's label and the feature that gives a bonus show the packs' and the system's
/// own translations, ids are never touched, English is unchanged, and the roll's label (the dice log's text) says the same words.</summary>
public class RollBonusNamesTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public RollBonusNamesTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    // The system as the loader holds it, not the localized copy: a host that passes either one gets the translated words.
    private RollBonusNames Names(string system, string culture) => RollBonusNames.For(_registry, _loader.GetDefinition(system), culture);

    [Theory]
    [InlineData("ru", "Засада", "Из укрытия или из ловушки")]
    [InlineData("he", "מארב", "ממסתור או ממלכודת")]
    public void ATickBoxLabel_AndItsSource_AreInTheViewersLanguage_ForAForgedInTheDarkAbility(string culture, string source, string label)
    {
        var names = Names("fitd-srd", culture);
        Assert.Equal(source, names.Source("Ambush"));
        Assert.Equal(label, names.Label("From hiding or a trap", "Ambush"));
    }

    [Theory]
    [InlineData("ru", "Священное оружие")]
    [InlineData("he", "נשק קדוש")]
    public void ALabelThatIsTheFeaturesName_TakesTheFeaturesTranslatedName_ForASubclassRow(string culture, string name)
    {
        var names = Names("srd5e", culture);
        Assert.Equal(name, names.Source("Sacred Weapon"));
        Assert.Equal(name, names.Label("Sacred Weapon", "Sacred Weapon"));
    }

    [Fact]
    public void English_AndWhatHasNoTranslation_StayAsWritten()
    {
        var english = Names("fitd-srd", "en");
        Assert.Equal("Ambush", english.Source("Ambush"));
        Assert.Equal("From hiding or a trap", english.Label("From hiding or a trap", "Ambush"));
        Assert.Equal("Ambush", RollBonusNames.English.Source("Ambush"));

        var russian = Names("fitd-srd", "ru");
        Assert.Equal("Nobody's feature", russian.Source("Nobody's feature"));
        Assert.Equal("A label nobody translated", russian.Label("A label nobody translated", "Nobody's feature"));
    }

    [Fact]
    public void TheRollsLabel_SaysTheTranslatedBonus_ButTheOptInIdIsUntouched()
    {
        var entry = new ContentEntry { Type = "subclass", Id = "tester", Name = "Tester", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "wizard";
        entry.Properties["features"] = new List<object>
        {
            new Dictionary<string, object>
            {
                ["name"] = "Sacred Weapon",
                ["level"] = 3L,
                ["description"] = "x",
                ["modifiers"] = new List<object>
                {
                    new Dictionary<string, object> { ["target"] = "check", ["amount"] = 2L, ["optIn"] = new Dictionary<string, object> { ["label"] = "Sacred Weapon" } }
                }
            }
        };
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, entry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "wizard";
        sheet["subclass"] = "tester";
        sheet["species"] = "human";
        sheet["level"] = 5L;
        var tray = new RollBonusTray();
        var offered = Assert.Single(engine.OptIns(sheet, engine.CheckSite("int")));
        tray.Set(offered.Id, true);

        var english = tray.Build(engine, sheet, engine.CheckSite("int"), "1d20", "Check");
        Assert.Equal("Check (+2 Sacred Weapon)", english.Label);

        var russian = tray.Build(engine, sheet, engine.CheckSite("int"), "1d20", "Check", names: Names("srd5e", "ru"));
        Assert.Equal("Check (+2 Священное оружие)", russian.Label);
        Assert.Equal(offered.Id, Assert.Single(russian.Applied).Id);
        Assert.Contains("Sacred Weapon", offered.Label);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
