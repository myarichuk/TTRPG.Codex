using Raven.Client.Documents;
using Raven.Embedded;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Operations.OngoingTasks;
using System;
using Microsoft.Extensions.Logging;

namespace Codex.Persistence;

public class RavenDbService : IDisposable
{
    private readonly Lazy<IDocumentStore> _store;
    private readonly ILogger<RavenDbService>? _logger;

    public IDocumentStore Store => _store.Value;

    /// <summary>True once the embedded server has finished booting. Read by
    /// <c>/health/ready</c> - unlike <see cref="Store"/>, this never triggers the boot.</summary>
    public bool IsStoreInitialized => _store.IsValueCreated;

    public RavenDbService(string dataDirectory, string databaseName = "Campaigns", bool runInMemory = false, ILogger<RavenDbService>? logger = null)
    {
        _logger = logger;
        _store = new Lazy<IDocumentStore>(() =>
        {
            var options = new ServerOptions
            {
                DataDirectory = dataDirectory,
                ServerUrl = "http://127.0.0.1:0",
                FrameworkVersion = null
            };
            options.CommandLineArgs.Add("--Setup.Mode=None");
            if (runInMemory)
            {
                options.CommandLineArgs.Add("--RunInMemory=True");
            }

            try
            {
                EmbeddedServer.Instance.StartServer(options);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("The server was already started"))
            {
                logger?.LogDebug(ex, "RavenDB server was already started.");
            }

            var databaseOptions = new DatabaseOptions(databaseName);
            var store = EmbeddedServer.Instance.GetDocumentStore(databaseOptions);
            store.Initialize();

            // Create indexes
            new KnowledgeIndex().Execute(store);
            new ActorsByCampaignIndex().Execute(store);
            new NotesByTargetIndex().Execute(store);
            new SessionsByCampaignIndex().Execute(store);
            new RegionsByCampaignIndex().Execute(store);
            new FactsByCampaignIndex().Execute(store);
            new EncountersByCampaignIndex().Execute(store);

            // 1.1: CampaignDocument.System was renamed to SystemId. A document written under the
            // old schema deserializes with SystemId empty and an ignored "System" field - patch
            // it once so existing campaigns aren't silently orphaned from their rules system.
            store.Operations.Send(new Raven.Client.Documents.Operations.PatchByQueryOperation(
                "from CampaignDocuments as c where c.SystemId = null and c.System != null " +
                "update { c.SystemId = c.System; delete c.System; }"))
                .WaitForCompletion(TimeSpan.FromSeconds(30));

            return store;
        });
    }

    public void Dispose()
    {
        if (_store.IsValueCreated)
        {
            _store.Value.Dispose();
        }
    }

    /// <summary>
    /// Registers (or updates, idempotently by task name) a daily full logical backup of this
    /// database into <paramref name="backupDirectory"/> (4.4). Logical <see cref="BackupType.Backup"/>
    /// rather than a snapshot so the file restores across RavenDB versions. Returns the server's
    /// backup task id. Safe to call on every startup - a second call updates the same task.
    /// </summary>
    public async Task<long> EnsureScheduledBackupAsync(string backupDirectory)
    {
        var fullPath = Path.GetFullPath(backupDirectory);
        Directory.CreateDirectory(fullPath);

        var existing = await Store.Maintenance.SendAsync(new GetOngoingTaskInfoOperation("codex-daily", OngoingTaskType.Backup));

        var config = new PeriodicBackupConfiguration
        {
            TaskId = existing?.TaskId ?? 0,
            Name = "codex-daily",
            BackupType = BackupType.Backup,
            FullBackupFrequency = "0 3 * * *",
            LocalSettings = new LocalSettings { FolderPath = fullPath }
        };
        var result = await Store.Maintenance.SendAsync(new UpdatePeriodicBackupOperation(config));
        _logger?.LogInformation("Scheduled daily RavenDB backup to {BackupDirectory} (task {TaskId}).", fullPath, result.TaskId);
        return result.TaskId;
    }

    /// <summary>
    /// Triggers a full backup on <paramref name="taskId"/> now and waits (up to
    /// <paramref name="timeout"/>) for a backup file to land in <paramref name="backupDirectory"/>.
    /// Used by the startup smoke check and the backup test - not by the daily schedule itself.
    /// </summary>
    public async Task BackupNowAsync(long taskId, string backupDirectory, TimeSpan timeout)
    {
        await Store.Maintenance.SendAsync(new StartBackupOperation(true, taskId));

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(backupDirectory)
                && Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories).Any())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException($"No backup file appeared in {backupDirectory} within {timeout}.");
    }
}