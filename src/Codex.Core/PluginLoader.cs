using System.Reflection;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Codex.Core;

public class PluginLoader(
    ILogger<PluginLoader> logger,
    ComponentRegistry registry,
    IContentPackLoader contentPackLoader) : ISystemCatalog
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    public bool IsLoaded { get; private set; }
    public bool IsLoading { get; private set; }
    public Exception? LoadException { get; private set; }
    public IReadOnlySet<string> LoadedSystemIds { get; private set; } = new HashSet<string>();
    public event Action? OnPluginsLoaded;

    private Dictionary<string, ICodexSystemPlugin> _pluginsBySystemId = new();
    private readonly List<PluginLoadContext> _loadContexts = new();

    internal IReadOnlyList<PluginLoadContext> LoadContexts => _loadContexts;

    public IEnumerable<UISchema> GetUISchemas(string systemId) =>
        _pluginsBySystemId.TryGetValue(systemId, out var plugin) ? plugin.GetUISchemas() : Enumerable.Empty<UISchema>();

    /// <summary>The loaded plugin ruling <paramref name="systemId"/>, or null if no such system is
    /// loaded. Used by <c>CampaignRuntime</c> (3.1) to wire that plugin's systems onto its own,
    /// per-campaign <see cref="CodexWorld"/> - there is no longer a shared world to register onto
    /// at startup (B8).</summary>
    public ICodexSystemPlugin? GetPlugin(string systemId) => _pluginsBySystemId.GetValueOrDefault(systemId);

    public async Task LoadAndInitializeAsync(string pluginsDirectory)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (IsLoaded || IsLoading)
            {
                return;
            }

            IsLoading = true;
            LoadException = null;

            try
            {
                var plugins = await Task.Run(() => LoadPlugins(pluginsDirectory));

                // Only components are registered globally at startup, into the shared
                // ComponentRegistry. Systems are no longer registered onto a shared world here -
                // there isn't one anymore (B8) - CampaignRuntimeManager looks the plugin up via
                // GetPlugin and calls InitializePlugins itself, once per campaign, onto that
                // campaign's own CodexWorld.
                foreach (var plugin in plugins)
                {
                    logger.LogInformation("Initializing plugin: {SystemId}", plugin.SystemId);
                    plugin.RegisterComponents(registry);
                }

                var activeSystemIds = plugins.Select(p => p.SystemId).ToHashSet();
                LoadedSystemIds = activeSystemIds;
                _pluginsBySystemId = plugins.ToDictionary(p => p.SystemId);

                // Load Content Packs after systems are initialized
                await LoadContentPacksAsync(pluginsDirectory, activeSystemIds);
            }
            catch (Exception ex)
            {
                LoadException = ex;
                logger.LogError(ex, "Plugin loading failed");
            }
            finally
            {
                IsLoaded = true;
                IsLoading = false;
                OnPluginsLoaded?.Invoke();
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // Internal (rather than private) so tests can exercise dependency-ordered pack loading
    // directly, without needing a real plugin assembly on disk just to populate activeSystemIds.
    internal async Task LoadContentPacksAsync(string pluginsDirectory, HashSet<string> activeSystemIds)
    {
        if (!Directory.Exists(pluginsDirectory)) return;

        // B5: directory-based packs (manifest.json + subfolders) were the only kind ever
        // discovered. Packs shipped as a single .zip/.cdx file directly under the plugins
        // directory - exactly what the Authoring app's exporter produces - were never found.
        var packPaths = Directory.GetFiles(pluginsDirectory, "manifest.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(dir => dir != null)
            .Cast<string>()
            .Concat(Directory.GetFiles(pluginsDirectory, "*.zip"))
            .Concat(Directory.GetFiles(pluginsDirectory, "*.cdx"))
            .Distinct();

        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

        // Read every manifest up front so packs can be loaded in dependency order (B9) rather
        // than whatever order the filesystem happens to enumerate them in - a pack's base has to
        // be registered before anything that inherits from it, regardless of load order.
        var manifestsByPath = new Dictionary<string, PackManifest>();
        foreach (var packPath in packPaths)
        {
            try
            {
                manifestsByPath[packPath] = await contentPackLoader.ReadManifestAsync(packPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read manifest for content pack at {Path}", packPath);
            }
        }

        foreach (var packPath in TopologicalOrder(manifestsByPath))
        {
            var manifest = manifestsByPath[packPath];

            if (!manifest.IsAppVersionCompatible(appVersion))
            {
                logger.LogWarning("Skipping content pack {PackId}: Requires app version {MinAppVersion}, but current is {AppVersion}",
                    manifest.Id, manifest.MinAppVersion, appVersion);
                continue;
            }

            if (!activeSystemIds.Contains(manifest.SystemId))
            {
                logger.LogWarning("Skipping content pack {PackId} because system {SystemId} is not loaded",
                    manifest.Id, manifest.SystemId);
                continue;
            }

            try
            {
                logger.LogInformation("Loading content pack: {PackName} ({PackId}) for system {SystemId}",
                    manifest.Name, manifest.Id, manifest.SystemId);
                await contentPackLoader.LoadPackAsync(packPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load content pack from {Path}", packPath);
            }
        }
    }

    /// <summary>
    /// Orders packs so every pack's <see cref="PackManifest.Dependencies"/> load before it does.
    /// A dependency that names a pack id not present among the discovered manifests is a load
    /// error naming the pack (B9) - that pack is skipped rather than silently loaded out of
    /// order, since its base content wouldn't be there to inherit from.
    /// </summary>
    private IEnumerable<string> TopologicalOrder(Dictionary<string, PackManifest> manifestsByPath)
    {
        var pathById = manifestsByPath.ToDictionary(kv => kv.Value.Id, kv => kv.Key);
        var visited = new HashSet<string>();
        var result = new List<string>();

        foreach (var path in manifestsByPath.Keys)
        {
            Visit(path, new HashSet<string>());
        }

        return result;

        void Visit(string path, HashSet<string> inProgress)
        {
            if (visited.Contains(path) || !inProgress.Add(path))
            {
                return;
            }

            var manifest = manifestsByPath[path];
            foreach (var dependencyId in manifest.Dependencies ?? Array.Empty<string>())
            {
                if (!pathById.TryGetValue(dependencyId, out var dependencyPath))
                {
                    logger.LogError("Content pack {PackId} depends on {DependencyId}, which was not found among the discovered packs. It will still be loaded, but its base content may be missing.",
                        manifest.Id, dependencyId);
                    continue;
                }

                Visit(dependencyPath, inProgress);
            }

            visited.Add(path);
            result.Add(path);
        }
    }

    public List<ICodexSystemPlugin> LoadPlugins(string pluginsDirectory)
    {
        var plugins = new List<ICodexSystemPlugin>();

        if (!Directory.Exists(pluginsDirectory))
        {
            logger.LogWarning("Plugins directory '{PluginsDirectory}' does not exist", pluginsDirectory);
            return plugins;
        }

        var dllFiles = Directory.GetFiles(pluginsDirectory, "*.dll");

        foreach (var file in dllFiles)
        {
            PluginLoadContext? context = null;
            try
            {
                // Each candidate gets its own collectible context (5.2b), kept only when it
                // actually yields a plugin - dependency-only DLLs unload again immediately.
                context = new PluginLoadContext(pluginsDirectory);
                var assembly = context.LoadFromAssemblyPath(file);
                // A stale plugin DLL (built against an older Abstractions) can fail type-load
                // for some of its types without poisoning the rest - keep what loads.
                Type[] pluginTypes;
                try
                {
                    pluginTypes = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    pluginTypes = ex.Types.Where(t => t != null).ToArray()!;
                    logger.LogWarning(ex, "Some types in {File} failed to load and will be skipped", file);
                }

                var candidates = pluginTypes
                    .Where(t => typeof(ICodexSystemPlugin).IsAssignableFrom(t) &&
                                t is
                                {
                                    IsInterface: false,
                                    IsAbstract: false
                                });

                var found = 0;
                foreach (var type in candidates)
                {
                    if (Activator.CreateInstance(type) is ICodexSystemPlugin plugin)
                    {
                        logger.LogInformation("Loaded plugin: {SystemId}", plugin.SystemId);
                        plugins.Add(plugin);
                        found++;
                    }
                }

                if (found > 0)
                {
                    _loadContexts.Add(context);
                }
                else
                {
                    context.Unload();
                }

                context = null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load plugin from {File}", file);
                context?.Unload();
            }
        }

        return plugins;
    }

    /// <summary>Unloads every plugin context kept by <see cref="LoadPlugins"/> (5.2b) and forgets
    /// the loaded systems. Callers must drop their own plugin references first - an unload only
    /// completes once nothing outside still holds the context's instances or types.</summary>
    public void UnloadPlugins()
    {
        foreach (var context in _loadContexts)
        {
            context.Unload();
        }

        _loadContexts.Clear();
        _pluginsBySystemId = new Dictionary<string, ICodexSystemPlugin>();
        LoadedSystemIds = new HashSet<string>();
    }

    public void InitializePlugins(IEnumerable<ICodexSystemPlugin> plugins, ISystemContext systems)
    {
        foreach (var plugin in plugins)
        {
            logger.LogInformation("Initializing plugin: {SystemId}", plugin.SystemId);
            plugin.RegisterComponents(registry);
            plugin.RegisterSystems(systems);
        }
    }
}