using Raven.Client.Documents;
using Raven.Embedded;


namespace Codex.Persistence;

public class RavenDbService : IDisposable
{
    private readonly Lazy<IDocumentStore> _store;

    public IDocumentStore Store => _store.Value;

    public RavenDbService(string dataDirectory, string databaseName = "Campaigns", bool runInMemory = false)
    {
        _store = new Lazy<IDocumentStore>(() =>
        {
            var options = new ServerOptions
            {
                DataDirectory = dataDirectory,
                ServerUrl = "http://127.0.0.1:0",
                FrameworkVersion = null // Allow RavenDB to auto-resolve the available runtime
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
                // Ignore if already started (for xUnit parallel tests)
            }

            var databaseOptions = new DatabaseOptions(databaseName);
            var store = EmbeddedServer.Instance.GetDocumentStore(databaseOptions);
            store.Initialize();
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
