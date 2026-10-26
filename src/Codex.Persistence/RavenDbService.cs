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

    public RavenDbService(string dataDirectory, string databaseName = "Campaigns", bool runInMemory = false, ILogger<RavenDbService>? logger = null)
    {
        _logger = logger;
        _store = new Lazy<IDocumentStore>(() =>
        {
            var options = new ServerOptions
            {
                DataDirectory = dataDirectory,
                ServerUrl = "http://127.0.0.1:0",
                FrameworkVersion = null,
                DotNetPath = ResolveDotNetHost()
            };
            options.CommandLineArgs.Add("--Setup.Mode=None");
            if (!runInMemory)
            {
                // Raven resolves these against the folder holding Raven.Server.dll, which for an installed
                // app is read-only (/Applications, Program Files). Keep everything it writes beside the data.
                var serverFiles = Path.Combine(Path.GetFullPath(dataDirectory), "..", "RavenServer");
                options.LogsPath = Path.GetFullPath(Path.Combine(serverFiles, "Logs"));
                var packages = Path.GetFullPath(Path.Combine(serverFiles, "Packages", "NuGet"));
                // Raven joins these into one command line without quoting, and the installed data folder
                // can contain spaces ("Application Support"), so quote the paths ourselves.
                options.CommandLineArgs.Add($"--Logs.NuGet.PackagesPath=\"{Path.Combine(packages, "Logging")}\"");
                options.CommandLineArgs.Add($"--Indexing.NuGetPackagesPath=\"{Path.Combine(packages, "Indexing")}\"");
            }
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
            new HomebrewByCampaignIndex().Execute(store);
            new WikiByCampaignIndex().Execute(store);
            new JournalByCampaignIndex().Execute(store);

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

    /// <summary>
    /// The embedded server runs as <c>dotnet Raven.Server.dll</c>, so it needs a host with the shared
    /// .NET and ASP.NET Core runtimes. A release ships one in <c>runtime/</c> and starts the app with
    /// it, so use (in order): <c>CODEX_DOTNET</c>, the host running this process, <c>DOTNET_ROOT</c>,
    /// a <c>runtime/</c> folder beside the app, and finally whatever <c>dotnet</c> is on the PATH.
    /// </summary>
    public static string ResolveDotNetHost()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("CODEX_DOTNET"),
            Environment.ProcessPath is { } process && Path.GetFileName(process).Equals(exe, StringComparison.OrdinalIgnoreCase) ? process : null,
            Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root ? Path.Combine(root, exe) : null,
            Path.Combine(AppContext.BaseDirectory, "runtime", exe)
        };

        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c)) ?? "dotnet";
    }

    /// <summary>
    /// Checks up front that every folder the embedded server writes to (the data and its own logs and
    /// packages, see the constructor) can be created and written, and returns a plain-language problem
    /// for the first one that can't - so an install into a read-only place fails at startup with the
    /// folder named, not on the first request with a RavenDB stack trace. Null when all is well.
    /// </summary>
    public static string? CheckWritable(string dataDirectory)
    {
        var data = Path.GetFullPath(dataDirectory);
        foreach (var folder in new[] { data, Path.GetFullPath(Path.Combine(data, "..", "RavenServer")) })
        {
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $".write-test-{Environment.ProcessId}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"TTRPG.Codex cannot write to '{folder}' ({ex.Message}). Choose a folder you own with the " +
                       "Codex__DataDirectory setting (or CODEX_DATA_HOME for the launcher), or fix its permissions.";
            }
        }

        return null;
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