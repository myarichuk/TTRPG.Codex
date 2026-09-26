using Codex.Core;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Codex.Tests;

public class PluginIsolationTests
{
    private static string CopyDnD5eToTempDir(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "PluginIsolation_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var source = Path.Combine(
            Path.GetDirectoryName(typeof(PluginIsolationTests).Assembly.Location)!,
            "Codex.Systems.DnD5e.dll");
        File.Copy(source, Path.Combine(dir, "Codex.Systems.DnD5e.dll"));
        return dir;
    }

    private static (WeakReference ContextRef, bool Found, bool SharesHostContract, bool ComponentsRegistered, int ContextsKept)
        LoadAndRelease(string dir)
    {
        var registry = new ComponentRegistry();
        var loader = new PluginLoader(
            NullLogger<PluginLoader>.Instance, registry, Substitute.For<IContentPackLoader>());

        var plugin = loader.LoadPlugins(dir).FirstOrDefault(p => p.SystemId == "DnD5e");
        var sharesContract = plugin != null
            && typeof(ICodexSystemPlugin).IsAssignableFrom(plugin.GetType())
            && string.Equals(
                Path.GetFullPath(plugin.GetType().Assembly.Location),
                Path.GetFullPath(Path.Combine(dir, "Codex.Systems.DnD5e.dll")),
                StringComparison.OrdinalIgnoreCase);
        var contextsKept = loader.LoadContexts.Count;
        var contextRef = new WeakReference(loader.LoadContexts.FirstOrDefault());

        using var world = new CodexWorld();
        loader.InitializePlugins(plugin != null ? new[] { plugin } : Array.Empty<ICodexSystemPlugin>(), world);
        var components = registry.Resolve(ComponentRegistry.NameOf(typeof(Codex.Core.Components.ResourcePoolComponent))) != null;
        loader.UnloadPlugins();
        return (contextRef, plugin != null, sharesContract, components, contextsKept);
    }

    [Fact]
    public void LoadPlugins_IsolatedCopy_SharesHostContractAndUnloads()
    {
        var dir = CopyDnD5eToTempDir(out _);
        try
        {
            // The temp dir holds ONLY the plugin DLL: its Core/Abstractions references can only
            // resolve via the shared default context, which is exactly what this proves.
            var (contextRef, found, sharesContract, components, contextsKept) = LoadAndRelease(dir);

            Assert.True(found);
            Assert.True(sharesContract);
            Assert.True(components);
            Assert.Equal(1, contextsKept);

            for (var i = 0; i < 3 && contextRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(contextRef.IsAlive);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
