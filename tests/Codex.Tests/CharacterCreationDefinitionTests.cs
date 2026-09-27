using Codex.Core;
using Codex.Plugin.Abstractions;
using Codex.Systems.DnD5e;
using Codex.Systems.Pf2e;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>
/// The per-system character-creation contract: each system plugin publishes its wizard flow
/// (steps + YAML kinds), and systems without one get a generic flow built from the rules
/// kinds their packs actually contain - no wizard code change per system.
/// </summary>
public class CharacterCreationDefinitionTests
{
    [Fact]
    public void DnD5e_PublishesEightStepClassOptionsFlow()
    {
        var definition = new DnD5ePlugin().GetCharacterCreation();

        Assert.NotNull(definition);
        Assert.Equal(
            new[] { "Name", "Class", "Subclass", "Race", "Scores", "Equipment", "Spells", "Review" },
            definition.Steps.Select(s => s.Key));
        Assert.Equal(CreationEquipmentMode.ClassOptions, definition.EquipmentMode);
        Assert.True(definition.SpellsFromClassLists);

        var cls = definition.Steps.Single(s => s.Key == "Class");
        Assert.Equal("class", cls.Kind);
        var sub = definition.Steps.Single(s => s.Key == "Subclass");
        Assert.Equal("subclassOf", sub.SameKindChildLink);
        var race = definition.Steps.Single(s => s.Key == "Race");
        Assert.Equal("ancestry", race.Kind);
    }

    [Fact]
    public void Pf2e_PublishesAncestryFirstSearchAddFlow()
    {
        var definition = new Pf2ePlugin().GetCharacterCreation();

        Assert.NotNull(definition);
        Assert.Equal(
            new[] { "Name", "Class", "Background", "Pf2eClass", "Scores", "Equipment", "Review" },
            definition.Steps.Select(s => s.Key));
        Assert.Equal(CreationEquipmentMode.SearchAdd, definition.EquipmentMode);
        Assert.False(definition.SpellsFromClassLists);

        var ancestry = definition.Steps.Single(s => s.Key == "Class");
        Assert.Equal("Ancestry", ancestry.Title);
        Assert.Equal("ancestry", ancestry.Kind);
        Assert.Equal("heritage", ancestry.ChildKind);
        Assert.Equal("ancestryOf", ancestry.ChildLinkKey);

        var cls = definition.Steps.Single(s => s.Key == "Pf2eClass");
        Assert.Equal("class", cls.Kind);
        Assert.Equal("patron", cls.ChildKind);
        Assert.Equal("witch", cls.ChildVisibleWhenPick);
    }

    [Fact]
    public void Fallback_BuildsOnePickerPerYamlKind()
    {
        var definition = CharacterCreationFallback.Build(new[] { "species", "career", "equipment" });

        Assert.NotNull(definition);
        Assert.Equal(
            new[] { "Name", "Pick:career", "Pick:equipment", "Pick:species", "Scores", "Equipment", "Review" },
            definition.Steps.Select(s => s.Key));
        Assert.Equal("career", definition.Steps[1].Kind);
        Assert.Equal("Career", definition.Steps[1].Title);
        Assert.Equal(CreationEquipmentMode.SearchAdd, definition.EquipmentMode);
    }

    [Fact]
    public void Fallback_ReturnsNull_WhenThereIsNothingToPick()
    {
        Assert.Null(CharacterCreationFallback.Build(Array.Empty<string>()));
    }

    [Fact]
    public void Plugin_WithoutOverride_FallsBackToNull()
    {
        var plugin = Substitute.For<ICodexSystemPlugin>();
        plugin.SystemId.Returns("Homebrew");

        Assert.Null(plugin.GetCharacterCreation());
    }

    [Fact]
    public void Catalog_WithoutSystems_ReturnsNullFlow()
    {
        var loader = new PluginLoader(
            NullLogger<PluginLoader>.Instance,
            new ComponentRegistry(),
            Substitute.For<IContentPackLoader>());

        Assert.Null(loader.GetCharacterCreation("Nope"));
        Assert.Null(((ISystemCatalog)new NoOpSystemCatalog()).GetCharacterCreation("Nope"));
    }
}
