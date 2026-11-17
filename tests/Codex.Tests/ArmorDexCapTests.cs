using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The Dexterity cap of body armor (`armorClass.dexCapLimitsOnlyBonus`): by default a cap of 0 adds no Dexterity either way; with the option the cap is a maximum of the bonus only, so a
/// negative modifier still counts. Positive caps and an armor with no cap read the same under both settings.
/// </summary>
public class ArmorDexCapTests
{
    private static string Yaml(bool only) => $$"""
        id: demo
        name: Demo
        attributes:
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: half-minus-five }
        resources:
          - { key: HP, label: Hit Points, maxKey: HP_Max, default: 10, isPrimary: true }
        actorFields:
          - { key: armorClass, label: Armor Class, type: Number, defaultValue: 10 }
        entityTypes:
          - { type: equipment, label: Equipment }
        sheetRules:
          armorClass: { unarmored: "10 + mod(dex)", dexAttribute: dex{{(only ? ", dexCapLimitsOnlyBonus: true" : string.Empty)}} }
          inventory: { types: [equipment] }
        """;

    private sealed class Resolver : IEntryResolver
    {
        private readonly List<ContentEntry> _entries = new();

        public Resolver()
        {
            Add("plate", 16L, 0L);
            Add("scale", 14L, 2L);
            Add("robe", 11L, null);
        }

        private void Add(string id, long armorBase, long? cap)
        {
            var entry = new ContentEntry { Type = "equipment", Id = id, Name = id, SystemId = "demo", PackId = "p" };
            entry.Properties["armorBase"] = armorBase;
            if (cap != null) entry.Properties["armorDexCap"] = cap.Value;
            _entries.Add(entry);
        }

        public ContentEntry? Resolve(string type, string id) => _entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => _entries.Where(e => e.Type == type);
    }

    private static int ArmorClass(bool only, long dex, string armor)
    {
        var engine = new SheetRuleEngine(SystemDefinitionLoader.Parse(Yaml(only)), new Resolver());
        var sheet = new Dictionary<string, object> { ["dex"] = dex };
        engine.InitializeSheet(sheet);
        sheet["inventory"] = new List<object> { armor + "|equipped:1" };
        return engine.Evaluate(sheet).ArmorClass!.Total;
    }

    [Fact]
    public void ByDefault_ACapOfZero_AddsNoDexterityEitherWay()
    {
        Assert.Empty(SystemDefinitionLoader.Validate(SystemDefinitionLoader.Parse(Yaml(false))));
        Assert.Equal(16, ArmorClass(false, 16, "plate")); // +3, none counts
        Assert.Equal(16, ArmorClass(false, 6, "plate"));  // -2, none counts
    }

    [Fact]
    public void WithTheOption_TheCapIsAMaximumOfTheBonusOnly_AndANegativeModifierStillCounts()
    {
        Assert.Empty(SystemDefinitionLoader.Validate(SystemDefinitionLoader.Parse(Yaml(true))));
        Assert.Equal(16, ArmorClass(true, 16, "plate")); // +3 is capped to 0
        Assert.Equal(14, ArmorClass(true, 6, "plate"));  // -2 passes the cap of 0
        Assert.Equal(16, ArmorClass(true, 10, "plate")); // +0
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APositiveCapAndNoCapReadTheSameUnderBothSettings(bool only)
    {
        Assert.Equal(16, ArmorClass(only, 16, "scale")); // 14 + the cap 2
        Assert.Equal(12, ArmorClass(only, 6, "scale"));  // 14 - 2
        Assert.Equal(14, ArmorClass(only, 16, "robe"));  // 11 + 3, uncapped
        Assert.Equal(9, ArmorClass(only, 6, "robe"));    // 11 - 2
    }
}
