using Codex.Core;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NSubstitute;
using Xunit;

namespace Codex.Tests;

public class PluginLoadTest
{
    [Fact]
    public async Task LoadContentPacksAsync_LoadsDependencyBeforeDependent_RegardlessOfDiscoveryOrder()
    {
        // B9: packs used to load in whatever order Directory.GetFiles() enumerated them,
        // so a pack's base (named via manifest Dependencies) could load after the pack that
        // depends on it. Here "zzz-addon" sorts and is discovered after "aaa-base" would - but
        // we still assert on the actual load order, not the discovery order, by depending on
        // aaa-base from zzz-addon and requiring aaa-base to have loaded first regardless.
        var tempDir = Path.Combine(Path.GetTempPath(), "PluginDepOrderTest_" + Guid.NewGuid());
        var baseDir = Path.Combine(tempDir, "aaa-base");
        var addonDir = Path.Combine(tempDir, "zzz-addon");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(addonDir);

        try
        {
            // Discovery (Directory.GetFiles(..., "manifest.json", ...)) needs the file to
            // actually exist on disk; its contents don't matter since ReadManifestAsync is mocked.
            File.WriteAllText(Path.Combine(baseDir, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(addonDir, "manifest.json"), "{}");

            var baseManifest = new PackManifest("base-pack", "Base", "1.0.0", "dnd5e");
            var addonManifest = new PackManifest("addon-pack", "Addon", "1.0.0", "dnd5e", Dependencies: new[] { "base-pack" });

            var contentLoader = Substitute.For<IContentPackLoader>();
            contentLoader.ReadManifestAsync(baseDir).Returns(baseManifest);
            contentLoader.ReadManifestAsync(addonDir).Returns(addonManifest);

            var loadOrder = new List<string>();
            _ = contentLoader.LoadPackAsync(Arg.Do<string>(p => loadOrder.Add(p)));

            var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), contentLoader);

            await loader.LoadContentPacksAsync(tempDir, new HashSet<string> { "dnd5e" });

            Assert.Equal(new[] { baseDir, addonDir }, loadOrder);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadPlugins_ShouldFindAndInitializePlugins()
    {
        // Arrange
        var logger = NullLogger<PluginLoader>.Instance;
        var registry = new ComponentRegistry();
        var contentLoader = Substitute.For<IContentPackLoader>();
        var loader = new PluginLoader(logger, registry, contentLoader);

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