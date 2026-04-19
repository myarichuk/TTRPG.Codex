using System;
using Xunit;
using Codex.Plugin.Abstractions;
using Codex.Systems.SWFFG;

namespace Codex.Tests;

public class SwffgPluginTest
{
    [Fact]
    public void RegisterComponents_ShouldRegisterExpectedComponents()
    {
        // Arrange
        var plugin = new SwffgPlugin();
        var registry = new ComponentRegistry();

        // Act
        plugin.RegisterComponents(registry);

        // Assert
        Assert.Contains(typeof(CharacteristicsComponent), registry.RegisteredComponents);
        Assert.Contains(typeof(WoundComponent), registry.RegisteredComponents);
        Assert.Contains(typeof(StrainComponent), registry.RegisteredComponents);
    }
}
