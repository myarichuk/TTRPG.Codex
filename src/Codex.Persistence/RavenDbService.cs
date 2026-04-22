using Raven.Client.Documents;
using Raven.Embedded;
using Raven.Client.Documents.Indexes;
using System;
using Microsoft.Extensions.Logging;

namespace Codex.Persistence;

public class RavenDbService : IDisposable
{
    private readonly Lazy<IDocumentStore> _store;

    public IDocumentStore Store => _store.Value;

    public RavenDbService(string dataDirectory, string databaseName = "Campaigns", bool runInMemory = false, ILogger<RavenDbService>? logger = null)
    {
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
}