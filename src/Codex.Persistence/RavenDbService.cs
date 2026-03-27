using Raven.Client.Documents;
using Raven.Embedded;

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
        Environment.SetEnvironmentVariable("RAVEN_Server_FrameworkVersion", "10.0.0");

        // Use the currently executing framework version dynamically
        var version = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            .Replace(".NET ", "")
            .Split(' ')[0];

        var options = new ServerOptions
        {
            DataDirectory = dataDirectory,
            FrameworkVersion = version,
            ServerUrl = "http://127.0.0.1:0"
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

        _store = EmbeddedServer.Instance.GetDocumentStore(new DatabaseOptions("Campaigns"));
        _store.Initialize();
    }

    public void Dispose()
    {
        _store.Dispose();
    }
}
