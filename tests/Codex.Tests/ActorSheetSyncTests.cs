using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Models;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

public class ActorSheetSyncTests
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        attributes:
          - { key: str, label: Strength, default: 10 }
          - { key: dex, label: Dexterity, default: 10 }
        resources:
          - { key: HP, label: Hit Points, maxKey: HP_Max, default: 8, isPrimary: true }
          - { key: TempHP, label: Temporary }
        """);

    private static ComponentRegistry Registry()
    {
        var registry = new ComponentRegistry();
        registry.Register<ResourcePoolComponent>();
        registry.Register<AttributesComponent>();
        registry.Register<TagsComponent>();
        return registry;
    }

    [Fact]
    public void ApplySheetToState_BuildsPoolAndAttributes_FromSheetKeys()
    {
        var registry = Registry();
        var actor = new ActorDocument { Sheet = { ["str"] = 16L, ["dex"] = "12", ["HP"] = 7L, ["HP_Max"] = 10L, ["class"] = "fighter" } };

        ActorSheetSync.ApplySheetToState(actor, System, registry);

        var components = registry.Hydrate(actor.State).ToList();
        var pool = components.OfType<ResourcePoolComponent>().Single();
        var attributes = components.OfType<AttributesComponent>().Single();
        Assert.Equal(7, pool.Get("HP"));
        Assert.Equal(10, pool.Get("HP_Max"));
        Assert.Equal(16, attributes.Get("str"));
        Assert.Equal(12, attributes.Get("dex"));
        Assert.Equal("fighter", actor.Sheet["class"]); // non-component fields stay on the sheet only
    }

    [Fact]
    public void ApplySheetToState_PreservesOtherComponents()
    {
        var registry = Registry();
        var actor = new ActorDocument { Sheet = { ["HP"] = 5L } };
        var tags = new TagsComponent { Tags = { "undead" } };
        actor.State = registry.Snapshot(new object[] { tags });

        ActorSheetSync.ApplySheetToState(actor, System, registry);

        Assert.Contains(registry.Hydrate(actor.State), c => c is TagsComponent t && t.Tags.Contains("undead"));
        Assert.Contains(registry.Hydrate(actor.State), c => c is ResourcePoolComponent);
    }

    [Fact]
    public void ApplyStateToSheet_CopiesLiveValuesBack()
    {
        var registry = Registry();
        var pool = new ResourcePoolComponent();
        pool.Set("HP", 3);
        pool.Set("HP_Max", 10);
        var actor = new ActorDocument { Sheet = { ["HP"] = 10L }, State = registry.Snapshot(new object[] { pool }) };

        ActorSheetSync.ApplyStateToSheet(actor, System, registry);

        Assert.Equal(3L, actor.Sheet["HP"]);
        Assert.Equal((3, 10, "Hit Points"), ActorSheetSync.PrimaryResource(actor, System));
    }

    [Fact]
    public void SeedFromBlueprint_CopiesStatsAndResources()
    {
        var registry = Registry();
        var blueprint = new ActorDefinition
        {
            Id = "goblin", SystemId = "demo", Name = "Goblin",
            Resources = { ["HP"] = 7, ["HP_Max"] = 7 },
            Properties = { ["str"] = 8L, ["armorClass"] = 15L }
        };
        var actor = new ActorDocument();

        ActorSheetSync.SeedFromBlueprint(actor, blueprint, System, registry);

        Assert.Equal(7L, actor.Sheet["HP"]);
        Assert.Equal(15L, actor.Sheet["armorClass"]);
        var pool = registry.Hydrate(actor.State).OfType<ResourcePoolComponent>().Single();
        Assert.Equal(7, pool.Get("HP"));
        Assert.Equal(8, registry.Hydrate(actor.State).OfType<AttributesComponent>().Single().Get("str"));
    }

    [Fact]
    public void WithoutASystem_ConventionalHpKeysStillDriveThePool()
    {
        var registry = Registry();
        var actor = new ActorDocument { Sheet = { ["HP"] = 4L, ["HP_Max"] = 9L } };

        ActorSheetSync.ApplySheetToState(actor, null, registry);

        var pool = registry.Hydrate(actor.State).OfType<ResourcePoolComponent>().Single();
        Assert.Equal(4, pool.Get("HP"));
        Assert.Equal(9, pool.Get("HP_Max"));
    }
}
