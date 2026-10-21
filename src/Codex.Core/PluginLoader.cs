using System.Reflection;
using Codex.Core.Components;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Codex.Core;

/// <summary>
/// Discovers game systems and content packs under one or more content roots and loads them once at
/// startup. A root may contain:
/// <list type="bullet">
/// <item>compiled plugins: <c>*.dll</c> directly in the root (code-level systems);</item>
/// <item>data systems: any <c>system.yaml</c> below the root (see <see cref="SystemDefinitionLoader"/>);</item>
/// <item>packs: any directory holding <c>manifest.yaml</c>/<c>manifest.json</c>, or a <c>*.zip</c>/<c>*.cdx</c> archive.</item>
/// </list>
/// Packs are loaded in dependency order and only for systems that actually loaded.
/// </summary>
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

    /// <summary>Problems found while loading that a DM should see (bad manifest, invalid system.yaml...).</summary>
    public IReadOnlyList<string> LoadWarnings => _warnings;

    private readonly List<string> _warnings = new();
    private Dictionary<string, ICodexSystemPlugin> _pluginsBySystemId = new();

    public IEnumerable<SystemSummary> Systems => _pluginsBySystemId.Values
        .Select(p => new SystemSummary(p.SystemId, p.DisplayName, p.Definition?.Description, p.Definition?.Compatibility, p.Definition?.License, p is DataDrivenSystemPlugin))
        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string GetDisplayName(string systemId) =>
        _pluginsBySystemId.TryGetValue(systemId, out var plugin) ? plugin.DisplayName : systemId;

    public SystemDefinition? GetDefinition(string systemId) =>
        _pluginsBySystemId.TryGetValue(systemId, out var plugin) ? plugin.Definition : null;

    public IEnumerable<UISchema> GetUISchemas(string systemId) =>
        _pluginsBySystemId.TryGetValue(systemId, out var plugin) ? plugin.GetUISchemas() : Enumerable.Empty<UISchema>();

    /// <summary>The loaded plugin ruling <paramref name="systemId"/>, or null if no such system is loaded.</summary>
    public ICodexSystemPlugin? GetPlugin(string systemId) => _pluginsBySystemId.GetValueOrDefault(systemId);

    public Task LoadAndInitializeAsync(string contentRoot) => LoadAndInitializeAsync(new[] { contentRoot });

    public async Task LoadAndInitializeAsync(IEnumerable<string> contentRoots)
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
            _warnings.Clear();

            var roots = contentRoots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct().ToList();
            foreach (var missing in contentRoots.Where(r => !Directory.Exists(r)))
            {
                logger.LogWarning("Content root '{Root}' does not exist", missing);
            }

            try
            {
                var plugins = await Task.Run(() => DiscoverPlugins(roots));

                // Core components every system relies on, registered once regardless of plugin.
                // Conditions (ActiveEffectsComponent) are stored too: their anchor is an actor id,
                // so a condition survives the table being ended and restarted.
                registry.Register<ResourcePoolComponent>();
                registry.Register<AttributesComponent>();
                registry.Register<StatModifierComponent>();
                registry.Register<TagsComponent>();
                registry.Register<ActiveEffectsComponent>();

                foreach (var plugin in plugins)
                {
                    logger.LogInformation("Initializing system: {SystemId} ({Name})", plugin.SystemId, plugin.DisplayName);
                    plugin.RegisterComponents(registry);
                }

                _pluginsBySystemId = plugins.ToDictionary(p => p.SystemId);
                LoadedSystemIds = _pluginsBySystemId.Keys.ToHashSet();

                await LoadContentPacksAsync(roots, LoadedSystemIds);
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

    private List<ICodexSystemPlugin> DiscoverPlugins(IReadOnlyList<string> roots)
    {
        var plugins = new List<ICodexSystemPlugin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var plugin in LoadPlugins(root))
            {
                if (!seen.Add(plugin.SystemId))
                {
                    Warn($"System '{plugin.SystemId}' is provided twice; the second copy (from {root}) was ignored.");
                    continue;
                }

                plugins.Add(plugin);
            }
        }

        foreach (var root in roots)
        {
            foreach (var systemFile in EnumerateFiles(root, SystemDefinitionLoader.FileName))
            {
                try
                {
                    var definition = SystemDefinitionLoader.Load(systemFile);
                    if (!seen.Add(definition.Id))
                    {
                        Warn($"System '{definition.Id}' in {systemFile} duplicates an already loaded system and was ignored.");
                        continue;
                    }

                    logger.LogInformation("Loaded data-driven system {SystemId} from {File}", definition.Id, systemFile);
                    plugins.Add(new DataDrivenSystemPlugin(definition));
                }
                catch (Exception ex)
                {
                    Warn($"Could not load {systemFile}: {ex.Message}");
                    logger.LogError(ex, "Failed to load system definition {File}", systemFile);
                }
            }
        }

        return plugins;
    }

    internal Task LoadContentPacksAsync(string contentRoot, HashSet<string> activeSystemIds) =>
        LoadContentPacksAsync(new[] { contentRoot }, activeSystemIds);

    // Internal (rather than private) so tests can exercise dependency-ordered pack loading
    // directly, without needing a real plugin assembly on disk just to populate activeSystemIds.
    internal async Task LoadContentPacksAsync(IEnumerable<string> contentRoots, IReadOnlySet<string> activeSystemIds)
    {
        var packPaths = new List<string>();
        foreach (var root in contentRoots.Where(Directory.Exists))
        {
            packPaths.AddRange(EnumerateFiles(root, YamlContentPackLoader.ManifestYaml).Select(Path.GetDirectoryName)!);
            packPaths.AddRange(EnumerateFiles(root, YamlContentPackLoader.ManifestJson).Select(Path.GetDirectoryName)!);
            packPaths.AddRange(EnumerateFiles(root, "*.zip"));
            packPaths.AddRange(EnumerateFiles(root, "*.cdx"));
        }

        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

        // Read every manifest up front so packs can be loaded in dependency order rather than
        // whatever order the filesystem happens to enumerate them in - a pack's base has to be
        // registered before anything that inherits from it, regardless of load order.
        var manifestsByPath = new Dictionary<string, PackManifest>();
        foreach (var packPath in packPaths.Where(p => p != null).Distinct())
        {
            try
            {
                manifestsByPath[packPath!] = await contentPackLoader.ReadManifestAsync(packPath!);
            }
            catch (Exception ex)
            {
                Warn($"Could not read the manifest of the pack at {packPath}: {ex.Message}");
                logger.LogError(ex, "Failed to read manifest for content pack at {Path}", packPath);
            }
        }

        foreach (var packPath in TopologicalOrder(manifestsByPath))
        {
            var manifest = manifestsByPath[packPath];

            if (!manifest.IsAppVersionCompatible(appVersion))
            {
                Warn($"Pack '{manifest.Id}' needs app version {manifest.MinAppVersion} (this is {appVersion}) and was skipped.");
                continue;
            }

            if (!activeSystemIds.Contains(manifest.SystemId))
            {
                Warn($"Pack '{manifest.Id}' is for system '{manifest.SystemId}', which is not loaded, and was skipped.");
                continue;
            }

            try
            {
                logger.LogInformation("Loading content pack: {PackName} ({PackId}) for system {SystemId}",
                    manifest.Name, manifest.Id, manifest.SystemId);
                await contentPackLoader.LoadPackAsync(packPath, GetDefinition(manifest.SystemId));
            }
            catch (Exception ex)
            {
                Warn($"Pack '{manifest.Id}' failed to load: {ex.Message}");
                logger.LogError(ex, "Failed to load content pack from {Path}", packPath);
            }
        }
    }

    /// <summary>
    /// Orders packs so every pack's <see cref="PackManifest.Dependencies"/> load before it does.
    /// A dependency that names a pack id not present among the discovered manifests is a load
    /// error naming the pack; that pack still loads, but its base content may be missing.
    /// </summary>
    private IEnumerable<string> TopologicalOrder(Dictionary<string, PackManifest> manifestsByPath)
    {
        var pathById = new Dictionary<string, string>();
        foreach (var (path, manifest) in manifestsByPath)
        {
            if (!pathById.TryAdd(manifest.Id, path))
            {
                Warn($"Pack id '{manifest.Id}' appears at both {pathById[manifest.Id]} and {path}; both will load and the higher priority/pack ordering wins per entry.");
            }
        }

        var visited = new HashSet<string>();
        var result = new List<string>();

        foreach (var path in manifestsByPath.Keys.OrderBy(p => p, StringComparer.Ordinal))
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
                    Warn($"Pack '{manifest.Id}' depends on '{dependencyId}', which was not found. It still loads, but inherited content may be missing.");
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

        foreach (var file in Directory.GetFiles(pluginsDirectory, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(ICodexSystemPlugin).IsAssignableFrom(t) &&
                                t is { IsInterface: false, IsAbstract: false } &&
                                t.GetConstructor(Type.EmptyTypes) != null);

                foreach (var type in pluginTypes)
                {
                    if (Activator.CreateInstance(type) is ICodexSystemPlugin plugin)
                    {
                        logger.LogInformation("Loaded plugin: {SystemId}", plugin.SystemId);
                        plugins.Add(plugin);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load plugin from {File}", file);
            }
        }

        return plugins;
    }

    public void InitializePlugins(IEnumerable<ICodexSystemPlugin> plugins, CodexWorld world)
    {
        foreach (var plugin in plugins)
        {
            logger.LogInformation("Initializing plugin: {SystemId}", plugin.SystemId);
            plugin.RegisterComponents(registry);
            plugin.RegisterSystems(world);
        }
    }

    private void Warn(string message)
    {
        _warnings.Add(message);
        logger.LogWarning("{Message}", message);
    }

    /// <summary>Recursive file search that skips build output and hidden folders.</summary>
    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, pattern);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules")
                {
                    continue;
                }

                pending.Push(sub);
            }
        }
    }
}
