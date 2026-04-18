using System.Reflection;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Codex.Core;

public class PluginLoader
{
    private readonly ILogger<PluginLoader> _logger;
    private readonly ComponentRegistry _registry;

    public bool IsLoaded { get; private set; }
    public event Action? OnPluginsLoaded;

    public PluginLoader(ILogger<PluginLoader> logger, ComponentRegistry registry)
    {
        _logger = logger;
        _registry = registry;
    }

    public async Task LoadAndInitializeAsync(string pluginsDirectory, CodexWorld world)
    {
        await Task.Run(() =>
        {
            var plugins = LoadPlugins(pluginsDirectory);
            InitializePlugins(plugins, world);
            IsLoaded = true;
            OnPluginsLoaded?.Invoke();
        });
    }

    public List<ICodexSystemPlugin> LoadPlugins(string pluginsDirectory)
    {
        var plugins = new List<ICodexSystemPlugin>();

        if (!Directory.Exists(pluginsDirectory))
        {
            _logger.LogWarning("Plugins directory '{PluginsDirectory}' does not exist.", pluginsDirectory);
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
                        _logger.LogInformation("Loaded plugin: {SystemId}", plugin.SystemId);
                        plugins.Add(plugin);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin from {File}", file);
            }
        }

        return plugins;
    }

    public void InitializePlugins(IEnumerable<ICodexSystemPlugin> plugins, CodexWorld world)
    {
        foreach (var plugin in plugins)
        {
            _logger.LogInformation("Initializing plugin: {SystemId}", plugin.SystemId);
            plugin.RegisterComponents(_registry);
            plugin.RegisterSystems(world);
        }
    }
}
