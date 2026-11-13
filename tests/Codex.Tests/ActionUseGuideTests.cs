using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>What a writer is told about the <c>use</c> property, from the system's own action sources, and that what it writes
/// there reaches the entry.</summary>
public class ActionUseGuideTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public ActionUseGuideTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private SystemDefinition Srd() => _loader.GetDefinition("srd5e")!;

    [Fact]
    public void TheGuide_ExistsOnlyForTypesAnActionReads()
    {
        var system = Srd();

        Assert.NotNull(ActionUseGuide.For(system, "spell"));
        Assert.NotNull(ActionUseGuide.For(system, "equipment"));
        Assert.Null(ActionUseGuide.For(system, "creature"));
        Assert.Null(ActionUseGuide.For(system, "subclass"));
        Assert.Null(ActionUseGuide.For(_loader.GetDefinition("simple-d6")!, "calling"));
    }

    [Fact]
    public void ASpellsGuide_UsesTheSystemsOwnNames()
    {
        var system = Srd();

        var guide = ActionUseGuide.For(system, "spell")!;

        Assert.Contains("\"use\"", guide);
        Assert.Contains("Leave \"use\" out", guide);
        foreach (var type in system.Vitals!.Damage!.Types) Assert.Contains(type, guide);
        Assert.Contains("dex (Dexterity)", guide);
        Assert.Contains("\"save\"", guide);
        Assert.Contains("\"perSlot\"", guide);
        Assert.Contains("\"atLevels\"", guide);
        Assert.Contains("Example: \"use\": {\"save\": \"dex\"", guide);
        Assert.DoesNotContain("tags include", guide);
    }

    [Fact]
    public void AnItemsGuide_NamesTheTagThatMakesItAWeapon()
    {
        var guide = ActionUseGuide.For(Srd(), "equipment")!;

        Assert.Contains("tags include \"weapons\"", guide);
        Assert.Contains("\"abilities\"", guide);
        Assert.Contains("\"attack\": \"melee\"", guide);
        Assert.DoesNotContain("\"perSlot\"", guide);
    }

    [Fact]
    public void ThePrompt_CarriesTheGuide()
    {
        var system = Srd();
        var request = new EntryRequest("A wizard", "Test", "spell", new List<FieldSpec>(), EntryGuide.For(system, "spell"), null, new List<string>());

        var (_, user) = AiPrompts.WriteEntry(request);

        Assert.Contains("What it rolls on the sheet", user);
    }

    [Fact]
    public void AWrittenEntry_KeepsItsUseWhenTheTypeDoesNotDeclareIt()
    {
        var system = Srd();
        var fields = new List<FieldSpec> { new("level", "Level", "Number", null, 0, 9) }.Concat(EntryGuide.Extras(system, "spell")).ToList();
        var request = new EntryRequest("A wizard", "Test", "spell", fields, null, null, new List<string>());

        var entry = AiAssistant.ParseEntry("""
            {"name": "Ember Lance", "description": "A lance of fire.",
             "properties": {"level": 2, "use": {"save": "dex", "damage": "3d6", "damageType": "fire", "perSlot": "1d6", "abilities": ["int"]}, "junk": {"a": 1}}}
            """, request)!;

        var use = Assert.IsType<Dictionary<string, object>>(entry.Properties["use"]);
        Assert.Equal("3d6", use["damage"]);
        Assert.Equal(new object[] { "int" }, (List<object>)use["abilities"]);
        Assert.False(entry.Properties.ContainsKey("junk"));
    }

    [Fact]
    public void AUseThatIsNotAMap_IsDropped()
    {
        var fields = EntryGuide.Extras(Srd(), "spell");

        var entry = AiAssistant.ParseEntry("""{"name": "Dud", "properties": {"use": "8d6 fire"}}""", new EntryRequest("x", "Test", "spell", fields, null, null, new List<string>()))!;

        Assert.False(entry.Properties.ContainsKey("use"));
    }

    [Fact]
    public void AWholeEntryFeature_KeepsItsGrantsToo()
    {
        var system = Srd();
        var fields = EntryGuide.Extras(system, "feat");

        Assert.Contains(fields, f => f.Key == "modifiers");
        Assert.Contains(fields, f => f.Key == "adds");
        Assert.Contains(fields, f => f.Key == "uses");
        Assert.DoesNotContain(fields, f => f.Key == "use");
        Assert.Empty(EntryGuide.Extras(system, "creature"));
    }
}
