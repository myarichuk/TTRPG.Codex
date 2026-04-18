using Raven.Client.Documents;
using Raven.Embedded;
using System.Reflection;

namespace Codex.Persistence;

public class RavenDbService : IDisposable
{
    private readonly IDocumentStore _store;

    public IDocumentStore Store => _store;

    public RavenDbService(string dataDirectory)
    {
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX", "2");
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD_PRE_RELEASE", "1");

        var options = new ServerOptions
        {
            DataDirectory = dataDirectory,
            ServerUrl = "http://127.0.0.1:0",
            FrameworkVersion = "10.0.0" // Re-adding FrameworkVersion due to RavenDB embedded matcher failing to find 10.0.2+ without explicit configuration.
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

        var databaseOptions = new DatabaseOptions("Campaigns");

        // This makes it so we can create individual document stores per context so multiple instances point at their respective temp folders in parallel.
        if (dataDirectory.Contains("TestRavenData_"))
        {
             // Unique database name per test context
             databaseOptions = new DatabaseOptions("Campaigns_" + Guid.NewGuid().ToString());
        }

        _store = EmbeddedServer.Instance.GetDocumentStore(databaseOptions);
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
    }
}
