using Codex.Plugin.Abstractions;
using Codex.Core.Components;

namespace Codex.Tests;

public class ComponentRegistryTests
{
    [Fact]
    public void SnapshotThenHydrate_ValueTypeComponent_RoundTripsEqual()
    {
        var registry = new ComponentRegistry();
        registry.Register<ResourcePoolComponent>();

        var original = new ResourcePoolComponent();
        original.Set("HP", 16);
        original.Set("HP_Max", 20);

        var snapshot = registry.Snapshot(new object[] { original });
        var hydrated = registry.Hydrate(snapshot).Cast<ResourcePoolComponent>().Single();

        Assert.Equal(original.Pools, hydrated.Pools);
    }

    [Fact]
    public void SnapshotThenHydrate_ComponentWithReferenceField_PreservesContents()
    {
        var registry = new ComponentRegistry();
        registry.Register<TagsComponent>();

        var original = new TagsComponent { Tags = new List<string> { "Poisoned", "Prone" } };

        var snapshot = registry.Snapshot(new object[] { original });
        var hydrated = registry.Hydrate(snapshot).Cast<TagsComponent>().Single();

        Assert.Equal(original.Tags, hydrated.Tags);
    }

    [Fact]
    public void Hydrate_UnregisteredComponentName_IsSkippedNotThrown()
    {
        var registry = new ComponentRegistry();
        registry.Register<AttributesComponent>();

        var snapshotFromAnotherRegistry = new ComponentRegistry();
        snapshotFromAnotherRegistry.Register<AttributesComponent>();
        snapshotFromAnotherRegistry.Register<TagsComponent>();
        var attributes = new AttributesComponent();
        attributes.Set("str", 10);
        var snapshot = snapshotFromAnotherRegistry.Snapshot(new object[]
        {
            attributes,
            new TagsComponent { Tags = new List<string> { "Stunned" } }
        });

        // registry never registered TagsComponent (its plugin is "uninstalled" relative to this
        // process) - hydration should skip it, not throw.
        var hydrated = registry.Hydrate(snapshot).ToList();

        var only = Assert.Single(hydrated);
        Assert.IsType<AttributesComponent>(only);
    }

    [Fact]
    public void NameOf_IsStableAcrossRegistries()
    {
        Assert.Equal(
            ComponentRegistry.NameOf(typeof(AttributesComponent)),
            ComponentRegistry.NameOf(typeof(AttributesComponent)));
    }
}
