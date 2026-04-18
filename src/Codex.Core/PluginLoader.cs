using System.Reflection;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Codex.Core;

public class PluginLoader(ILogger<PluginLoader> logger, ComponentRegistry registry)
{
    public bool IsLoaded { get; private set; }
    public bool IsLoading { get; private set; }
    public Exception? LoadException { get; private set; }
    public event Action? OnPluginsLoaded;

    public async Task LoadAndInitializeAsync(string pluginsDirectory, CodexWorld world)
    {
        if (IsLoaded || IsLoading)
        {
            return;
        }

        IsLoading = true;
        LoadException = null;

        // Ensure the high-fidelity loading screen is visible to the user as requested.
        await Task.Delay(1500);

        try
        {
            await Task.Run(() =>
            {
                var plugins = LoadPlugins(pluginsDirectory);
                InitializePlugins(plugins, world);
            });
        }
        catch (Exception ex)
        {
            LoadException = ex;
            logger.LogError(ex, "Plugin loading failed.");
        }
        finally
        {
            IsLoaded = true; // "Done attempting" so the UI doesn't get stuck on a perpetual loading screen.
            IsLoading = false;
            OnPluginsLoaded?.Invoke();
        }
    }

    public List<ICodexSystemPlugin> LoadPlugins(string pluginsDirectory)
    {
        var plugins = new List<ICodexSystemPlugin>();

        if (!Directory.Exists(pluginsDirectory))
        {
            logger.LogWarning("Plugins directory '{PluginsDirectory}' does not exist.", pluginsDirectory);
            return plugins;
        }

        var dllFiles = Directory.GetFiles(pluginsDirectory, "*.dll");

        foreach (var file in dllFiles)
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(ICodexSystemPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

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
