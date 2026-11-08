using Codex.Core;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        var contentRegistry = new ContentRegistry();
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

    [Fact]
    public async Task LoadProblems_SayWhatEachIsAbout_WhichSystemItBelongsTo_AndKeepPathsOutOfTheMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "LoadProblems_" + Guid.NewGuid());
        void Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        try
        {
            Write("good/system.yaml", "id: good\nname: Good\n");
            Write("good/packs/addon/manifest.json", """{ "id": "addon", "name": "Addon", "version": "1.0.0", "systemId": "good", "dependencies": ["nowhere"] }""");
            Write("elsewhere/manifest.json", """{ "id": "stray", "name": "Stray", "version": "1.0.0", "systemId": "absent" }""");
            Write("broken/system.yaml", "id: broken\nname: Broken\nattributes:\n  - key: a\n  - key: a\n");
            File.WriteAllBytes(Path.Combine(root, "junk.dll"), new byte[] { 1, 2, 3, 4 });

            var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry()));
            await loader.LoadAndInitializeAsync(root);

            Assert.Equal(new[] { "good" }, loader.LoadedSystemIds);

            // A broken system.yaml is a failed system, named by the id it states, with its errors one by one.
            var failed = Assert.Single(loader.FailedSystems);
            Assert.Equal("broken", failed.Id);
            Assert.Contains(failed.Errors, e => e.Contains("declared more than once"));
            Assert.EndsWith(Path.Combine("broken", "system.yaml"), failed.Path);

            // A plugin that failed is reported; it used to reach only the server log.
            Assert.Contains(loader.LoadProblems, p => p.Source == LoadProblemSource.Plugin && p.Subject == "junk.dll");

            // A pack problem belongs to its system.
            var dependency = Assert.Single(loader.LoadProblems, p => p.Subject == "addon");
            Assert.Equal("good", dependency.SystemId);
            Assert.Contains("nowhere", dependency.Message);

            // A system's problems, and those that belong to no system, but not another system's.
            var forGood = loader.ProblemsFor("good").ToList();
            Assert.Contains(dependency, forGood);
            Assert.Contains(forGood, p => p.Subject == "junk.dll");
            Assert.DoesNotContain(forGood, p => p.Subject == "stray" || p.SystemId == "broken");

            // Messages are shown to people who don't run the server: no paths in them.
            Assert.All(loader.LoadProblems, p => Assert.DoesNotContain(root, p.Message));
            Assert.All(failed.Errors, e => Assert.DoesNotContain(root, e));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}