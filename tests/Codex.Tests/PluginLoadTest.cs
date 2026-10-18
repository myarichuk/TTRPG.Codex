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
            var addonManifest = new PackManifest("addon-pack", "Addon", "1.0.0", "dnd5e", dependencies: new[] { "base-pack" });

            var contentLoader = Substitute.For<IContentPackLoader>();
            contentLoader.ReadManifestAsync(baseDir).Returns(baseManifest);
            contentLoader.ReadManifestAsync(addonDir).Returns(addonManifest);

            var loadOrder = new List<string>();
            _ = contentLoader.LoadPackAsync(Arg.Do<string>(p => loadOrder.Add(p)), Arg.Any<SystemDefinition?>());

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
    public async Task LoadAndInitialize_DiscoversDataDrivenSystemsAndTheirPacks()
    {
        // The example system under content/examples is a system.yaml plus one pack - no compiled
        // plugin anywhere. Loading it proves the data-driven path end to end.
        var root = Path.Combine(TestPaths.RepoRoot, "content", "examples", "simple-d6");
        var registry = new ComponentRegistry();
        var scriptEvaluator = new Codex.Core.Scripting.ScriptEvaluator(NullLogger<Codex.Core.Scripting.ScriptEvaluator>.Instance);
        var contentRegistry = new ContentRegistry(scriptEvaluator);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, registry, new YamlContentPackLoader(contentRegistry));

        await loader.LoadAndInitializeAsync(root);

        Assert.True(loader.IsLoaded);
        Assert.Null(loader.LoadException);
        Assert.Contains("simple-d6", loader.LoadedSystemIds);
        Assert.Equal("Simple d6 (example homebrew)", loader.GetDisplayName("simple-d6"));
        Assert.NotNull(loader.GetDefinition("simple-d6"));
        Assert.Contains(registry.RegisteredComponents, t => t == typeof(Codex.Core.Components.AttributesComponent));

        var actorSchema = loader.GetUISchemas("simple-d6").Single(s => s.EntityType == "Actor");
        Assert.Contains(actorSchema.Fields, f => f.Key == "body");
        Assert.Contains(actorSchema.Fields, f => f.Key == "HP_Max");
        Assert.Contains(actorSchema.Fields, f => f.Key == "calling" && f.Type == FieldType.EntityReference);

        Assert.Equal(3, contentRegistry.GetEntries("simple-d6", "calling").Count());
        // Foes are declared actorBlueprint: true, so they are reachable both as entries and as actor blueprints.
        var hound = contentRegistry.GetActor("simple-d6:clockwork-hound");
        Assert.NotNull(hound);
        Assert.Equal(8, hound!.Resources["HP"]);
        Assert.Equal(4L, hound.Properties["body"]);

        using var world = new CodexWorld();
        loader.InitializePlugins(new[] { loader.GetPlugin("simple-d6")! }, world);
        world.Tick(0.1f);
    }

    [Fact]
    public async Task LoadAndInitialize_ReportsInvalidSystemYamlAsWarning_NotCrash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BadSystem_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "system.yaml"), "name: Nameless\nattributes:\n  - key: str\n  - key: str\n");
            var registry = new ComponentRegistry();
            var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, registry, Substitute.For<IContentPackLoader>());

            await loader.LoadAndInitializeAsync(tempDir);

            Assert.True(loader.IsLoaded);
            Assert.Empty(loader.LoadedSystemIds);
            Assert.Contains(loader.LoadWarnings, w => w.Contains("id is required") && w.Contains("declared more than once"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}