using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

public class SystemDefinitionTests
{
    private const string Minimal = """
        id: demo
        name: Demo System
        attributes:
          - { key: might, label: Might, default: 3, modifier: none }
        resources:
          - { key: HP, label: Health, maxKey: HP_Max, default: 10, isPrimary: true }
        actorFields:
          - { key: calling, label: Calling, type: EntityReference, targetEntityType: calling }
        entityTypes:
          - type: calling
            label: Calling
            folder: callings
            fields:
              - { key: bonus, label: Bonus, type: Text }
        """;

    [Fact]
    public void Parse_ReadsAttributesResourcesFieldsAndTypes()
    {
        var definition = SystemDefinitionLoader.Parse(Minimal);

        Assert.Equal("demo", definition.Id);
        Assert.Equal("Demo System", definition.Name);
        var might = Assert.Single(definition.Attributes);
        Assert.Equal(3, might.Default);
        Assert.Equal("HP", definition.PrimaryResource!.Key);
        Assert.Equal("HP_Max", definition.PrimaryResource.MaxKey);
        var calling = Assert.Single(definition.ActorFields);
        Assert.Equal(FieldType.EntityReference, calling.Type);
        Assert.Equal("callings", definition.EntityTypes.Single().FolderOrType);
        Assert.NotNull(definition.FindType("callings"));
        Assert.NotNull(definition.FindType("calling"));
    }

    [Theory]
    [InlineData("name: X\n", "id is required")]
    [InlineData("id: Bad Id\nname: X\n", "must be lower-case")]
    [InlineData("id: ok\n", "name is required")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\n  - key: a\n", "declared more than once")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\n    modifier: weird\n", "unknown modifier rule")]
    [InlineData("id: ok\nname: X\nresources:\n  - { key: a, isPrimary: true }\n  - { key: b, isPrimary: true }\n", "only one resource")]
    [InlineData("id: ok\nname: X\nentityTypes:\n  - type: spell\n    folder: abilities\n", "reserved folder")]
    [InlineData("id: ok\nname: X\nactorFields:\n  - { key: cls, label: Class, type: EntityReference, targetEntityType: class }\n", "does not declare")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: str\nactorFields:\n  - { key: str, label: Dup, type: Number }\n", "collides")]
    public void Parse_RejectsInvalidDefinitions_WithReadableMessages(string yaml, string expectedFragment)
    {
        var ex = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml));
        Assert.Contains(expectedFragment, ex.Message);
    }

    // Keys naming an attribute or a derived value have no 5e default: a section that leaves one out is an error naming it.
    [Theory]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nsheetRules:\n  armorClass: { unarmored: \"10\" }\n", "sheetRules.armorClass.dexAttribute is required")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nsheetRules:\n  armorClass: { dexAttribute: a }\n", "sheetRules.armorClass.unarmored is required")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nsheetRules:\n  armorClass: { unarmored: \"10\", dexAttribute: dex }\n", "dexAttribute 'dex' is not one of the system's attributes")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nsheetRules:\n  hitPoints: { classField: cls, levelField: lvl }\n", "sheetRules.hitPoints.attribute is required")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nsheetRules:\n  skills: { list: [] }\n", "sheetRules.skills.bonusKey is required")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nsheetRules:\n  spellcasting: { saveBase: 8 }\n", "sheetRules.spellcasting.bonusKey is required")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nsheetRules:\n  proficiencies: { strengthAttribute: str }\n", "strengthAttribute 'str' is not one of the system's attributes")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nvitals:\n  concentration: { condition: C }\n", "vitals.concentration.saveAttribute is required")]
    [InlineData("id: ok\nname: X\nattributes:\n  - key: a\nactorFields:\n  - { key: lvl, label: L, type: Number }\n  - { key: cls, label: C, type: EntityReference, targetEntityType: c }\nentityTypes:\n  - type: c\nvitals:\n  hitDice: { key: HD }\n", "vitals.hitDice.attribute is required")]
    public void Parse_RuleSectionsMustNameTheirAttributesAndBonuses(string yaml, string expectedFragment)
    {
        var ex = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Parse_NotYaml_ExplainsItself()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse("attributes: [unclosed"));
        Assert.Contains("not a valid system definition", ex.Message);
    }

    [Fact]
    public void DataDrivenPlugin_BuildsActorSchemaFromDefinition()
    {
        var plugin = new DataDrivenSystemPlugin(SystemDefinitionLoader.Parse(Minimal));

        Assert.Equal("demo", plugin.SystemId);
        Assert.Equal("Demo System", plugin.DisplayName);
        Assert.Same(plugin.Definition, ((ICodexSystemPlugin)plugin).Definition);

        var schemas = plugin.GetUISchemas().ToList();
        var actor = schemas.Single(s => s.EntityType == DataDrivenSystemPlugin.ActorSchemaType);
        Assert.Equal(new[] { "might", "HP", "HP_Max", "calling" }, actor.Fields.Select(f => f.Key));
        Assert.Equal(DataDrivenSystemPlugin.AttributesCategory, actor.Fields[0].Category);
        Assert.Equal(DataDrivenSystemPlugin.ResourcesCategory, actor.Fields[1].Category);
        Assert.Equal(3, actor.Fields[0].DefaultValue);

        var calling = schemas.Single(s => s.EntityType == "calling");
        // The type's own field, then the lore fields every entry type's editor offers.
        Assert.Equal(new[] { "bonus", "flavor", "imagePrompt" }, calling.Fields.Select(f => f.Key));
        Assert.Equal("Content", calling.Intent);
    }

    [Fact]
    public void DataDrivenPlugin_RegistersGenericComponents()
    {
        var registry = new ComponentRegistry();
        new DataDrivenSystemPlugin(SystemDefinitionLoader.Parse(Minimal)).RegisterComponents(registry);

        Assert.NotNull(registry.Resolve(ComponentRegistry.NameOf(typeof(Codex.Core.Components.AttributesComponent))));
        Assert.NotNull(registry.Resolve(ComponentRegistry.NameOf(typeof(Codex.Core.Components.ResourcePoolComponent))));
        // Conditions live only at the table: never persisted, so never registered for snapshots.
        Assert.Null(registry.Resolve(ComponentRegistry.NameOf(typeof(Codex.Core.Components.ActiveEffectsComponent))));
    }

    [Fact]
    public void ShippedSystems_AreValid()
    {
        foreach (var file in Directory.GetFiles(TestPaths.ContentRoot, SystemDefinitionLoader.FileName, SearchOption.AllDirectories))
        {
            var definition = SystemDefinitionLoader.Load(file);
            Assert.Empty(SystemDefinitionLoader.Validate(definition));
        }
    }

    [Fact]
    public void KeysTheModelDoesNotHave_AreNamedByTheirPath_AndTheSystemStillLoads()
    {
        const string yaml = """
            id: typo
            name: Typo
            actorFields: [{ key: class, label: Class, type: EntityReference, targetEntityType: class }]
            entityTypes: [{ type: class, label: Class }, { type: spell, label: Spell }]
            resources:
              - { key: HP, maxkey: HP_Max }
            sheetRules:
              hitPoint: { key: HP_Max }
              lists:
                - key: spells
                  targetEntityType: spell
                  where:
                    - { property: level, equals: 0, mesage: "not a cantrip." }
              slotPools:
                - key: slots
                  combine: { weights: { full: 1 }, roundng: up }
            values:
              - { key: EMP, formula: "floor(Humanity / 10)", reads: [Humanity] }
            """;
        var unknown = new List<string>();

        var definition = SystemDefinitionLoader.Parse(yaml, null, unknown);

        Assert.Equal(new[]
        {
            "resources[0].maxkey",
            "sheetRules.hitPoint",
            "sheetRules.lists[0].where[0].mesage",
            "sheetRules.slotPools[0].combine.roundng",
            "values" // what is under an unknown key is not listed again
        }, unknown);
        Assert.Equal("typo", definition.Id);
        Assert.Null(definition.Resources[0].MaxKey);
    }

    [Fact]
    public void MapsTakeAnyKey_ButWhatTheyHoldIsChecked()
    {
        // weights and armorTraining are maps: their keys are the author's own. A map of objects still checks each object.
        const string yaml = """
            id: maps
            name: Maps
            actorFields: [{ key: class, label: Class, type: EntityReference, targetEntityType: class }]
            entityTypes: [{ type: class, label: Class }]
            sheetRules:
              proficiencies: { armorTraining: { light: Light, heavy: Heavy } }
              slotPools:
                - { key: slots, combine: { weights: { full: 1, third: 0.33 } } }
            widgets:
              cyberware: { type: "Acme.Street.Cyberware, Acme.Street", lable: x }
            """;
        var unknown = new List<string>();

        SystemDefinitionLoader.Parse(yaml, null, unknown);

        Assert.Equal(new[] { "widgets.cyberware.lable" }, unknown);
    }

    [Fact]
    public void ShippedSystems_HaveNoKeysTheLoaderIgnores()
    {
        foreach (var file in Directory.GetFiles(TestPaths.ContentRoot, SystemDefinitionLoader.FileName, SearchOption.AllDirectories))
        {
            var unknown = new List<string>();
            SystemDefinitionLoader.Load(file, unknown);
            Assert.True(unknown.Count == 0, $"{file}: {string.Join(", ", unknown)}");
        }
    }
}
