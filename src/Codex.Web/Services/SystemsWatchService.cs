using Codex.Core;
using Codex.Core.Systems;

namespace Codex.Web.Services;

/// <summary>
/// Reloads the systems when files under the content roots change (<c>Codex:Systems:Watch</c>, on by default in Development):
/// see <see cref="SystemsWatcher"/>. Off, a change copied onto the server waits for <i>Reload systems</i> on About or a restart.
/// </summary>
public sealed class SystemsWatchService(PluginLoader loader, SystemInstaller installer, IConfiguration configuration, IWebHostEnvironment environment,
    ILogger<SystemsWatchService> logger) : BackgroundService
{
    /// <summary>How long the roots must be quiet before a reload: long enough for a folder being copied to land whole.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Codex:Systems:Watch", environment.IsDevelopment())) return;
        try
        {
            while (!loader.IsLoaded) await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            using var watcher = new SystemsWatcher(loader.ContentRoots, ReloadAsync, Quiet, ex => logger.LogError(ex, "Reloading the systems after a change on disk failed"));
            logger.LogInformation("Watching {Roots} for system changes", string.Join(", ", loader.ContentRoots));
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReloadAsync()
    {
        var reload = await installer.ReloadFromDiskAsync();
        if (reload.Error != null)
        {
            logger.LogWarning("Systems changed on disk, and the reload failed; the systems loaded before stay: {Error}", reload.Error);
        }
        else if (reload.Added.Count + reload.Removed.Count + reload.Changed.Count > 0)
        {
            logger.LogInformation("Systems changed on disk and were reloaded. Added: {Added}; removed: {Removed}; changed: {Changed}",
                string.Join(", ", reload.Added), string.Join(", ", reload.Removed), string.Join(", ", reload.Changed));
        }
    }
}
