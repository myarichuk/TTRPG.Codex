using System.Reflection;
using System.Runtime.Loader;

namespace Codex.Core;

/// <summary>One collectible load context per plugin file (5.2b). The contract assembly
/// (<c>Codex.Plugin.Abstractions</c>), <c>Codex.Core</c>, and <c>DefaultEcs</c> are always
/// resolved from the default context, so a plugin's <c>ICodexSystemPlugin</c> is identical
/// to the host's - no duplicate-assembly "same name, different identity" mismatches, which
/// is also what lets the old double-loaded DnD5e copy (bin + plugins/) finally go away.
/// Everything else resolves from the plugin directory. Unload via
/// <see cref="PluginLoader.UnloadPlugins"/>.</summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Codex.Plugin.Abstractions",
        "Codex.Core",
        "DefaultEcs"
    };

    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginDirectory)
        : base(isCollectible: true)
    {
        _pluginDirectory = pluginDirectory;
        Resolving += OnResolving;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && !SharedAssemblies.Contains(name))
        {
            var candidate = Path.Combine(_pluginDirectory, name + ".dll");
            if (File.Exists(candidate))
            {
                return LoadFromAssemblyPath(candidate);
            }
        }

        return null;
    }

    private Assembly? OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        if (SharedAssemblies.Contains(assemblyName.Name))
        {
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch
            {
                return null;
            }
        }

        var candidate = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        if (File.Exists(candidate))
        {
            try
            {
                return LoadFromAssemblyPath(candidate);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
