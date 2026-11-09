using Codex.Persistence;

namespace Codex.Web.Services;

/// <summary>Whether the database's own backups run on this server.</summary>
public enum DatabaseBackups
{
    Unknown,
    On,

    /// <summary>The RavenDB licence (the free one) does not allow them.</summary>
    NotLicensed,
    Failed
}

/// <summary>What the About page says about backups (D11): where they go, when the last one was written, and whether the
/// database's own backups run beside them.</summary>
public sealed class BackupStatus
{
    public required string Folder { get; init; }
    public bool Enabled { get; init; } = true;
    public int Keep { get; init; } = 7;
    public DatabaseBackups Database { get; set; }
    public DateTime? LastWrittenAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Writes the server's own backup (<see cref="JsonBackups"/>) once a day, and once soon after start when the last one is
/// older than that, then keeps only the newest <see cref="BackupStatus.Keep"/>. A failure is logged and shown on the About
/// page, and tried again an hour later; it never stops the server.
/// </summary>
public sealed class BackupService(JsonBackups backups, BackupStatus status, ILogger<BackupService> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan AfterStart = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!status.Enabled)
        {
            return;
        }

        status.LastWrittenAt = JsonBackups.LastWrittenAt(status.Folder);
        try
        {
            await Task.Delay(AfterStart, stoppingToken);
            using var timer = new PeriodicTimer(CheckEvery);
            do
            {
                await RunIfDueAsync(DateTime.UtcNow, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Writes a backup when the last one is a day old (an hour's grace, so the hourly check does not drift a day
    /// late), and prunes old ones. Returns whether it wrote one.</summary>
    internal async Task<bool> RunIfDueAsync(DateTime utcNow, CancellationToken ct = default)
    {
        if (status.LastWrittenAt is { } last && utcNow - last < Every - CheckEvery)
        {
            return false;
        }

        try
        {
            var result = await backups.WriteAsync(status.Folder, utcNow, ct);
            JsonBackups.Prune(status.Folder, status.Keep);
            status.LastWrittenAt = utcNow;
            status.LastError = null;
            logger.LogInformation("Backed up {Campaigns} campaigns, {Images} images and {Users} accounts to {Folder}",
                result.Campaigns, result.Images, result.Users, result.Folder);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status.LastError = ex.Message;
            logger.LogError(ex, "The daily backup to {Folder} failed; it is tried again in an hour", status.Folder);
            return false;
        }
    }
}

public static class BackupFailures
{
    /// <summary>Whether the database refused a backup because its licence does not allow one (RavenDB's
    /// <c>LicenseLimitException</c>, wherever it sits in the chain, or our own check of the licence before asking), rather than because something is wrong.</summary>
    public static bool IsLicenceRefusal(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is Codex.Persistence.BackupNotLicensedException || current.GetType().Name == "LicenseLimitException") return true;
        }

        return false;
    }
}
