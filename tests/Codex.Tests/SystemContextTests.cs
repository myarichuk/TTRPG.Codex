using Codex.Core;
using Codex.Plugin.Abstractions;
using Codex.Systems.DnD5e;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Codex.Tests;

public class SystemContextTests
{
    private sealed class RecordingPlugin : ICodexSystemPlugin
    {
        public string SystemId => "Test";
        public readonly List<float> Ticks = new();

        public void RegisterComponents(ComponentRegistry registry) { }

        public IEnumerable<UISchema> GetUISchemas() => Enumerable.Empty<UISchema>();

        public void RegisterSystems(ISystemContext systems) =>
            systems.AddTickSystem(new Tick(Ticks));

        private sealed class Tick(List<float> ticks) : ITickSystem
        {
            public void Update(float deltaTime) => ticks.Add(deltaTime);
        }
    }

    [Fact]
    public void InitializePlugins_TypedSystemsPath_TickSystemRunsOnTick()
    {
        var loader = new PluginLoader(
            NullLogger<PluginLoader>.Instance,
            new ComponentRegistry(),
            Substitute.For<IContentPackLoader>());
        var plugin = new RecordingPlugin();

        using var world = new CodexWorld();
        loader.InitializePlugins(new[] { plugin }, world);

        world.Tick(0.1f);
        world.Tick(0.2f);

        Assert.Equal(new[] { 0.1f, 0.2f }, plugin.Ticks);
    }

    [Fact]
    public void InitializePlugins_DnD5e_RegistersComponentsWithoutDynamic()
    {
        var registry = new ComponentRegistry();
        var loader = new PluginLoader(
            NullLogger<PluginLoader>.Instance,
            registry,
            Substitute.For<IContentPackLoader>());

        using var world = new CodexWorld();
        loader.InitializePlugins(new ICodexSystemPlugin[] { new DnD5ePlugin() }, world);

        Assert.NotNull(registry.Resolve(ComponentRegistry.NameOf(typeof(Codex.Core.Components.ResourcePoolComponent))));
        world.Tick(0.1f);
    }
}
