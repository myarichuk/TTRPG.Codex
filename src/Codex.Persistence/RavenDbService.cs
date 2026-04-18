using Raven.Client.Documents;
using Raven.Embedded;


namespace Codex.Persistence;

public class RavenDbService : IDisposable
{
    private readonly IDocumentStore _store;

    public IDocumentStore Store => _store;

    public RavenDbService(string dataDirectory, string databaseName = "Campaigns")
    {
        var options = new ServerOptions
        {
            DataDirectory = dataDirectory,
            ServerUrl = "http://127.0.0.1:0",
            FrameworkVersion = null // Allow RavenDB to auto-resolve the available runtime
        };
        options.CommandLineArgs.Add("--Setup.Mode=None");

        try
        {
            EmbeddedServer.Instance.StartServer(options);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("The server was already started"))
        {
            // Ignore if already started (for xUnit parallel tests)
        }

        var databaseOptions = new DatabaseOptions(databaseName);

        _store = EmbeddedServer.Instance.GetDocumentStore(databaseOptions);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
    }
}
