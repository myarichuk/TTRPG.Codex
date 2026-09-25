using Codex.Plugin.Abstractions;
using Codex.Systems.DnD5e;

namespace Codex.Tests;

public class ComponentRegistryTests
{
    [Fact]
    public void SnapshotThenHydrate_ValueTypeComponent_RoundTripsEqual()
    {
        var registry = new ComponentRegistry();
        registry.Register<AbilityScoresComponent>();

        var original = new AbilityScoresComponent
        {
            Strength = 16,
            Dexterity = 12,
            Constitution = 14,
            Intelligence = 10,
            Wisdom = 8,
            Charisma = 18
        };

        var snapshot = registry.Snapshot(new object[] { original });
        var hydrated = registry.Hydrate(snapshot).Cast<AbilityScoresComponent>().Single();

        Assert.Equal(original, hydrated);
    }

    [Fact]
    public void SnapshotThenHydrate_ComponentWithReferenceField_PreservesContents()
    {
        var registry = new ComponentRegistry();
        registry.Register<ConditionComponent>();

        var original = new ConditionComponent { Conditions = new List<string> { "Poisoned", "Prone" } };

        var snapshot = registry.Snapshot(new object[] { original });
        var hydrated = registry.Hydrate(snapshot).Cast<ConditionComponent>().Single();

        Assert.Equal(original.Conditions, hydrated.Conditions);
    }

    [Fact]
    public void Hydrate_UnregisteredComponentName_IsSkippedNotThrown()
    {
        var registry = new ComponentRegistry();
        registry.Register<AbilityScoresComponent>();

        var snapshotFromAnotherRegistry = new ComponentRegistry();
        snapshotFromAnotherRegistry.Register<AbilityScoresComponent>();
        snapshotFromAnotherRegistry.Register<ConditionComponent>();
        var snapshot = snapshotFromAnotherRegistry.Snapshot(new object[]
        {
            new AbilityScoresComponent { Strength = 10 },
            new ConditionComponent { Conditions = new List<string> { "Stunned" } }
        });

        // registry never registered ConditionComponent (its content pack/plugin is "uninstalled"
        // relative to this process) - hydration should skip it, not throw.
        var hydrated = registry.Hydrate(snapshot).ToList();

        var abilityScores = Assert.Single(hydrated);
        Assert.IsType<AbilityScoresComponent>(abilityScores);
    }

    [Fact]
    public void NameOf_IsStableAcrossRegistries()
    {
        Assert.Equal(
            ComponentRegistry.NameOf(typeof(AbilityScoresComponent)),
            ComponentRegistry.NameOf(typeof(AbilityScoresComponent)));
    }
}
