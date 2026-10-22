using Codex.Persistence.Runtime;

namespace Codex.Web.Services;

/// <summary>
/// Stops live tables nobody ended. A runtime that has taken no command for <see cref="IdleFor"/> is
/// evicted: it leaves memory and open table pages let go of it. Its session stays Live, so the
/// DM's next start picks the same evening back up.
/// </summary>
public sealed class IdleRuntimeEvictionService(CampaignRuntimeManager runtimes, ILogger<IdleRuntimeEvictionService> logger) : BackgroundService
{
    public static readonly TimeSpan IdleFor = TimeSpan.FromHours(2);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var campaignId in await runtimes.EvictIdleAsync(IdleFor, DateTime.UtcNow))
                    {
                        logger.LogInformation("Evicted the idle live table of campaign {CampaignId}", campaignId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Idle runtime eviction failed; will retry");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
