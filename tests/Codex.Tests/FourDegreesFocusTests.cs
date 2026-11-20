using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The focus pool and the daily preparations of the generated <c>rules-core</c> pack on a Four Degrees character sheet. The pool is as big as the focus spells the character
/// knows, to 3 at most, and none before a first one (rules entry <c>focus-spells</c>); the daily preparations refill it (the same entry), and also the spell slots (the
/// <c>dailyPrep</c> moment).
/// </summary>
public class FourDegreesFocusTests : IAsyncLifetime
{
    private const string SystemId = "four-degrees";
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "four-degrees-core", "four-degrees-rules" };

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;

    public FourDegreesFocusTests()
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

    private Dictionary<string, object> Character(string cls, string? subclass, int level)
    {
        var sheet = new Dictionary<string, object>();
        Engine().InitializeSheet(sheet);
        sheet["ancestry"] = "dwarf-ancestry";
        sheet["background"] = "acolyte";
        sheet["class"] = cls;
        if (subclass != null) sheet["subclass"] = subclass;
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        foreach (var key in new[] { "str", "dex", "con", "int", "wis", "cha" }) sheet[SheetRuleEngine.BasePrefix + key] = 12L;
        return sheet;
    }

    private double FocusMax(Dictionary<string, object> sheet) => Convert.ToDouble(Engine().Recompute(sheet).Model!.Values["focusPoints_Max"]);

    private IEnumerable<string> FocusSpells(int count) =>
        _registry.GetEntries(SystemId, "spell", Packs).Where(e => Equals(e.Properties.GetValueOrDefault("kind"), "focus")).Select(e => e.Id).Take(count);

    [Fact]
    public void ACharacterWithNoFocusSpell_HasNoPool()
    {
        Assert.Equal(0, FocusMax(Character("fighter-class", null, 1)));
    }

    [Fact]
    public void ABardsGrantedFocusSpell_GivesAPoolOfOne()
    {
        var sheet = Character("bard-class", "maestro", 1);

        Assert.Equal(1, FocusMax(sheet));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    public void ThePool_IsTheNumberOfFocusSpells_ButNeverMoreThanThree(int spells, int expected)
    {
        var sheet = Character("fighter-class", null, 1);
        sheet["focusSpells"] = FocusSpells(spells).Cast<object>().ToList();
        Assert.Equal(spells, ((List<object>)sheet["focusSpells"]).Count);

        Assert.Equal(expected, FocusMax(sheet));
    }

    [Fact]
    public void TheDailyPreparations_FillTheFocusPool_AndTheSpellSlots_AndTheMomentIsOffered()
    {
        var sheet = Character("bard-class", "maestro", 5);
        sheet["focusSpells"] = FocusSpells(2).Cast<object>().ToList();
        var engine = Engine();
        engine.Recompute(sheet); // writes the slot maxima onto the sheet
        sheet["focusPoints"] = 0L;
        var slotKeys = SystemMoments.SlotKeysRecoveredAt(_system, sheet.Keys, "dailyPrep");

        Assert.NotEmpty(slotKeys);
        Assert.All(slotKeys, key => Assert.StartsWith("slots_", key));
        Assert.True(SystemMoments.Matters(_system, sheet, "dailyPrep"));
        Assert.Contains("Spell slots", SystemMoments.Effects(_system, sheet, "dailyPrep").Tracks);
        Assert.Contains("Focus Points", SystemMoments.Effects(_system, sheet, "dailyPrep").Tracks);

        SheetEvents.Raise(_system, new PackResolver(_registry), sheet, new SheetEvent("dailyPrep", new Dictionary<string, object?>(StringComparer.Ordinal)));

        // The bard's granted focus spell and the two on the list: three in all, which is also the cap.
        Assert.Equal(3, YamlValues.GetInt(sheet, "focusPoints"));
        Assert.DoesNotContain("Long Rest", SystemMoments.RefillWords(_system));
        Assert.Empty(engine.RestNames());
    }
}
