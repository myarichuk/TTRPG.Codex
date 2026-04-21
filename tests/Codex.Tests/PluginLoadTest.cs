using Codex.Core;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Reflection;
using NSubstitute;
using Xunit;

namespace Codex.Tests;

public class PluginLoadTest
{
    [Fact]
    public void LoadPlugins_ShouldFindAndInitializePlugins()
    {
        // Arrange
        var logger = NullLogger<PluginLoader>.Instance;
        var registry = new ComponentRegistry();
        var abilityLoader = Substitute.For<IAbilityPackLoader>();
        var loader = new PluginLoader(logger, registry, abilityLoader);

        // We will just point it to the output directory of the tests, which should have the plugin dlls copied.
        var pluginsDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // Act
        var plugins = loader.LoadPlugins(pluginsDir!);

        // Assert
        Assert.NotEmpty(plugins);
        Assert.Contains(plugins, p => p.SystemId == "DnD5e");

        using var world = new CodexWorld();
        loader.InitializePlugins(plugins, world);

        // By running the system without throwing, we consider it a success.
        var entity = world.CreateEntity();
        world.Tick(0.1f);
    }
}