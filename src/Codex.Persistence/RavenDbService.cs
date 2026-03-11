using Raven.Client.Documents;
using Raven.Embedded;

namespace Codex.Persistence;

public class RavenDbService : IDisposable
{
    private readonly IDocumentStore _store;

    public IDocumentStore Store => _store;

    public RavenDbService(string dataDirectory)
    {
        var options = new ServerOptions
        {
            DataDirectory = dataDirectory,
        };

        try
        {
            EmbeddedServer.Instance.StartServer(options);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("The server was already started"))
        {
            // Ignore if already started (for xUnit parallel tests)
        }

        _store = EmbeddedServer.Instance.GetDocumentStore(new DatabaseOptions("Campaigns"));
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
    }
}
