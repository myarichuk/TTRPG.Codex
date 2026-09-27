using System.Reflection;
using Codex.Core;
using Codex.Plugin.Abstractions;
using Codex.Systems.Pf2e;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>The PF2e system shell (6.1): identity, components, and sheet schemas. Rules data
/// itself lives in the pf2e-remastered YAML pack, never here.</summary>
public class Pf2ePluginTests
{
    [Fact]
    public void SystemId_IsPf2e()
    {
        Assert.Equal("Pf2e", new Pf2ePlugin().SystemId);
    }

    [Fact]
    public void RegisterComponents_RegistersExpectedSet()
    {
        var registry = new ComponentRegistry();
        new Pf2ePlugin().RegisterComponents(registry);

        Assert.Contains(typeof(AbilityScoresComponent), registry.RegisteredComponents);
        Assert.Contains(typeof(ConditionComponent), registry.RegisteredComponents);
        Assert.Contains(typeof(Codex.Core.Components.ResourcePoolComponent), registry.RegisteredComponents);
        Assert.Contains(typeof(Codex.Core.Components.StatModifierComponent), registry.RegisteredComponents);
    }

    [Fact]
    public void GetUISchemas_CoversActorAndAbility()
    {
        var schemas = new Pf2ePlugin().GetUISchemas().ToList();

        var actor = Assert.Single(schemas, s => s.EntityType == "Actor");
        Assert.Contains(actor.Fields, f => f.Key == "Level");
        Assert.Contains(actor.Fields, f => f.Key == "Strength");

        var ability = Assert.Single(schemas, s => s.EntityType == "Ability");
        Assert.Contains(ability.Fields, f => f.Key == "Rank");
        Assert.Contains(ability.Fields, f => f.Key == "Traits");
    }

    [Fact]
    public void LoadPlugins_DiscoversPf2e_FromTestOutput()
    {
        // Same discovery path as PluginLoadTest: the test output dir has the plugin DLLs
        // copied via ProjectReference.
        var loader = new PluginLoader(
            NullLogger<PluginLoader>.Instance,
            new ComponentRegistry(),
            Substitute.For<IContentPackLoader>());
        var pluginsDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        var plugins = loader.LoadPlugins(pluginsDir!);

        Assert.Contains(plugins, p => p.SystemId == "Pf2e");
    }
}
