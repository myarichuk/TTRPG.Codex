using System.Reflection;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Codex.Core;

public class PluginLoader(
    ILogger<PluginLoader> logger,
    ComponentRegistry registry,
    IContentPackLoader contentPackLoader)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    public bool IsLoaded { get; private set; }
    public bool IsLoading { get; private set; }
    public Exception? LoadException { get; private set; }
    public event Action? OnPluginsLoaded;

    public async Task LoadAndInitializeAsync(string pluginsDirectory, CodexWorld world)
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
                InitializePlugins(plugins, world);

                // Load Content Packs after systems are initialized
                await LoadContentPacksAsync(pluginsDirectory, plugins.Select(p => p.SystemId).ToHashSet());
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

    private async Task LoadContentPacksAsync(string pluginsDirectory, HashSet<string> activeSystemIds)
    {
        if (!Directory.Exists(pluginsDirectory)) return;

        var manifests = Directory.GetFiles(pluginsDirectory, "manifest.json", SearchOption.AllDirectories);

        foreach (var manifestPath in manifests)
        {
            var packDir = Path.GetDirectoryName(manifestPath);
            if (packDir == null) continue;

            try
            {
                var manifest = await contentPackLoader.ReadManifestAsync(packDir);
                if (activeSystemIds.Contains(manifest.SystemId))
                {
                    logger.LogInformation("Loading content pack: {PackName} ({PackId}) for system {SystemId}",
                        manifest.Name, manifest.Id, manifest.SystemId);
                    await contentPackLoader.LoadPackAsync(packDir);
                }
                else
                {
                    logger.LogWarning("Skipping content pack {PackId} because system {SystemId} is not loaded",
                        manifest.Id, manifest.SystemId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load content pack from {Directory}", packDir);
            }
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
            try
            {
                var assembly = Assembly.LoadFrom(file);
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(ICodexSystemPlugin).IsAssignableFrom(t) &&
                                t is
                                {
                                    IsInterface: false,
                                    IsAbstract: false
                                });

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
}