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
        Assert.Single(calling.Fields);
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
}
